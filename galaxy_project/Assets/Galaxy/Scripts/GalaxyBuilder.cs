// ============================================================================
// 总装：galaxy.json -> 力导向布局 -> 文件实体 + 依赖连线。
//
// 语义约定（每个实体 = 一个文件节点）：
//   - 头文件（.h/.hpp/.hh/.hxx）-> 透明正方体（全息容器意象）
//   - 源文件（.c/.cpp/.cc/.cxx）-> 透明正四面体（晶体意象）
//   - 其余文件                  -> 自发光球体
//   - 第一层文件夹              -> 一个"宇宙"：大透明球（菲涅尔边缘光环）包裹其
//                                  全部成员 —— 球体互不干涉、半径 ∝ 文件数³√、
//                                  成员被约束在球体内散开（布局见 UniverseLayout）
//   - 根目录散文件（无文件夹）  -> 一枚独立风格的宇宙球（固定紫色调 + 更实球体 +
//                                  更宽软光环，与文件夹宇宙一眼可辨）
//   - 连线只存在于有真实依赖（#include）关系的文件之间（来自 scanner 的 links）
//   - 实体之间不允许互相穿插：碰撞松弛（ResolveCollisions）+ 体积自适应
//     （CapRadiiToLocalClearance）构造性保证零重叠（见 ForceDirectedLayout.cs）
//   - 生成后把场景数据注入相机上的 GalaxyPicker：点击实体 -> 高亮依赖链 + HUD
//
// Unity 概念对照（C++ 视角）：
//   - MonoBehaviour 不是 new 出来的对象：引擎在场景加载/AddComponent 时替你构造
//     （类似工厂分配 + placement new），所以不要写构造函数，初始化放 Awake/Start。
//   - Start() 在对象首次激活那一帧调用一次，当作"进主循环之前的初始化阶段"。
//   - Destroy() 不是立即 free：对象在本帧末尾统一销毁（延迟析构，避免用坏指针）。
// ============================================================================
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Galaxy
{
    public class GalaxyBuilder : MonoBehaviour
    {
        [Header("数据")]
        [Tooltip("galaxy.json 路径。留空 = 默认取仓库根下的 scanner/galaxy.json（相对 Assets 上两级）")]
        public string jsonPath = "";

        [Header("外观")]
        [Tooltip("星系半径（世界单位）：布局收敛后整体缩放到该尺度")]
        public float galaxyRadius = 25f;
        [Tooltip("自发光强度（>1 进入 HDR 区间，由 URP Bloom 点亮）")]
        public float emissionIntensity = 1.6f;

        [Tooltip("连线颜色：起点（依赖发出方）。未选中时极低调（明度+透明度双压）：连线是结构背景，不抢戏")]
        public Color linkColorSource = new Color(0.19f, 0.50f, 0.55f, 0.12f);
        [Tooltip("连线颜色：终点（被依赖方）—— 明暗渐变表达依赖方向")]
        public Color linkColorTarget = new Color(0.08f, 0.22f, 0.55f, 0.04f);

        [Header("力导向布局（算法细节见 ForceDirectedLayout.cs 顶部说明）")]
        [Tooltip("迭代轮数：收敛更充分，代价随轮数线性上升")]
        public int layoutIterations = 300;
        [Tooltip("k：弹簧自然长度 / 斥力尺度 —— 局部结构的特征间距")]
        public float idealDistance = 3.0f;
        [Tooltip("全局斥力强度：F = strength · k² / d")]
        public float repulsionStrength = 1.5f;
        [Tooltip("依赖弹簧强度（胡克：F = strength · |d − k|，k 为自然长度）")]
        public float springStiffness = 1.0f;
        [Tooltip("目录内聚系数（传统模式）／层级引力核基础强度（宇宙模式：一级目录档，越深越强）")]
        public float dirCohesion = 1.2f;
        [Tooltip("层级引力递增系数（宇宙模式）：越深（越接近叶目录）的引力核越强（>1）")]
        public float dirLevelGain = 1.6f;
        [Tooltip("向心引力：线性弱引力，把外围目录团与游离节点收拢成星系形态")]
        public float gravity = 0.01f;
        [Tooltip("初始退火温度：每轮单步最大位移，线性冷却到 0")]
        public float startTemperature = 5f;
        [Tooltip("硬核接触刚度：实体包络球侵入时的推开力度（防穿插的强约束）")]
        public float contactStiffness = 8f;

        [Header("文件实体（每个实体 = 一个文件节点）")]
        [Tooltip("头文件立方体边长 = 实体尺寸 × 该系数（实体尺寸随行数对数增长）")]
        public float headerCubeSizeFactor = 0.8f;
        [Tooltip("透明多面体（立方体/正四面体）填充不透明度（建议 0.05 ~ 0.2）")]
        public float polyhedronFillAlpha = 0.12f;
        [Tooltip("透明多面体棱线不透明度")]
        public float polyhedronEdgeAlpha = 0.5f;

        [Header("宇宙（第一层文件夹 = 一个球体宇宙）")]
        [Tooltip("宇宙球半径 = 该系数 × 文件数³√（体积正比于文件数，内部密度近似一致）")]
        public float universeRadiusScale = 1.9f;
        [Tooltip("宇宙球最小半径（文件数很少的文件夹）")]
        public float universeMinRadius = 2.5f;
        [Tooltip("宇宙球之间的最小间隙：心距 ≥ 半径和 + 该值（不干涉约束）")]
        public float universeGap = 3.0f;
        [Tooltip("文件活动半径 = 球半径 × 该系数（球壁留出视觉间隙）")]
        public float universeWallFrac = 0.85f;
        [Tooltip("散文件宇宙专属半径系数（独立于其它宇宙的 universeRadiusScale；" +
                 "内部还要为黑洞留出中心空腔）")]
        public float scatteredRadiusScale = 2.6f;
        [Tooltip("散文件宇宙黑洞核心半径 = 该系数 × 文件数³√（视觉包络 = 核心 × 2，" +
                 "布局据此在中心留空）")]
        public float blackHoleRadiusScale = 0.7f;
        [Tooltip("球面基础不透明度（几乎透明：球体只靠菲涅尔边缘光环隐约显形）")]
        public float universeBaseAlpha = 0.005f;
        [Tooltip("边缘光环强度（刻意极低：存在感越弱越好，别抢内容）")]
        public float universeRimAlpha = 0.055f;
        [Tooltip("边缘光环收束指数：越大光环越细")]
        public float universeRimPower = 4.5f;

        [Header("粒子氛围（星尘）")]
        [Tooltip("星尘基础数量：总数 = 基础 + 每文件追加 × 文件数（clamp 400~8000）")]
        public int dustBase = 300;
        [Tooltip("每个文件追加的星尘数量")]
        public int dustPerFile = 2;

        // 无干涉约束（位置投影求解）的轮数上限：它只负责"前置减压"（把互相穿插
        // 的实体尽量推开，减少后续体积收缩的幅度）；实测极限堆积区（全部临界接触）
        // 无法靠它再降，最终的零穿插由 CapRadiiToLocalClearance 构造性保证。
        private const int CollisionRounds = 150;

        // 实体形状分类（与 GalaxyPicker.SceneData.shapes 的取值约定一致；
        // 拾取器按此决定高亮策略：多面体要额外提填充透明度、棱线要重着色）
        public const byte ShapeSphere = 0;   // 其余文件：自发光球体
        public const byte ShapeCube   = 1;   // 头文件：透明正方体
        public const byte ShapeTetra  = 2;   // 源文件：透明正四面体

        private readonly Dictionary<string, Material> m_DirMaterials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Material> m_PolyhedronMaterials = new Dictionary<string, Material>();
        private readonly Dictionary<string, Material> m_UniverseMaterials = new Dictionary<string, Material>();
        private Material m_BinaryMaterial;   // 二进制红球（单份共享）
        private GalaxyParticles m_Particles; // 星尘（设置面板实时调数量用）

        // 增量布局状态（上次构建的末态；Rebuild/重扫时按路径匹配复用位置）
        private Vector3[] m_LastPositions;
        private string[] m_LastPaths;
        private string[] m_LastUniverseKeys;
        private readonly Dictionary<string, Vector3> m_LastCentersByKey = new Dictionary<string, Vector3>();

        /// <summary>星尘组件引用（可能为 null：Build 尚未运行）。</summary>
        public GalaxyParticles Particles => m_Particles;

        // 名碑引用与基准色（运行时设置面板调亮度用：基准色 × 系数，不丢原色）
        private readonly List<TextMesh> m_LabelTexts = new List<TextMesh>();
        private readonly List<Color> m_LabelBaseColors = new List<Color>();

        // 运行时创建的网格（原生对象不随 GameObject 销毁自动回收，Rebuild 时显式释放防泄漏）
        private Mesh m_LinksMesh;
        private Mesh m_EdgeMesh;
        private Mesh m_TetraMesh;

        private void Start()
        {
            Build();
        }

        // 组件标题右键 -> Rebuild：不重启编辑器直接重建（改完 json/参数后快速迭代用）。
        // 注意：编辑模式下重建会向场景里真实生成对象，记得撤销（Ctrl+Z）或别保存场景。
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                DestroyNow(transform.GetChild(i).gameObject);
            }
            Build();
        }

        private void Build()
        {
            // 清掉上一轮缓存的材质与运行时网格：Rebuild 后 Inspector 里改的不透明度/
            // 颜色才能生效（网格为显式释放：原生对象不随 GameObject 销毁自动回收）
            foreach (Material m in m_DirMaterials.Values) DestroyNow(m);
            foreach (Material m in m_PolyhedronMaterials.Values) DestroyNow(m);
            foreach (Material m in m_UniverseMaterials.Values) DestroyNow(m);
            DestroyNow(m_BinaryMaterial);
            m_BinaryMaterial = null;
            m_LabelTexts.Clear();
            m_LabelBaseColors.Clear();
            m_DirMaterials.Clear();
            m_PolyhedronMaterials.Clear();
            m_UniverseMaterials.Clear();
            DestroyNow(m_LinksMesh);  m_LinksMesh = null;
            DestroyNow(m_EdgeMesh);   m_EdgeMesh = null;
            DestroyNow(m_TetraMesh);  m_TetraMesh = null;

            GalaxyGraph graph = GalaxyLoader.Load(ResolveJsonPath());
            if (graph == null) return;

            // 多元宇宙布局：先按"第一层文件夹"分区（互不干涉的球体打包 + 层级
            // 引力核/递归星团场种子），再让力导向在各自的宇宙球内收敛（跨宇宙不施力）
            UniverseLayout.Result universes = UniverseLayout.Compute(
                graph, universeRadiusScale, universeMinRadius, universeGap, universeWallFrac,
                dirCohesion, dirLevelGain, scatteredRadiusScale, blackHoleRadiusScale);

            var layout = new ForceDirectedLayout.Params
            {
                iterations       = layoutIterations,
                idealDistance    = idealDistance,
                repulsion        = repulsionStrength,
                spring           = springStiffness,
                cohesion         = dirCohesion,
                gravity          = gravity,
                startTemperature = startTemperature,
                contactStiffness = contactStiffness,
            };

            // 形状分类一次定案：包络半径（碰撞）与实体生成共用同一份判定
            byte[] shapes = ClassifyShapes(graph);

            // 实体包络半径（球 = 半径；立方体 = 半对角线；正四面体 = 外接球半径）的
            // "期望值"：供迭代中的硬核接触力与事后碰撞松弛使用；随后还会按局部间隙收缩
            float[] collisionRadii = ComputeCollisionRadii(graph, shapes);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ufield = new ForceDirectedLayout.UniverseField
            {
                universeOf = universes.universeOf,
                centers = universes.centers,
                wallRadii = universes.wallRadii,
                innerRadii = universes.innerRadii,
                anchorOf = universes.anchorOf,
                anchorStiffness = universes.anchorStiffness,
            };
            // 增量布局：与上次构建按路径匹配，命中节点把"相对旧球心的偏移"平移
            // 到新球心作种子（球体打包可能微移）；命中过半才启用
            Vector3[] seeds = universes.seed;
            if (PrepareIncrementalSeeds(graph, universes, ref seeds, ref layout))
            {
                Debug.Log($"[Galaxy] 增量布局：沿用上次位置收敛（迭代降至 {layout.iterations} 轮）");
            }
            Vector3[] positions = ForceDirectedLayout.Simulate(graph, seeds, layout,
                                                               galaxyRadius, collisionRadii, ufield);
            int leftoverCollisions = ForceDirectedLayout.ResolveCollisions(positions, collisionRadii, CollisionRounds);
            // PBD 松弛可能把个别节点推出球壁：先收紧回墙内，再由体积自适应按局部
            // 间隙收缩半径 —— "不出球 + 不穿插"两条硬约束同时成立
            UniverseLayout.ClampIntoWalls(positions, collisionRadii, universes);

            // 体积自适应：按最近邻居间隙收缩包络半径 —— 构造性保证零穿插
            int shrunkCount = ForceDirectedLayout.CapRadiiToLocalClearance(positions, collisionRadii);
            int finalViolations = ForceDirectedLayout.CountViolations(positions, collisionRadii);
            sw.Stop();

            // 保存布局状态（供下次构建的增量复用）：位置/路径/宇宙键/球心
            SaveLayoutState(graph, universes, positions);

            var renderers = new MeshRenderer[graph.nodes.Length];
            EntitySpawnResult spawn = SpawnEntities(graph, positions, collisionRadii, shapes, renderers);

            var linkVerts = new Vector3[graph.links.Length * 2];
            var linkColors = new Color[graph.links.Length * 2];
            Mesh linksMesh = SpawnLinks(graph, positions, linkVerts, linkColors);
            m_LinksMesh = linksMesh;

            // 多元宇宙：每个第一层文件夹一枚大透明"宇宙球"（菲涅尔边缘光环）；
            // 散文件宇宙中心顺带生成黑洞（吞噬光束需要实体位置与色调）
            int universeCount = SpawnUniverses(universes, positions, renderers);

            // 度数统计：出度 = 引用了多少文件，入度 = 被多少文件引用（HUD 展示用）
            var inDegree = new int[graph.nodes.Length];
            var outDegree = new int[graph.nodes.Length];
            foreach (GalaxyLink link in graph.links)
            {
                outDegree[link.source]++;
                inDegree[link.target]++;
            }

            // 自动取景：以星系实际最大半径为基准设定相机距离（2.8 倍 ≈ 画面利用率
            // 与边缘留白之间的平衡点），保证整个星系入画且构图饱满。
            // 宇宙球轮廓比成员位置更外圈，取景半径要一并计入（球体不被裁掉）。
            float maxR = 0f;
            foreach (Vector3 p in positions) maxR = Mathf.Max(maxR, p.magnitude);
            for (int u = 0; u < universes.keys.Length; u++)
            {
                maxR = Mathf.Max(maxR, universes.centers[u].magnitude + universes.meshRadii[u]);
            }

            // 粒子氛围：星系尺度的缓慢星尘（纯视觉装饰，随星系重建）；
            // 数量随文件数增长（大星系更多星尘，参数见 dustBase/dustPerFile）
            var dustGo = new GameObject("Dust");
            dustGo.transform.SetParent(transform, false);
            m_Particles = dustGo.AddComponent<GalaxyParticles>();
            m_Particles.Configure(maxR, GalaxyParticles.DefaultCount(graph.nodes.Length, dustBase, dustPerFile));
            OrbitCamera orbit = FindAnyObjectByType<OrbitCamera>();
            if (orbit != null) orbit.SetOrbit(Vector3.zero, maxR * 2.8f);

            // 拾取交互：把场景数据交给相机上的 GalaxyPicker（点击高亮 + HUD 详情）。
            // 组件通常已由 GalaxySceneSetup 预先挂好；缺失时兜底补挂，保证可用性。
            Camera cam = Camera.main;
            if (cam != null)
            {
                GalaxyPicker picker = cam.GetComponent<GalaxyPicker>();
                if (picker == null) picker = cam.gameObject.AddComponent<GalaxyPicker>();
                picker.Initialize(new GalaxyPicker.SceneData
                {
                    graph = graph,
                    positions = positions,
                    radii = collisionRadii,
                    renderers = renderers,
                    inDegree = inDegree,
                    outDegree = outDegree,
                    linksMesh = linksMesh,
                    linkDefaultColors = linkColors,
                    shapes = shapes,
                    edgeMesh = spawn.edgeMesh,
                    edgeVertexBase = spawn.edgeVertexBase,
                    edgeVertCount = spawn.edgeVertCount,
                    edgeDefaultColors = spawn.edgeDefaultColors,
                }, transform);
            }
            else
            {
                Debug.LogWarning("[Galaxy] 找不到 MainCamera，拾取交互未启用");
            }

            Debug.Log($"[Galaxy] 生成完毕: {graph.nodes.Length} 节点 = {spawn.cubeCount} 立方体 + " +
                      $"{spawn.tetraCount} 正四面体 + {graph.nodes.Length - spawn.cubeCount - spawn.tetraCount} 球体 / " +
                      $"{graph.links.Length} 连线 / {universeCount} 宇宙球 / " +
                      $"侵入(松弛后残留 {leftoverCollisions} → 体积自适应后 {finalViolations}) / " +
                      $"收缩体积 {shrunkCount} 个 / 力导向 {layout.iterations} 轮 {sw.ElapsedMilliseconds} ms / 取景半径 {maxR:F1}");
        }

        // ------------------------------------------------------------------
        // 数据路径解析优先级：
        //   1) Inspector 显式指定；
        //   2) 编辑器：仓库演示数据 scanner/galaxy.json —— **跳过 PlayerPrefs**，
        //      "上次打开的项目"只是构建版用户的特性；编辑器侧 prefs 残留过某次
        //      对话框扫描的路径，会让自动化重跑加载别数据集（实测踩坑）；
        //   3) 构建产物：prefs 上次项目（存在才用）→ StreamingAssets 演示数据。
        // ------------------------------------------------------------------
        private string ResolveJsonPath()
        {
            if (!string.IsNullOrEmpty(jsonPath)) return jsonPath;

#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../../scanner/galaxy.json"));
#else
            string last = PlayerPrefs.GetString("galaxy.lastJson", "");
            if (!string.IsNullOrEmpty(last) && File.Exists(last)) return last;
            return Path.Combine(Application.streamingAssetsPath, "galaxy.json");
#endif
        }

        // ------------------------------------------------------------------
        // 文件实体：头文件 = 透明正方体；源文件 = 透明正四面体；其余 = 自发光球体。
        // 每个实体挂 StarNode（记录数据索引，供将来拾取交互）；实体尺寸对
        // 行数取对数，几千行的大文件不会视觉上吞掉整个星系。
        //
        // 棱线记录：多面体的棱线与主连线同思路并入"一张 Lines 网格"（一次
        // DrawCall），这里同时记录每个节点占用的顶点段（base + count），
        // 供拾取器选中时按节点重着色（金框/增亮/压暗）。
        // ------------------------------------------------------------------
        private struct EntitySpawnResult
        {
            public int cubeCount;
            public int tetraCount;
            public Mesh edgeMesh;               // 多面体棱线网格（可能为 null：无多面体时）
            public Color[] edgeDefaultColors;   // 棱线默认顶点色副本（拾取恢复用）
            public int[] edgeVertexBase;        // 各节点棱线顶点起始下标（-1 = 无棱线）
            public int[] edgeVertCount;         // 各节点棱线顶点数（立方体 24 / 四面体 12）
        }

        private EntitySpawnResult SpawnEntities(GalaxyGraph graph, Vector3[] positions, float[] collisionRadii,
                                                byte[] shapes, MeshRenderer[] renderers)
        {
            var parent = new GameObject("Entities").transform;
            parent.SetParent(transform, false);

            var result = new EntitySpawnResult
            {
                edgeVertexBase = new int[graph.nodes.Length],
                edgeVertCount = new int[graph.nodes.Length],
            };

            var edgeVerts = new List<Vector3>();
            var edgeColors = new List<Color>();
            Mesh tetraMesh = null;   // 全部四面体共享一份网格（差异在 transform 缩放）

            for (int i = 0; i < graph.nodes.Length; i++)
            {
                GalaxyNode node = graph.nodes[i];
                float boundRadius = collisionRadii[i];   // 体积自适应后的包络半径
                result.edgeVertexBase[i] = -1;

                GameObject entity;
                if (shapes[i] == ShapeCube)
                {
                    int baseIndex = edgeVerts.Count;
                    entity = CreateHeaderCube(node, positions[i], boundRadius, edgeVerts, edgeColors);
                    result.edgeVertexBase[i] = baseIndex;
                    result.edgeVertCount[i] = edgeVerts.Count - baseIndex;
                    result.cubeCount++;
                }
                else if (shapes[i] == ShapeTetra)
                {
                    if (tetraMesh == null)
                    {
                        tetraMesh = CreateTetrahedronMesh();
                        m_TetraMesh = tetraMesh;
                    }
                    int baseIndex = edgeVerts.Count;
                    entity = CreateTetrahedron(node, positions[i], boundRadius, edgeVerts, edgeColors, tetraMesh);
                    result.edgeVertexBase[i] = baseIndex;
                    result.edgeVertCount[i] = edgeVerts.Count - baseIndex;
                    result.tetraCount++;
                }
                else
                {
                    entity = CreateSphere(node, boundRadius);
                }

                entity.name = node.name;
                entity.transform.SetParent(parent, false);
                entity.transform.position = positions[i];

                renderers[i] = entity.GetComponent<MeshRenderer>();   // 拾取高亮按实体调色要用
                StarNode sn = entity.AddComponent<StarNode>();
                sn.NodeIndex = i;
                sn.Data = node;
            }

            if (edgeVerts.Count > 0)
            {
                result.edgeMesh = BuildEdgeMesh(parent, edgeVerts, edgeColors);
                result.edgeDefaultColors = edgeColors.ToArray();
                m_EdgeMesh = result.edgeMesh;
            }
            return result;
        }

        private GameObject CreateSphere(GalaxyNode node, float boundRadius)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * (boundRadius * 2f);   // 直径 = 2 × 包络半径

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            // 二进制（查看器不可读）→ 固定红色球标识；其余文本 → 目录色相球
            mr.sharedMaterial = node.kind == "binary" ? GetBinaryMaterial() : GetDirMaterial(node.dir);
            // 不参与任何阴影 pass：自发光 + 少量环境光已足够，省一半渲染开销
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        private GameObject CreateHeaderCube(GalaxyNode node, Vector3 position, float boundRadius,
                                            List<Vector3> edgeVerts, List<Color> edgeColors)
        {
            // 立方体边长 = 包络半径 × 2/√3（半对角线 = 包络半径，与球体的包围球语义一致；
            // 未收缩时等价于原 StarScale × headerCubeSizeFactor 的视觉大小）
            float side = boundRadius * 1.1547005f;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = Vector3.one * side;
            DestroyNow(go.GetComponent<Collider>());   // 实体不需要物理碰撞体

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GetPolyhedronMaterial(node.dir);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // 12 条棱：全部立方体并入一个 Lines 网格（与连线同思路，一次 DrawCall）
            Color edgeColor = Color.HSVToRGB(GalaxyLayout.DirHue(node.dir), 0.85f, 1f);
            edgeColor.a = polyhedronEdgeAlpha;
            AppendCubeEdges(edgeVerts, edgeColors, position, side * 0.5f, edgeColor);

            return go;
        }

        // 源文件实体：透明正四面体（与立方体同一套透明材质/棱线美学）
        private GameObject CreateTetrahedron(GalaxyNode node, Vector3 position, float boundRadius,
                                             List<Vector3> edgeVerts, List<Color> edgeColors, Mesh sharedMesh)
        {
            var go = new GameObject("tetra");   // 外层循环统一改名，这里只占位
            // 单位网格的外接球半径为 1，缩放即包络半径（与球体半径语义一致）
            go.transform.localScale = Vector3.one * boundRadius;

            go.AddComponent<MeshFilter>().sharedMesh = sharedMesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetPolyhedronMaterial(node.dir);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // 6 条棱：4 顶点两两相连，色相与立方体棱线一致（并入同一张 Lines 网格）
            Color edgeColor = Color.HSVToRGB(GalaxyLayout.DirHue(node.dir), 0.85f, 1f);
            edgeColor.a = polyhedronEdgeAlpha;
            Vector3[] v = TetraLocalVerts(boundRadius);
            AppendEdge(edgeVerts, edgeColors, position, v[0], v[1], edgeColor);
            AppendEdge(edgeVerts, edgeColors, position, v[0], v[2], edgeColor);
            AppendEdge(edgeVerts, edgeColors, position, v[0], v[3], edgeColor);
            AppendEdge(edgeVerts, edgeColors, position, v[1], v[2], edgeColor);
            AppendEdge(edgeVerts, edgeColors, position, v[1], v[3], edgeColor);
            AppendEdge(edgeVerts, edgeColors, position, v[2], v[3], edgeColor);

            return go;
        }

        // 正四面体 4 顶点：坐标取 (±1,±1,±1) 中"符号乘积为 +1"的交替组合，
        // 归一化到外接球半径 1 后按 scale 缩放（正四面体外接球 = 包络球）
        private static Vector3[] TetraLocalVerts(float scale)
        {
            float s = scale / Mathf.Sqrt(3f);
            return new[]
            {
                new Vector3( s,  s,  s),
                new Vector3( s, -s, -s),
                new Vector3(-s,  s, -s),
                new Vector3(-s, -s,  s),
            };
        }

        // 正四面体共享网格：每个面独立顶点（12 顶点 / 4 面）→ 平面着色，棱面
        // 分明的"晶体"感。缠绕方向不靠纸面推导约定：先按候选缠绕建面，用
        // Unity 自己的 RecalculateNormals 反推每个面法线，发现朝内（与外接球
        // 心同侧的半空间不符）就把该面反转缠绕重算 —— 以引擎实现为唯一事实
        // 来源，绕开"顺时针/逆时针"记忆出错导致面朝内的风险。
        private static Mesh CreateTetrahedronMesh()
        {
            Vector3[] v = TetraLocalVerts(1f);
            int[][] faces = { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 0, 3, 2 }, new[] { 1, 2, 3 } };

            var verts = new Vector3[12];
            var tris = new int[12];
            for (int f = 0; f < 4; f++)
            {
                verts[f * 3]     = v[faces[f][0]];
                verts[f * 3 + 1] = v[faces[f][1]];
                verts[f * 3 + 2] = v[faces[f][2]];
                tris[f * 3] = f * 3; tris[f * 3 + 1] = f * 3 + 1; tris[f * 3 + 2] = f * 3 + 2;
            }

            var mesh = new Mesh { name = "galaxy-tetra" };
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();

            // 外接球心在原点：面法线与面心点积为负 = 该面朝内 → 反转该面缠绕
            Vector3[] normals = mesh.normals;
            bool flipped = false;
            for (int f = 0; f < 4; f++)
            {
                Vector3 centroid = (verts[f * 3] + verts[f * 3 + 1] + verts[f * 3 + 2]) / 3f;
                if (Vector3.Dot(normals[f * 3], centroid) < 0f)
                {
                    int t = tris[f * 3]; tris[f * 3] = tris[f * 3 + 1]; tris[f * 3 + 1] = t;
                    flipped = true;
                }
            }
            if (flipped)
            {
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float StarScale(int lines)
        {
            return 0.27f + Mathf.Log10(lines + 1f) * 0.25f;
        }

        // 实体尺寸统一入口：文本按行数对数；二进制按字节数对数（lines 恒为 0，
        // 用行数公式会让图片/固件全渲染成最小颗粒，分不出体积）。二进制系数
        // 0.13 使 ~200KB 资源与该尺寸的中等源码文件视觉重量相当。
        private static float EntityScale(GalaxyNode node)
        {
            return node.kind == "binary"
                ? 0.27f + Mathf.Log10(node.bytes + 1f) * 0.13f
                : StarScale(node.lines);
        }

        // 形状分类：头文件 = 立方体、源文件 = 正四面体、其余 = 球体
        private static byte[] ClassifyShapes(GalaxyGraph graph)
        {
            var shapes = new byte[graph.nodes.Length];
            for (int i = 0; i < shapes.Length; i++)
            {
                string path = graph.nodes[i].path;
                shapes[i] = IsHeaderFile(path) ? ShapeCube
                          : IsSourceFile(path) ? ShapeTetra
                          : ShapeSphere;
            }
            return shapes;
        }

        // 头文件判定：.h/.hpp/.hh/.hxx（大小写不敏感）
        private static bool IsHeaderFile(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            return ext == ".h" || ext == ".hpp" || ext == ".hh" || ext == ".hxx";
        }

        // 源文件判定：.c/.cc/.cpp/.cxx（大小写不敏感）
        private static bool IsSourceFile(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            return ext == ".c" || ext == ".cc" || ext == ".cpp" || ext == ".cxx";
        }

        // 实体包络半径：球体/正四面体 = 半径/外接球半径；立方体 = 半对角线
        // （√3/2 × 边长）。用包络球做无干涉判定：两包络球不重叠 => 实体必不穿插
        // （保守但简单可靠）。
        private float[] ComputeCollisionRadii(GalaxyGraph graph, byte[] shapes)
        {
            var radii = new float[graph.nodes.Length];
            for (int i = 0; i < graph.nodes.Length; i++)
            {
                GalaxyNode node = graph.nodes[i];
                float scale = EntityScale(node);
                radii[i] = shapes[i] == ShapeCube
                    ? 0.8660254f * scale * headerCubeSizeFactor
                    : 0.5f * scale;
            }
            return radii;
        }

        // 每个目录一份共享材质：只占一份显存、只有一次状态切换。
        // （C++ 类比：共享只读资源，而不是每个对象各持一份副本。）
        // 构建产物里优先从"不透明模板资产"实例化 —— 保住 URP/Lit 的不透明+自发光
        // 变体（shader_feature 变体只被构建时引用的材质保留，模板放 Resources 随包）
        private Material GetDirMaterial(string dir)
        {
            if (m_DirMaterials.TryGetValue(dir, out Material cached)) return cached;

            float hue = GalaxyLayout.DirHue(dir);
            Color baseColor = Color.HSVToRGB(hue, 0.8f, 1f);

            Material template = Resources.Load<Material>("GalaxyLitOpaque");
            var mat = template != null
                ? new Material(template)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", baseColor * 0.35f);                  // 本体压暗，靠自发光发亮
            mat.SetColor("_EmissionColor", baseColor * emissionIntensity);  // HDR -> 被 Bloom 拾取
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;  // 纯实时，不进 GI 烘焙
            mat.SetFloat("_Smoothness", 0.15f);
            mat.SetFloat("_Metallic", 0f);

            m_DirMaterials[dir] = mat;
            return mat;
        }

        // 二进制文件的固定红色球材质（"查看器不可读"的视觉标识）；与目录球同一
        // 造型参数（不透明+自发光），只换色 —— 压暗/点亮逻辑对两者一致工作。
        private Material GetBinaryMaterial()
        {
            if (m_BinaryMaterial != null) return m_BinaryMaterial;

            var red = new Color(1f, 0.22f, 0.20f);
            Material template = Resources.Load<Material>("GalaxyLitOpaque");
            Material mat = template != null
                ? new Material(template)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetColor("_BaseColor", red * 0.35f);
            mat.SetColor("_EmissionColor", red * emissionIntensity);
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetFloat("_Smoothness", 0.15f);
            mat.SetFloat("_Metallic", 0f);

            m_BinaryMaterial = mat;
            return mat;
        }

        // 透明多面体（头文件立方体 / 源文件正四面体）的填充材质
        // （URP Lit 透明模式，同目录色相）
        private Material GetPolyhedronMaterial(string dir)
        {
            if (m_PolyhedronMaterials.TryGetValue(dir, out Material cached)) return cached;

            float hue = GalaxyLayout.DirHue(dir);
            Color c = Color.HSVToRGB(hue, 0.85f, 1f);

            // 优先从"透明模板资产"实例化（Resources 随包，构建脚本创建）：
            // _SURFACE_TYPE_TRANSPARENT 这类 shader_feature 变体只被"构建时被
            // 引用的材质"保留 —— 材质纯运行时创建时构建产物只剩不透明变体，
            // 实体在应用里渲染成实心（实测踩坑）。模板缺失（首次构建前的编辑器
            // 会话）才退回手工配方 —— 编辑器里全量变体可查，两者行为一致。
            Material template = Resources.Load<Material>("GalaxyLitTransparent");
            Material mat = template != null ? new Material(template) : CreateTransparentLitMaterial();

            mat.SetColor("_BaseColor", new Color(c.r, c.g, c.b, polyhedronFillAlpha));
            mat.SetColor("_EmissionColor", c * 0.6f);         // 微自发光：暗场景里仍可辨识
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            mat.SetFloat("_Smoothness", 0.2f);
            mat.SetFloat("_Metallic", 0f);

            m_PolyhedronMaterials[dir] = mat;
            return mat;
        }

        // URP Lit 切透明模式的手工配方：surface 标记 + 混合状态 + 关键字 + 渲染
        // 队列，四者缺一不可（只改 _Surface 不会真正混合；官方 MaterialUpgrader
        // 同款配方）。编辑器兜底路径；构建走 GalaxyLitTransparent 模板资产。
        private static Material CreateTransparentLitMaterial()
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.SetFloat("_Surface", 1f);                     // 0 = Opaque, 1 = Transparent
            mat.SetFloat("_Blend", 0f);                       // Alpha 混合
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
            return mat;
        }

        // ------------------------------------------------------------------
        // 依赖连线：把所有边烘焙进"一个 Mesh"，一次 DrawCall 画完。
        // 对比方案 LineRenderer：每条边一个 GameObject + 一次 DrawCall，
        // 4592 条边就是 4592 次 DrawCall —— 直接压垮渲染线程。
        // MeshTopology.Lines 的代价是线宽固定 1px；"能量流动"需要的粗线 +
        // 流动动画（顶点加宽成 ribbon + UV 滚动）留给视觉后处理阶段。
        // ------------------------------------------------------------------
        private Mesh SpawnLinks(GalaxyGraph graph, Vector3[] positions, Vector3[] vertices, Color[] colors)
        {
            int linkCount = graph.links.Length;

            for (int i = 0; i < linkCount; i++)
            {
                GalaxyLink link = graph.links[i];
                vertices[i * 2] = positions[link.source];
                vertices[i * 2 + 1] = positions[link.target];
                colors[i * 2] = linkColorSource;    // 发出依赖的一端亮
                colors[i * 2 + 1] = linkColorTarget; // 被依赖的一端暗
            }

            var indices = new int[vertices.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;

            var mesh = new Mesh { name = "galaxy-links" };
            // 顶点/索引可能超过 65k：显式切 32 位索引，绕开默认的 16 位上限
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.MarkDynamic();   // 拾取高亮会反复改写顶点色（几何位置不动）
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var go = new GameObject("Links");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            // Sprites/Default：随管线始终可用、且会把顶点色乘进结果的 unlit shader。
            // 连线的渐变/透明度正是靠顶点色传递的。
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mesh;   // 拾取器持有引用：选择时重建顶点色（位置不变）
        }

        // ------------------------------------------------------------------
        // 宇宙球生成：球心/半径来自 UniverseLayout（互不干涉、半径 ∝ 文件数³√，
        // 成员已被约束在球内散开）。球体对拾取不可见（GalaxyPicker 只认实体）。
        // 深度排序：材质渲染队列排在实体之前（先画球、后画内容）→ 内容永远
        // 叠在球面之上，规避同队列大透明球排序跳变的闪烁。
        // ------------------------------------------------------------------
        private int SpawnUniverses(UniverseLayout.Result universes, Vector3[] positions,
                                   MeshRenderer[] renderers)
        {
            var parent = new GameObject("Universes").transform;
            parent.SetParent(transform, false);

            var report = new System.Text.StringBuilder("[Galaxy] 宇宙球:");
            for (int u = 0; u < universes.keys.Length; u++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = universes.keys[u] == UniverseLayout.ScatteredKey
                    ? "Universe_scattered"
                    : "Universe_" + universes.keys[u];
                DestroyNow(go.GetComponent<Collider>());
                go.transform.SetParent(parent, false);
                go.transform.position = universes.centers[u];
                go.transform.localScale = Vector3.one * (universes.meshRadii[u] * 2f);   // 内置球网格直径 1

                MeshRenderer mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = GetUniverseMaterial(universes.keys[u]);
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                CreateUniverseLabel(parent, universes.keys[u], universes.centers[u], universes.meshRadii[u]);

                // 黑洞：散文件宇宙专属（核心 + 动态吸积环；实体已被布局的"内核
                // 排斥"挡在包络外 —— 不干涉由几何约束保证）
                if (universes.keys[u] == UniverseLayout.ScatteredKey && universes.innerRadii[u] > 0f)
                {
                    SpawnBlackHole(parent, universes.centers[u],
                                   universes.innerRadii[u] / UniverseLayout.BlackHoleEnvelopeFactor,
                                   universes, positions, renderers, u);
                }

                report.Append($" {universes.keys[u]}({universes.counts[u]}) 半径{universes.meshRadii[u]:F1} " +
                              $"心距{universes.centers[u].magnitude:F1}");
                if (universes.innerRadii[u] > 0f)
                    report.Append($" 黑洞包络{universes.innerRadii[u]:F1}");
                report.Append(" |");
            }
            Debug.Log(report.ToString());
            return universes.keys.Length;
        }

        // 黑洞生成：挂在宇宙球父节点下（与球体/名碑同生命周期）。
        // 顺带收集本宇宙全部实体作为"吞噬光束"的供体（位置 + 实体色调）。
        private static void SpawnBlackHole(Transform parent, Vector3 center, float coreRadius,
                                           UniverseLayout.Result universes, Vector3[] positions,
                                           MeshRenderer[] renderers, int universeIndex)
        {
            if (coreRadius <= 0.01f) return;

            var feederPositions = new List<Vector3>();
            var feederColors = new List<Color>();
            for (int i = 0; i < universes.universeOf.Length; i++)
            {
                if (universes.universeOf[i] != universeIndex) continue;
                feederPositions.Add(positions[i]);
                Color c = Color.white;
                MeshRenderer r = renderers[i];
                if (r != null && r.sharedMaterial != null)
                {
                    Color em = r.sharedMaterial.GetColor("_EmissionColor");
                    if (em.maxColorComponent > 0.01f) c = em;
                }
                feederColors.Add(c);
            }

            var go = new GameObject("BlackHole");
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.AddComponent<BlackHole>().Build(coreRadius, UniverseTint(UniverseLayout.ScatteredKey),
                                               feederPositions, feederColors);
        }

        // 宇宙名碑：球壳外侧悬浮文件夹名（billboard 每帧面向相机；色相 = 球壳色调）。
        // 文字世界高度跨宇宙统一（2.0）；亮度刻意压低（"要不明显"：色相保持一致，
        // 只降明度 + 略降透明度），位置在球顶上方 meshRadius + 2.2 处。
        private const float LabelWorldHeight = 2.0f;

        private void CreateUniverseLabel(Transform parent, string key, Vector3 center, float meshRadius)
        {
            string display = key == UniverseLayout.ScatteredKey ? "scattered" : key;

            var go = new GameObject("Label_" + display);
            go.transform.SetParent(parent, false);
            go.transform.position = center + Vector3.up * (meshRadius + 2.2f);

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = display;
            tm.font = GalaxyHud.GetFont();
            tm.fontSize = 80;           // 图集分辨率（大 → 字形清晰）
            tm.characterSize = 0.05f;   // 先给初值，下面按实测包围盒归一
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            // 名碑亮度：球壳色调降明度（×0.65）+ 略降透明度 —— "看得到但不明显"
            Color tint = UniverseTint(key);
            Color labelColor = new Color(tint.r * 0.65f, tint.g * 0.65f, tint.b * 0.65f, 0.85f);
            tm.color = labelColor;
            m_LabelTexts.Add(tm);
            m_LabelBaseColors.Add(labelColor);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            Material fontMat = tm.font != null ? tm.font.material : null;
            if (fontMat != null) mr.sharedMaterial = fontMat;
            else Debug.LogWarning("[Galaxy] 宇宙名碑字体材质缺失（文字可能不可见）");
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // 世界高度归一：TextMesh 实际字高随字体度量而变，实测包围盒后反推
            // characterSize —— 所有名碑高度一致，不必猜字体参数
            float h = mr.bounds.size.y;
            if (h > 1e-4f) tm.characterSize *= LabelWorldHeight / h;

            go.AddComponent<UniverseLabel>();   // billboard（每帧面向相机）
        }

        // 宇宙色调：球壳材质与名碑共用的唯一事实来源
        // （降饱和 + 降明度：外壳颜色刻意暗淡 —— 用户要求"颜色和线条都不能亮"；
        //   散文件宇宙 = 固定紫色调，与文件夹宇宙一眼可辨）
        private static Color UniverseTint(string key)
        {
            return key == UniverseLayout.ScatteredKey
                ? Color.HSVToRGB(0.78f, 0.45f, 0.78f)
                : Color.HSVToRGB(GalaxyLayout.DirHue(key), 0.55f, 0.78f);
        }

        // 每个宇宙一份材质实例：文件夹宇宙 = 文件夹名哈希色相 + Inspector 菲涅尔参数；
        // 散文件宇宙 = 更实的球体 + 更宽软的光环（一眼可辨的区分）
        private Material GetUniverseMaterial(string key)
        {
            if (m_UniverseMaterials.TryGetValue(key, out Material cached)) return cached;

            Material mat;
            if (key == UniverseLayout.ScatteredKey)
            {
                mat = CreateBubbleMaterial(UniverseTint(key), universeBaseAlpha * 2.5f, universeRimAlpha * 0.9f, 2.0f);
            }
            else
            {
                mat = CreateBubbleMaterial(UniverseTint(key), universeBaseAlpha, universeRimAlpha, universeRimPower);
            }
            m_UniverseMaterials[key] = mat;
            return mat;
        }

        private static Material CreateBubbleMaterial(Color c, float baseAlpha, float rimAlpha, float rimPower)
        {
            Shader shader = Shader.Find("Galaxy/Bubble");
            Material mat;
            if (shader != null)
            {
                mat = new Material(shader);
                mat.SetColor("_Color", c);
                mat.SetFloat("_BaseAlpha", baseAlpha);
                mat.SetFloat("_RimAlpha", rimAlpha);
                mat.SetFloat("_RimPower", rimPower);
            }
            else
            {
                // 兜底：Sprites/Default（均匀淡色），仅保证"有球可见"
                Debug.LogWarning("[Galaxy] 未找到 Galaxy/Bubble，宇宙球退回 Sprites/Default");
                mat = new Material(Shader.Find("Sprites/Default"));
                mat.SetColor("_Color", new Color(c.r, c.g, c.b, baseAlpha + rimAlpha * 0.4f));
            }
            mat.renderQueue = (int)RenderQueue.Transparent - 30;   // 先于实体绘制
            return mat;
        }

        // ------------------------------------------------------------------
        // 立方体棱线：位运算枚举 12 条棱（corner 的 bit0/1/2 = x/y/z 正负号），
        // 全部立方体的棱并入一个 Lines 网格，与连线同思路一次 DrawCall。
        // ------------------------------------------------------------------
        private static void AppendCubeEdges(List<Vector3> verts, List<Color> colors,
                                            Vector3 center, float half, Color color)
        {
            for (int axis = 0; axis < 3; axis++)
            for (int corner = 0; corner < 8; corner++)
            {
                if (((corner >> axis) & 1) != 0) continue;   // 每条棱只生成一次
                int other = corner | (1 << axis);
                verts.Add(center + CornerOffset(corner, half));
                verts.Add(center + CornerOffset(other, half));
                colors.Add(color);
                colors.Add(color);
            }
        }

        // corner 的 bit0/1/2 -> x/y/z 的正负号（0 = 负，1 = 正）
        private static Vector3 CornerOffset(int corner, float half)
        {
            return new Vector3(
                ((corner & 1) != 0) ? half : -half,
                ((corner & 2) != 0) ? half : -half,
                ((corner & 4) != 0) ? half : -half);
        }

        // 追加一条棱（两端点 + 同色），供四面体使用（立方体走 AppendCubeEdges 的位枚举）
        private static void AppendEdge(List<Vector3> verts, List<Color> colors,
                                       Vector3 center, Vector3 a, Vector3 b, Color color)
        {
            verts.Add(center + a); colors.Add(color);
            verts.Add(center + b); colors.Add(color);
        }

        private static Mesh BuildEdgeMesh(Transform parent, List<Vector3> verts, List<Color> colors)
        {
            var mesh = new Mesh { name = "polyhedron-edges" };
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.MarkDynamic();   // 拾取高亮会按节点改写顶点色（几何位置不动）
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            var indices = new int[verts.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var go = new GameObject("PolyhedronEdges");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mesh;   // 拾取器持有引用：选择时重建顶点色
        }

        // ---- 增量布局：状态保存与种子重建 ----

        private void SaveLayoutState(GalaxyGraph graph, UniverseLayout.Result universes, Vector3[] positions)
        {
            m_LastPositions = positions;
            int n = graph.nodes.Length;
            if (m_LastPaths == null || m_LastPaths.Length != n) m_LastPaths = new string[n];
            if (m_LastUniverseKeys == null || m_LastUniverseKeys.Length != n) m_LastUniverseKeys = new string[n];
            for (int i = 0; i < n; i++)
            {
                m_LastPaths[i] = graph.nodes[i].path;
                m_LastUniverseKeys[i] = universes.keys[universes.universeOf[i]];
            }
            m_LastCentersByKey.Clear();
            for (int u = 0; u < universes.keys.Length; u++)
            {
                m_LastCentersByKey[universes.keys[u]] = universes.centers[u];
            }
        }

        // 命中过半才启用增量（否则视作换了项目，走全量）；命中节点的种子 =
        // 新球心 + (旧位置 − 旧球心)。自动化每次全新 Play 无历史状态，
        // 恒走全量路径——确定性不受影响。
        private bool PrepareIncrementalSeeds(GalaxyGraph graph, UniverseLayout.Result universes,
                                             ref Vector3[] seeds, ref ForceDirectedLayout.Params layout)
        {
            if (m_LastPositions == null || m_LastPaths == null) return false;

            var oldIndexByPath = new Dictionary<string, int>(m_LastPaths.Length);
            for (int i = 0; i < m_LastPaths.Length; i++) oldIndexByPath[m_LastPaths[i]] = i;

            var newCentersByKey = new Dictionary<string, Vector3>(universes.keys.Length);
            for (int u = 0; u < universes.keys.Length; u++)
                newCentersByKey[universes.keys[u]] = universes.centers[u];

            var candidate = (Vector3[])seeds.Clone();
            int matched = 0;
            for (int i = 0; i < graph.nodes.Length; i++)
            {
                if (!oldIndexByPath.TryGetValue(graph.nodes[i].path, out int old)) continue;
                if (old >= m_LastPositions.Length || old >= m_LastUniverseKeys.Length) continue;
                string oldKey = m_LastUniverseKeys[old];
                if (!m_LastCentersByKey.TryGetValue(oldKey, out Vector3 oldCenter)) continue;
                if (!newCentersByKey.TryGetValue(oldKey, out Vector3 newCenter)) continue;
                candidate[i] = newCenter + (m_LastPositions[old] - oldCenter);
                matched++;
            }

            if (matched < 16 || matched * 2 < graph.nodes.Length) return false;

            seeds = candidate;
            layout.iterations = Mathf.Min(layout.iterations, 90);
            layout.startTemperature = Mathf.Min(layout.startTemperature, 1.5f);
            return true;
        }

        // ---- 运行时外观调节（设置面板调用）：只改材质/颜色实例，不动字段原值 ----

        /// <summary>宇宙球壳亮度（散文件宇宙沿用派生风格：更实的底 + 更弱的光环）。</summary>
        public void ApplyUniverseAppearance(float baseAlpha, float rimAlpha)
        {
            foreach (KeyValuePair<string, Material> pair in m_UniverseMaterials)
            {
                Material mat = pair.Value;
                if (mat == null) continue;
                bool scattered = pair.Key == UniverseLayout.ScatteredKey;
                mat.SetFloat("_BaseAlpha", scattered ? baseAlpha * 2.5f : baseAlpha);
                mat.SetFloat("_RimAlpha", scattered ? rimAlpha * 0.9f : rimAlpha);
            }
        }

        /// <summary>名碑亮度系数（基准色 × 系数）。</summary>
        public void ApplyLabelBrightness(float factor)
        {
            for (int i = 0; i < m_LabelTexts.Count; i++)
            {
                if (m_LabelTexts[i] == null) continue;
                Color c = m_LabelBaseColors[i];
                m_LabelTexts[i].color = new Color(c.r * factor, c.g * factor, c.b * factor, c.a);
            }
        }

        // Destroy 是运行时 API（延迟到帧末销毁）；编辑模式必须用 DestroyImmediate。
        // 统一走这个薄封装，保证两种模式下 Rebuild 行为一致。
        private static void DestroyNow(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}
