// ============================================================================
// 项目加载器（桌面应用形态）：运行时"打开任意项目"的完整链路。
//
// 流程：OPEN PROJECT 按钮（或命令行 --scan <目录>）→ 系统文件夹对话框
// （PowerShell + FolderBrowserDialog，避免为选目录引入原生插件）→ 调用随包
// 分发的 galaxy-scan.exe 扫描目标目录 → 生成 json 到 persistentDataPath →
// GalaxyBuilder.Rebuild() 原地重建星系（旧星系销毁、相机重新取景、拾取器
// 换新数据）。上次打开的项目记入 PlayerPrefs，下次启动自动恢复。
//
// 进程管理说明（C++ 类比：这里相当于做父进程的 spawn + waitpid）：
//   System.Diagnostics.Process 异步启动，在 Update 里轮询 HasExited —— 不阻塞
//   主线程（扫描期间星系照常动画）。输出量很小（对话框一行路径 / 扫描器几条
//   统计），退出后一次性 ReadToEnd 不会撑爆管道缓冲（4KB 以内）。
//
// 路径分流（编辑器 vs 构建产物）：
//   编辑器：scanner 取仓库里的 build/bin/galaxy-scan.exe（改完 C++ 即测）；
//   构建：scanner 与内置演示 json 均来自 StreamingAssets（随包分发）。
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;   // 消除与 System.Diagnostics.Debug 的二义性

namespace Galaxy
{
    public class GalaxyProjectLoader : MonoBehaviour
    {
        private enum State { Idle, DialogOpen, Scanning }

        // 文件夹选择对话框（PowerShell STA + WinForms；输出路径一行）
        private const string FolderDialogCommand =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; " +
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "$f = New-Object System.Windows.Forms.FolderBrowserDialog; " +
            "$f.Description = '选择要扫描的项目根目录'; " +
            "if ($f.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $f.SelectedPath }";

        private State m_State = State.Idle;
        private Process m_Process;
        private string m_PendingFolder = "";
        private string m_ScanFolder = "";
        private float m_ScanStart;
        private float m_NextStatusTick;
        private bool m_Cancelled;
        private bool m_PendingPersist;
        private readonly StringBuilder m_Out = new StringBuilder();
        private readonly StringBuilder m_Err = new StringBuilder();
        private readonly object m_OutLock = new object();
        private readonly object m_ErrLock = new object();
        private volatile string m_ProgressText = "";   // 扫描器进度行（"[progress] a/b" 的 "a/b"）

        // 渲染体量上限：布局引擎已升级（Barnes-Hut + 网格化，近线性），上限从
        // 1 万抬到 4 万；仍设上限是因为超大规模时实体网格构建与逐对象渲染
        //（未走实例化）也会成为瓶颈。
        private const int MaxRenderableNodes = 40000;

        /// <summary>是否有流程在跑（对话框打开中 / 扫描中）。</summary>
        public bool IsBusy => m_State != State.Idle;

        /// <summary>是否正在扫描（OPEN PROJECT 按钮据此变为 CANCEL SCAN）。</summary>
        public bool IsScanning => m_State == State.Scanning;

        /// <summary>取消进行中的扫描（杀子进程；Update 的收尾路径会走"已取消"分支）。</summary>
        public void CancelScan()
        {
            if (m_State != State.Scanning || m_Process == null) return;
            m_Cancelled = true;
            try { m_Process.Kill(); }
            catch (Exception e) { Debug.LogWarning($"[Galaxy] 取消扫描时终止进程失败: {e.Message}"); }
        }

        private void Start()
        {
            // 命令行入口：Galaxy.exe --scan <目录> —— 启动即扫描（自动化验证与
            // 快捷方式两用；此路径不写 PlayerPrefs，不污染用户偏好）
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--scan")
                {
                    string folder = args[i + 1];
                    Debug.Log($"[Galaxy] 启动参数 --scan: {folder}");
                    if (Directory.Exists(folder)) ScanAndLoad(folder, persist: false);
                    else Debug.LogError($"[Galaxy] --scan 目录不存在: {folder}");
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // 对外入口
        // ------------------------------------------------------------------

        /// <summary>OPEN PROJECT 按钮：弹系统文件夹对话框 → 选完自动扫描。</summary>
        public void OpenDialog()
        {
            if (IsBusy) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Sta -NoProfile -ExecutionPolicy Bypass -Command \"" + FolderDialogCommand + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                m_Process = Process.Start(psi);
                AttachOutputCapture(m_Process);
                m_State = State.DialogOpen;
                SetStatus("Selecting folder ...");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Galaxy] 文件夹对话框启动失败: {e.Message}");
                SetStatus("Dialog failed: " + e.Message);
            }
        }

