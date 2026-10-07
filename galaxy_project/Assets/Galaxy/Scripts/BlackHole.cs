// ============================================================================
// 黑洞（散文件宇宙专属，动态装饰）：黑色核心 + 双层倾斜旋转吸积环。
//
// 构图：
//   - 核心：无光照纯黑球（Sprites/Default 纯黑）—— 引力井的视觉体；
//   - 吸积环：两圈"虚线"细环（每圈 40 段弧 + 空隙），不同倾角、反向自旋。
//     为什么是虚线：完美的整圆绕自身轴自旋在视觉上完全静止（旋转不变性），
//     断开的弧段转动才看得见 —— 动态感来自"段的位置在变"。
//   - 呼吸：整体 ±3% 缓慢缩放脉动。
//
// 尺寸：核心半径由 Builder 给定（∝ ³√文件数，与宇宙尺寸同一数学）；
//   环半径 = 核心 × 1.5 / × 1.9，全部落在布局的"内核排斥包络"
//   （coreR × 2.0，见 UniverseLayout.BlackHoleEnvelopeFactor）之内 ——
//   实体被布局硬约束挡在包络之外，黑洞与实体不干涉。
//
// 定位：纯装饰对象 —— 不参与拾取（GalaxyPicker 只认实体列表）、不参与碰撞。
// 动画只依赖 Time.time，不影响布局确定性（几何在 Build 时一次成型）。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Galaxy
{
    public class BlackHole : MonoBehaviour
    {
        private const int RingSegments = 40;      // 每圈弧段数（虚线密度）
        private const float RingDuty = 0.62f;     // 弧段占空比（剩余为空隙）

        private Transform m_Ring1;
        private Transform m_Ring2;
        private float m_Phase;

        /// <summary>构建黑洞（coreRadius = 黑色核心半径；tint = 吸积环色相，取宇宙色调）。</summary>
        public void Build(float coreRadius, Color tint)
        {
            m_Phase = coreRadius * 7.13f;   // 每个黑洞相位错开（同尺寸也各异，纯视觉）

            // 核心：纯黑球。Sprites/Default 是无光照 shader，颜色即所见 —— 保证
            // 任何光照方向下都是纯黑（有光照的材质会出高光，破坏"黑洞"感）
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "Core";
            Object.Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(transform, false);
            core.transform.localScale = Vector3.one * (coreRadius * 2f);
            MeshRenderer cmr = core.GetComponent<MeshRenderer>();
            var coreMat = new Material(Shader.Find("Sprites/Default")) { name = "black-hole-core" };
            coreMat.color = Color.black;
            cmr.sharedMaterial = coreMat;
            cmr.shadowCastingMode = ShadowCastingMode.Off;
            cmr.receiveShadows = false;

            // 吸积环：XZ 平面内的虚线圆环；环 2 倾斜 62°，两层立体感
            m_Ring1 = CreateRing("AccretionRing1", coreRadius * 1.5f, 0f,
                                 new Color(tint.r, tint.g, tint.b, 0.55f));
            m_Ring2 = CreateRing("AccretionRing2", coreRadius * 1.9f, 62f,
                                 new Color(tint.r, tint.g, tint.b, 0.32f));
        }

        private Transform CreateRing(string name, float radius, float tiltX, Color color)
        {
            var verts = new List<Vector3>(RingSegments * 2);
            var colors = new List<Color>(RingSegments * 2);
            float arc = Mathf.PI * 2f / RingSegments;
            for (int i = 0; i < RingSegments; i++)
            {
                float a0 = i * arc;
                float a1 = a0 + arc * RingDuty;
                verts.Add(new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius));
                verts.Add(new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius));
                colors.Add(color);
                colors.Add(color);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            var indices = new int[verts.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localRotation = Quaternion.Euler(tiltX, 0f, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { name = name + "-mat" };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (m_Ring1 != null) m_Ring1.Rotate(0f, 26f * dt, 0f, Space.Self);    // 环 1：自旋
            if (m_Ring2 != null) m_Ring2.Rotate(0f, -17f * dt, 0f, Space.Self);   // 环 2：反向

            // 呼吸脉动：整体 ±3% 缓慢起伏（相位按尺寸错开）
            float s = 1f + 0.03f * Mathf.Sin(Time.time * 1.6f + m_Phase);
            transform.localScale = new Vector3(s, s, s);
        }
    }
}
