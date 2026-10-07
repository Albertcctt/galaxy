// ============================================================================
// 桌面构建（一次性引导，build-rev 9）：检出 %TEMP%\galaxy.build 标记后，设置播放器参数并
// 调 BuildPipeline 产出 Windows 独立应用 —— 与 GalaxyBootstrap 同一套"标记
// 文件 + 域重载触发"的注入机制（避免对已打开的编辑器起第二个实例抢项目锁）。
//
// 输出：仓库根/dist/Galaxy/Galaxy.exe（含 Data 目录；StreamingAssets 里的
// scanner 与演示数据自动随包）。
//
// 注意：BuildPipeline 是同步调用，构建期间编辑器主线程被占用（有进度条）；
// 完成后日志输出 [GalaxyBuild] 结果行（供自动化流水线 grep）。
// ============================================================================
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Galaxy.EditorTools
{
    public static class GalaxyBuildPlayer
    {
        private static readonly string MarkerPath = Path.Combine(Path.GetTempPath(), "galaxy.build");
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";

        [InitializeOnLoadMethod]
        private static void MaybeBuild()
        {
            if (!File.Exists(MarkerPath)) return;
            File.Delete(MarkerPath);   // 先删：无论成败都只执行一次

            EditorApplication.delayCall += RunWhenInEditMode;
        }

        // 构建必须在编辑模式（Play 中调用 BuildPlayer 会抛异常）；重载后若还在
        // Play（脚本变更时"停播再编译"策略下一般不会），先退出再轮询
        private static void RunWhenInEditMode()
        {
            if (!EditorApplication.isPlaying)
            {
                Run();
                return;
            }
            EditorApplication.ExitPlaymode();
            EditorApplication.update -= TickWaitEditMode;
            EditorApplication.update += TickWaitEditMode;
        }

        private static void TickWaitEditMode()
        {
            if (EditorApplication.isPlaying) return;
            EditorApplication.update -= TickWaitEditMode;
            Run();
        }

        private static void Run()
        {
            try
            {
                // 仓库根：Assets 上两级（编辑器上下文）
                string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
                string outDir = Path.Combine(repoRoot, "dist", "Galaxy");
                string outExe = Path.Combine(outDir, "Galaxy.exe");
                Directory.CreateDirectory(outDir);

                // 播放器参数：窗口化桌面应用（1600×900 可调大小，失焦继续跑）
                PlayerSettings.companyName = "Galaxy";
                PlayerSettings.productName = "Galaxy";
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 1600;
                PlayerSettings.defaultScreenHeight = 900;
                PlayerSettings.resizableWindow = true;
                PlayerSettings.runInBackground = true;

                // 场景清单：只含 SampleScene（幂等设置）
                EditorBuildSettings.scenes = new[]
                {
                    new EditorBuildSettingsScene(ScenePath, true),
                };

                // 应用图标（Assets/ai-icon.png，来自 asset/ai.jpg 的正方形裁剪）
                EnsureAppIcon();

                // 随包资产自检：scanner 与演示数据必须已放进 StreamingAssets
                string streamTools = Path.Combine(Application.dataPath, "StreamingAssets/tools/galaxy-scan.exe");
                string streamJson = Path.Combine(Application.dataPath, "StreamingAssets/galaxy.json");
                if (!File.Exists(streamTools) || !File.Exists(streamJson))
                {
                    Debug.LogError($"[GalaxyBuild] StreamingAssets 缺失: scanner={File.Exists(streamTools)} json={File.Exists(streamJson)}");
                }

                // 运行时 Shader.Find 的 shader 必须进"始终包含"清单 —— 构建产物
                // 没有编辑器里的全量 shader 库，仅被 Shader.Find 使用的 shader 会
                // 在剥离阶段丢失（实测：构建版 GetPolyhedronMaterial 拿到 null
                // shader 直接抛异常）
                EnsureAlwaysIncludedShaders(
                    "Universal Render Pipeline/Lit",
                    "Sprites/Default",
                    "Galaxy/Bubble",
                    "Galaxy/OverlayLine");

                // 模板材质资产（透明 + 不透明两份）：shader_feature 变体（如
                // _SURFACE_TYPE_TRANSPARENT、_EMISSION）只被"构建内实际引用的
                // 材质"保留 —— 材质全在运行时创建时，构建产物只剩默认变体：
                // 透明实体渲染成实心、球体失去自发光（实测踩坑）。模板放
                // Resources 随包，运行时从它实例化（GalaxyBuilder）
                EnsureTemplateMaterials();

                Debug.Log($"[GalaxyBuild] 开始构建 -> {outExe}");
                BuildReport report = BuildPipeline.BuildPlayer(
                    new[] { ScenePath }, outExe, BuildTarget.StandaloneWindows64, BuildOptions.None);

                BuildSummary sum = report.summary;
                Debug.Log($"[GalaxyBuild] 结果: {sum.result} / 大小 {sum.totalSize / (1024 * 1024)} MB / " +
                          $"输出 {sum.outputPath} / 错误 {sum.totalErrors} 警告 {sum.totalWarnings}");

                if (sum.result != BuildResult.Succeeded)
                {
                    foreach (BuildStep step in report.steps)
                    foreach (BuildStepMessage msg in step.messages)
                    {
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"[GalaxyBuild] {msg.content}");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GalaxyBuild] 构建异常: {e}");
            }
        }

        // 应用图标：同一张源图铺满 Standalone 的全部图标尺寸槽，构建时由 Unity
        // 自动缩放到各尺寸（exe/任务栏/窗口角标都用它）。导入设置先校正：
        // Sprite 类型 + 可读（图标缩放需要）+ 关 mipmap。
        private static void EnsureAppIcon()
        {
            const string iconPath = "Assets/ai-icon.png";
            var importer = AssetImporter.GetAtPath(iconPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[GalaxyBuild] 找不到图标资源: {iconPath}（跳过图标设置）");
                return;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
            if (!importer.isReadable) { importer.isReadable = true; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; dirty = true; }
            if (dirty) importer.SaveAndReimport();

            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
            if (icon == null)
            {
                Debug.LogWarning($"[GalaxyBuild] 图标加载失败: {iconPath}（跳过图标设置）");
                return;
            }

            int slotCount = PlayerSettings.GetIconSizesForTargetGroup(BuildTargetGroup.Standalone).Length;
            var icons = new Texture2D[slotCount];
            for (int i = 0; i < slotCount; i++) icons[i] = icon;
            PlayerSettings.SetIconsForTargetGroup(BuildTargetGroup.Standalone, icons);
            Debug.Log($"[GalaxyBuild] 应用图标已设置: {iconPath}（{slotCount} 个尺寸槽）");
        }

        // 模板材质（幂等创建）：透明 + 不透明两份，各 EnableKeyword("_EMISSION")
        // 保住自发光变体。运行时从模板实例化保证构建/编辑器行为一致。
        //
        // 关键陷阱（实测根因）：新建材质默认 _EmissionColor = 黑 + GI 标志
        // EmissiveIsBlack —— 资产导入时 URP 的材质后处理器会顺手把 _EMISSION
        // 关键字清掉（m_ValidKeywords 里根本不会出现它），构建剥离发射变体、
        // 球体全部变哑光。对策：给模板写一个非黑发射色（哨兵值）+ GI 标志清零，
        // 骗过导入清理；运行时实例照常覆盖成各自颜色。
        private static void EnsureTemplateMaterials()
        {
            const string dir = "Assets/Galaxy/Resources";
            if (!AssetDatabase.IsValidFolder(dir))
            {
                AssetDatabase.CreateFolder("Assets/Galaxy", "Resources");
            }

            // 透明模板（多面体填充）
            const string transparentPath = dir + "/GalaxyLitTransparent.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(transparentPath) == null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.SetFloat("_Surface", 1f);                     // Transparent
                mat.SetFloat("_Blend", 0f);                       // Alpha 混合
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.5f, 0.5f, 0.5f));   // 哨兵：防导入清理
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                AssetDatabase.CreateAsset(mat, transparentPath);
                Debug.Log($"[GalaxyBuild] 已创建透明材质模板: {transparentPath}");
            }

            // 不透明模板（自发光球体/红球）
            const string opaquePath = dir + "/GalaxyLitOpaque.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(opaquePath) == null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", new Color(0.5f, 0.5f, 0.5f));   // 哨兵：防导入清理
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                AssetDatabase.CreateAsset(mat, opaquePath);
                Debug.Log($"[GalaxyBuild] 已创建不透明材质模板: {opaquePath}");
            }

            AssetDatabase.SaveAssets();
        }

        // 把 shader 加入 Graphics Settings 的 Always Included Shaders（幂等）。
        // GraphicsSettings 的该清单没有公开 API，走 SerializedObject 编辑项目设置资产
        private static void EnsureAlwaysIncludedShaders(params string[] names)
        {
            UnityEngine.Object gs = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "ProjectSettings/GraphicsSettings.asset");
            if (gs == null)
            {
                Debug.LogError("[GalaxyBuild] 找不到 GraphicsSettings.asset");
                return;
            }

            var so = new SerializedObject(gs);
            SerializedProperty arr = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (string name in names)
            {
                Shader shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogWarning($"[GalaxyBuild] 找不到 shader: {name}");
                    continue;
                }
                bool exists = false;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) { exists = true; break; }
                }
                if (exists) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
                Debug.Log($"[GalaxyBuild] Always Included Shaders += {name}");
            }
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