        /// <summary>扫描目录并原地重建星系（对话框路径与 --scan 共用）。</summary>
        public void ScanAndLoad(string folder, bool persist)
        {
            if (IsBusy)
            {
                Debug.LogWarning("[Galaxy] 已有扫描/对话框进行中，忽略本次请求");
                return;
            }

            string scanner = ScannerExePath();
            if (!File.Exists(scanner))
            {
                Debug.LogError($"[Galaxy] 找不到扫描器: {scanner}");
                SetStatus("Scanner not found");
                return;
            }

            try
            {
                string outDir = Path.Combine(Application.persistentDataPath, "scans");
                Directory.CreateDirectory(outDir);
                string name = SanitizeName(Path.GetFileName(folder.TrimEnd('\\', '/')));
                m_PendingFolder = Path.Combine(outDir, (name.Length > 0 ? name : "project") + ".json");
                m_PendingPersist = persist;

                m_Out.Clear();
                m_Err.Clear();
                var psi = new ProcessStartInfo
                {
                    FileName = scanner,
                    Arguments = $"\"{folder}\" -o \"{m_PendingFolder}\" --compact",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                m_Process = Process.Start(psi);
                AttachOutputCapture(m_Process);
                m_State = State.Scanning;
                m_Cancelled = false;
                m_ScanFolder = folder;
                m_ScanStart = Time.realtimeSinceStartup;
                m_NextStatusTick = 0f;
                m_ProgressText = "";
                SetStatus($"Scanning {folder} ...");
                SetButtonLabel("CANCEL SCAN");
                Debug.Log($"[Galaxy] 开始扫描: {folder} -> {m_PendingFolder}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Galaxy] 扫描器启动失败: {e.Message}");
                SetStatus("Scan failed to start");
            }
        }

        // ------------------------------------------------------------------
        // 进程输出异步捕获：扫描器现在持续输出进度行（每 0.5s 一行），
        // 若不边跑边收会把管道缓冲写满导致子进程写阻塞（死锁）。异步事件在
        // 后台线程追加到缓冲；结束后用"参数不带时间的 WaitForExit()"等事件排干
        //（.NET 文档语义），再读缓冲。
        // ------------------------------------------------------------------
        private void AttachOutputCapture(Process process)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("[progress] "))
                {
                    m_ProgressText = e.Data.Substring(11).Trim();
                }
                lock (m_OutLock) { m_Out.AppendLine(e.Data); }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (m_ErrLock) { m_Err.AppendLine(e.Data); }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        // ------------------------------------------------------------------
        // 进程轮询：对话框退出 → 解析路径 → 扫描；扫描退出 → 重建
        // ------------------------------------------------------------------
        private void Update()
        {
            if (m_State == State.Idle || m_Process == null) return;

            // 扫描中：状态行实时刷新 —— 有进度行时显示真实百分比，否则退回走秒
            if (m_State == State.Scanning && Time.realtimeSinceStartup >= m_NextStatusTick)
            {
                m_NextStatusTick = Time.realtimeSinceStartup + 0.25f;
                string progress = m_ProgressText;
                string pct = "";
                if (!string.IsNullOrEmpty(progress))
                {
                    int slash = progress.IndexOf('/');
                    if (slash > 0 &&
                        long.TryParse(progress.Substring(0, slash), out long doneN) &&
                        long.TryParse(progress.Substring(slash + 1), out long totalN) && totalN > 0)
                    {
                        pct = $" · {doneN * 100 / totalN}% ({doneN}/{totalN})";
                    }
                }
                SetStatus($"Scanning {m_ScanFolder}{pct} · " +
                          $"{Time.realtimeSinceStartup - m_ScanStart:F0}s (CANCEL SCAN to abort)");
            }

            if (!m_Process.HasExited) return;

            m_Process.WaitForExit();   // 不带参数的二次等待：确保异步输出事件已排干
            string stdout, stderr;
            lock (m_OutLock) stdout = m_Out.ToString();
            lock (m_ErrLock) stderr = m_Err.ToString();
            int exit = m_Process.ExitCode;
            m_Process.Dispose();
            m_Process = null;

            if (m_State == State.DialogOpen)
            {
                m_State = State.Idle;
                string folder = FirstNonEmptyLine(stdout);
                if (exit == 0 && folder.Length > 0 && Directory.Exists(folder))
                {
                    ScanAndLoad(folder, persist: true);
                }
                else
                {
                    SetStatus("");   // 用户取消：静默回空闲
                }
                return;
            }

            // State.Scanning
            m_State = State.Idle;
            SetButtonLabel("OPEN PROJECT");
            if (m_Cancelled)
            {
                m_Cancelled = false;
                SetStatus("Scan cancelled");
                Debug.Log("[Galaxy] 扫描已被用户取消");
                return;
            }
            if (exit == 0 && File.Exists(m_PendingFolder))
            {
                Rebuild(m_PendingFolder, m_PendingPersist);
            }
            else
            {
                string tail = LastLines(stderr.Length > 0 ? stderr : stdout, 2);
                Debug.LogError($"[Galaxy] 扫描失败 (exit {exit}): {tail}");
                SetStatus($"Scan failed: {tail}");
            }
        }

