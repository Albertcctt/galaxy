// ============================================================================
// 运行时 HUD（uGUI，全代码搭建）：左上角标题、右上角文件详情面板、左下角操作提示。
//
// 为什么不用预制体：本项目的装配流程（GalaxySceneSetup / 自动引导）要求全程
// 无人值守，代码建 UI 才能做到"装配 -> Play -> 截图"零手工步骤（预制体需要
// 维护 .prefab 资产，场景引用还容易断链）。
//
// 交互说明：HUD 只负责显示，不挂 EventSystem —— "点击穿透"由 GalaxyPicker
// 用矩形命中测试处理（Screen Space Overlay 下 RectTransformUtility 即为全部
// 所需），少一层事件系统依赖，自动化环境更稳。
//
// 字体：Unity 6 的内置字体资源名是 LegacyRuntime.ttf（旧教程里的 Arial.ttf
// 已被移除）；动态字体在 Windows 下走系统回退，中文可正常渲染。
//
// Unity 概念对照（C++ 视角）：Canvas 相当于 UI 的"根坐标系 + 合成层"，
// Screen Space Overlay 表示最后合成、盖在一切 3D 内容之上；RectTransform
// 就是带锚点约束的 2D 变换（类似相对布局的约束求解器）。
// ============================================================================
using UnityEngine;
using UnityEngine.UI;

namespace Galaxy
{
    public class GalaxyHud : MonoBehaviour
    {
        private static Font s_Font;

        private RectTransform m_Panel;   // 详情面板矩形（供拾取的点击穿透判定）
        private RectTransform m_OpenBtnRect;   // OPEN PROJECT 按钮矩形（点击命中路由用）
        private Text m_OpenBtnLabel;           // 按钮文字（扫描时切换为 CANCEL SCAN）
        private Text m_Header;
        private Text m_Detail;
        private Text m_Hint;
        private Text m_Status;
        private bool m_Created;

        /// <summary>屏幕坐标是否落在详情面板内（Screen Space Overlay 下 camera 参数为 null）。</summary>
        public bool ContainsScreenPoint(Vector2 screenPos)
        {
            return m_Panel != null && RectTransformUtility.RectangleContainsScreenPoint(m_Panel, screenPos, null);
        }

        // 幂等创建：首次调用搭出全部 UI，之后只刷新标题。
        public void EnsureCreated(string headerText)
        {
            if (m_Created)
            {
                m_Header.text = headerText;
                return;
            }
            m_Created = true;

            var canvasGo = new GameObject("GalaxyHUD", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;   // 最后合成，盖在 3D 场景之上

            CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;   // 宽高各半加权，任意窗口比例下布局都不挤

            m_Header = CreateText(canvasGo.transform, "Header", 24, TextAnchor.UpperLeft,
                                  new Color(0.72f, 0.90f, 1f, 0.95f));
            m_Header.text = headerText;
            Place(m_Header.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(28f, -26f), new Vector2(900f, 40f));

            // OPEN PROJECT 按钮（标题下方）：桌面形态的"打开任意项目"入口。
            // 点击由 GalaxyPicker 手动命中路由（与关闭按钮同一套无 EventSystem 架构）
            var openGo = new GameObject("OpenProjectButton", typeof(RectTransform));
            openGo.transform.SetParent(canvasGo.transform, false);
            Image openBg = openGo.AddComponent<Image>();
            openBg.color = new Color(0.04f, 0.10f, 0.20f, 0.85f);
            openBg.raycastTarget = false;
            m_OpenBtnRect = openGo.GetComponent<RectTransform>();
            Place(m_OpenBtnRect, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(28f, -68f), new Vector2(190f, 34f));
            Text openText = CreateText(openGo.transform, "Label", 15, TextAnchor.MiddleCenter,
                                       new Color(0.50f, 0.83f, 1f, 0.95f));
            openText.text = "OPEN PROJECT";
            m_OpenBtnLabel = openText;
            Place(openText.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                  new Vector2(0f, 0f), new Vector2(190f, 34f));
            openText.rectTransform.anchorMin = Vector2.zero;
            openText.rectTransform.anchorMax = Vector2.one;

            // 状态行（扫描进度 / 错误 / 当前项目摘要）
            m_Status = CreateText(canvasGo.transform, "Status", 16, TextAnchor.UpperLeft,
                                  new Color(0.55f, 0.66f, 0.82f, 0.9f));
            m_Status.text = "";
            Place(m_Status.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(28f, -108f), new Vector2(900f, 60f));

            // 详情面板（右上角）：纯色半透明底 + 标题 + 明细
            var panelGo = new GameObject("DetailPanel", typeof(RectTransform));
            panelGo.transform.SetParent(canvasGo.transform, false);
            Image bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.02f, 0.035f, 0.07f, 0.78f);
            bg.raycastTarget = false;
            m_Panel = panelGo.GetComponent<RectTransform>();
            Place(m_Panel, new Vector2(1f, 1f), new Vector2(1f, 1f),
                  new Vector2(-28f, -26f), new Vector2(440f, 320f));

            Text title = CreateText(panelGo.transform, "Title", 20, TextAnchor.UpperLeft,
                                    new Color(0.72f, 0.90f, 1f, 1f));
            title.text = "FILE INFO";
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(20f, -16f), new Vector2(400f, 30f));

