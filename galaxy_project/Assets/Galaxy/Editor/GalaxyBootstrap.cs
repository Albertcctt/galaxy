// ============================================================================
// 一次性引导：检出 TEMP 下的标记文件后，自动装配场景 + 进入 Play + 截图。
//
// 用途：向"已经打开的编辑器"注入执行流 —— 编辑器检测到本文件后会导入并
// 编译，在随之而来的域重载中 [InitializeOnLoadMethod] 自动执行，全程无需
// 任何人工点击。标记文件"存在才执行、执行即删除"，保证恰好运行一次。
//
// 防抖设计：编辑器的"脚本变化时"策略可能是"重新编译并继续播放" —— 域重载
// 完成后编辑器可能仍处于（或立刻回到）Play 模式，而 EditorSceneManager 的
// 场景 API 在 Play 中不可用（实测踩坑：OpenScene 抛 InvalidOperationException）。
// 因此这里保证先回到编辑模式，再装配场景。
// ============================================================================
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Galaxy.EditorTools
{
    public static class GalaxyBootstrap
    {
        private static readonly string MarkerPath = Path.Combine(Path.GetTempPath(), "galaxy.bootstrap");

        // 自动化流水线的前提：脚本变更在 Play 期间应当"停止后编译"。
        // 默认策略"重新编译并继续播放"会在域重载后自动重进 Play，与注入引导的
        // 时序打架（实测踩坑：引导链断掉、截图丢失）。该偏好没有公开 API，
        // 只存在 EditorPrefs 里，已知 key：ScriptCompilationDuringPlay
        // （0 = 重新编译并继续播放，1 = 停止后编译，2 = 停止并编译）。
        [InitializeOnLoadMethod]
        private static void EnforceDeterministicCompileMode()
        {
            if (EditorPrefs.GetInt("ScriptCompilationDuringPlay", 0) != 1)
            {
                EditorPrefs.SetInt("ScriptCompilationDuringPlay", 1);
                Debug.Log("[GalaxyBootstrap] 已把 Script Changes While Playing 偏好设为\"停止后编译\"（自动化流水线前提）");
            }
        }

        [InitializeOnLoadMethod]
        private static void MaybeRun()
        {
            if (!File.Exists(MarkerPath)) return;
            File.Delete(MarkerPath);  // 先删：无论成功与否都只执行一次，避免重载风暴时反复触发

            // InitializeOnLoadMethod 的时机在脚本加载刚完成时，此时做场景操作偏早；
            // 推迟到编辑器下一次空闲 tick（delayCall）执行更稳。
            EditorApplication.delayCall += RunWhenInEditMode;
        }

        // 场景 API 只能在编辑模式调用：若重载后编辑器在（或自动回到）Play，
        // 先请求退出，再逐帧轮询直到真正回到编辑模式才继续装配。
        // 用轮询而非 playModeStateChanged 一次性事件：编译/重启的过渡态里
        // 事件可能丢失（实测踩坑——引导链断掉、截图消失）。
        private static void RunWhenInEditMode()
        {
            if (!EditorApplication.isPlaying)
            {
                Run();
                return;
            }

            Debug.Log("[GalaxyBootstrap] 重载后仍处于 Play：先退出，待回到编辑模式再装配");
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
                Debug.Log("[GalaxyBootstrap] 检测到引导标记：装配场景并进入演示流程 (run-42)");
                GalaxySceneSetup.SetupInternal(interactive: false);
                GalaxyAutoPlay.Begin();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GalaxyBootstrap] 引导失败: {e}");
            }
        }
    }
}