        private void Rebuild(string jsonPath, bool persist)
        {
            GalaxyBuilder builder = FindAnyObjectByType<GalaxyBuilder>();
            GalaxyPicker picker = FindAnyObjectByType<GalaxyPicker>();
            if (builder == null)
            {
                Debug.LogError("[Galaxy] 找不到 GalaxyBuilder，无法重建");
                SetStatus("Builder missing");
                return;
            }

            // 先验证新数据可加载：扫描产物为空/损坏时保留当前星系不拆（"原地重建"
            // 的关键安全点 —— 拆旧星系的动作一旦发生就不可逆，实测踩坑：扫描到了
            // 空目录（0 节点 json），旧星系被拆、新星系建不出来，只剩空场景）
            GalaxyGraph candidate = GalaxyLoader.Load(jsonPath);
            if (candidate == null)
            {
                Debug.LogError($"[Galaxy] 扫描产物不可加载，保留当前星系: {jsonPath}");
                SetStatus("Scanned data invalid — kept current galaxy");
                return;
            }
            if (candidate.nodes.Length > MaxRenderableNodes)
            {
                Debug.LogError($"[Galaxy] 文件数 {candidate.nodes.Length} 超过渲染上限 {MaxRenderableNodes}，拒绝重建");
                SetStatus($"Too many files ({candidate.nodes.Length} > {MaxRenderableNodes}) — open a project subfolder");
                return;
            }

            // 查看器持有旧项目的文件路径，重建前先关掉（防"旧文件挂在新星系上"）
            if (picker != null) picker.CloseViewer();

            builder.jsonPath = jsonPath;
            builder.Rebuild();

            int nodes = picker != null && picker.Data != null ? picker.Data.graph.nodes.Length : 0;
            if (persist) PlayerPrefs.SetString("galaxy.lastJson", jsonPath);
            SetStatus($"{Path.GetFileNameWithoutExtension(jsonPath)} · {nodes} files");
            Debug.Log($"[Galaxy] 项目重建完成: {jsonPath}（{nodes} 节点）");
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private void SetStatus(string text)
        {
            GalaxyHud hud = GetComponent<GalaxyHud>();
            if (hud != null) hud.SetStatus(text);
        }

        private void SetButtonLabel(string text)
        {
            GalaxyHud hud = GetComponent<GalaxyHud>();
            if (hud != null) hud.SetButtonLabel(text);
        }

        private static string ScannerExePath()
        {
#if UNITY_EDITOR
            // 编辑器：直接用仓库里构建的 C++ 产物（改完 scanner 即改即测）
            return Path.GetFullPath(Path.Combine(Application.dataPath, "../../scanner/build/bin/galaxy-scan.exe"));
#else
            // 构建产物：随包分发的 scanner
            return Path.Combine(Application.streamingAssetsPath, "tools/galaxy-scan.exe");
#endif
        }

        private static string FirstNonEmptyLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t;
            }
            return "";
        }

        private static string LastLines(string text, int count)
        {
            string[] lines = text.Split('\n');
            int start = Mathf.Max(0, lines.Length - count);
            var sb = new StringBuilder();
            for (int i = start; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length > 0) sb.Append(t).Append(" / ");
            }
            return sb.ToString().TrimEnd(' ', '/');
        }

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