            m_Detail = CreateText(panelGo.transform, "Detail", 19, TextAnchor.UpperLeft,
                                  new Color(0.85f, 0.90f, 1f, 0.95f));
            Place(m_Detail.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                  new Vector2(20f, -60f), new Vector2(400f, 240f));

            m_Hint = CreateText(canvasGo.transform, "Hint", 18, TextAnchor.LowerLeft,
                                new Color(0.55f, 0.66f, 0.82f, 0.85f));
            Place(m_Hint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                  new Vector2(28f, 26f), new Vector2(1100f, 36f));
        }

        public void SetHeader(string text)
        {
            if (m_Header != null) m_Header.text = text;
        }

        /// <summary>屏幕坐标是否落在 OPEN PROJECT 按钮内。</summary>
        public bool ContainsOpenButton(Vector2 screenPos)
        {
            return m_OpenBtnRect != null &&
                   RectTransformUtility.RectangleContainsScreenPoint(m_OpenBtnRect, screenPos, null);
        }

        /// <summary>状态行（扫描进度 / 错误 / 项目摘要）；空串 = 清空。</summary>
        public void SetStatus(string text)
        {
            if (m_Status != null) m_Status.text = text;
        }

        /// <summary>按钮文字（OPEN PROJECT / CANCEL SCAN）。</summary>
        public void SetButtonLabel(string text)
        {
            if (m_OpenBtnLabel != null) m_OpenBtnLabel.text = text;
        }

        public void ShowEmpty()
        {
            if (m_Detail == null) return;
            m_Detail.text = "<color=#8fb7d9>Click a star to inspect its file.</color>";
        }

        public void ShowFile(GalaxyNode node, int inDegree, int outDegree, string kind)
        {
            if (m_Detail == null) return;
            m_Detail.text =
                $"<color=#7fd4ff>{node.name}</color>\n" +
                $"<color=#8fb7d9>{node.path}</color>\n\n" +
                $"Type: {kind}\n" +
                $"Lines: {node.lines}\n" +
                $"Referenced by: {Plural(inDegree, "file")}\n" +
                $"References: {Plural(outDegree, "file")}";
        }

        /// <summary>英文单复数（1 file / 0 files / N files）；标题栏计数也用它。</summary>
        public static string Plural(int n, string noun)
        {
            return n == 1 ? $"1 {noun}" : $"{n} {noun}s";
        }

        public void SetHint(string text)
        {
            if (m_Hint != null) m_Hint.text = text;
        }

        private static Text CreateText(Transform parent, string name, int fontSize,
                                       TextAnchor align, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Text t = go.AddComponent<Text>();
            t.font = GetFont();
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        // 锚点/轴心/位置/尺寸一次设定（UI 相对布局的四个自由度，分开写极易漏项）
        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // 内置字体（HUD 与宇宙名碑共用；Unity 6 的内置字体资源名是 LegacyRuntime.ttf）
        public static Font GetFont()
        {
            if (s_Font == null)
            {
                s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (s_Font == null)
                {
                    Debug.LogWarning("[GalaxyHud] 未找到内置字体 LegacyRuntime.ttf，HUD 文字将不可见");
                }
            }
            return s_Font;
        }
    }
}
