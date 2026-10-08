// ============================================================================
// 运行时设置面板（桌面应用脱离 Unity Inspector 调参的唯一入口）。
//
// 两类参数：
//   即时生效：宇宙球壳亮度（底/光环）、名碑亮度、连线亮度 —— 直接改材质/
//             顶点色实例（GalaxyBuilder.ApplyXxx / GalaxyPicker.SetLinkBrightness）；
//   布局参数：宇宙半径系数、宇宙间隙 —— 改值后点 APPLY (REBUILD) 触发整树
//             重建（O(n²) 仿真，秒级耗时，故不做逐格即刻重建）。
//
// UI 由 GalaxyHud 搭建（面板 + 六行 + 步进按钮），点击路由由 GalaxyPicker
// 转发到 Handle(action)；本组件只持有状态与套用逻辑。
// ============================================================================
using UnityEngine;

namespace Galaxy
{
    public class GalaxySettings : MonoBehaviour
    {
        public const int RowCount = 7;

        // 行定义：id / 步长 / 下限 / 上限（与 GalaxyHud 的行序一致）
        private static readonly string[] Ids = { "rim", "base", "label", "link", "radius", "gap", "dust" };
        private static readonly float[] Step = { 0.01f, 0.005f, 0.05f, 0.02f, 0.1f, 0.5f, 200f };
        private static readonly float[] Min = { 0.02f, 0.002f, 0.2f, 0.2f, 1.2f, 0f, 0f };
        private static readonly float[] Max = { 0.6f, 0.06f, 2f, 2.5f, 5f, 12f, 8000f };

        private GalaxyHud m_Hud;
        private GalaxyPicker m_Picker;
        private GalaxyBuilder m_Builder;
        private bool m_Visible;

        private float m_RimAlpha = 0.055f;
        private float m_BaseAlpha = 0.005f;
        private float m_LabelBrightness = 1f;
        private float m_LinkBrightness = 1f;
        private float m_RadiusScale = 1.9f;
        private float m_Gap = 3f;
        private int m_DustCount;

        public bool IsVisible => m_Visible;

        public void Toggle()
        {
            EnsureRefs();
            if (!m_Visible) LoadFromBuilder();   // 每次打开时同步（重建后字段可能已变）
            m_Visible = !m_Visible;
            if (m_Hud != null) m_Hud.SetSettingsVisible(m_Visible);
            RefreshUi();
        }

        /// <summary>设置面板按钮点击（由 GalaxyPicker 路由）。</summary>
        public void Handle(string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            EnsureRefs();

            if (action == "apply") { ApplyRebuild(); return; }
            if (action == "close") { Toggle(); return; }

            bool plus = action.EndsWith("+");
            string id = action.Substring(0, action.Length - 1);
            int row = System.Array.IndexOf(Ids, id);
            if (row < 0) return;
            float dir = plus ? 1f : -1f;

            switch (id)
            {
                case "rim":    m_RimAlpha = Mathf.Clamp(m_RimAlpha + dir * Step[row], Min[row], Max[row]); break;
                case "base":   m_BaseAlpha = Mathf.Clamp(m_BaseAlpha + dir * Step[row], Min[row], Max[row]); break;
                case "label":  m_LabelBrightness = Mathf.Clamp(m_LabelBrightness + dir * Step[row], Min[row], Max[row]); break;
                case "link":   m_LinkBrightness = Mathf.Clamp(m_LinkBrightness + dir * Step[row], Min[row], Max[row]); break;
                case "radius": m_RadiusScale = Mathf.Clamp(m_RadiusScale + dir * Step[row], Min[row], Max[row]); break;
                case "gap":    m_Gap = Mathf.Clamp(m_Gap + dir * Step[row], Min[row], Max[row]); break;
                case "dust":   m_DustCount = Mathf.RoundToInt(Mathf.Clamp(m_DustCount + dir * Step[row], Min[row], Max[row])); break;
            }
            ApplyLive();
        }

        private void EnsureRefs()
        {
            if (m_Hud == null) m_Hud = GetComponent<GalaxyHud>();
            if (m_Picker == null) m_Picker = GetComponent<GalaxyPicker>();
            if (m_Builder == null) m_Builder = FindAnyObjectByType<GalaxyBuilder>();
        }

        private void LoadFromBuilder()
        {
            if (m_Builder == null) return;
            m_RimAlpha = m_Builder.universeRimAlpha;
            m_BaseAlpha = m_Builder.universeBaseAlpha;
            m_RadiusScale = m_Builder.universeRadiusScale;
            m_Gap = m_Builder.universeGap;
            // 星尘数量：以运行中实例的当前值为准（没有实例则取默认公式初值）
            if (m_Builder.Particles != null) m_DustCount = m_Builder.Particles.CurrentCount;
            // 亮度类参数是本会话的叠加值，保持现状不重置
        }

        // 即时项套用（布局项不受影响，照常重复套用无副作用）
        private void ApplyLive()
        {
            EnsureRefs();
            if (m_Builder != null) m_Builder.ApplyUniverseAppearance(m_BaseAlpha, m_RimAlpha);
            if (m_Builder != null) m_Builder.ApplyLabelBrightness(m_LabelBrightness);
            if (m_Builder != null && m_Builder.Particles != null) m_Builder.Particles.SetCount(m_DustCount);
            if (m_Picker != null) m_Picker.SetLinkBrightness(m_LinkBrightness);
            RefreshUi();
        }

        // 布局参数套用：写回 Builder 字段 → 整树重建 → 新材质实例上重套即时项
        private void ApplyRebuild()
        {
            EnsureRefs();
            if (m_Builder == null) return;
            m_Builder.universeRadiusScale = m_RadiusScale;
            m_Builder.universeGap = m_Gap;
            m_Builder.Rebuild();
            ApplyLive();
        }

        private void RefreshUi()
        {
            if (m_Hud == null) return;
            var values = new string[RowCount];
            values[0] = m_RimAlpha.ToString("F3");
            values[1] = m_BaseAlpha.ToString("F3");
            values[2] = m_LabelBrightness.ToString("F2");
            values[3] = m_LinkBrightness.ToString("F2");
            values[4] = m_RadiusScale.ToString("F2");
            values[5] = m_Gap.ToString("F1");
            values[6] = m_DustCount.ToString();
            m_Hud.SetSettingsValues(values);
        }
    }
}
