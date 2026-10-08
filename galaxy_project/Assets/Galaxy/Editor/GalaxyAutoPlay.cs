// ============================================================================
// 自动流水线：进入 Play -> 等星系生成并渲染稳定 -> 两段式截图 -> 保持运行。
// 用途：命令行无人值守地验证"渲染出来长什么样"；也是将来自动化视觉回归的种子。
//
// 两段式截图：
//   1) galaxy_shot.png  —— 标准全景（ReframeInstant 复位取景，结果可比）；
//   2) galaxy_shot2.png —— 拾取演示：程序化选中"被引用最多的枢纽文件"。
//      演示前把枢纽的世界坐标投影到屏幕，用与鼠标点击完全相同的 PickAt 求交
//      做命中校验 —— 这是对"相机 -> 屏幕射线 -> 实体"整条拾取链路的端到端测试。
//
// 命令行入口（编辑器 GUI 启动参数）：
//   Unity.exe -projectPath <proj> -executeMethod Galaxy.EditorTools.GalaxyAutoPlay.Begin
//
// 实现要点：本项目开启了 Enter Play Mode Options（禁用域重载）——进入 Play
// 不重跑静态构造函数，且上一次会话的 static 字段会残留，因此 Begin 里必须
// 显式重置计数器；跨重载的"是否待办"状态仍走 SessionState（编辑器会话级
// 存储，类似进程级共享内存）。
// ============================================================================
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Galaxy.EditorTools
{
    [InitializeOnLoad]
    public static class GalaxyAutoPlay
    {
        private const string SessionKey = "galaxy.autoplay.pending";
        private static readonly string ShotPath = Path.Combine(Path.GetTempPath(), "galaxy_shot.png");
        private static readonly string ShotPathSelected = Path.Combine(Path.GetTempPath(), "galaxy_shot2.png");

        private static int s_Frames;
        private static int s_Phase;        // 0 = 等首图 / 1 = 拾取演示 + 二图 / 2 = 等收尾
        private static int s_MarkFrame;
        private static double s_StartTime;

        static GalaxyAutoPlay()
        {
            // 域重载后重新挂载（仅当 Begin 已下达过指令）
            if (!SessionState.GetBool(SessionKey, false)) return;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        // 入口：重置流水线状态，下一帧请求进入 Play（编辑器启动早期需要先让初始化跑完）
        public static void Begin()
        {
            SessionState.SetBool(SessionKey, true);
            s_Frames = 0;
            s_Phase = 0;
            s_MarkFrame = 0;
            s_StartTime = 0;
            // 直接挂载截图回调：禁用了域重载时 static 构造函数不会重跑，不能只靠它挂载
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlaying) return;  // 已在 Play 中（罕见）：不再请求
                Debug.Log("[GalaxyAutoPlay] 请求进入 Play 模式...");
                EditorApplication.EnterPlaymode();
            };
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            if (s_StartTime <= 0) s_StartTime = EditorApplication.timeSinceStartup;
            s_Frames++;

            if (s_Phase == 0)
            {
                // 星系生成（~1900 个对象 + 力导向迭代）会同步阻塞主线程，期间本回调
                // 不运行 —— 帧计数天然从"生成完成后"开始。故这里用"帧数 AND 时长"
                // 双条件：保证截图时至少已渲染过 180 帧（约 3 秒）的完整星系。
                bool ready = s_Frames >= 180 && EditorApplication.timeSinceStartup - s_StartTime >= 8.0;
                if (!ready) return;
                // 截图前恢复标准取景姿态：用户此刻若在手动缩放/旋转，会污染截图内容
                OrbitCamera cam = Object.FindAnyObjectByType<OrbitCamera>();
                if (cam != null) cam.ReframeInstant();
                ScreenCapture.CaptureScreenshot(ShotPath);
                Debug.Log($"[GalaxyAutoPlay] 已请求截图: {ShotPath}");
                s_MarkFrame = s_Frames;
                s_Phase = 1;
                return;
            }

            if (s_Phase == 1)
            {
                if (s_Frames < s_MarkFrame + 30) return;   // 等首图写盘，避免两张图写盘竞争
                // 护栏：演示一旦抛异常，若不捕获会中断本回调、相位机卡死并逐帧刷屏
                // （实测踩坑）。捕获后至少保证二图与收尾继续走完。
                try { DemoPick(); }
                catch (System.Exception e) { Debug.LogError($"[GalaxyAutoPlay] 拾取演示异常: {e}"); }
                ScreenCapture.CaptureScreenshot(ShotPathSelected);
                Debug.Log($"[GalaxyAutoPlay] 已请求选中态截图: {ShotPathSelected}");
                s_MarkFrame = s_Frames;
                s_Phase = 2;
                return;
            }

            if (s_Phase == 2 && s_Frames >= s_MarkFrame + 45)
            {
                // 设置面板冒烟（安排在截图之后：APPLY 会重建星系并清掉选中态）；
                // 护栏：回调里未捕获异常会中断相位机并逐帧刷屏（铁律）
                try
                {
                    GalaxyPicker smokePicker = Object.FindAnyObjectByType<GalaxyPicker>();
                    if (smokePicker != null) smokePicker.DebugSmokeSettings();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[GalaxyAutoPlay] 设置冒烟异常: {e}");
                }

                Debug.Log($"[GalaxyAutoPlay] 截图流程结束（全景 {File.Exists(ShotPath)} / 选中态 " +
                          $"{File.Exists(ShotPathSelected)}），保持 Play 供查看");
                SessionState.SetBool(SessionKey, false);
                EditorApplication.update -= Tick;
                // 故意不退出 Play：用户回到电脑前直接就能看到运行中的星系
            }
        }

        // 拾取演示：选中"被引用最多的文件"（枢纽节点），顺带验证拾取代码路径 ——
        // 把枢纽的世界坐标投影到屏幕，用与鼠标点击完全相同的 PickAt 求交。
        // 若枢纽正被前景实体遮挡，命中的会是更近的实体——这本身就是正确的
        // "最近命中优先"行为，日志里记录判定结果供人工比对。
        private static void DemoPick()
        {
            GalaxyPicker picker = Object.FindAnyObjectByType<GalaxyPicker>();
            Camera cam = Camera.main;
            if (picker == null || cam == null || picker.Data == null || picker.HubIndex < 0)
            {
                Debug.LogWarning("[GalaxyAutoPlay] 拾取演示跳过：picker / 相机 / 数据不可用");
                return;
            }

            int hub = picker.HubIndex;
            Vector3 screen = cam.WorldToScreenPoint(picker.Data.positions[hub]);
            int hit = screen.z > 0f ? picker.PickAt(new Vector2(screen.x, screen.y)) : -1;
            GalaxyNode node = picker.Data.graph.nodes[hub];
            string verdict = hit == hub ? "直达命中"
                           : hit >= 0 ? $"被前置实体 #{hit} 遮挡（最近命中优先，行为正确）"
                           : "未命中";
            Debug.Log($"[GalaxyAutoPlay] 拾取校验: 枢纽 #{hub} {node.name}（入度 {picker.Data.inDegree[hub]}）" +
                      $"投影 ({screen.x:F0},{screen.y:F0}) -> {verdict}");

            picker.SelectNode(hub);

            // 模拟"双击打开"：走与交互完全相同的入口打开代码查看器（截图可验证）
            picker.OpenViewer(hub);
            Debug.Log("[GalaxyAutoPlay] 已打开代码查看器（模拟双击枢纽文件）");

            // 搜索定位抽查：直接调用匹配纯函数（不经 UI 状态），验证文件名定位链路
            var matches = GalaxySearch.Match(picker.Data.graph, "lv_conf");
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < matches.Count && i < 3; i++)
            {
                names.Append(picker.Data.graph.nodes[matches[i]].name).Append("  ");
            }
            Debug.Log($"[GalaxyAutoPlay] 搜索抽查 'lv_conf' -> {matches.Count} 命中: {names}");

            // include 跳转抽查：枢纽的一条出边按目标路径文本反向解析，应命中同一目标
            for (int e = 0; e < picker.Data.graph.links.Length; e++)
            {
                if (picker.Data.graph.links[e].source != hub) continue;
                int target = picker.Data.graph.links[e].target;
                string includeText = picker.Data.graph.nodes[target].path;
                int resolved = picker.ResolveInclude(hub, includeText);
                Debug.Log($"[GalaxyAutoPlay] include 跳转抽查: '{includeText}' -> #{resolved}" +
                          $"（期望 #{target}）{(resolved == target ? " OK" : " FAIL")}");
                break;
            }

            // 黑洞特写定位（自动截图裁剪用）：投影到屏幕坐标
            BlackHole blackHole = Object.FindAnyObjectByType<BlackHole>();
            if (blackHole != null)
            {
                Vector3 bhScreen = cam.WorldToScreenPoint(blackHole.transform.position);
                Debug.Log($"[GalaxyAutoPlay] 黑洞投影 ({bhScreen.x:F0},{bhScreen.y:F0})");
            }
        }
    }
}
