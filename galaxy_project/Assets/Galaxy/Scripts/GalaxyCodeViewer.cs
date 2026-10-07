// ============================================================================
// 代码查看器：双击实体后在屏幕右侧打开的"迷你 VS Code"面板。
//
// 视觉规格（对齐 VS Code Dark+ 配色）：
//   面板底 #1E1E1E / 标题栏 #252526 / 行号 #858585 / 正文 #D4D4D4
//   关键字 #569CD6 / 预处理 #C586C0 / 字符串 #CE9178 / 注释 #6A9955 / 数字 #B5CEA8
//   字体 = 系统等宽字体（Consolas 优先，Font.CreateDynamicFontFromOSFont）。
//
// 渲染策略（为什么不是放一个全文件 Text）：
//   uGUI Text 的网格顶点 ≈ 字符数 × 4，大文件（lv_conf_internal.h 4831 行）会有
//   数百万顶点，直接压垮 TextGenerator。这里做"虚拟窗口"：只把 [first, first+N)
//   行做进 Text —— N ≈ 50 行，网格 ~2 万顶点，滚轮翻页时重建窗口（微秒级字符串
//   拼接）。C++ 类比：这是文本编辑器里标准的"可见区虚拟化"，而非全量布局。
//
// 高亮：手写行级扫描器（注释/字符串/预处理/关键字/数字五类 token），块注释
//   状态跨行 —— 打开文件时预计算每行起始的块注释状态（stateAtLine），窗口从
//   中途渲染也能正确高亮。
//
// 富文本安全：C 代码里满是 '<'（#include <x.h>、a < b），会被 uGUI 富文本误判
//   成标签；含尖括号的片段一律用 <noparse> 包裹（内部按字面渲染，颜色由外层
//   <color> 标签维持）。
//
// 输入：本面板不挂 EventSystem（与 HUD 同一架构）—— 关闭按钮命中测试与滚轮
//   分发由 GalaxyPicker 的 Update 手动路由（ContainsScreenPoint / IsOverClose）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Galaxy
{
    public class GalaxyCodeViewer : MonoBehaviour
    {
        // ---- VS Code Dark+ 调色板（Color32 十六进制直读，便于对照主题）----
        private static readonly Color32 PanelBg    = new Color32(0x1E, 0x1E, 0x1E, 0xFF);
        private static readonly Color32 TitleBg    = new Color32(0x25, 0x25, 0x26, 0xFF);
        private static readonly Color32 TitleText  = new Color32(0xCC, 0xCC, 0xCC, 0xFF);
        private static readonly Color32 PathText   = new Color32(0x85, 0x85, 0x85, 0xFF);
        private static readonly Color32 GutterText = new Color32(0x85, 0x85, 0x85, 0xFF);
        private static readonly Color32 CodeText   = new Color32(0xD4, 0xD4, 0xD4, 0xFF);
        private static readonly Color32 KeywordCol = new Color32(0x56, 0x9C, 0xD6, 0xFF);
        private static readonly Color32 PreprocCol = new Color32(0xC5, 0x86, 0xC0, 0xFF);
        private static readonly Color32 StringCol  = new Color32(0xCE, 0x91, 0x78, 0xFF);
        private static readonly Color32 CommentCol = new Color32(0x6A, 0x99, 0x55, 0xFF);
        private static readonly Color32 NumberCol  = new Color32(0xB5, 0xCE, 0xA8, 0xFF);

        private const int CodeFontSize = 14;
        private const float PanelWidth = 840f;
        private const float TitleHeight = 36f;
        private const float GutterWidth = 56f;

        // C/C++ 关键字表（高亮用；不求完备，覆盖高频即可）
        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "auto", "break", "case", "char", "const", "continue", "default", "do", "double",
            "else", "enum", "extern", "float", "for", "goto", "if", "inline", "int", "long",
            "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct",
            "switch", "typedef", "union", "unsigned", "void", "volatile", "while",
            "class", "namespace", "public", "private", "protected", "virtual", "template",
            "typename", "using", "new", "delete", "this", "nullptr", "true", "false",
            "constexpr", "static_assert", "bool", "wchar_t",
        };

        private static Font s_Mono;

        private bool m_Created;
        private GameObject m_CanvasRoot;
        private RectTransform m_PanelRect;
        private RectTransform m_BodyRect;
        private RectTransform m_CloseRect;
        private RectTransform m_TrackRect;   // 滚动条命中区（不可见，宽 14；按下/拖拽 = 擦洗直达）
        private Text m_Title;
        private Text m_Subtitle;
        private Text m_Gutter;
        private Text m_Code;
        private Image m_ScrollThumb;
        private bool m_ScrubActive;          // 正在拖拽滚动条（期间相机输入整体让位）

        private string[] m_Lines = Array.Empty<string>();
        private bool[] m_State = Array.Empty<bool>();   // 每行起始处是否处于块注释中
        private int m_FirstLine;
        private int m_WindowLines = 40;
        private float m_LineStride = 19f;

        private readonly StringBuilder m_GutterSb = new StringBuilder(1024);
        private readonly StringBuilder m_CodeSb = new StringBuilder(8192);

        /// <summary>面板是否打开（用于拾取器的点击拦截判定）。</summary>
        public bool IsOpen => m_Created && m_CanvasRoot.activeSelf;

        // ------------------------------------------------------------------
        // 对外接口（由 GalaxyPicker 路由）
        // ------------------------------------------------------------------

        /// <summary>打开文件并渲染窗口（root = 扫描根目录，node.path 为相对路径）。</summary>
        public void Show(string rootPath, GalaxyNode node)
        {
            EnsureCreated();
            m_CanvasRoot.SetActive(true);
            m_Title.text = node.name;
            m_Subtitle.text = node.path;

            // 可视行数按实际布局高度校正（画布缩放后的真实高度，创建时的估算
            // 只作初值 —— 不同分辨率/宽高比下参考高度不等于 1080）
            float bodyH = m_BodyRect.rect.height;
            if (bodyH > 50f)
            {
                m_WindowLines = Mathf.Max(5, Mathf.FloorToInt((bodyH - 14f) / m_LineStride));
            }

            if (node.kind == "binary")
            {
                // 二进制文件：拒绝读取（读出的是乱码）—— 面板显示说明
                //（星系里这些实体是红色球，这里给文字层面的确认）
                m_Lines = new[]
                {
                    "Binary file — preview not available.",
                    "",
                    $"Size: {FormatBytes(node.bytes)}",
                };
                m_State = new[] { false, false, false };
            }
            else
            {
                try
                {
                    string full = Path.Combine(rootPath ?? "", node.path ?? "");
                    m_Lines = File.ReadAllLines(full);   // 自动处理 \r\n 与 BOM
                    m_State = ComputeBlockState(m_Lines);
                }
                catch (Exception e)
                {
                    m_Lines = new[] { "Cannot open file: " + e.Message };
                    m_State = new[] { false };
                }
            }

            m_FirstLine = 0;
            RenderWindow();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }

        public void Close()
        {
            if (m_Created) m_CanvasRoot.SetActive(false);
        }

        /// <summary>屏幕点是否落在面板内（拾取器据此拦截点击穿透）。</summary>
        public bool ContainsScreenPoint(Vector2 screenPos)
        {
            return IsOpen && RectTransformUtility.RectangleContainsScreenPoint(m_PanelRect, screenPos, null);
        }

        /// <summary>屏幕点是否落在关闭按钮上。</summary>
        public bool IsOverClose(Vector2 screenPos)
        {
            return IsOpen && RectTransformUtility.RectangleContainsScreenPoint(m_CloseRect, screenPos, null);
        }

        /// <summary>滚轮滚动（仅当指针悬停面板上）。返回是否消费了本次滚动。</summary>
        public bool TryScroll(Vector2 screenPos, float wheelDelta)
        {
            if (!IsOpen || !RectTransformUtility.RectangleContainsScreenPoint(m_PanelRect, screenPos, null))
                return false;

            int step = wheelDelta > 0f
                ? Mathf.Max(1, Mathf.RoundToInt(wheelDelta / 120f * 3f))
                : -Mathf.Max(1, Mathf.RoundToInt(-wheelDelta / 120f * 3f));
            m_FirstLine += step;
            RenderWindow();
            return true;
        }

        // ---- 滚动条擦洗（Sidebar 拖拽直达）：按住轨道任意位置 → 视图实时跟随 ----
        // 交互说明：VS Code 习惯 —— 按下即抓取（不要求精确按在滑块上），拖动过程
        // 中指针可以离开轨道范围，松开结束。手势期间由 Picker 拦截，不触发拾取；
        // OrbitCamera 也通过 IsScrubbing 保持封锁（拖出面板边缘也不会转相机）。

        /// <summary>是否正在拖拽滚动条（相机封锁要用；指针移出面板也保持封锁）。</summary>
        public bool IsScrubbing => m_ScrubActive;

        /// <summary>屏幕点是否落在这条滚动条上。</summary>
        public bool IsOverScrollArea(Vector2 screenPos)
        {
            return IsOpen && RectTransformUtility.RectangleContainsScreenPoint(m_TrackRect, screenPos, null);
        }

        public void BeginScrub(Vector2 screenPos)
        {
            if (!IsOpen) return;
            m_ScrubActive = true;
            ScrubTo(screenPos);
        }

        public void UpdateScrub(Vector2 screenPos)
        {
            if (m_ScrubActive) ScrubTo(screenPos);
        }

        public void EndScrub()
        {
            m_ScrubActive = false;
        }

        // 指针位置 -> 滚动位置：轨道纵向比例直接映射到 [0, total - window]
        private void ScrubTo(Vector2 screenPos)
        {
            if (m_Lines.Length == 0) return;
            Rect r = m_TrackRect.rect;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    m_TrackRect, screenPos, null, out Vector2 local)) return;
            float fromTop = 1f - Mathf.Clamp01((local.y - r.yMin) / Mathf.Max(r.height, 1f));
            int maxFirst = Mathf.Max(0, m_Lines.Length - m_WindowLines);
            m_FirstLine = Mathf.RoundToInt(fromTop * maxFirst);
            RenderWindow();
        }

        // ------------------------------------------------------------------
        // 窗口渲染：行号 + 高亮代码（只重建可见区）
        // ------------------------------------------------------------------
        private void RenderWindow()
        {
            int total = m_Lines.Length;
            m_FirstLine = Mathf.Clamp(m_FirstLine, 0, Mathf.Max(0, total - m_WindowLines));
            int end = Mathf.Min(total, m_FirstLine + m_WindowLines);

            m_GutterSb.Clear();
            m_CodeSb.Clear();
            for (int li = m_FirstLine; li < end; li++)
            {
                if (li > m_FirstLine)
                {
                    m_GutterSb.Append('\n');
                    m_CodeSb.Append('\n');
                }
                m_GutterSb.Append(li + 1);   // 行号（UpperRight 对齐，右缘贴齐代码区）
                HighlightInto(m_CodeSb, m_Lines[li], li < m_State.Length && m_State[li]);
            }
            m_Gutter.text = m_GutterSb.ToString();
            m_Code.text = m_CodeSb.ToString();
            UpdateScrollbar(total);
        }

        private void UpdateScrollbar(int total)
        {
            float bodyH = m_BodyRect.rect.height;
            float ratio = total > 0 ? Mathf.Clamp01((float)m_WindowLines / total) : 1f;
            float thumbH = Mathf.Max(28f, bodyH * ratio);
            int maxFirst = Mathf.Max(1, total - m_WindowLines);
            float y = (bodyH - thumbH) * Mathf.Clamp01(m_FirstLine / (float)maxFirst);
            m_ScrollThumb.rectTransform.sizeDelta = new Vector2(6f, thumbH);
            m_ScrollThumb.rectTransform.anchoredPosition = new Vector2(-7f, -(4f + y));
        }

        // ------------------------------------------------------------------
        // 行级高亮扫描器：五类 token，块注释状态由调用方传入（跨行）
        // ------------------------------------------------------------------
        private static void HighlightInto(StringBuilder sb, string line, bool inBlockComment)
        {
            int n = line.Length;
            int lead = 0;
            while (lead < n && (line[lead] == ' ' || line[lead] == '\t')) lead++;
            bool preproc = !inBlockComment && lead < n && line[lead] == '#';
            Color32 defColor = preproc ? PreprocCol : CodeText;

            int i = 0, runStart = 0;

            while (i < n)
            {
                char c = line[i];

                if (inBlockComment)
                {
                    int e = line.IndexOf("*/", i, StringComparison.Ordinal);
                    int stop = e < 0 ? n : e + 2;
                    Flush(sb, line, runStart, i, defColor);
                    Append(sb, line.Substring(i, stop - i), CommentCol);
                    i = runStart = stop;
                    inBlockComment = e < 0;
                    continue;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '/')
                {
                    Flush(sb, line, runStart, i, defColor);
                    Append(sb, line.Substring(i), CommentCol);
                    i = runStart = n;
                    break;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    Flush(sb, line, runStart, i, defColor);
                    int e = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int stop = e < 0 ? n : e + 2;
                    Append(sb, line.Substring(i, stop - i), CommentCol);
                    i = runStart = stop;
                    inBlockComment = e < 0;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    Flush(sb, line, runStart, i, defColor);
                    int j = i + 1;
                    while (j < n && line[j] != c) { if (line[j] == '\\') j++; j++; }
                    if (j < n) j++;   // 吃掉闭合引号
                    Append(sb, line.Substring(i, j - i), StringCol);
                    i = runStart = j;
                    continue;
                }
                if (char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(line[i - 1]) || line[i - 1] == '_')))
                {
                    Flush(sb, line, runStart, i, defColor);
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_')) j++;
                    Append(sb, line.Substring(i, j - i), NumberCol);
                    i = runStart = j;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int j = i + 1;
                    while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                    if (Keywords.Contains(line.Substring(i, j - i)))
                    {
                        Flush(sb, line, runStart, i, defColor);
                        Append(sb, line.Substring(i, j - i), KeywordCol);
                        runStart = j;
                    }
                    i = j;
                    continue;
                }
                i++;
            }
            Flush(sb, line, runStart, n, defColor);
        }

        // 把 [from, to) 的默认色游段刷进缓冲
        private static void Flush(StringBuilder sb, string line, int from, int to, Color32 color)
        {
            if (to > from) Append(sb, line.Substring(from, to - from), color);
        }

        // 富文本安全输出：含尖括号的片段用 noparse 包裹（颜色由外层标签维持）
        private static void Append(StringBuilder sb, string text, Color32 color)
        {
            if (text.Length == 0) return;
            sb.Append(TagOf(color));
            if (text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0)
            {
                sb.Append("<noparse>").Append(text).Append("</noparse>");
            }
            else
            {
                sb.Append(text);
            }
            sb.Append("</color>");
        }

        // <color=#RRGGBB> 前缀缓存（每行几十个 token，避免逐段查表+格式化）
        private static readonly Dictionary<Color32, string> s_TagCache = new Dictionary<Color32, string>();
        private static string TagOf(Color32 c)
        {
            if (!s_TagCache.TryGetValue(c, out string tag))
            {
                tag = $"<color=#{ColorUtility.ToHtmlStringRGB(c)}>";
                s_TagCache[c] = tag;
            }
            return tag;
        }

        // 预计算每行起始处的块注释状态（窗口从中途渲染也要正确高亮）
        private static bool[] ComputeBlockState(string[] lines)
        {
            var state = new bool[lines.Length];
            bool inBlock = false;
            for (int li = 0; li < lines.Length; li++)
            {
                state[li] = inBlock;
                string s = lines[li];
                int i = 0;
                while (i < s.Length)
                {
                    if (inBlock)
                    {
                        int e = s.IndexOf("*/", i, StringComparison.Ordinal);
                        if (e < 0) break;
                        inBlock = false;
                        i = e + 2;
                        continue;
                    }
                    if (s[i] == '"' || s[i] == '\'')
                    {
                        char q = s[i];
                        int j = i + 1;
                        while (j < s.Length && s[j] != q) { if (s[j] == '\\') j++; j++; }
                        i = j + 1;
                        continue;
                    }
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/') break;
                    if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { inBlock = true; i += 2; continue; }
                    i++;
                }
            }
            return state;
        }

        // ------------------------------------------------------------------
        // 面板搭建（全代码，风格对齐 VS Code：标题栏 / 行号槽 / 代码区 / 细滚动条）
        // ------------------------------------------------------------------
        private void EnsureCreated()
        {
            if (m_Created) return;
            m_Created = true;

            var canvasGo = new GameObject("GalaxyCodeViewer", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;   // 高于 HUD（100）：打开时盖住 FILE INFO 面板
            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            m_CanvasRoot = canvasGo;

            Font mono = GetMonoFont();
            m_LineStride = Mathf.Max(10f, mono.lineHeight);
            float panelH = 1080f - 48f;                 // 上下各留 24
            m_WindowLines = Mathf.Max(5, Mathf.FloorToInt((panelH - TitleHeight - 14f) / m_LineStride));

            // 面板：右侧，全高（上下 24 边距）
            var panelGo = new GameObject("Panel", typeof(RectTransform));
            panelGo.transform.SetParent(canvasGo.transform, false);
            Image bg = panelGo.AddComponent<Image>();
            bg.color = PanelBg;
            bg.raycastTarget = false;
            // 面板矩形：纵向"拉伸锚"铺满（上下各留 24 边距），横向固定宽度靠右。
            // 注意：anchorMin == anchorMax（点锚）时 sizeDelta 是绝对尺寸，负值会
            // 把面板压没；-48 只在拉伸轴上才表示"边距"（实测踩坑：压成横线一条）
            m_PanelRect = panelGo.GetComponent<RectTransform>();
            m_PanelRect.anchorMin = new Vector2(1f, 0f);
            m_PanelRect.anchorMax = new Vector2(1f, 1f);
            m_PanelRect.pivot = new Vector2(1f, 0.5f);
            m_PanelRect.anchoredPosition = new Vector2(-24f, 0f);
            m_PanelRect.sizeDelta = new Vector2(PanelWidth, -48f);

            // 标题栏（VS Code 页签风）：文件名 + 暗色路径 + 关闭按钮
            var titleGo = new GameObject("TitleBar", typeof(RectTransform));
            titleGo.transform.SetParent(panelGo.transform, false);
            Image titleBg = titleGo.AddComponent<Image>();
            titleBg.color = TitleBg;
            titleBg.raycastTarget = false;
            RectTransform titleRect = titleGo.GetComponent<RectTransform>();
            Place(titleRect, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, Vector2.zero);
            titleRect.anchorMax = new Vector2(1f, 1f);   // 横向拉伸
            titleRect.sizeDelta = new Vector2(0f, TitleHeight);

            m_Title = CreateText(titleGo.transform, "FileName", CodeFontSize + 1, TextAnchor.UpperLeft, TitleText, mono);
            m_Title.text = "";
            Place(m_Title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(14f, -5f), new Vector2(700f, 18f));

            m_Subtitle = CreateText(titleGo.transform, "FilePath", CodeFontSize - 3, TextAnchor.UpperLeft, PathText, mono);
            m_Subtitle.text = "";
            Place(m_Subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(14f, -22f), new Vector2(700f, 14f));

            Text close = CreateText(titleGo.transform, "Close", 22, TextAnchor.MiddleCenter, TitleText, mono);
            close.text = "×";
            m_CloseRect = close.rectTransform;
            Place(m_CloseRect, new Vector2(1f, 1f), new Vector2(1f, 1f),
                  new Vector2(-10f, -3f), new Vector2(30f, 30f));

            // 正文区（RectMask2D：窗口文本永远不越出面板）+ 行号槽 + 代码 + 滚动条
            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(panelGo.transform, false);
            bodyGo.AddComponent<RectMask2D>();
            m_BodyRect = bodyGo.GetComponent<RectTransform>();
            m_BodyRect.anchorMin = Vector2.zero;
            m_BodyRect.anchorMax = Vector2.one;
            m_BodyRect.pivot = new Vector2(0.5f, 0.5f);
            m_BodyRect.sizeDelta = new Vector2(0f, -TitleHeight);
            m_BodyRect.anchoredPosition = new Vector2(0f, -TitleHeight * 0.5f);

            m_Gutter = CreateText(bodyGo.transform, "Gutter", CodeFontSize, TextAnchor.UpperRight, GutterText, mono);
            Place(m_Gutter.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(0f, -8f), new Vector2(GutterWidth, 4000f));

            m_Code = CreateText(bodyGo.transform, "Code", CodeFontSize, TextAnchor.UpperLeft, CodeText, mono);
            Place(m_Code.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(GutterWidth + 16f, -8f), new Vector2(PanelWidth - GutterWidth - 40f, 4000f));
            m_Code.supportRichText = true;

            // 滚动条命中区（不可见，宽 14；视觉只画 6px 的滑块——命中区更宽好按）
            var trackGo = new GameObject("ScrollTrack", typeof(RectTransform));
            trackGo.transform.SetParent(bodyGo.transform, false);
            m_TrackRect = trackGo.GetComponent<RectTransform>();
            m_TrackRect.anchorMin = new Vector2(1f, 0f);
            m_TrackRect.anchorMax = new Vector2(1f, 1f);
            m_TrackRect.pivot = new Vector2(1f, 0.5f);
            m_TrackRect.anchoredPosition = new Vector2(-2f, 0f);
            m_TrackRect.sizeDelta = new Vector2(14f, 0f);

            var thumbGo = new GameObject("ScrollThumb", typeof(RectTransform));
            thumbGo.transform.SetParent(bodyGo.transform, false);
            m_ScrollThumb = thumbGo.AddComponent<Image>();
            m_ScrollThumb.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            m_ScrollThumb.raycastTarget = false;
            Place(m_ScrollThumb.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                  new Vector2(-7f, -4f), new Vector2(6f, 100f));

            m_CanvasRoot.SetActive(false);   // 默认关闭，双击实体才打开
        }

        private static Text CreateText(Transform parent, string name, int fontSize,
                                       TextAnchor align, Color32 color, Font font)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;   // 代码不换行（超宽被 RectMask2D 裁掉）
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // 系统等宽字体（VS Code 默认 Consolas；逐级回退）
        private static Font GetMonoFont()
        {
            if (s_Mono == null)
            {
                s_Mono = Font.CreateDynamicFontFromOSFont(
                    new[] { "Consolas", "Cascadia Mono", "Courier New", "monospace" }, CodeFontSize);
                if (s_Mono == null)
                {
                    s_Mono = GalaxyHud.GetFont();
                    Debug.LogWarning("[CodeViewer] 未找到系统等宽字体，退回内置字体");
                }
                // 预热：一次性光栅化 ASCII 可打印字符。动态字体图集是"按需生成"的，
                // 首帧上万个字形请求来不及光栅化时文字会采样到未初始化的图集
                //（实测出现过整块异常色块的瞬态故障形态）
                s_Mono.RequestCharactersInTexture(
                    " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`" +
                    "abcdefghijklmnopqrstuvwxyz{|}~", CodeFontSize);
            }
            return s_Mono;
        }
    }
}
