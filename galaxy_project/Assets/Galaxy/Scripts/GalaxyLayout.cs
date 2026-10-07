// ============================================================================
// 确定性种子布局：无随机、无物理迭代 —— 同一份 galaxy.json 永远得到同一张
// 初始星图。力导向迭代（ForceDirectedLayout）从这些位置出发收敛到平衡态。
//
// 策略（两层级，模仿星系结构）：
//   1) 每个目录 = 一个簇心，均匀分布在球面壳上（球面均匀采样见下）
//   2) 节点散布在簇心附近的小球体内
//
// 确定性 => 初值可复现 => 配合力导向固定的浮点累加顺序，最终布局也可复现。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace Galaxy
{
    public static class GalaxyLayout
    {
        /// <summary>计算所有节点的世界坐标。radius 为簇心所在球壳的半径。</summary>
        public static Vector3[] Compute(GalaxyGraph graph, float radius)
        {
            // 目录 -> 簇心。按 nodes 顺序首次出现建立，顺序天然确定。
            var clusterCenter = new Dictionary<string, Vector3>();
            foreach (GalaxyNode node in graph.nodes)
            {
                if (!clusterCenter.ContainsKey(node.dir))
                {
                    clusterCenter[node.dir] = RandomDirection(Fnv1a("#dir#" + node.dir), 7) * radius;
                }
            }

            var positions = new Vector3[graph.nodes.Length];
            for (int i = 0; i < graph.nodes.Length; i++)
            {
                GalaxyNode node = graph.nodes[i];
                Vector3 center = clusterCenter[node.dir];

                // 体积均匀采样：半径取 u^(1/3)。
                // 若直接取 u*R，点会向球心聚堆（球壳体积 ∝ r²）；
                // 乘上立方根后密度处处均匀 —— 和蒙特卡洛重要性采样是同一个数学事实。
                uint hash = Fnv1a(node.path);
                float r = Mathf.Pow(Hash01(hash, 0), 1f / 3f) * 6f;
                Vector3 dir = RandomDirection(hash, 1);
                positions[i] = center + dir * r;
            }
            return positions;
        }

        /// <summary>目录 -> [0,1) 色调。星体着色的唯一事实来源（颜色由渲染端决定，扫描器不管）。</summary>
        public static float DirHue(string dir) => Hash01(Fnv1a("#hue#" + dir), 21);

        // ---- 确定性伪随机的原语（给定输入，输出永远相同；internal 供 UniverseLayout 复用）----

        // FNV-1a：与 C++ 社区常用实现同款的字串哈希（32bit）
        internal static uint Fnv1a(string s)
        {
            uint h = 2166136261u;   // FNV offset basis
            foreach (char c in s)
            {
                h ^= c;             // 注意：按 UTF-16 码元逐位异或
                h *= 16777619u;     // FNV prime
            }
            return h;
        }

        // 同一 hash + 不同 salt -> [0,1) 上的独立均匀值（位混淆避免相邻 salt 相关）
        internal static float Hash01(uint hash, int salt)
        {
            uint x = hash ^ (uint)(salt * 0x9E3779B9);  // 黄金比例混合
            x ^= x >> 16; x *= 0x7feb352du;             // xorshift 系混淆
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / (float)(1 << 24);
        }

        // 球面均匀方向：z = 2u-1, φ = 2πv。
        // （Archimedes 投影保证面积均匀 —— 若对角度直接均匀采样，极区会过密。）
        internal static Vector3 RandomDirection(uint hash, int salt)
        {
            float z = Hash01(hash, salt) * 2f - 1f;
            float phi = Hash01(hash, salt + 1) * 2f * Mathf.PI;
            float s = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(s * Mathf.Cos(phi), z, s * Mathf.Sin(phi));
        }
    }
}
