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
// 复杂度：斥力 O(n²)。n ≈ 2k 时每轮 356 万次力计算（全对全，不做 i<j
//   对称优化：否则两个线程会写同一个 disp 元素产生数据竞争），多核并行
//   单轮仅几毫秒。若仓库规模涨到上万文件，把斥力段换成 Barnes-Hut 八叉树
//   （质心近似，O(n log n)）即可，外部接口不变。
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

            for (int iter = 0; iter < iterations; iter++)
            {
                float temperature = p.startTemperature * (1f - (float)iter / iterations);

                // ---- 1) 全局斥力 + 硬核接触力（对 i 并行；j 循环在单线程内固定顺序）----
                Parallel.For(0, n, i =>
                {
                    Vector3 pi = pos[i];
                    float ri = entityRadius[i];
                    float ax = 0f, ay = 0f, az = 0f;
                    for (int j = 0; j < n; j++)
                    {
                        if (j == i) continue;
                        float dx = pi.x - pos[j].x;
                        float dy = pi.y - pos[j].y;
                        float dz = pi.z - pos[j].z;
                        float d2raw = dx * dx + dy * dy + dz * dz;
                        // 宇宙模式：跨宇宙不施力（间隙 ≥ gap 已保证不接触，
                        // 互相推挤只会把住户压在球壁附近、分布失衡）；斥力短程截断
                        if (universe != null)
                        {
                            if (universe.universeOf[j] != universe.universeOf[i]) continue;
                            if (d2raw > repCutSq) continue;
                        }

                        // 全局斥力（+0.01 软化：几乎重合时避免除零）
                        float s = repK / (d2raw + 0.01f);
                        ax += dx * s;
                        ay += dy * s;
                        az += dz * s;

                        // 硬核接触力：包络球互相侵入时，越深推得越狠（线性弹簧硬度）
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
        // 顺序遍历 + 固定顺序 => 和其它阶段一样是确定性的。
        // 复杂度 O(n²·轮数)；轻度违规几轮即净，深度卡死区可能需要数百轮
        // （提前收敛会退出）。返回值 = 残留侵入对数（正常为 0，硬性校验数字）。
        // ------------------------------------------------------------------
        private const float RELAX = 0.9f;   // 每轮把一个违规对推开的比例

        public static int ResolveCollisions(Vector3[] pos, float[] radius, int rounds)
        {
            int n = pos.Length;
            for (int round = 0; round < rounds; round++)
            {
                bool violated = false;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
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
                if (!violated) return 0;   // 提前收敛

                if ((round + 1) % 100 == 0)
                    Debug.Log($"[Collision] 第 {round + 1} 轮：剩余侵入 {CountViolations(pos, radius)} 对");
            }

            // 保险路径：轮数用尽仍有违规 —— 统计 + 诊断（最深侵入对 / 大小体积分解）
            int leftover = 0, bigNeed = 0;
            float worstOverlap = 0f, worstD = 0f, worstNeed = 0f;
            int wi = -1, wj = -1;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float need = radius[i] + radius[j];
                    float dx = pos[j].x - pos[i].x;
                    float dy = pos[j].y - pos[i].y;
                    float dz = pos[j].z - pos[i].z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 >= need * need) continue;
                    leftover++;
                    if (need > 1f) bigNeed++;   // 大体积实体对决（字体/大文件）占比
                    float d = Mathf.Sqrt(d2);
                    if (need - d > worstOverlap)
                    {
                        worstOverlap = need - d; worstD = d; worstNeed = need;
                        wi = i; wj = j;
                    }
                }
            }
            if (wi >= 0)
            {
                Debug.Log($"[Collision] 最深侵入: 节点 {wi}<->{wj} 距离 {worstD:F2} / 需要 {worstNeed:F2} " +
                          $"(半径 {radius[wi]:F2}/{radius[wj]:F2})；大体积违规 {bigNeed}/{leftover}");
            }
            return leftover;
        }

        // 违规计数（供日志与校验；O(n²) 单遍）
        public static int CountViolations(Vector3[] pos, float[] radius)
        {
            int n = pos.Length;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float need = radius[i] + radius[j];
                    float dx = pos[j].x - pos[i].x;
                    float dy = pos[j].y - pos[i].y;
                    float dz = pos[j].z - pos[i].z;
                    if (dx * dx + dy * dy + dz * dz < need * need) count++;
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
