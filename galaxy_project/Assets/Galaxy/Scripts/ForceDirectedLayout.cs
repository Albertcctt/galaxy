// ============================================================================
// 力导向布局迭代（3D 版 Fruchterman-Reingold 变体）
//
// 把每个文件看作一个粒子，对整个星系做 N 体物理模拟收敛（C++ 视角：一个
// 巨型粒子系统的主循环）。每轮迭代按顺序累加 4 种力到位移累加器 disp：
//   1) 全局斥力   ：任意两粒子间，|F| = repulsion · k² / d（k = idealDistance）
//   2) 依赖弹簧力 ：有依赖边的两粒子间，|F| = spring · |d − k|（胡克弹簧，
//      k 为自然长度；d < k 时反向推开，天然防止整图塌缩成一点）
//   3) 目录内聚力 ：同目录粒子被拉向本目录质心，刚度 = 系数 × √目录文件数
//      （随规模缓增 => 大目录既保持聚拢、又不会被压成过密的实心小球）
//   4) 向心引力   ：|F| = gravity · r（线性弱引力，防止孤立分量飘散）
//
// 每轮按退火温度限幅积分：单步位移不超过 T，T 从 startTemperature 线性
// 冷却到 0 —— 前期大步收敛，后期微调定型，最终冻结在收敛态。
//
// 收敛后另做一遍实体碰撞松弛（ResolveCollisions）：布局的物理模型只把节点
// 当"点"处理、不感知实体体积，穿插约束在这一步用实体包络球补上，
// 保证"实体之间无干涉"。
//
// 确定性（与 scanner 端同一设计哲学：同输入必得同输出）：
//   斥力用 Parallel.For 按粒子并行，但每个粒子 i 的合力在单个线程内按
//   j = 0..n-1 固定顺序累加 —— 浮点加法顺序与串行实现完全一致，
//   线程调度不影响结果。初值（GalaxyLayout 的种子）本身也是确定性的。
//
// 复杂度（v2：性能层，渲染上限从 1 万抬到数万级）：
//   - 斥力：Barnes-Hut 八叉树质心近似 O(n log n)（θ=0.75）。每轮重建一棵树
//     （宇宙模式 = 每宇宙一棵，天然隔离跨宇宙作用），查询为只读并行遍历。
//   - 接触力与碰撞解算/违规计数：空间哈希网格（桶内按下标升序、查询按固定
//     27 邻域顺序）——三套结构把布局代价从平方级压到近线性。
//   确定性不变：树按粒子下标顺序插入（质心增量累加顺序固定）、网格按下标
//   顺序填充、并行查询只读 —— 与旧的全对全固定循环一样可复现。
//
// 宇宙模式（Multiverse，可选 universe 参数非空时启用）：
//   每个节点属于一个"宇宙"（第一层文件夹的球体，见 UniverseLayout）。物理改为：
//     - 跨宇宙的斥力/弹簧一律不参与（宇宙只通过可视化连线发生关系）；
//     - 引力指向本宇宙球心（而不是世界原点）；
//     - 每轮积分后做球壁投影：节点被压回本宇宙的"墙上"（文件不出球体）。
//   收敛后不做全局重心归零/缩放（各球心是绝对坐标，由图外给定）。
// ============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Galaxy
{
    public static class ForceDirectedLayout
    {
        // 参数打包（由 GalaxyBuilder 的 Inspector 字段在运行时填充）。
        // C++ 类比：把散装全局变量收进一个配置 struct 传参，便于测试与调参。
        public struct Params
        {
            public int   iterations;        // 迭代轮数
            public float idealDistance;     // k：弹簧自然长度 / 斥力尺度
            public float repulsion;         // 全局斥力强度
            public float spring;            // 依赖边弹簧强度
            public float cohesion;          // 目录内聚强度
            public float gravity;           // 向心引力强度
            public float startTemperature;  // 初始退火温度（单步最大位移）
            public float contactStiffness;  // 硬核接触刚度（实体互不穿插的强约束）
        }

        // 宇宙模式下的全局斥力衰减：均匀斥力相当于"气体压强"，会把星团压平
        // 成均匀填满（实测：全强度斥力下层级引力核形同虚设）。衰减 + 短程截断
        // （见 Simulate 内 repCut）双管齐下：真空里只有近邻斥力负责间距，没有
        // 长程气压负责填平，星团与空隙才能共存。防重叠仍由硬核接触力 +
        // 体积自适应硬保证。
        private const float UniverseRepulsionScale = 0.25f;

        // 宇宙约束（多元宇宙模式；null = 传统全局布局）。
        // C++ 类比：给布局主循环传一个可选的"分区元数据表"，热循环里按表分支。
        public sealed class UniverseField
        {
            public int[] universeOf;      // 每个节点所属宇宙序号
            public Vector3[] centers;     // 每个宇宙的球心
            public float[] wallRadii;     // 每个宇宙的文件活动半径
            public float[] innerRadii;    // 内核排斥包络（> 0 的宇宙中心留空：黑洞不干涉）
            public Vector3[][] anchorOf;  // 每个节点的引力核链（[0] = 宇宙球心，越深越强）
            public float[][] anchorStiffness;   // 对应引力强度
        }

        // ==================================================================
        // Barnes-Hut 八叉树：斥力的 O(n²) 全对全 → O(n log n) 质心近似。
        //   - 构建：按粒子下标顺序插入，质心/质量沿插入路径增量累加 —— 顺序
        //     固定 => 确定性（与旧版固定 j 循环同一哲学）。
        //   - 查询：只读遍历（手工栈，无递归 / 无分配），Parallel.For 下安全。
        //   - 数据结构：SoA 平铺数组按需倍增（C++ 视角：预分配竞技场 + 扩容，
        //     而不是每个树节点 new 一个对象）。
        //   - 极端重合：叶子细分到分辨率下限后多余粒子挂链在叶子上，
        //     查询时逐个精确求和（近似失真只发生在"本来就要接触挤压"的区域）。
        // ==================================================================
        private const float BhTheta = 0.75f;   // 近似判定：节点边长/距离 < θ 用质心聚合

        private sealed class Octree
        {
            private const float MinHalf = 0.02f;   // 叶子最小半边长（细分分辨率下限）
            private const int StackCap = 512;      // 查询栈容量（深度 × 8 待访兄弟，够用）

            private float[] mGeoX = new float[1024], mGeoY = new float[1024], mGeoZ = new float[1024];
            private float[] mComX = new float[1024], mComY = new float[1024], mComZ = new float[1024];
            private float[] mMass = new float[1024];
            private float[] mHalf = new float[1024];
            private int[] mChildBase = new int[1024];
            private int[] mFirstBody = new int[1024];
            private int[] mNextBody = System.Array.Empty<int>();
            private int mCount;
            private readonly int[] mStack = new int[StackCap];

            /// <summary>重建树。members = null 表示全体节点（传统模式）。</summary>
            public void Build(Vector3[] pos, List<int> members, int n)
            {
                mCount = 0;
                if (mNextBody.Length < n) mNextBody = new int[n];
                bool global = members == null;
                int count = global ? n : members.Count;

                // 根包围盒：全体成员 AABB 立方体化（+1 边距防边界粒子落格）
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int t = 0; t < count; t++)
                {
                    Vector3 p = pos[global ? t : members[t]];
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
                }
                if (count == 0) { minX = minY = minZ = 0f; maxX = maxY = maxZ = 1f; }
                float half = Mathf.Max(maxX - minX, Mathf.Max(maxY - minY, maxZ - minZ)) * 0.5f + 1f;
                AllocNode((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f, half);

                for (int t = 0; t < count; t++)
                {
                    int body = global ? t : members[t];
                    Insert(body, pos);
                }
            }

            private int AllocNode(float gx, float gy, float gz, float half)
            {
                if (mCount == mGeoX.Length) Grow();
                int i = mCount++;
                mGeoX[i] = gx; mGeoY[i] = gy; mGeoZ[i] = gz; mHalf[i] = half;
                mComX[i] = 0f; mComY[i] = 0f; mComZ[i] = 0f; mMass[i] = 0f;
                mChildBase[i] = -1; mFirstBody[i] = -1;
                return i;
            }

            private void Grow()
            {
                int cap = mGeoX.Length * 2;
                System.Array.Resize(ref mGeoX, cap); System.Array.Resize(ref mGeoY, cap);
                System.Array.Resize(ref mGeoZ, cap);
                System.Array.Resize(ref mComX, cap); System.Array.Resize(ref mComY, cap);
                System.Array.Resize(ref mComZ, cap);
                System.Array.Resize(ref mMass, cap); System.Array.Resize(ref mHalf, cap);
                System.Array.Resize(ref mChildBase, cap); System.Array.Resize(ref mFirstBody, cap);
            }

            private int Octant(int node, Vector3 p)
            {
                return (p.x >= mGeoX[node] ? 1 : 0)
                     | (p.y >= mGeoY[node] ? 2 : 0)
                     | (p.z >= mGeoZ[node] ? 4 : 0);
            }

            private void AddAggregate(int node, Vector3 p)
            {
                float m = (mMass[node] += 1f);
                float inv = 1f / m;
                mComX[node] += (p.x - mComX[node]) * inv;
                mComY[node] += (p.y - mComY[node]) * inv;
                mComZ[node] += (p.z - mComZ[node]) * inv;
            }

            private void Subdivide(int node)
            {
                while (mCount + 8 > mGeoX.Length) Grow();
                float half = mHalf[node] * 0.5f;
                float q = half * 0.5f;   // 子节点几何中心相对父中心的偏移
                float gx = mGeoX[node], gy = mGeoY[node], gz = mGeoZ[node];
                int baseIdx = mCount;
                mCount += 8;
                for (int o = 0; o < 8; o++)
                {
                    int i = baseIdx + o;
                    mGeoX[i] = gx + ((o & 1) != 0 ? q : -q);
                    mGeoY[i] = gy + ((o & 2) != 0 ? q : -q);
                    mGeoZ[i] = gz + ((o & 4) != 0 ? q : -q);
                    mHalf[i] = half; mMass[i] = 0f;
                    mComX[i] = 0f; mComY[i] = 0f; mComZ[i] = 0f;
                    mChildBase[i] = -1; mFirstBody[i] = -1;
                }
                mChildBase[node] = baseIdx;
            }

            private void Insert(int body, Vector3[] pos)
            {
                Vector3 p = pos[body];
                int node = 0;
                while (true)
                {
                    AddAggregate(node, p);   // 每访问一个节点恰好叠加一次

                    if (mChildBase[node] >= 0)
                    {
                        node = mChildBase[node] + Octant(node, p);
                        continue;
                    }
                    // 叶子
                    if (mFirstBody[node] < 0)
                    {
                        mFirstBody[node] = body;
                        mNextBody[body] = -1;
                        return;
                    }
                    if (mHalf[node] <= MinHalf)
                    {
                        // 分辨率下限：挂链，查询时逐个精确求和
                        mNextBody[body] = mFirstBody[node];
                        mFirstBody[node] = body;
                        return;
                    }
                    // 细分并把旧链下放到 8 个子叶（聚合随之转移）
                    Subdivide(node);
                    int b = mFirstBody[node];
                    mFirstBody[node] = -1;
                    while (b >= 0)
                    {
                        int nb = mNextBody[b];
                        Vector3 bp = pos[b];
                        int child = mChildBase[node] + Octant(node, bp);
                        mNextBody[b] = mFirstBody[child];
                        mFirstBody[child] = b;
                        AddAggregate(child, bp);
                        b = nb;
                    }
                    // 本粒子继续下探（本节点已叠加过，直接进子节点）
                    node = mChildBase[node] + Octant(node, p);
                }
            }

            /// <summary>
            /// 累计 p 受到的斥力（近似）：远节点用质心聚合，近节点/含自身节点细分，
            /// 叶子逐个精确。cutoff=true 时对超过截断距离的节点不施力（宇宙模式
            /// 的"短程截断"：没有全局气压，星团间空隙才保得住）。
            /// </summary>
            public void AccumulateRepulsion(Vector3[] pos, Vector3 p, int self, float repK,
                                            bool cutoff, float cutSq,
                                            ref float ax, ref float ay, ref float az)
            {
                int sp = 0;
                mStack[sp++] = 0;
                while (sp > 0)
                {
                    int node = mStack[--sp];

                    if (mChildBase[node] < 0)
                    {
                        // 叶：链上逐个精确（跳过自身）
                        for (int b = mFirstBody[node]; b >= 0; b = mNextBody[b])
                        {
                            if (b == self) continue;
                            float dx = p.x - pos[b].x;
                            float dy = p.y - pos[b].y;
                            float dz = p.z - pos[b].z;
                            float d2 = dx * dx + dy * dy + dz * dz;
                            if (cutoff && d2 > cutSq) continue;
                            float f = repK / (d2 + 0.01f);
                            ax += dx * f; ay += dy * f; az += dz * f;
                        }
                        continue;
                    }

                    float dxc = p.x - mComX[node];
                    float dyc = p.y - mComY[node];
                    float dzc = p.z - mComZ[node];
                    float d2c = dxc * dxc + dyc * dyc + dzc * dzc;

                    // 含自身的节点不能用聚合（会把自身算进去）→ 必须细分
                    bool containsSelf =
                        Mathf.Abs(p.x - mGeoX[node]) <= mHalf[node] &&
                        Mathf.Abs(p.y - mGeoY[node]) <= mHalf[node] &&
                        Mathf.Abs(p.z - mGeoZ[node]) <= mHalf[node];
                    float s = mHalf[node] * 2f;
                    if (!containsSelf && s * s < BhTheta * BhTheta * d2c)
                    {
                        if (cutoff && d2c > cutSq) continue;
                        float f = repK * mMass[node] / (d2c + 0.01f);
                        ax += dxc * f; ay += dyc * f; az += dzc * f;
                        continue;
                    }

                    if (sp + 8 > StackCap) continue;   // 栈满保险（正常深度远达不到）
                    int b0 = mChildBase[node];
                    for (int o = 0; o < 8; o++) mStack[sp++] = b0 + o;
                }
            }
        }

        // ==================================================================
        // 空间哈希网格（容器复用版）：接触力（Simulate 内）与碰撞解算 /
        // 违规计数共用。桶按下标升序填充、查询按固定 27 邻域顺序遍历
        // => 与八叉树同款的确定性。cell 尺寸约定：≥ 2×最大半径 ——
        // 任意互侵对的间距 < need ≤ 2×maxR ≤ cell，必落在邻域内。
        // ==================================================================
        private sealed class SpatialGrid
        {
            private readonly Dictionary<(int, int, int), List<int>> mCells =
                new Dictionary<(int, int, int), List<int>>();
            private readonly Stack<List<int>> mPool = new Stack<List<int>>();

            public void Clear()
            {
                foreach (KeyValuePair<(int, int, int), List<int>> kv in mCells)
                {
                    kv.Value.Clear();
                    mPool.Push(kv.Value);
                }
                mCells.Clear();
            }

            public void Add(Vector3 p, float cell, int index)
            {
                var key = (Mathf.FloorToInt(p.x / cell),
                           Mathf.FloorToInt(p.y / cell),
                           Mathf.FloorToInt(p.z / cell));
                if (!mCells.TryGetValue(key, out List<int> list))
                {
                    list = mPool.Count > 0 ? mPool.Pop() : new List<int>();
                    mCells[key] = list;
                }
                list.Add(index);
            }

            public List<int> Get(int x, int y, int z)
            {
                return mCells.TryGetValue((x, y, z), out List<int> list) ? list : null;
            }
        }

        // 复用的结构实例（单线程构建 → 并行只读查询；Simulate 不会并发调用）
        private static readonly Octree s_GlobalTree = new Octree();
        private static readonly List<Octree> s_UniverseTrees = new List<Octree>();
        private static readonly List<List<int>> s_UniverseMembers = new List<List<int>>();
        private static readonly SpatialGrid s_ContactGrid = new SpatialGrid();

        /// <summary>
        /// 从 seed 位置出发做力导向收敛，返回收敛后的新位置数组（长度不变）。
        /// entityRadius：每个实体的包络半径（球 = 半径；立方体 = 半对角线），
        /// 用于硬核接触力 —— 多处大目录被跨目录依赖边互相拽紧时，接触区会被
        /// 压到间距远小于 k，仅靠点模型的斥力平衡不了（实测残留数千对穿插），
        /// 必须在迭代中就让实体"占体积"。
        /// universe 非空时切换为"宇宙模式"（见文件头说明）：节点被约束在自己的
        /// 宇宙球内，targetRadius 不再使用（球心为绝对坐标）。
        /// </summary>
        public static Vector3[] Simulate(GalaxyGraph graph, Vector3[] seed, Params p,
                                         float targetRadius, float[] entityRadius,
                                         UniverseField universe = null)
        {
            int n = seed.Length;
            if (n == 0) return seed;

            var pos  = (Vector3[])seed.Clone();   // 不改调用方的数组（值语义复制）
            var disp = new Vector3[n];

            // 把链接展开成两个扁平 int 数组：热点循环里少一层对象解引用
            int linkCount = graph.links != null ? graph.links.Length : 0;
            var edgeSrc = new int[linkCount];
            var edgeDst = new int[linkCount];
            for (int e = 0; e < linkCount; e++)
            {
                edgeSrc[e] = graph.links[e].source;
                edgeDst[e] = graph.links[e].target;
            }

            // 目录分组：dirOf[node] = 组号（-1 = 根目录，不参与内聚）
            var dirMap = new Dictionary<string, int>();
            var dirMembers = new List<List<int>>();
            var dirOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                string dir = graph.nodes[i].dir;
                if (string.IsNullOrEmpty(dir) || dir == ".") { dirOf[i] = -1; continue; }

                if (!dirMap.TryGetValue(dir, out int g))
                {
                    g = dirMembers.Count;
                    dirMap[dir] = g;
                    dirMembers.Add(new List<int>());
                }
                dirOf[i] = g;
                dirMembers[g].Add(i);
            }
            var dirCentroid = new Vector3[dirMembers.Count];
            // 内聚刚度 = 系数 × √成员数。曾用 ∝N：大目录刚度线性增长，被压成
            // 半径不足 3 的实心密球（实测中央糊成一团）。√N 缓增：大目录保持
            // 可读的团尺度，小目录也不会被过度拉散。
            var dirStiffness = new float[dirMembers.Count];
            for (int g = 0; g < dirMembers.Count; g++)
                dirStiffness[g] = p.cohesion * Mathf.Sqrt(dirMembers[g].Count);

            float k = Mathf.Max(0.01f, p.idealDistance);
            float repK = p.repulsion * k * k * (universe != null ? UniverseRepulsionScale : 1f);
            // 宇宙模式：斥力短程截断（2.5 倍自然长度之外完全无作用）—— 没有全局
            // "气压"，星团间空隙不会被填平；巡航在空隙里的节点由引力核链召回
            float repCutSq = (k * 2.5f) * (k * 2.5f);
            float contactK = Mathf.Max(0f, p.contactStiffness);
            int iterations = Mathf.Max(1, p.iterations);

            // ---- 结构预备：接触网格 cell 尺寸（≥ 2×最大半径）与宇宙成员清单 ----
            float maxEntityR = 0f;
            for (int i = 0; i < n; i++) maxEntityR = Mathf.Max(maxEntityR, entityRadius[i]);
            float contactCell = Mathf.Max(maxEntityR * 2f, 0.5f);

            int universeCount = 0;
            if (universe != null)
            {
                for (int i = 0; i < n; i++)
                    universeCount = Mathf.Max(universeCount, universe.universeOf[i] + 1);
                s_UniverseMembers.Clear();
                for (int u = 0; u < universeCount; u++) s_UniverseMembers.Add(new List<int>());
                for (int i = 0; i < n; i++) s_UniverseMembers[universe.universeOf[i]].Add(i);
                while (s_UniverseTrees.Count < universeCount) s_UniverseTrees.Add(new Octree());
            }

            for (int iter = 0; iter < iterations; iter++)
            {
                float temperature = p.startTemperature * (1f - (float)iter / iterations);

                // ---- 0) 重建斥力结构（串行构建 => 确定性；查询阶段只读）----
                if (universe == null)
                {
                    s_GlobalTree.Build(pos, null, n);
                }
                else
                {
                    // 宇宙模式：每宇宙一棵树 —— 跨宇宙作用天然被隔离
                    for (int u = 0; u < universeCount; u++)
                        s_UniverseTrees[u].Build(pos, s_UniverseMembers[u], n);
                }
                s_ContactGrid.Clear();
                for (int i = 0; i < n; i++) s_ContactGrid.Add(pos[i], contactCell, i);

                // ---- 1) 斥力（Barnes-Hut 质心近似 O(n log n)）+ 硬核接触力（网格 27 邻域）----
                // 树/网格只在读侧使用；每个 i 的遍历顺序固定 => 与旧版一样可复现
                Parallel.For(0, n, i =>
                {
                    Vector3 pi = pos[i];
                    float ax = 0f, ay = 0f, az = 0f;

                    if (universe == null)
                    {
                        s_GlobalTree.AccumulateRepulsion(pos, pi, i, repK, false, 0f,
                                                         ref ax, ref ay, ref az);
                    }
                    else
                    {
                        int u = universe.universeOf[i];
                        s_UniverseTrees[u].AccumulateRepulsion(pos, pi, i, repK, true, repCutSq,
                                                               ref ax, ref ay, ref az);
                    }

                    // 硬核接触力：包络球互相侵入时，越深推得越狠（线性弹簧硬度）
                    int cx = Mathf.FloorToInt(pi.x / contactCell);
                    int cy = Mathf.FloorToInt(pi.y / contactCell);
                    int cz = Mathf.FloorToInt(pi.z / contactCell);
                    float ri = entityRadius[i];
                    for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        List<int> list = s_ContactGrid.Get(cx + ox, cy + oy, cz + oz);
                        if (list == null) continue;
                        for (int t = 0; t < list.Count; t++)
                        {
                            int j = list[t];
                            if (j == i) continue;
                            if (universe != null &&
                                universe.universeOf[j] != universe.universeOf[i]) continue;
                            float dx = pi.x - pos[j].x;
                            float dy = pi.y - pos[j].y;
                            float dz = pi.z - pos[j].z;
                            float d2raw = dx * dx + dy * dy + dz * dz;
                            float need = ri + entityRadius[j];
                            if (d2raw < need * need && d2raw > 1e-8f)
                            {
                                float d = Mathf.Sqrt(d2raw);
                                float push = contactK * (need - d) / d;
                                ax += dx * push;
                                ay += dy * push;
                                az += dz * push;
                            }
                        }
                    }

                    disp[i] = new Vector3(ax, ay, az);
                });

                // ---- 2) 依赖弹簧（胡克模型，串行：边数少，且固定累加顺序）----
                // 力 = spring·(d − k)：d > k 时相吸、d < k 时相斥。必须有"自然
                // 长度"：FR 原版的 d²/k 无静长，上千节点时会把整图绞成致密核心，
                // 只剩斥力把低度数节点顶到外圈（实测塌缩后的失败形态）。
                for (int e = 0; e < linkCount; e++)
                {
                    int a = edgeSrc[e];
                    int b = edgeDst[e];
                    // 宇宙模式：跨宇宙依赖边只作可视化连线（虫洞），不参与物理
                    if (universe != null && universe.universeOf[a] != universe.universeOf[b]) continue;
                    float dx = pos[b].x - pos[a].x;
                    float dy = pos[b].y - pos[a].y;
                    float dz = pos[b].z - pos[a].z;
                    float d = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d < 1e-4f) continue;
                    float f = p.spring * (d - k) / d;   // 贡献向量 = delta · f
                    disp[a].x += dx * f; disp[a].y += dy * f; disp[a].z += dz * f;
                    disp[b].x -= dx * f; disp[b].y -= dy * f; disp[b].z -= dz * f;
                }

                // ---- 3) 目录内聚 ----
                if (universe != null && universe.anchorOf != null)
                {
                    // 层级引力核（递归星团场）：拉向"宇宙球心 + 各级目录核"的祖先链，
                    // 越深（越接近叶目录）的核引力越强 —— 大尺度松散、小尺度紧致，
                    // 核为固定点（非动态质心）：越吸越紧时斥力随之增长，团有稳定
                    // 尺寸，不会塌成实心球。
                    for (int i = 0; i < n; i++)
                    {
                        Vector3[] anchors = universe.anchorOf[i];
                        float[] stiff = universe.anchorStiffness[i];
                        for (int t = 0; t < anchors.Length; t++)
                        {
                            disp[i] += (anchors[t] - pos[i]) * stiff[t];
                        }
                    }
                }
                else
                {
                    // 传统模式：动态质心内聚（先重算各目录质心，再把成员弱拉向质心）
                    for (int g = 0; g < dirMembers.Count; g++)
                    {
                        List<int> members = dirMembers[g];
                        Vector3 sum = Vector3.zero;
                        for (int t = 0; t < members.Count; t++) sum += pos[members[t]];
                        dirCentroid[g] = sum / members.Count;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        int g = dirOf[i];
                        if (g < 0) continue;
                        disp[i] += (dirCentroid[g] - pos[i]) * dirStiffness[g];
                    }
                }

                // ---- 4) 向心引力（弱）：传统模式指向原点；宇宙模式指向本宇宙球心 ----
                if (universe == null)
                {
                    for (int i = 0; i < n; i++)
                    {
                        disp[i] -= pos[i] * p.gravity;
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        disp[i] -= (pos[i] - universe.centers[universe.universeOf[i]]) * p.gravity;
                    }
                }

                // ---- 5) 退火限幅积分：单步位移 = min(|disp|, T)，方向不变 ----
                for (int i = 0; i < n; i++)
                {
                    Vector3 d = disp[i];
                    float len = d.magnitude;
                    if (len < 1e-6f) continue;
                    pos[i] += d * (Mathf.Min(len, temperature) / len);
                }

                // ---- 6) 宇宙软墙：越界节点沿径向压回墙内（文件不出球体）；
                //      黑洞宇宙再加"内核排斥"：节点被挡在黑洞视觉包络之外
                //      （实体与黑洞不干涉的硬保证，实体包络整体参与判定）----
                if (universe != null)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int u = universe.universeOf[i];
                        Vector3 off = pos[i] - universe.centers[u];
                        float d = off.magnitude;
                        float outerLimit = Mathf.Max(0.05f, universe.wallRadii[u] - entityRadius[i]);
                        float innerLimit = universe.innerRadii != null && universe.innerRadii[u] > 0f
                            ? universe.innerRadii[u] + entityRadius[i] + 0.15f
                            : 0f;
                        if (d > outerLimit)
                        {
                            pos[i] = universe.centers[u] + off * (outerLimit / Mathf.Max(d, 1e-6f));
                        }
                        else if (d < innerLimit)
                        {
                            Vector3 dirVec = d > 1e-5f ? off / d : new Vector3(1f, 0f, 0f);
                            pos[i] = universe.centers[u] + dirVec * innerLimit;
                        }
                    }
                }
            }

            // 传统模式：重心归零 + 缩放到目标半径；宇宙模式：坐标已是绝对位置，不动
            if (universe == null) RecenterAndRescale(pos, targetRadius);
            return pos;
        }

        // ------------------------------------------------------------------
        // 无干涉约束（实体不可互相穿插）：布局收敛后的空间校正。
        // 位置投影（PBD 风格）接触求解：对互相侵入的对子沿中心连线推开，
        // 每轮几乎完全分离（松弛因子 RELAX），迭代到无违规为止。
        // 关键：这是纯几何求解、没有对抗力 —— 高度数枢纽文件会把周边实体
        // 压成"焊死"的密集球（数百条长弹簧的拉力远大于代用接触力，实测
        // 接触力 8 量级完全顶不住），只有几何投影能把它逐步撬开。
        // 复杂度（v2 网格版）：每轮 O(n + 邻域对数) —— 空间哈希分桶后只检查
        // 27 邻域（cell ≥ 2×最大半径 => 任意侵入对必在邻域内），大仓库不再
        // 被平方级的对数扫描拖死。确定性：桶按下标升序填充、对子按 (i<j) 的
        // 下标序处理，与旧版全局顺序遍历同一结果语义。
        // 返回值 = 残留侵入对数（正常为 0，硬性校验数字）。
        // ------------------------------------------------------------------
        private const float RELAX = 0.9f;   // 每轮把一个违规对推开的比例

        public static int ResolveCollisions(Vector3[] pos, float[] radius, int rounds)
        {
            int n = pos.Length;
            float maxR = 0f;
            for (int i = 0; i < n; i++) maxR = Mathf.Max(maxR, radius[i]);
            float cell = Mathf.Max(maxR * 2f, 0.5f);
            var grid = new SpatialGrid();

            for (int round = 0; round < rounds; round++)
            {
                grid.Clear();
                for (int i = 0; i < n; i++) grid.Add(pos[i], cell, i);

                bool violated = false;
                for (int i = 0; i < n; i++)
                {
                    int cx = Mathf.FloorToInt(pos[i].x / cell);
                    int cy = Mathf.FloorToInt(pos[i].y / cell);
                    int cz = Mathf.FloorToInt(pos[i].z / cell);
                    for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    for (int oz = -1; oz <= 1; oz++)
                    {
                        List<int> list = grid.Get(cx + ox, cy + oy, cz + oz);
                        if (list == null) continue;
                        for (int t = 0; t < list.Count; t++)
                        {
                            int j = list[t];
                            if (j <= i) continue;   // 每个无序对只处理一次（小下标为准）
                            float need = radius[i] + radius[j];
                            float dx = pos[j].x - pos[i].x;
                            float dy = pos[j].y - pos[i].y;
                            float dz = pos[j].z - pos[i].z;
                            float d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 >= need * need) continue;   // 未侵入：跳过

                            float d = Mathf.Sqrt(d2);
                            if (d < 1e-5f)
                            {
                                // 两实体完全重合（极端情形）：按索引生成一个确定方向
                                float angle = i * 2.399963f;   // 黄金角，相邻索引方向错开
                                dx = Mathf.Cos(angle);
                                dy = Mathf.Sin(angle);
                                dz = 0.5f;
                                float inv = 1f / Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                                dx *= inv; dy *= inv; dz *= inv;
                                d = 0f;
                            }
                            else
                            {
                                dx /= d; dy /= d; dz /= d;
                            }

                            float push = (need - d) * 0.5f * RELAX;   // 两侧各退让一半 × 松弛因子
                            pos[i].x -= dx * push; pos[i].y -= dy * push; pos[i].z -= dz * push;
                            pos[j].x += dx * push; pos[j].y += dy * push; pos[j].z += dz * push;
                            violated = true;
                        }
                    }
                }
                if (!violated) return 0;   // 提前收敛

                if ((round + 1) % 100 == 0)
                    Debug.Log($"[Collision] 第 {round + 1} 轮：剩余侵入 {CountViolations(pos, radius)} 对");
            }

            // 保险路径：轮数用尽仍有违规 —— 返回残留计数（网格统计）
            int leftover = CountViolations(pos, radius);
            if (leftover > 0)
            {
                Debug.LogWarning($"[Collision] 轮数用尽仍残留侵入 {leftover} 对（体积自适应将按其兜底）");
            }
            return leftover;
        }

        // 违规计数（供日志与校验；网格版 O(n + 邻域对数)）
        public static int CountViolations(Vector3[] pos, float[] radius)
        {
            int n = pos.Length;
            float maxR = 0f;
            for (int i = 0; i < n; i++) maxR = Mathf.Max(maxR, radius[i]);
            float cell = Mathf.Max(maxR * 2f, 0.5f);
            var grid = new SpatialGrid();
            for (int i = 0; i < n; i++) grid.Add(pos[i], cell, i);

            int count = 0;
            for (int i = 0; i < n; i++)
            {
                int cx = Mathf.FloorToInt(pos[i].x / cell);
                int cy = Mathf.FloorToInt(pos[i].y / cell);
                int cz = Mathf.FloorToInt(pos[i].z / cell);
                for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    List<int> list = grid.Get(cx + ox, cy + oy, cz + oz);
                    if (list == null) continue;
                    for (int t = 0; t < list.Count; t++)
                    {
                        int j = list[t];
                        if (j <= i) continue;
                        float need = radius[i] + radius[j];
                        float dx = pos[j].x - pos[i].x;
                        float dy = pos[j].y - pos[i].y;
                        float dz = pos[j].z - pos[i].z;
                        if (dx * dx + dy * dy + dz * dz < need * need) count++;
                    }
                }
            }
            return count;
        }

        // ------------------------------------------------------------------
        // 体积自适应（"调整实体体积"的构造性硬保证）：
        // 求每个实体到最近邻居的距离 dmin，把包络半径压到 r ≤ 0.45·dmin。
        // 于是任意一对 (i,j)：r_i + r_j ≤ 0.45·d_ij + 0.45·d_ij = 0.9·d_ij < d_ij
        // —— 包络球必不重叠 => 实体必不穿插，与位置解算是否收敛无关。
        // 密集区实体自然收缩成小颗粒（星核星尘感），稀疏区保持原始体积。
        // 用均匀网格求 dmin（O(n)）。返回被收缩的实体数（供日志）。
        // ------------------------------------------------------------------
        public static int CapRadiiToLocalClearance(Vector3[] pos, float[] radius)
        {
            int n = pos.Length;
            const float Cell = 5f;
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < n; i++)
            {
                (int, int, int) key = CellOf(pos[i], Cell);
                if (!grid.TryGetValue(key, out List<int> list)) grid[key] = list = new List<int>();
                list.Add(i);
            }

            int shrunkCount = 0;
            for (int i = 0; i < n; i++)
            {
                (int cx, int cy, int cz) = CellOf(pos[i], Cell);
                float dmin2 = float.MaxValue;
                for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    if (!grid.TryGetValue((cx + ox, cy + oy, cz + oz), out List<int> list)) continue;
                    for (int t = 0; t < list.Count; t++)
                    {
                        int j = list[t];
                        if (j == i) continue;
                        float dx = pos[j].x - pos[i].x;
                        float dy = pos[j].y - pos[i].y;
                        float dz = pos[j].z - pos[i].z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < dmin2) dmin2 = d2;
                    }
                }

                if (dmin2 < float.MaxValue)
                {
                    float allowed = 0.45f * Mathf.Sqrt(dmin2);
                    if (allowed < radius[i])
                    {
                        radius[i] = Mathf.Max(allowed, 0.02f);
                        shrunkCount++;
                    }
                }
            }
            return shrunkCount;
        }

        private static (int, int, int) CellOf(Vector3 p, float cell)
        {
            return (Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
        }

        // 收敛后把重心平移回原点，并缩放到目标半径。缩放基准用半径的 98 分位数
        // 而不是最大值：个别游离节点（无边文件，只受斥力）会飘到很外圈，用 max
        // 做基准会把整张星图压小、真实结构挤成一团。分位数对离群点天然鲁棒。
        private static void RecenterAndRescale(Vector3[] pos, float targetRadius)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < pos.Length; i++) center += pos[i];
            center /= pos.Length;

            var radii = new float[pos.Length];
            for (int i = 0; i < pos.Length; i++)
            {
                pos[i] -= center;
                radii[i] = pos[i].magnitude;
            }

            var sorted = (float[])radii.Clone();
            System.Array.Sort(sorted);
            float reference = sorted[Mathf.Min(sorted.Length - 1, (int)(sorted.Length * 0.98f))];
            if (reference < 1e-4f || targetRadius <= 0f) return;

            float scale = targetRadius / reference;
            for (int i = 0; i < pos.Length; i++) pos[i] *= scale;
        }
    }
}
