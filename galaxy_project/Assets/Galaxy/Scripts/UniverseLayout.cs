// ============================================================================
// 宇宙布局：把"第一层文件夹"变成互不干涉的球体宇宙 —— 多元宇宙的几何事实来源。
//
// 职责（顺序即执行顺序）：
//   1) 分组：按 dir 的首个路径分量把节点分成若干"宇宙"；根目录散文件共用
//      一个特殊宇宙（ScatteredKey）；
//   2) 定尺寸：宇宙球半径 = 系数 × 文件数³√ —— 体积正比于文件数，各宇宙内部
//      密度近似一致（"球体大小与文件数目有关"）；小宇宙有最小半径下限；
//   3) 打包：把 N 个球在空间中排成互不重叠的紧凑布局（心距 ≥ 半径和 + 间隙）
//      —— 先按半径降序螺旋初始放置，再做"缓缩 + 分离松弛"交替迭代收敛
//      （N = 第一层文件夹数，很小，直接对儿计算即可）；
//   4) 层级引力核（递归星团场）：每个宇宙内部按目录建树，每个目录节点 =
//      一个固定引力核，位置递归取自父核的邻域（方向/距离由哈希确定，天然
//      不对称）；每个文件感受"从宇宙球心到其叶目录"整条祖先链的引力，
//      越深（越接近叶子）引力越强 —— 球心最弱、叶子最强（用户设计："层级
//      越高引力核的引力越小"）。大尺度松散、小尺度紧致：球内呈大小不一、
//      位置不对称的星团与空隙，而不是均匀填满；
//      核是"固定点"而非动态质心 —— 越吸越紧时斥力随之增长，天然形成有限
//      大小的团（不会像早期"动态质心+∝N 刚度"那样把大目录压成实心球）。
//   5) 种子：每个节点围绕其叶核按"体积均匀"采样初始位置（哈希确定）。
//
// 确定性（与 scanner 端同一哲学：同输入必得同输出）：分组按首次出现顺序、
// 打包排序按半径降序（同半径按索引稳定）、树遍历按深度分批 + 索引升序、
// 全程无随机数。
//
// C++ 类比：这就是"先分区、后逐区初始化"的批处理初始化器；引力核链等价于
// 每个节点持有一份"祖先指针表"，迭代时按表施加梯形拉力。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Galaxy
{
    public static class UniverseLayout
    {
        // 无文件夹归属的根目录散文件共用的宇宙键
        public const string ScatteredKey = "<scattered>";

        // 宇宙布局的计算结果（引用传递零拷贝给 Builder / 力导向引擎）
        public sealed class Result
        {
            public string[] keys;          // 每个宇宙的名字（文件夹名 / ScatteredKey）
            public int[] counts;           // 每个宇宙的文件数
            public int[] universeOf;       // 每个节点所属宇宙序号
            public Vector3[] centers;      // 每个宇宙的球心
            public float[] meshRadii;      // 宇宙球网格半径（∝ 文件数³√）
            public float[] wallRadii;      // 文件活动半径（= 网格半径 × wallFrac）
            public float[] innerRadii;     // 内核排斥包络半径（> 0 的宇宙中心留空；
                                           // 散文件宇宙 = 黑洞视觉包络：核心 + 吸积环外缘）
            public Vector3[] seed;         // 每个节点的初始位置（围绕叶核采样）
            public Vector3[][] anchorOf;   // 每个节点的引力核链（[0] = 宇宙球心，依次加深）
            public float[][] anchorStiffness;  // 对应引力强度（越深越强）
        }

        // 黑洞视觉包络 = 核心半径 × 该系数（吸积环最外圈 ~1.9×核心，留一点余量）
        public const float BlackHoleEnvelopeFactor = 2.0f;

        public static Result Compute(GalaxyGraph graph, float radiusScale, float minRadius,
                                     float gap, float wallFrac, float cohesion, float levelGain,
                                     float scatteredRadiusScale, float blackHoleRadiusScale)
        {
            int n = graph.nodes.Length;
            var result = new Result();

            // ---- 1) 分组（首次出现顺序 = 确定顺序）----
            var indexOfKey = new Dictionary<string, int>();
            var members = new List<List<int>>();
            var universeOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                string key = TopLevelDir(graph.nodes[i].dir) ?? ScatteredKey;
                if (!indexOfKey.TryGetValue(key, out int u))
                {
                    u = members.Count;
                    indexOfKey[key] = u;
                    members.Add(new List<int>());
                }
                universeOf[i] = u;
                members[u].Add(i);
            }
            int uCount = members.Count;

            var keys = new string[uCount];
            var counts = new int[uCount];
            var meshRadii = new float[uCount];
            var innerRadii = new float[uCount];
            foreach (KeyValuePair<string, int> pair in indexOfKey) keys[pair.Value] = pair.Key;
            for (int u = 0; u < uCount; u++)
            {
                counts[u] = members[u].Count;
                bool scattered = keys[u] == ScatteredKey;
                // 体积 ∝ 文件数 => 半径 ∝ 文件数³√。
                // 散文件宇宙有专属半径函数（scatteredRadiusScale）—— 独立调参，
                // 因为它内部还要为黑洞（核心 + 吸积环）留出中心空腔。
                float scale = scattered ? scatteredRadiusScale : radiusScale;
                meshRadii[u] = Mathf.Max(minRadius, scale * Mathf.Pow(counts[u], 1f / 3f));

                // 黑洞包络（仅散文件宇宙）：核心半径 ∝ 文件数³√（与宇宙尺寸同一
                // 数学），布局用它做"内核排斥"，保证实体与黑洞不干涉
                if (scattered && blackHoleRadiusScale > 0f)
                {
                    float coreR = blackHoleRadiusScale * Mathf.Pow(counts[u], 1f / 3f);
                    innerRadii[u] = coreR * BlackHoleEnvelopeFactor;
                }
            }

            // ---- 2) 打包（互不重叠；已平移到原点附近）----
            Vector3[] centers = PackUniverses(meshRadii, gap);

            // ---- 3) 墙半径 ----
            var wallRadii = new float[uCount];
            for (int u = 0; u < uCount; u++) wallRadii[u] = meshRadii[u] * wallFrac;

            // ---- 4/5) 层级引力核 + 球内种子 ----
            var seed = new Vector3[n];
            var anchorOf = new Vector3[n][];
            var anchorStiffness = new float[n][];
            BuildHierarchy(graph, keys, members, centers, wallRadii, innerRadii, cohesion, levelGain,
                           seed, anchorOf, anchorStiffness);

            result.keys = keys;
            result.counts = counts;
            result.universeOf = universeOf;
            result.centers = centers;
            result.meshRadii = meshRadii;
            result.wallRadii = wallRadii;
            result.innerRadii = innerRadii;
            result.seed = seed;
            result.anchorOf = anchorOf;
            result.anchorStiffness = anchorStiffness;
            return result;
        }

        // ------------------------------------------------------------------
        // 层级引力核（递归星团场）：见文件头第 4 条。
        // 目录树节点索引约定：0 = 顶层节点（相对路径 ""，锚定球心）。
        // ------------------------------------------------------------------
        private static void BuildHierarchy(GalaxyGraph graph, string[] keys, List<List<int>> members,
                                           Vector3[] centers, float[] wallRadii, float[] innerRadii,
                                           float cohesion, float levelGain,
                                           Vector3[] seed, Vector3[][] anchorOf, float[][] anchorStiffness)
        {
            float gain = Mathf.Max(1f, levelGain);

            for (int u = 0; u < members.Count; u++)
            {
                List<int> list = members[u];
                Vector3 center = centers[u];
                float wallR = wallRadii[u];
                string top = keys[u];
                int total = list.Count;

                // 文件 -> 相对目录（相对宇宙顶层文件夹；散文件宇宙中全部为 ""）
                var relOf = new string[list.Count];
                for (int t = 0; t < list.Count; t++)
                {
                    string dir = graph.nodes[list[t]].dir ?? "";
                    dir = dir.Replace('\\', '/').Trim('/');
                    string rel = "";
                    if (top != ScatteredKey && dir.Length > 0)
                    {
                        if (dir == top) rel = "";
                        else if (dir.StartsWith(top + "/")) rel = dir.Substring(top.Length + 1);
                    }
                    relOf[t] = rel;
                }

                // 建目录树（父链按需创建；dir 出现顺序不保证父先于子）
                var nodeIndex = new Dictionary<string, int>();
                var nodeRel = new List<string>();
                var nodeParent = new List<int>();
                var nodeDepth = new List<int>();
                var nodeSubtree = new List<int>();    // 先记直接文件数，之后累计子树数
                var nodeAnchor = new List<Vector3>();
                var nodeTerritory = new List<float>();

                nodeIndex[""] = 0;
                nodeRel.Add(""); nodeParent.Add(-1); nodeDepth.Add(0);
                nodeSubtree.Add(0); nodeAnchor.Add(center); nodeTerritory.Add(0f);

                int EnsureNode(string rel)
                {
                    if (nodeIndex.TryGetValue(rel, out int found)) return found;
                    int slash = rel.LastIndexOf('/');
                    int parent = slash < 0 ? 0 : EnsureNode(rel.Substring(0, slash));
                    int idx = nodeParent.Count;
                    nodeIndex[rel] = idx;
                    nodeRel.Add(rel);
                    nodeParent.Add(parent);
                    nodeDepth.Add(nodeDepth[parent] + 1);
                    nodeSubtree.Add(0);
                    nodeAnchor.Add(Vector3.zero);
                    nodeTerritory.Add(0f);
                    return idx;
                }

                for (int t = 0; t < relOf.Length; t++)
                {
                    int node = relOf[t].Length == 0 ? 0 : EnsureNode(relOf[t]);
                    nodeSubtree[node]++;
                }

                // 子树文件数：按深度由深到浅向父节点累计
                int maxDepth = 0;
                for (int v = 0; v < nodeDepth.Count; v++) maxDepth = Mathf.Max(maxDepth, nodeDepth[v]);
                for (int d = maxDepth; d >= 1; d--)
                {
                    for (int v = 0; v < nodeDepth.Count; v++)
                    {
                        if (nodeDepth[v] == d) nodeSubtree[nodeParent[v]] += nodeSubtree[v];
                    }
                }

                // 领地半径：体积 ∝ 子树文件数（与宇宙半径同一公式，保证嵌套收缩）
                float Territory(int v) =>
                    wallR * Mathf.Pow(Mathf.Max(nodeSubtree[v], 1) / (float)total, 1f / 3f);
                nodeTerritory[0] = Territory(0);

                // 核位置：按深度由浅到深 —— 父锚点 + 哈希方向 × 父领地半径的比例偏移。
                // 顶层（p == 0）的"父领地"取整个球体（0.88 倍留壁距）：一级目录核
                // 能铺满全球（实测教训：用缩小的领地做上限会把所有核挤到球心，
                // 内容凝成一团、外圈空掉）；深层的偏移范围放宽到 0.35~1.0 倍父领地
                // —— 星团凝缩后自带空隙，核铺开也不会挤到一起。末尾做全局钳制。
                for (int d = 1; d <= maxDepth; d++)
                {
                    for (int v = 0; v < nodeDepth.Count; v++)
                    {
                        if (nodeDepth[v] != d) continue;
                        int p = nodeParent[v];
                        // 父领地半径按需现算（父节点一定已在浅层处理过）
                        float parentT = p == 0 ? wallR * 0.88f : nodeTerritory[p];
                        uint hash = GalaxyLayout.Fnv1a("#uni#" + top + "§" + nodeRel[v]);
                        Vector3 dirVec = GalaxyLayout.RandomDirection(hash, 40);
                        float frac = 0.35f + 0.65f * GalaxyLayout.Hash01(hash, 41);
                        Vector3 anchor = nodeAnchor[p] + dirVec * (parentT * frac);
                        // 安全钳制：核不越出宇宙墙的 82%（留给外围内容）
                        Vector3 off = anchor - center;
                        float lim = wallR * 0.82f;
                        if (off.magnitude > lim) anchor = center + off.normalized * lim;
                        nodeAnchor[v] = anchor;
                        nodeTerritory[v] = Territory(v);
                    }
                }

                // 每文件的种子 + 引力核链
                for (int t = 0; t < list.Count; t++)
                {
                    int i = list[t];
                    int leaf = relOf[t].Length == 0 ? 0 : nodeIndex[relOf[t]];

                    uint h = GalaxyLayout.Fnv1a(graph.nodes[i].path);
                    // 种子半径：环形区域体积均匀（r³ 均匀插值）。有内核排斥（黑洞）
                    // 时从包络外起步 —— 初始位置也不落进黑洞
                    float rMin = Mathf.Min(innerRadii[u] + 0.3f, nodeTerritory[leaf] * 0.9f);
                    float rMax = Mathf.Max(rMin, nodeTerritory[leaf] * 0.9f);
                    float r3Min = rMin * rMin * rMin;
                    float r = Mathf.Pow(r3Min + GalaxyLayout.Hash01(h, 31) * (rMax * rMax * rMax - r3Min), 1f / 3f);
                    seed[i] = nodeAnchor[leaf] + GalaxyLayout.RandomDirection(h, 30) * r;

                    // 祖先链（自浅到深），核链 = 球心 + 各前缀目录
                    var chain = new List<int>();
                    for (int v = leaf; v > 0; v = nodeParent[v]) chain.Add(v);
                    chain.Reverse();

                    var anchors = new Vector3[chain.Count + 1];
                    var stiff = new float[chain.Count + 1];
                    anchors[0] = center;                             // 球心层
                    stiff[0] = cohesion / gain;                      // 链条最弱档（"层级越高引力越小"）
                    for (int c = 0; c < chain.Count; c++)
                    {
                        anchors[c + 1] = nodeAnchor[chain[c]];
                        stiff[c + 1] = cohesion * Mathf.Pow(gain, c);   // 一级=cohesion，越深越强
                    }
                    anchorOf[i] = anchors;
                    anchorStiffness[i] = stiff;
                }
            }
        }

        // 球体打包：按半径降序螺旋初始放置，再做"缓缩 + 分离松弛"交替迭代，
        // 收敛到紧凑且互不重叠（心距 ≥ 半径和 + gap）的排布；最后把包围范围
        // 的中点平移到原点（相机看向原点）。全流程确定性。
        private static Vector3[] PackUniverses(float[] radii, float gap)
        {
            int uCount = radii.Length;
            var order = new int[uCount];
            for (int i = 0; i < uCount; i++) order[i] = i;
            System.Array.Sort(order, (a, b) =>
            {
                int cmp = radii[b].CompareTo(radii[a]);   // 半径降序
                return cmp != 0 ? cmp : a.CompareTo(b);   // 同半径按索引升序（稳定）
            });

            var centers = new Vector3[uCount];
            for (int k = 0; k < uCount; k++)
            {
                int u = order[k];
                if (k == 0)
                {
                    centers[u] = Vector3.zero;   // 最大的球放中心
                    continue;
                }
                // 黄金角螺旋方向 × "与首球相切"的距离起步
                float z = 2f * (k * 0.618034f % 1f) - 1f;
                float phi = k * 2.399963f;
                float s = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
                Vector3 dir = new Vector3(s * Mathf.Cos(phi), z, s * Mathf.Sin(phi));
                centers[u] = dir * (radii[order[0]] + radii[u] + gap);
            }

            // 缓缩 + 分离交替：缓缩把布局压紧，分离消除重叠 —— 交替收敛到
            // 接近"最小接触排布"的紧凑状态
            for (int iter = 0; iter < 400; iter++)
            {
                Vector3 mid = Vector3.zero;
                for (int u = 0; u < uCount; u++) mid += centers[u];
                mid /= uCount;
                for (int u = 0; u < uCount; u++) centers[u] = mid + (centers[u] - mid) * 0.995f;

                for (int a = 0; a < uCount; a++)
                for (int b = a + 1; b < uCount; b++)
                {
                    float need = radii[a] + radii[b] + gap;
                    Vector3 d = centers[b] - centers[a];
                    float dist = d.magnitude;
                    if (dist >= need) continue;
                    if (dist < 1e-5f)
                    {
                        // 完全重合（极端情形）：按索引生成确定方向（黄金角错开）
                        float ang = a * 2.399963f;
                        d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0.5f).normalized;
                        dist = 0f;
                    }
                    float push = (need - dist) * 0.5f * 0.9f;
                    Vector3 dir = d / Mathf.Max(dist, 1e-6f);
                    centers[a] -= dir * push;
                    centers[b] += dir * push;
                }
            }

            // 包围范围的中点归零（相机看向原点，保证取景对称）
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int u = 0; u < uCount; u++)
            {
                mn = Vector3.Min(mn, centers[u] - Vector3.one * radii[u]);
                mx = Vector3.Max(mx, centers[u] + Vector3.one * radii[u]);
            }
            Vector3 shift = (mn + mx) * 0.5f;
            for (int u = 0; u < uCount; u++) centers[u] -= shift;
            return centers;
        }

        /// <summary>
        /// 软墙收紧（力导向收敛后调用）：实体碰撞松弛（PBD）可能把个别节点推到
        /// 墙外（或黑洞包络内），这里把节点球心重新压回"墙半径 − 实体包络半径"
        /// 内、且推出"内核排斥 + 实体包络"外，保证实体包络整体落在宇宙球墙与
        /// 黑洞之间（调用时机须在 CapRadiiToLocalClearance 之前 —— 收紧引起的
        /// 微量再侵入由后者按局部间隙收缩半径兜底）。
        /// </summary>
        public static void ClampIntoWalls(Vector3[] positions, float[] entityRadius, Result universes)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                int u = universes.universeOf[i];
                Vector3 off = positions[i] - universes.centers[u];
                float d = off.magnitude;
                float outerLimit = Mathf.Max(0.05f, universes.wallRadii[u] - entityRadius[i]);
                float innerLimit = universes.innerRadii[u] > 0f
                    ? universes.innerRadii[u] + entityRadius[i] + 0.15f
                    : 0f;
                if (d > outerLimit)
                {
                    positions[i] = universes.centers[u] + off * (outerLimit / Mathf.Max(d, 1e-6f));
                }
                else if (d < innerLimit)
                {
                    Vector3 dirVec = d > 1e-5f ? off / d : new Vector3(1f, 0f, 0f);
                    positions[i] = universes.centers[u] + dirVec * innerLimit;
                }
            }
        }

        // 第一层目录段：dir 的首个路径分量；"."/".."/空 视为无归属（根目录散文件）
        public static string TopLevelDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            dir = dir.Replace('\\', '/');
            int slash = dir.IndexOf('/');
            string top = slash < 0 ? dir : dir.Substring(0, slash);
            if (top.Length == 0 || top == "." || top == "..") return null;
            return top;
        }
    }
}
