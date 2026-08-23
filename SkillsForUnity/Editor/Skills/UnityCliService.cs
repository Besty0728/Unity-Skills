using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Unity CLI（官方 unity 命令行工具，实验性 beta）集成服务。
    ///
    /// 职责：
    ///  1. 检测本机是否安装 Unity CLI（后台线程跑 `unity --version`，结果供 UI 轮询）；
    ///  2. 项目级绑定配置读写 —— Library/UnitySkills/cli_config.json（机器本地，不进 git，
    ///     编辑器关闭时 AI 客户端也按该固定路径读取，用于冷启动门控）；
    ///  3. 绑定状态同步进 RegistryService 全局注册表，便于跨项目发现。
    ///
    /// 对 CLI 零硬依赖：检测失败只影响面板显示，不影响任何 REST skill。
    /// </summary>
    public static class UnityCliService
    {
        public const int ConfigSchemaVersion = 1;

        // 检测失败原因常量（DetectResult.error 取值）。不写入 cli_config.json，仅供运行时/UI 诊断。
        public const string CliErrorNotFound = "not_found";
        public const string CliErrorNotExecutable = "not_executable";
        public const string CliErrorLaunchFailed = "launch_failed";
        public const string CliErrorIncompatibleSystem = "incompatible_system";

        // ===== 配置模型（Newtonsoft 序列化，字段名即 JSON key，客户端按此契约读取）=====

        public class CliFeatures
        {
            public bool coldStart = true;   // 冷启动 / 生命周期管理
            public bool openArgs  = true;   // unity open --args 传参启动
            public bool cliTest   = true;   // unity test 无头测试
            public bool cliRun    = false;  // unity run 批处理运行（新能力默认关，旧配置缺键=false，须在面板显式开启）
            public bool cliBuild  = false;  // unity build 无头构建（同上，显式开启）
        }

        public class CliConfig
        {
            public int schemaVersion = ConfigSchemaVersion;
            public bool enabled;
            public string cliPath = "";
            public string cliVersion = "";
            public string projectPath = "";
            public string editorVersion = "";
            public string boundAt = "";     // ISO-8601 UTC
            public CliFeatures features = new CliFeatures();
        }

        // ===== 检测结果（后台线程写，主线程 UI 轮询读）=====

        public class DetectResult
        {
            public bool found;
            public string cliPath = "";
            public string version = "";
            public string error = "";
        }

        private static volatile bool _detecting;
        private static volatile DetectResult _lastResult;

        /// <summary>当前是否有检测在后台进行中。</summary>
        public static bool IsDetecting => _detecting;

        /// <summary>最近一次检测结果；未检测过为 null。UI 用 schedule.Execute 轮询。</summary>
        public static DetectResult LastResult => _lastResult;

        // ===== 路径（在主线程静态初始化时捕获，后台线程只读缓存值）=====

        private static readonly string ConfigDir =
            Path.Combine(Application.dataPath, "../Library/UnitySkills");
        private static readonly string ConfigFile =
            Path.Combine(ConfigDir, "cli_config.json");
        private static readonly string ProjectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        private static CliConfig _cached;

        // ===== 检测 =====

        /// <summary>
        /// 后台线程检测 Unity CLI。探测顺序：用户指定路径 → 已绑定配置里的路径 →
        /// PATH（login shell，macOS GUI 进程不继承 shell PATH）→ 常见安装位。
        /// 完成后写 <see cref="LastResult"/>，不回调（UI 侧轮询，避免跨线程触碰 Unity API）。
        /// </summary>
        public static void DetectAsync(string userPath = null)
        {
            if (_detecting) return;
            _detecting = true;

            string configuredPath = LoadConfig()?.cliPath;

            var t = new Thread(() =>
            {
                var result = new DetectResult();
                try
                {
                    string lastConcreteError = null;
                    foreach (var candidate in EnumerateCandidates(userPath, configuredPath))
                    {
                        if (string.IsNullOrEmpty(candidate)) continue;
                        var attempt = TryGetVersion(candidate);
                        if (attempt.success)
                        {
                            result.found = true;
                            result.cliPath = candidate;
                            result.version = attempt.version;
                            break;
                        }
                        // 记录最后一个“找到了文件但启动失败”的具体原因；
                        // 若某个候选根本不存在，则保留更具体的错误。
                        if (attempt.error != null)
                            lastConcreteError = attempt.error;
                    }
                    if (!result.found)
                        result.error = lastConcreteError ?? CliErrorNotFound;
                }
                catch (Exception ex)
                {
                    result.error = ex.Message;
                }
                _lastResult = result;
                _detecting = false;
            });
            t.IsBackground = true;
            t.Start();
        }

        private static IEnumerable<string> EnumerateCandidates(string userPath, string configuredPath)
        {
            if (!string.IsNullOrEmpty(userPath)) yield return userPath;
            if (!string.IsNullOrEmpty(configuredPath)) yield return configuredPath;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#if UNITY_EDITOR_WIN
            yield return Path.Combine(home, ".unity", "bin", "unity.exe");
            yield return ResolveViaShell("where unity", winWhere: true);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity", "cli", "unity.exe");
            yield return "unity.exe";
#else
            // 官方 install.sh 的默认安装位（PATH 注入走 .zshrc 的 `. ~/.unity/env`，
            // GUI 进程拿不到，必须直接探这个目录）
            yield return Path.Combine(home, ".unity", "bin", "unity");
            yield return ResolveViaShell("command -v unity", winWhere: false);
            yield return Path.Combine(home, ".local", "bin", "unity");
            yield return "/usr/local/bin/unity";
            yield return "/opt/homebrew/bin/unity";
            yield return "unity";
#endif
        }

        /// <summary>
        /// 经 login shell 解析 PATH 上的 unity。Editor GUI 进程（尤其 macOS Dock 启动）
        /// 不继承用户 shell 的 PATH，必须走 -lc 让 profile 生效。失败返回 null。
        /// </summary>
        private static string ResolveViaShell(string cmd, bool winWhere)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (winWhere)
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + cmd;
                }
                else
                {
                    string shell = Environment.GetEnvironmentVariable("SHELL");
                    psi.FileName = string.IsNullOrEmpty(shell) ? "/bin/zsh" : shell;
                    // -lic：login + interactive。仅 -l 不会加载 .zshrc（交互专属），
                    // 而 Unity CLI 等工具的 PATH 注入恰恰写在 .zshrc —— 实测 -lc 找不到。
                    psi.Arguments = "-lic \"" + cmd + "\"";
                }
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.BeginErrorReadLine();          // 排空 stderr，防缓冲区满死锁（rc 文件可能很吵）
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                    // where 可能返回多行，取第一行
                    var first = (stdout ?? "").Trim()
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return first.Length > 0 ? first[0].Trim() : null;
                }
            }
            catch { return null; }
        }

        private readonly struct VersionAttempt
        {
            public readonly bool success;
            public readonly string version;
            public readonly string error;
            public VersionAttempt(bool success, string version = "", string error = null)
            {
                this.success = success;
                this.version = version ?? "";
                this.error = error;
            }
        }

        /// <summary>跑 `&lt;path&gt; --version` 验证可执行并取版本号；返回具体失败原因。</summary>
        /// <remarks>
        /// 先异步启动 stdout/stderr 读取再 WaitForExit，避免子进程输出填满管道缓冲区后死锁。
        /// 仅用于输出极小的 `--version` 探测；不适用于可能产生大量日志的命令。
        /// </remarks>
        private static VersionAttempt TryGetVersion(string cliPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // 先开始异步读取，防止管道缓冲区满导致 WaitForExit 无限等待。
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();

                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return new VersionAttempt(false, error: CliErrorLaunchFailed); }

                    string stdout = stdoutTask.Result;
                    string stderr = stderrTask.Result;

                    if (p.ExitCode != 0)
                    {
                        if (LooksLikeGlibcError(stderr) || LooksLikeGlibcError(stdout))
                            return new VersionAttempt(false, error: CliErrorIncompatibleSystem);
                        return new VersionAttempt(false, error: CliErrorLaunchFailed);
                    }
                    string version = (stdout ?? "").Trim();
                    if (string.IsNullOrEmpty(version))
                        return new VersionAttempt(false, error: CliErrorLaunchFailed);
                    return new VersionAttempt(true, version);
                }
            }
            catch (System.ComponentModel.Win32Exception w32)
            {
                // NativeErrorCode 比 Message 可靠：Mono 的措辞（"Cannot find the specified file" /
                // "Access denied"）与 .NET Framework 不同，且随 locale 变化。
                // 2 = ERROR_FILE_NOT_FOUND / ENOENT，3 = ERROR_PATH_NOT_FOUND，5 = ERROR_ACCESS_DENIED / EACCES，13 = EACCES(POSIX)。
                switch (w32.NativeErrorCode)
                {
                    // 文件不存在或 PATH 上找不到：不算“启动失败”，让探测继续下一个候选。
                    case 2:
                    case 3:
                        return new VersionAttempt(false);
                    case 5:
                    case 13:
                        return new VersionAttempt(false, error: CliErrorNotExecutable);
                    default:
                        // 未知 errno：回退到措辞匹配，覆盖 Mono / .NET / 各平台的不同文案。
                        return ClassifyByMessage(w32.Message);
                }
            }
            catch (FileNotFoundException)
            {
                return new VersionAttempt(false);
            }
            catch (UnauthorizedAccessException)
            {
                return new VersionAttempt(false, error: CliErrorNotExecutable);
            }
            catch (Exception ex)
            {
                return ClassifyByMessage(ex.Message);
            }
        }

        /// <summary>无可用 errno 时的兜底分类：按异常措辞区分“不存在”“不可执行”和其他启动失败。</summary>
        private static VersionAttempt ClassifyByMessage(string message)
        {
            string msg = (message ?? "").ToLowerInvariant();
            if (msg.Contains("no such file") || msg.Contains("cannot find") || msg.Contains("not found"))
                return new VersionAttempt(false);
            if (msg.Contains("permission denied") || msg.Contains("access denied") || msg.Contains("access is denied"))
                return new VersionAttempt(false, error: CliErrorNotExecutable);
            return new VersionAttempt(false, error: CliErrorLaunchFailed);
        }

        private static bool LooksLikeGlibcError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            return t.Contains("glibc") || t.Contains("libc.so") || t.Contains("version `glib");
        }

        // ===== 配置读写 =====

        /// <summary>读取项目绑定配置；文件不存在或损坏返回 null。结果缓存，Bind/Unbind 后失效。</summary>
        public static CliConfig LoadConfig()
        {
            if (_cached != null) return _cached;
            try
            {
                if (!File.Exists(ConfigFile)) return null;
                _cached = JsonConvert.DeserializeObject<CliConfig>(File.ReadAllText(ConfigFile));
                return _cached;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to read cli_config.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>当前项目是否已绑定且启用 Unity CLI。</summary>
        public static bool IsBound
        {
            get
            {
                var cfg = LoadConfig();
                return cfg != null && cfg.enabled && !string.IsNullOrEmpty(cfg.cliPath);
            }
        }

        /// <summary>绑定当前项目：写 cli_config.json 并同步注册表。保留已有 feature 开关。</summary>
        public static void Bind(string cliPath, string cliVersion)
        {
            var cfg = LoadConfig() ?? new CliConfig();
            cfg.schemaVersion = ConfigSchemaVersion;
            cfg.enabled = true;
            cfg.cliPath = cliPath ?? "";
            cfg.cliVersion = cliVersion ?? "";
            cfg.projectPath = ProjectRoot;
            cfg.editorVersion = Application.unityVersion;
            cfg.boundAt = DateTime.UtcNow.ToString("o");
            SaveConfig(cfg);
            RegistryService.UpdateCliBinding(true, cfg.cliPath);
            SkillsLogger.Log($"Unity CLI bound: {cfg.cliPath} ({cfg.cliVersion})");
        }

        /// <summary>解绑：enabled=false（保留路径便于重新绑定），注册表清除标记。</summary>
        public static void Unbind()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            cfg.enabled = false;
            SaveConfig(cfg);
            RegistryService.UpdateCliBinding(false, null);
            SkillsLogger.Log("Unity CLI unbound.");
        }

        /// <summary>更新单个 feature 开关并落盘（面板 Toggle 直接调用）。</summary>
        public static void SetFeature(Action<CliFeatures> mutate)
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            if (cfg.features == null) cfg.features = new CliFeatures();
            mutate(cfg.features);
            SaveConfig(cfg);
        }

        private static void SaveConfig(CliConfig cfg)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                _cached = cfg;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to write cli_config.json: {ex.Message}");
            }
        }

        /// <summary>供 RegistryService.Register 读取绑定状态（不触发检测）。</summary>
        public static void GetRegistryBinding(out bool bound, out string cliPath)
        {
            var cfg = LoadConfig();
            bound = cfg != null && cfg.enabled && !string.IsNullOrEmpty(cfg.cliPath);
            cliPath = bound ? cfg.cliPath : null;
        }

        // ===== 冷启动自动开服 =====

        /// <summary>AI 经 Unity CLI 冷启动时随 --args 传入的标记。</summary>
        public const string ColdStartArg = "-unityskills-coldstart";

        private const string ColdStartConsumedKey = "UnitySkills_CliColdStartConsumed";

        /// <summary>
        /// 编辑器本次会话是否由 Unity CLI 冷启动、且应自动拉起服务器。
        /// 条件：命令行含 <see cref="ColdStartArg"/> + 项目已绑定 + coldStart 开关开启。
        /// 每个编辑器会话只返回一次 true（SessionState 跨 Domain Reload 记忆已消费），
        /// 避免用户会话中途手动停服后被每次 Reload 反复强启。
        /// </summary>
        public static bool ConsumeColdStartRequest()
        {
            if (UnityEditor.SessionState.GetBool(ColdStartConsumedKey, false)) return false;
            UnityEditor.SessionState.SetBool(ColdStartConsumedKey, true);

            bool hasArg = false;
            foreach (var arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, ColdStartArg, StringComparison.OrdinalIgnoreCase)) { hasArg = true; break; }
            if (!hasArg) return false;

            var cfg = LoadConfig();
            return cfg != null && cfg.enabled
                && (cfg.features == null || cfg.features.coldStart);
        }
    }
}

// Producer:Betsy
