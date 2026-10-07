// ============================================================================
// 拾取交互：左键点击实体 -> 高亮它的依赖链（连线 + 邻接实体）+ HUD 详情面板。
//
// 拾取算法：CPU 射线求交，不用 Physics.Raycast —— 实体本来就没有碰撞体
// （布局期已禁用），而"位置 + 包络半径"在力导向阶段就已在手，1887 个实体的
// 射线-球求交一次点击仅微秒级。C++ 类比：对现成的 SoA 数据做一次 O(n) 遍历，
// 比搭一整套物理场景（collider 组件 + 宽相/窄相）轻得多。
//
// 高亮渲染（四种手段，各有所长）：
//   1) 主连线网格重建顶点色：关联边金色渐变，其余压暗（9k 顶点，微秒级）；
//   2) 叠加网格（Galaxy/OverlayLine，ZTest Always）：把高亮链"照穿"前景实体
//      —— 密集星核里普通深度的线会被其他实体挡得所剩无几；
//   3) 实体逐色：MaterialPropertyBlock 调 _EmissionColor/_BaseColor，
//      不新建材质实例、不破坏共享材质（恢复 = 清空 MPB，材质即事实来源）。
//      注意：MPB 会让这些渲染器退出 SRP Batcher（退回普通批处理）——
//      本场景规模下（~2k 实体）代价可忽略。
//   4) 多面体棱线网格重建顶点色：选中金框、邻接增亮、其余压暗（与连线同法），
//      按"每节点顶点段"（edgeVertexBase/edgeVertCount）逐段重写。
//      多面体填充是半透明的（α≈0.12），只加自发光视觉贡献会被 alpha 缩水，
//      点亮时须同时把填充透明度提上来（BrightenedFill）。
//
// 最小可点半径：密集星核里实体被体积自适应缩得很小（最小 0.02 世界单位），
// 直接按包络半径拾取等于不可点。把"屏幕上 8 像素"折算到实体所在深度作为
// 半径下界（透视投影：每像素世界尺寸 = 2·d·tan(fov/2)/屏幕高）。
//
// 点击/拖拽判定：按下与抬起的位移超过阈值视为拖拽（旋转相机），不触发拾取；
// 点击落在 HUD 详情面板矩形内也不触发（手写命中测试，代替 EventSystem）。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Galaxy
{
    public class GalaxyPicker : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // 场景数据：由 GalaxyBuilder 在每次生成完成后注入（引用传递，零拷贝）。
        // ------------------------------------------------------------------
        public sealed class SceneData
        {
            public GalaxyGraph graph;
            public Vector3[] positions;        // 世界坐标（实体位置）
            public float[] radii;              // 包络半径（体积自适应后的最终值）
            public MeshRenderer[] renderers;   // 逐实体渲染器（高亮调色用）
            public int[] inDegree;             // 被多少文件引用
            public int[] outDegree;            // 引用了多少文件
            public Mesh linksMesh;             // 主连线网格（选择时重建顶点色）
            public Color[] linkDefaultColors;  // 2E 默认顶点色（保持原始副本，勿改）

            public byte[] shapes;              // 形状分类（GalaxyBuilder.ShapeXxx：多面体要提填充/棱线）
            public Mesh edgeMesh;              // 多面体棱线网格（选择时重建顶点色；可能为 null）
            public int[] edgeVertexBase;       // 各节点棱线顶点起始下标（-1 = 无棱线）
            public int[] edgeVertCount;        // 各节点棱线顶点数
            public Color[] edgeDefaultColors;  // 棱线默认顶点色副本（保持原始副本，勿改）

            // 由 Initialize 从共享材质读取填充（恢复时材质本身就是事实来源）
            [System.NonSerialized] public Color[] baseColors;
            [System.NonSerialized] public Color[] baseEmissions;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private const float ClickSlopPixels = 6f;    // 超过该位移视为拖拽（旋转），不算点击
        private const float MinPickPixels = 8f;      // 最小可点半径（屏幕像素，折算到世界）
        private const float DoubleClickSeconds = 0.35f;   // 双击判定窗口：两次点击同实体且间隔小于该值
        private const float DimBaseColor = 0.22f;    // 非关联实体：基色压暗系数
        private const float DimBaseAlpha = 0.40f;    // 非关联实体：透明度压暗系数（立方体填充）
        private const float DimEmission = 0.06f;     // 非关联实体：自发光压暗系数
        private const float DimLinkColor = 0.12f;    // 非关联连线：压暗系数
        // 增亮倍率对 Bloom 敏感：过高时枢纽核心（数百条高亮线 + 大量邻接实体）
        // 会过曝成白团、金色链与光晕全部被吞掉（实测 1.55/2.40 的失败形态）。
        private const float NeighborBoost = 1.30f;   // 邻接实体：自发光增强倍率
        private const float SelectedBoost = 1.90f;   // 选中实体：自发光增强倍率
        // 多面体填充是半透明的（α≈0.12）：点亮时把透明度提上来（自发光会被
        // alpha 缩水，不提透明度等于没点亮——实测"立方体点亮后亮度不够"的成因）
        private const float PolySelectedAlphaBoost = 4.2f;   // 选中多面体：填充 α 0.12 → ≈0.50
        private const float PolyNeighborAlphaBoost = 2.2f;   // 邻接多面体：填充 α 0.12 → ≈0.26
        private const float PolyMaxFillAlpha = 0.6f;         // 填充透明度上限（防高亮+Bloom 过曝）
        private const float EdgeDim = 0.18f;                 // 非关联棱线：压暗系数（含 alpha）
        private const float EdgeNeighborAlpha = 0.8f;        // 邻接棱线：透明度（默认 0.5 → 0.8）

        private static readonly Color HighlightColor = new Color(1f, 0.84f, 0.38f, 1f);
        private static readonly Color HighlightColorFar = new Color(1f, 0.84f, 0.38f, 0.45f);
        private static readonly Color EdgeSelectedColor = new Color(1f, 0.84f, 0.38f, 0.95f);   // 选中多面体棱线 = 金框

        private const string HintIdle = "Left-click: file details · Double-click: open file · Drag: orbit · Wheel: zoom · Right-drag: pan";
        private const string HintSelected = "Click again or click empty space to deselect";

        private SceneData m_Data;
        private Transform m_Root;
        private List<int>[] m_Incident;              // 每节点的关联边下标（Initialize 时一次构建）

        // 注意：绝不能在字段初始化器里 new MaterialPropertyBlock —— 它的构造函数
        // 要走 native 侧（CreateImpl），而 MonoBehaviour 在构造期（AddComponent /
        // 反序列化的字段初始化阶段）禁止调用 native。实测踩坑：字段初始化器抛
        // UnityException 后会跳过其后声明的所有初始化器（m_Neighbors 因此为 null，
        // 又在 Initialize 里二次抛 NRE）。这类原生资源统一延迟到 Initialize 创建。
        private MaterialPropertyBlock m_Mpb;
        private Color[] m_LinkScratch;               // 连线顶点色复用缓冲
        private Color[] m_EdgeScratch;               // 棱线顶点色复用缓冲
        private bool[] m_EdgeMark;                   // 关联边标记复用缓冲（长度 E）

        private int m_Selected = -1;
        private readonly HashSet<int> m_Neighbors = new HashSet<int>();

        private Mesh m_OverlayMesh;
        private MeshRenderer m_OverlayRenderer;
        private readonly List<Vector3> m_OverlayVerts = new List<Vector3>();
        private readonly List<Color> m_OverlayColors = new List<Color>();

        private GalaxyHud m_Hud;
        private GalaxyCodeViewer m_Viewer;           // 代码查看器（双击路径懒创建，与 HUD 同挂相机）
        private GalaxyProjectLoader m_Loader;        // 项目加载器（OPEN PROJECT 按钮 / --scan）
        private bool m_Pressed;
        private Vector2 m_PressPos;
        private int m_LastClickHit = -1;             // 上一次点击命中的实体（双击判定）
        private float m_LastClickTime = -10f;
        private bool m_ViewerScrubbing;              // 正在拖拽查看器滚动条（整段手势拦截）

        // 指针在 UI 面板上时，相机输入（拖拽/滚轮）让位给面板操作
        private void OnEnable() { OrbitCamera.PointerBlocked = IsPointerOverPanel; }
        private void OnDisable()
        {
            // 只卸载自己挂的钩子（delegate.Target 即实例引用；ReferenceEquals 避免
            // UnityEngine.Object 的 == 重载导致语义歧义）
            if (OrbitCamera.PointerBlocked != null &&
                ReferenceEquals(OrbitCamera.PointerBlocked.Target, this))
            {
                OrbitCamera.PointerBlocked = null;
            }
        }

        private bool IsPointerOverPanel(Vector2 screenPos)
        {
            // 面板范围内、或正在拖拽滚动条（拖出面板也保持封锁）→ 相机让位
            if (m_Viewer != null && m_Viewer.IsOpen &&
                (m_Viewer.ContainsScreenPoint(screenPos) || m_Viewer.IsScrubbing)) return true;
            return m_Hud != null && m_Hud.ContainsScreenPoint(screenPos);
        }

        /// <summary>被引用最多的文件（枢纽节点）；-1 = 无数据。自动化演示与将来"引导视角"用。</summary>
        public int HubIndex { get; private set; } = -1;

        public SceneData Data => m_Data;

        // ------------------------------------------------------------------
        // 数据注入：每次 Build 完成后由 GalaxyBuilder 调用（含编辑模式 Rebuild）。
        // ------------------------------------------------------------------
        public void Initialize(SceneData data, Transform galaxyRoot)
        {
            // 原生资源在运行时创建（构造期禁止，见 m_Mpb 字段处说明）
            if (m_Mpb == null) m_Mpb = new MaterialPropertyBlock();

            // 上一轮的叠加层 GameObject 随星系根一并销毁，但其 Mesh 是运行时对象、
            // 不会自动回收 —— 在这里兜底释放，避免每次 Rebuild 泄漏一个网格。
            if (m_OverlayMesh != null) DestroyNow(m_OverlayMesh);
            m_OverlayMesh = null;
            m_OverlayRenderer = null;

            m_Data = data;
            m_Root = galaxyRoot;
            m_Selected = -1;
            m_Neighbors.Clear();

            int n = data.positions.Length;
            m_Incident = new List<int>[n];
            for (int i = 0; i < n; i++) m_Incident[i] = new List<int>();
            for (int e = 0; e < data.graph.links.Length; e++)
            {
                GalaxyLink l = data.graph.links[e];
                m_Incident[l.source].Add(e);
                m_Incident[l.target].Add(e);
            }

            int count = data.renderers.Length;
            data.baseColors = new Color[count];
            data.baseEmissions = new Color[count];
            for (int i = 0; i < count; i++)
            {
                Material mat = data.renderers[i] != null ? data.renderers[i].sharedMaterial : null;
                data.baseColors[i] = mat != null ? mat.GetColor(BaseColorId) : Color.white;
                data.baseEmissions[i] = mat != null ? mat.GetColor(EmissionId) : Color.black;
            }

            m_LinkScratch = new Color[data.linkDefaultColors.Length];
            m_EdgeScratch = data.edgeDefaultColors != null ? new Color[data.edgeDefaultColors.Length] : null;
            m_EdgeMark = new bool[data.graph.links.Length];

            HubIndex = -1;
            int bestIn = -1;
            for (int i = 0; i < n; i++)
            {
                if (data.inDegree[i] > bestIn) { bestIn = data.inDegree[i]; HubIndex = i; }
            }

            // HUD 只在 Play 中创建：编辑模式 Rebuild 不应往场景里塞 UI 对象
            if (Application.isPlaying)
            {
                EnsureLoader();
                EnsureHud();
                m_Hud.SetHeader($"GALAXY · {GalaxyHud.Plural(data.graph.nodes.Length, "file")} · " +
                                $"{GalaxyHud.Plural(data.graph.links.Length, "link")}");
                m_Hud.ShowEmpty();
                m_Hud.SetHint(HintIdle);
            }
        }

        // ------------------------------------------------------------------
        // 拾取：屏幕点 -> 相机射线 -> 与所有实体的包络球求交 -> 最近命中。
        // 返回实体下标；-1 = 未命中。点击与自动化校验共用同一条代码路径。
        // ------------------------------------------------------------------
        public int PickAt(Vector2 screenPos)
        {
            if (m_Data == null) return -1;
            Camera cam = GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            if (cam == null) return -1;

            Ray ray = cam.ScreenPointToRay(screenPos);
            Vector3 camPos = cam.transform.position;
            // 透视投影下"一个屏幕像素"在该深度对应的世界尺寸的一半系数
            float pxWorld = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;

            int best = -1;
            float bestT = float.MaxValue;
            for (int i = 0; i < m_Data.positions.Length; i++)
            {
                Vector3 p = m_Data.positions[i];
                float depth = (p - camPos).magnitude;
                float pickR = Mathf.Max(m_Data.radii[i], MinPickPixels * 2f * pxWorld * depth);
                float t = RaySphere(ray, p, pickR);
                if (t >= 0f && t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
            return best;
        }

        // 射线-球解析求交（二次方程）。返回最近正向交点的射线参数 t；无交返回 -1。
        private static float RaySphere(Ray ray, Vector3 center, float radius)
        {
            Vector3 m = center - ray.origin;
            float b = Vector3.Dot(m, ray.direction);
            float c = Vector3.Dot(m, m) - radius * radius;
            if (c > 0f && b < 0f) return -1f;          // 球整体在相机背后
            float disc = b * b - c;
            if (disc < 0f) return -1f;
            float t = b - Mathf.Sqrt(disc);
            return t < 0f ? 0f : t;                    // 相机在球内：t 取 0
        }

        // ------------------------------------------------------------------
        // 选择：重建全部高亮状态（无任何增量状态 —— 每次从数据全量重算，
        // 不存在"上一轮残留"类 bug；实体颜色恢复交给 MPB 清空）。
        // ------------------------------------------------------------------
        public void SelectNode(int index)
        {
            if (m_Data == null || index < 0 || index >= m_Data.positions.Length) return;
            m_Selected = index;

            m_Neighbors.Clear();
            List<int> incident = m_Incident[index];
            for (int t = 0; t < incident.Count; t++)
            {
                GalaxyLink l = m_Data.graph.links[incident[t]];
                m_Neighbors.Add(l.source == index ? l.target : l.source);
            }

            ApplyEntityStates();
            RebuildLinksColors();
            RebuildEdgeColors();
            RebuildOverlay();

            if (Application.isPlaying)
            {
                EnsureHud();
                GalaxyNode node = m_Data.graph.nodes[index];
                m_Hud.ShowFile(node, m_Data.inDegree[index], m_Data.outDegree[index], KindOf(index));
                m_Hud.SetHint(HintSelected);
            }
        }

        public void ClearSelection()
        {
            if (m_Data == null || m_Selected < 0) return;
            m_Selected = -1;
            m_Neighbors.Clear();

            // 清空 MPB 即回到共享材质原状（无需手工记忆任何"原始值"）
            for (int i = 0; i < m_Data.renderers.Length; i++)
            {
                if (m_Data.renderers[i] != null) m_Data.renderers[i].SetPropertyBlock(null);
            }
            RebuildLinksColors();   // 无选择分支 = 直接复制默认色
            RebuildEdgeColors();    // 同上：棱线恢复默认色
            if (m_OverlayRenderer != null && m_OverlayMesh != null)
            {
                m_OverlayMesh.Clear();
                m_OverlayRenderer.enabled = false;
            }
            if (m_Hud != null)
            {
                m_Hud.ShowEmpty();
                m_Hud.SetHint(HintIdle);
            }
        }

        // ------------------------------------------------------------------
        // 点击检测：新的 Input System 按帧读设备快照；按下/抬起位移做拖拽区分。
        // ------------------------------------------------------------------
        private void Update()
        {
            if (m_Data == null) return;
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // 滚轮：指针悬停代码查看器上时翻页（虚拟窗口滚动）
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f && m_Viewer != null && m_Viewer.IsOpen)
            {
                m_Viewer.TryScroll(mouse.position.ReadValue(), wheel);
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                Vector2 pressPos = mouse.position.ReadValue();
                // 查看器滚动条：按下即开始擦洗（拖拽直达目标位置；整段手势不与拾取/相机交互）
                if (m_Viewer != null && m_Viewer.IsOpen && m_Viewer.IsOverScrollArea(pressPos))
                {
                    m_ViewerScrubbing = true;
                    m_Viewer.BeginScrub(pressPos);
                    return;
                }
                m_Pressed = true;
                m_PressPos = pressPos;
                return;
            }

            // 擦洗中：每帧跟随指针；松开结束（即使指针已拖出面板/轨道范围）
            if (m_ViewerScrubbing)
            {
                if (mouse.leftButton.wasReleasedThisFrame)
                {
                    m_Viewer.EndScrub();
                    m_ViewerScrubbing = false;
                }
                else if (mouse.leftButton.isPressed)
                {
                    m_Viewer.UpdateScrub(mouse.position.ReadValue());
                }
                return;
            }

            if (!m_Pressed || !mouse.leftButton.wasReleasedThisFrame) return;
            m_Pressed = false;

            Vector2 pos = mouse.position.ReadValue();
            if (Vector2.Distance(pos, m_PressPos) > ClickSlopPixels) return;   // 拖拽 = 旋转相机

            // 代码查看器优先拦截：关闭按钮 → 关面板；面板内部 → 吞掉（不改变选中）
            if (m_Viewer != null && m_Viewer.IsOpen)
            {
                if (m_Viewer.IsOverClose(pos)) { m_Viewer.Close(); return; }
                if (m_Viewer.ContainsScreenPoint(pos)) return;
            }
            // OPEN PROJECT 按钮（标题下方）：空闲 → 系统文件夹对话框；
            // 扫描中 → 取消扫描（按钮文字同步变为 CANCEL SCAN）
            if (m_Hud != null && m_Hud.ContainsOpenButton(pos))
            {
                EnsureLoader();
                if (m_Loader.IsScanning) m_Loader.CancelScan();
                else m_Loader.OpenDialog();
                return;
            }
            if (m_Hud != null && m_Hud.ContainsScreenPoint(pos)) return;       // 点在 HUD 面板上

            int hit = PickAt(pos);

            // 双击判定：与上一次点击命中同一实体、且间隔小于阈值 → 打开代码查看器
            bool doubleClick = hit >= 0 && hit == m_LastClickHit &&
                               Time.unscaledTime - m_LastClickTime <= DoubleClickSeconds;
            m_LastClickHit = hit;
            m_LastClickTime = Time.unscaledTime;
            if (doubleClick)
            {
                OpenViewer(hit);
                if (hit != m_Selected) SelectNode(hit);   // 防御：双击前未选中则补选中
                return;
            }

            if (hit < 0 || hit == m_Selected) ClearSelection();   // 空白 / 再点同一实体 = 取消
            else SelectNode(hit);
        }

        /// <summary>打开代码查看器（双击路径与自动化演示共用同一条入口）。</summary>
        public void OpenViewer(int index)
        {
            if (m_Data == null || index < 0 || index >= m_Data.positions.Length) return;
            EnsureViewer();
            m_Viewer.Show(m_Data.graph.root, m_Data.graph.nodes[index]);
        }

        private void EnsureViewer()
        {
            if (m_Viewer != null) return;
            m_Viewer = GetComponent<GalaxyCodeViewer>();
            if (m_Viewer == null) m_Viewer = gameObject.AddComponent<GalaxyCodeViewer>();
        }

        private void EnsureLoader()
        {
            if (m_Loader != null) return;
            m_Loader = GetComponent<GalaxyProjectLoader>();
            if (m_Loader == null) m_Loader = gameObject.AddComponent<GalaxyProjectLoader>();
        }

        /// <summary>关闭代码查看器（重建星系前调用：旧文件路径随新项目失效）。</summary>
        public void CloseViewer()
        {
            if (m_Viewer != null) m_Viewer.Close();
        }

        // ---- 实体逐色调光：选中增亮、邻接增亮、其余压暗 ----
        private void ApplyEntityStates()
        {
            SceneData d = m_Data;
            for (int i = 0; i < d.renderers.Length; i++)
            {
                MeshRenderer r = d.renderers[i];
                if (r == null) continue;

                if (i == m_Selected)
                {
                    SetMpb(r, BrightenedFill(d, i, PolySelectedAlphaBoost), d.baseEmissions[i] * SelectedBoost);
                }
                else if (m_Neighbors.Contains(i))
                {
                    SetMpb(r, BrightenedFill(d, i, PolyNeighborAlphaBoost), d.baseEmissions[i] * NeighborBoost);
                }
                else
                {
                    Color b = d.baseColors[i];
                    Color e = d.baseEmissions[i];
                    SetMpb(r,
                        new Color(b.r * DimBaseColor, b.g * DimBaseColor, b.b * DimBaseColor, b.a * DimBaseAlpha),
                        new Color(e.r * DimEmission, e.g * DimEmission, e.b * DimEmission, e.a));
                }
            }
        }

        // 多面体（立方体/正四面体）填充增亮：球体不透明（原样返回）；多面体填充
        // 半透明，点亮时按倍率提亮并封顶——透明度不提，发光会被 alpha 缩水，
        // 视觉上"点了跟没点一样"。
        private static Color BrightenedFill(SceneData d, int i, float boost)
        {
            Color b = d.baseColors[i];
            if (d.shapes[i] == GalaxyBuilder.ShapeSphere) return b;
            b.a = Mathf.Min(b.a * boost, PolyMaxFillAlpha);
            return b;
        }

        private string KindOf(int index)
        {
            switch (m_Data.shapes[index])
            {
                case GalaxyBuilder.ShapeCube:  return "header";
                case GalaxyBuilder.ShapeTetra: return "source";
                default:
                    return m_Data.graph.nodes[index].kind == "binary" ? "binary" : "text";
            }
        }

        private void SetMpb(MeshRenderer r, Color baseColor, Color emission)
        {
            m_Mpb.Clear();
            m_Mpb.SetColor(BaseColorId, baseColor);
            m_Mpb.SetColor(EmissionId, emission);
            r.SetPropertyBlock(m_Mpb);
        }

        // ---- 主连线网格重着色：关联边金色渐变（选中端亮、对端半透明，方向即依赖方向），
        //      其余压暗。只写颜色，几何位置不动（SetColors 9184 项，微秒级）。----
        private void RebuildLinksColors()
        {
            if (m_Data == null || m_Data.linksMesh == null) return;
            Color[] colors = m_LinkScratch;
            System.Array.Copy(m_Data.linkDefaultColors, colors, colors.Length);

            if (m_Selected >= 0)
            {
                System.Array.Clear(m_EdgeMark, 0, m_EdgeMark.Length);
                List<int> incident = m_Incident[m_Selected];
                for (int t = 0; t < incident.Count; t++) m_EdgeMark[incident[t]] = true;

                for (int e = 0; e < m_EdgeMark.Length; e++)
                {
                    int v0 = e * 2;
                    int v1 = v0 + 1;
                    if (m_EdgeMark[e])
                    {
                        bool srcSelected = m_Data.graph.links[e].source == m_Selected;
                        colors[v0] = srcSelected ? HighlightColor : HighlightColorFar;
                        colors[v1] = srcSelected ? HighlightColorFar : HighlightColor;
                    }
                    else
                    {
                        Color a = colors[v0];
                        Color b = colors[v1];
                        colors[v0] = new Color(a.r * DimLinkColor, a.g * DimLinkColor, a.b * DimLinkColor, a.a * DimLinkColor);
                        colors[v1] = new Color(b.r * DimLinkColor, b.g * DimLinkColor, b.b * DimLinkColor, b.a * DimLinkColor);
                    }
                }
            }
            m_Data.linksMesh.SetColors(colors);
        }

        // ---- 多面体棱线重着色：按节点顶点段重写（选中 = 金框、邻接 = 提透明度、
        //      其余 = 压暗）。与连线同法：只写颜色，几何不动。----
        private void RebuildEdgeColors()
        {
            if (m_Data == null || m_Data.edgeMesh == null || m_EdgeScratch == null) return;
            Color[] colors = m_EdgeScratch;
            System.Array.Copy(m_Data.edgeDefaultColors, colors, colors.Length);

            if (m_Selected >= 0)
            {
                for (int i = 0; i < m_Data.edgeVertexBase.Length; i++)
                {
                    int baseIndex = m_Data.edgeVertexBase[i];
                    if (baseIndex < 0) continue;
                    int count = m_Data.edgeVertCount[i];

                    if (i == m_Selected)
                    {
                        for (int t = 0; t < count; t++) colors[baseIndex + t] = EdgeSelectedColor;
                    }
                    else if (m_Neighbors.Contains(i))
                    {
                        for (int t = 0; t < count; t++)
                        {
                            Color c = colors[baseIndex + t];
                            colors[baseIndex + t] = new Color(c.r, c.g, c.b, EdgeNeighborAlpha);
                        }
                    }
                    else
                    {
                        for (int t = 0; t < count; t++)
                        {
                            Color c = colors[baseIndex + t];
                            colors[baseIndex + t] = new Color(c.r * EdgeDim, c.g * EdgeDim, c.b * EdgeDim, c.a * EdgeDim);
                        }
                    }
                }
            }
            m_Data.edgeMesh.SetColors(colors);
        }

        // ---- 叠加层：高亮链（金色，ZTest Always 照穿前景）+ 选中光晕（线框球）----
        private void RebuildOverlay()
        {
            if (m_Selected < 0) return;
            EnsureOverlay();

            m_OverlayVerts.Clear();
            m_OverlayColors.Clear();

            Vector3 center = m_Data.positions[m_Selected];
            List<int> incident = m_Incident[m_Selected];
            for (int t = 0; t < incident.Count; t++)
            {
                GalaxyLink l = m_Data.graph.links[incident[t]];
                int other = l.source == m_Selected ? l.target : l.source;
                m_OverlayVerts.Add(center);
                m_OverlayColors.Add(HighlightColor);
                m_OverlayVerts.Add(m_Data.positions[other]);
                m_OverlayColors.Add(HighlightColorFar);
            }

            // 光晕半径比包络半径大一圈（"瞄准环"），三正交大圆构成线框球；
            // 密集星核里实体半径可能被收缩到极小，加 1.0 的下限保证光晕可见
            float halo = Mathf.Max(m_Data.radii[m_Selected] * 1.45f, 1.0f);
            Color haloColor = new Color(HighlightColor.r, HighlightColor.g, HighlightColor.b, 0.5f);
            AppendCircle(center, halo, 0, haloColor);
            AppendCircle(center, halo, 1, haloColor);
            AppendCircle(center, halo, 2, haloColor);

            var indices = new int[m_OverlayVerts.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;

            m_OverlayMesh.Clear();
            m_OverlayMesh.SetVertices(m_OverlayVerts);
            m_OverlayMesh.SetColors(m_OverlayColors);
            m_OverlayMesh.SetIndices(indices, 0, indices.Length, MeshTopology.Lines, 0);
            m_OverlayMesh.RecalculateBounds();
            m_OverlayRenderer.enabled = true;
        }

        private void EnsureOverlay()
        {
            if (m_OverlayRenderer != null) return;

            var go = new GameObject("SelectionOverlay");
            if (m_Root != null) go.transform.SetParent(m_Root, false);

            m_OverlayMesh = new Mesh { name = "galaxy-selection" };
            m_OverlayMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m_OverlayMesh.MarkDynamic();

            go.AddComponent<MeshFilter>().sharedMesh = m_OverlayMesh;
            m_OverlayRenderer = go.AddComponent<MeshRenderer>();

            Shader shader = Shader.Find("Galaxy/OverlayLine");
            if (shader == null)
            {
                Debug.LogWarning("[Pick] 未找到 Galaxy/OverlayLine，退回 Sprites/Default（高亮线会被前景实体遮挡）");
                shader = Shader.Find("Sprites/Default");
            }
            m_OverlayRenderer.sharedMaterial = new Material(shader) { name = "galaxy-selection-overlay" };
            m_OverlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_OverlayRenderer.receiveShadows = false;
        }

        private void EnsureHud()
        {
            if (m_Hud != null) return;
            m_Hud = GetComponent<GalaxyHud>();
            if (m_Hud == null) m_Hud = gameObject.AddComponent<GalaxyHud>();
            m_Hud.EnsureCreated("GALAXY");
        }

        private void AppendCircle(Vector3 center, float radius, int plane, Color color)
        {
            const int segments = 48;
            for (int i = 0; i < segments; i++)
            {
                float a0 = (i / (float)segments) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segments) * Mathf.PI * 2f;
                m_OverlayVerts.Add(center + CirclePoint(plane, a0, radius));
                m_OverlayVerts.Add(center + CirclePoint(plane, a1, radius));
                m_OverlayColors.Add(color);
                m_OverlayColors.Add(color);
            }
        }

        private static Vector3 CirclePoint(int plane, float angle, float radius)
        {
            float c = Mathf.Cos(angle) * radius;
            float s = Mathf.Sin(angle) * radius;
            switch (plane)
            {
                case 0:  return new Vector3(c, s, 0f);   // XY 平面
                case 1:  return new Vector3(c, 0f, s);   // XZ 平面
                default: return new Vector3(0f, c, s);   // YZ 平面
            }
        }

        // Destroy 是运行时 API；编辑模式必须用 DestroyImmediate（同 GalaxyBuilder 的约定）
        private static void DestroyNow(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
