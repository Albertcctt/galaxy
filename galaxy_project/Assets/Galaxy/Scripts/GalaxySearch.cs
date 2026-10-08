// ============================================================================
// 文件搜索定位：点击搜索框激活 → 键盘输入 → 实时匹配 → ↑↓ 循环候选 →
// Enter 跳到该天体（选中 + 相机视点引导），Esc / 点击别处取消。
//
// 键盘输入不走 EventSystem/InputField（本项目与 HUD 同一套"手动路由"架构，
// 不引入事件系统）：激活期间直接订阅 Input System 的 onTextInput 事件拿
// 字符流，退格/回车/上下/退出键在 Update 里按帧查询。Ctrl+V 读系统剪贴板。
//
// 匹配规则（确定性）：文件名不含路径；前缀命中优先，其次子串命中；各趟按
// 节点数组原顺序取前 N 个——同一查询永远给出同样的候选列表。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Galaxy
{
    public class GalaxySearch : MonoBehaviour
    {
        private const int MaxResults = 8;

        private GalaxyHud m_Hud;
        private GalaxyPicker m_Picker;
        private bool m_Active;
        private string m_Query = "";
        private readonly List<int> m_Matches = new List<int>();
        private int m_Cursor;

        public bool IsActive => m_Active;

        public void Activate()
        {
            EnsureRefs();
            m_Active = true;
            m_Query = "";
            m_Cursor = 0;
            Keyboard kb = Keyboard.current;
            if (kb != null) kb.onTextInput += OnTextInput;
            Refresh();
            UpdateUi();
        }

        public void Deactivate()
        {
            if (!m_Active) return;
            m_Active = false;
            Keyboard kb = Keyboard.current;
            if (kb != null) kb.onTextInput -= OnTextInput;
            if (m_Hud != null)
            {
                m_Hud.SetSearchText("SEARCH FILE", false);
                m_Hud.SetSearchResults("");
            }
        }

        private void EnsureRefs()
        {
            if (m_Hud == null) m_Hud = GetComponent<GalaxyHud>();
            if (m_Picker == null) m_Picker = GetComponent<GalaxyPicker>();
        }

        private void OnTextInput(char c)
        {
            if (!m_Active) return;
            if (c == '\b' || c == '\n' || c == '\r') return;   // 控制字符单独处理
            m_Query += c;
            Refresh();
            UpdateUi();
        }

        private void Update()
        {
            if (!m_Active) return;
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (kb.backspaceKey.wasPressedThisFrame && m_Query.Length > 0)
            {
                m_Query = m_Query.Substring(0, m_Query.Length - 1);
                Refresh();
                UpdateUi();
            }
            if (kb.ctrlKey.isPressed && kb.vKey.wasPressedThisFrame)
            {
                // 剪贴板粘贴：压平换行（路径/文件名场景下换行是噪声）
                string clip = GUIUtility.systemCopyBuffer ?? "";
                clip = clip.Replace("\r", " ").Replace("\n", " ").Trim();
                if (clip.Length > 0)
                {
                    m_Query += clip;
                    Refresh();
                    UpdateUi();
                }
            }
            if (kb.upArrowKey.wasPressedThisFrame && m_Matches.Count > 0)
            {
                m_Cursor = (m_Cursor + m_Matches.Count - 1) % m_Matches.Count;
                UpdateUi();
            }
            if (kb.downArrowKey.wasPressedThisFrame && m_Matches.Count > 0)
            {
                m_Cursor = (m_Cursor + 1) % m_Matches.Count;
                UpdateUi();
            }
            if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                && m_Matches.Count > 0)
            {
                int target = m_Matches[m_Cursor];
                Deactivate();
                if (m_Picker != null) m_Picker.SelectNode(target);   // 选中 + 视点引导一体
            }
        }

        private void Refresh()
        {
            m_Matches.Clear();
            m_Cursor = 0;
            if (m_Picker == null || m_Picker.Data == null || m_Query.Length == 0) return;
            m_Matches.AddRange(Match(m_Picker.Data.graph, m_Query, MaxResults));
        }

        private void UpdateUi()
        {
            if (m_Hud == null) return;
            m_Hud.SetSearchText(m_Query.Length > 0 ? m_Query + "_" : "", true);
            if (m_Matches.Count == 0)
            {
                m_Hud.SetSearchResults(m_Query.Length == 0 ? "" :
                    "<color=#8fb7d9>no match</color>");
                return;
            }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < m_Matches.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                GalaxyNode node = m_Picker.Data.graph.nodes[m_Matches[i]];
                if (i == m_Cursor)
                    sb.Append($"<color=#ffd166>▸ {node.name}</color>");
                else
                    sb.Append($"<color=#8fb7d9>  {node.name}</color>");
                sb.Append($"<color=#5f7a99>  {node.dir}</color>");
            }
            m_Hud.SetSearchResults(sb.ToString());
        }

        /// <summary>
        /// 文件名匹配（静态纯函数：搜索与自动化抽查共用）。前缀命中优先，
        /// 其次子串命中；组内保持节点原顺序（确定性）。
        /// </summary>
        public static List<int> Match(GalaxyGraph graph, string query, int limit = MaxResults)
        {
            var result = new List<int>();
            if (graph == null || string.IsNullOrEmpty(query)) return result;
            string q = query.ToLowerInvariant();

            for (int pass = 0; pass < 2 && result.Count < limit; pass++)
            {
                for (int i = 0; i < graph.nodes.Length && result.Count < limit; i++)
                {
                    string name = graph.nodes[i].name.ToLowerInvariant();
                    bool hit = pass == 0 ? name.StartsWith(q)
                                         : !name.StartsWith(q) && name.Contains(q);
                    if (hit) result.Add(i);
                }
            }
            return result;
        }
    }
}
