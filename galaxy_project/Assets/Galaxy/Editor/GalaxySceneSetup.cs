// ============================================================================
// 场景装配工具：给 SampleScene 装上 Galaxy Builder + Orbit Camera + 深空底色。
//
// 两种用法：
//   1. 编辑器菜单  Galaxy -> Setup Scene
//   2. 命令行无人值守：
//      Unity.exe -batchmode -quit -projectPath <proj> \
//        -executeMethod Galaxy.EditorTools.GalaxySceneSetup.SetupAndVerify
//      （SetupAndVerify 额外跑一遍"加载 JSON + 布局计算"，用退出码报告结果）
// ============================================================================
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Galaxy.EditorTools
{
    public static class GalaxySceneSetup
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        [MenuItem("Galaxy/Setup Scene")]
        public static void Setup()
        {
            SetupInternal(interactive: true);
        }

        // interactive=false 为静默路径（自动化/引导注入用）：不弹任何保存对话框。
        internal static void SetupInternal(bool interactive)
        {
            if (interactive)
            {
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            }
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 1) 星系根物体 + GalaxyBuilder
            GameObject galaxy = GameObject.Find("Galaxy");
            if (galaxy == null) galaxy = new GameObject("Galaxy");
            galaxy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            GalaxyBuilder builder = galaxy.GetComponent<GalaxyBuilder>();
            if (builder == null) builder = galaxy.AddComponent<GalaxyBuilder>();
            // 场景实例的参数是序列化存储的：改 C# 默认值不会影响已存在的实例。
            // 每次装配时统一同步为"代码默认值"，让代码成为唯一事实来源。
            SyncBuilderDefaults(builder);
            // 非交互装配（自动化/引导）：把 jsonPath 钉死为空 —— 解析链回落
            // 到"编辑器→仓库演示数据"，避免场景里残留的路径（如某次编辑器内
            // 对话框扫描写回的 jsonPath）让自动化跑在别的数据集上（实测踩坑）
            if (!interactive) builder.jsonPath = "";

            // 2) 主相机 + OrbitCamera + 深空底色
            Camera cam = Camera.main;
            if (cam != null)
            {
                if (cam.GetComponent<OrbitCamera>() == null)
                {
                    cam.gameObject.AddComponent<OrbitCamera>();
                }
                if (cam.GetComponent<GalaxyPicker>() == null)
                {
                    cam.gameObject.AddComponent<GalaxyPicker>();   // 拾取交互：点击高亮 + HUD
                }
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.02f, 0.03f, 0.06f, 1f);  // ≈ #050810
            }
            else
            {
                Debug.LogWarning("[GalaxySetup] 场景里找不到 MainCamera（tag 必须是 MainCamera）");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[GalaxySetup] 场景已装配并保存: Galaxy Builder + Orbit Camera + 深空底色");
        }

        // 读取"代码默认值"的小技巧：临时挂一个组件实例 —— 其字段初值就是代码里的
        // 默认值，拷回场景实例后销毁探针对象（编辑模式用 DestroyImmediate）。
        // 例外：jsonPath 是用户数据源配置，保留场景里的现值。
        private static void SyncBuilderDefaults(GalaxyBuilder target)
        {
            GameObject probeGo = new GameObject("~galaxy-probe");
            try
            {
                GalaxyBuilder probe = probeGo.AddComponent<GalaxyBuilder>();
                target.galaxyRadius         = probe.galaxyRadius;
                target.emissionIntensity    = probe.emissionIntensity;
                target.linkColorSource      = probe.linkColorSource;
                target.linkColorTarget      = probe.linkColorTarget;
                target.layoutIterations     = probe.layoutIterations;
                target.idealDistance        = probe.idealDistance;
                target.repulsionStrength    = probe.repulsionStrength;
                target.springStiffness      = probe.springStiffness;
                target.dirCohesion          = probe.dirCohesion;
                target.dirLevelGain         = probe.dirLevelGain;
                target.gravity              = probe.gravity;
                target.startTemperature     = probe.startTemperature;
                target.contactStiffness     = probe.contactStiffness;
                target.headerCubeSizeFactor = probe.headerCubeSizeFactor;
                target.polyhedronFillAlpha  = probe.polyhedronFillAlpha;
                target.polyhedronEdgeAlpha  = probe.polyhedronEdgeAlpha;
                target.universeRadiusScale  = probe.universeRadiusScale;
                target.universeMinRadius    = probe.universeMinRadius;
                target.universeGap          = probe.universeGap;
                target.universeWallFrac     = probe.universeWallFrac;
                target.scatteredRadiusScale = probe.scatteredRadiusScale;
                target.blackHoleRadiusScale = probe.blackHoleRadiusScale;
                target.universeBaseAlpha    = probe.universeBaseAlpha;
                target.universeRimAlpha     = probe.universeRimAlpha;
                target.universeRimPower     = probe.universeRimPower;
                target.dustBase             = probe.dustBase;
                target.dustPerFile          = probe.dustPerFile;
            }
            finally
            {
                Object.DestroyImmediate(probeGo);
            }
        }

        // ---- 命令行入口：装配 + 数据管线验证（不需要进 Play 模式）----
        public static void SetupAndVerify()
        {
            SetupInternal(interactive: false);

            // 直接调用与 Play 时完全相同的加载/布局代码路径
            string jsonPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../scanner/galaxy.json"));
            GalaxyGraph graph = GalaxyLoader.Load(jsonPath);
            if (graph == null)
            {
                Debug.LogError("[GalaxySetup] 数据加载失败");
                EditorApplication.Exit(1);
                return;
            }

            Vector3[] positions = GalaxyLayout.Compute(graph, 25f);
            float maxR = 0f;
            foreach (Vector3 p in positions) maxR = Mathf.Max(maxR, p.magnitude);
            Debug.Log($"[GalaxySetup] 数据管线 OK: {graph.nodes.Length} 节点 / {graph.links.Length} 边 / " +
                      $"最远星体半径 {maxR:F1} / 首节点位置 ({positions[0].x:F1}, {positions[0].y:F1}, {positions[0].z:F1})");

            EditorApplication.Exit(0);
        }
    }
}
