using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// 包升级后自动把"已安装"的 AI 工具副本刷新到当前版本，省掉用户再去面板点一次安装。
    ///
    /// 触发点是 [InitializeOnLoadMethod]，每次域重载都会跑，所以同版本的快路径只做一次
    /// 小文件读取就返回。版本记录写在 Library/UnitySkills/install_sync.json（按项目存放、
    /// 不进 git），而不是 EditorPrefs —— 后者按机器全局存，同一台机器上双开项目会互相覆盖。
    ///
    /// 覆盖语义与面板手动点"安装"完全一致：现有安装机制没有内容清单可判断用户是否手改过
    /// 副本，本服务不另造一套 hash 校验，直接覆盖。只更新检测为已安装的目标，从不自动安装
    /// 新目标；全程主线程、无模态弹窗，单个目标失败只跳过它自己。
    /// </summary>
    public static class SkillInstallSyncService
    {
        public const int StateSchemaVersion = 1;

        // SessionState 跨域重载存活、编辑器重启才清空：本会话尝试过就不再重复，
        // 避免同步持续失败时每次重编译都重跑一轮文件复制。
        private const string SessionAttemptedKey = "UnitySkills.InstallSync.Attempted";

        private static readonly string DefaultStateDir =
            Path.Combine(Application.dataPath, "../Library/UnitySkills");

        /// <summary>测试专用：把状态文件重定向到临时目录，避免碰真实工程记录。</summary>
        internal static string StateFilePathOverride;

        internal static string StateFilePath =>
            StateFilePathOverride ?? Path.Combine(DefaultStateDir, "install_sync.json");

        private static string _prefEnabled;

        // 与 SkillsHttpServer 同款键式：带 InstanceId，天然按项目隔离。
        internal static string PrefEnabled =>
            _prefEnabled ??= $"UnitySkills_{RegistryService.InstanceId}_AutoSyncInstalls";

        /// <summary>是否在包升级后自动同步已安装的 AI 工具。默认开启。</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        // ===== 状态模型（Newtonsoft 序列化，字段名即 JSON key）=====

        public class SyncState
        {
            public int schemaVersion = StateSchemaVersion;
            public string lastSyncedVersion = "";
            public string lastSyncedAt = "";              // ISO-8601 UTC
            public List<string> lastSyncedTargets = new List<string>();
        }

        /// <summary>一轮同步的结果。</summary>
        public sealed class SyncReport
        {
            public readonly List<string> Updated = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public int SkippedNotInstalled;
            public int SkippedDuplicatePath;
        }

        // ===== 触发 =====

        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            try
            {
                if (!ShouldSyncNow(Application.isBatchMode))
                    return;

                if (SessionState.GetBool(SessionAttemptedKey, false))
                    return;
                SessionState.SetBool(SessionAttemptedKey, true);

                // 延后一拍到编辑器就绪：InitializeOnLoad 期间 PackageManager 的包信息
                // 不保证可查，而模板根目录的解析依赖它。
                EditorApplication.delayCall += RunSyncOnce;
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] SkillInstallSyncService init failed: " + ex);
            }
        }

        /// <summary>
        /// 域重载即时门，无副作用：batchmode 排除 → 版本比对 → 用户开关。三项全过才值得排一次同步。
        ///
        /// 开关判断排在 SessionState 之前（见调用方）：否则关着开关的会话会把"已尝试"标记提前落下，
        /// 用户中途打开开关后要等到编辑器重启才生效。
        /// </summary>
        internal static bool ShouldSyncNow(bool batchMode)
        {
            // batchmode 排除：`unity test` / `run` / `build` 等无头流程同样跑 InitializeOnLoad，
            // 不该让一次 CI 构建去改写用户主目录下的 skill 副本。
            if (batchMode)
                return false;

            // 快路径：版本没变就到此为止，整条路径只花一次小文件读取。
            if (!NeedsSync(ReadRecordedVersion(), SkillsLogger.Version))
                return false;

            return Enabled;
        }

        private static void RunSyncOnce()
        {
            try
            {
                var report = SyncTargets(SkillInstaller.EnumerateTargets());
                LogReport(report);

                // 有目标失败就不写记录，把重试留给下一次编辑器会话（本会话已被
                // SessionState 拦住，不会立刻重跑）。
                if (report.Failed.Count == 0)
                    WriteState(SkillsLogger.Version, report.Updated);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("AI tool auto-sync aborted: " + ex.Message);
            }
        }

        // ===== 核心逻辑（可注入目标，便于测试）=====

        /// <summary>记录版本与当前版本不同（含记录缺失）时需要同步。</summary>
        internal static bool NeedsSync(string recordedVersion, string currentVersion)
        {
            return !string.Equals(recordedVersion, currentVersion, StringComparison.Ordinal);
        }

        /// <summary>
        /// 对已安装的目标逐个执行安装（= 覆盖更新）。未安装的目标一律跳过，绝不新装。
        /// </summary>
        internal static SyncReport SyncTargets(IEnumerable<SkillInstaller.InstallTarget> targets)
        {
            var report = new SyncReport();
            if (targets == null)
                return report;

            // Codex 与 Antigravity 的项目级目标指向同一个 .agents/skills 目录，
            // 按规范化全路径去重，免得同一份文件被复制两遍、日志还重复计数。
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                if (target == null)
                    continue;

                try
                {
                    if (!string.IsNullOrEmpty(target.Path))
                    {
                        var fullPath = Path.GetFullPath(target.Path);
                        if (!seenPaths.Add(fullPath))
                        {
                            report.SkippedDuplicatePath++;
                            continue;
                        }
                    }

                    if (target.IsInstalled == null || !target.IsInstalled())
                    {
                        report.SkippedNotInstalled++;
                        continue;
                    }

                    var (success, message) = target.Install();
                    if (success)
                        report.Updated.Add(target.DisplayName);
                    else
                        report.Failed.Add($"{target.DisplayName}: {message}");
                }
                catch (Exception ex)
                {
                    report.Failed.Add($"{target.DisplayName}: {ex.Message}");
                }
            }

            return report;
        }

        // 日志文案跟随面板语言（SkillsLocalization.Current）。字符串内联在本文件而非 Localization.cs：
        // Console 用 Unity 内置字体，不吃面板字体图集；进 Localization.cs 会被
        // UISkillsFontAssetBaker 当 UI 字符收集、强制图集覆盖这些字形。
        private static string L(string en, string zh, string ru)
        {
            switch (SkillsLocalization.Current)
            {
                case SkillsLocalization.Language.Chinese: return zh;
                case SkillsLocalization.Language.Russian: return ru;
                default: return en;
            }
        }

        private static void LogReport(SyncReport report)
        {
            var version = SkillsLogger.Version;

            if (report.Updated.Count > 0)
            {
                SkillsLogger.Log(string.Format(
                    L("AI tool auto-sync: updated {0} installed target(s) to {1} — {2}",
                      "AI 工具自动同步：已将 {0} 个已安装目标更新到 {1} —— {2}",
                      "Автосинхронизация AI-инструментов: обновлено установленных целей: {0}, версия {1} — {2}"),
                    report.Updated.Count, version, string.Join(", ", report.Updated)));
            }
            else if (report.Failed.Count == 0)
            {
                SkillsLogger.LogVerbose(string.Format(
                    L("AI tool auto-sync: no installed AI tool copies found, nothing to update for {0}.",
                      "AI 工具自动同步：未检测到已安装的 AI 工具副本，{0} 无需更新。",
                      "Автосинхронизация AI-инструментов: установленных копий не найдено, для {0} обновлять нечего."),
                    version));
            }

            if (report.Failed.Count > 0)
            {
                SkillsLogger.LogWarning(string.Format(
                    L("AI tool auto-sync: {0} target(s) skipped after an error — {1}. Reinstall them from the UnitySkills panel (AI Config tab) if needed.",
                      "AI 工具自动同步：{0} 个目标因出错被跳过 —— {1}。如有需要，请到 UnitySkills 面板的 AI Config 页签重新安装。",
                      "Автосинхронизация AI-инструментов: пропущено целей из-за ошибки: {0} — {1}. При необходимости переустановите их на панели UnitySkills (вкладка AI Config)."),
                    report.Failed.Count, string.Join(" | ", report.Failed)));
            }
        }

        // ===== 状态文件 =====

        /// <summary>读取上次自动同步的版本号；记录缺失或损坏时返回 null。</summary>
        internal static string ReadRecordedVersion()
        {
            try
            {
                var path = StateFilePath;
                if (!File.Exists(path))
                    return null;

                var state = JsonConvert.DeserializeObject<SyncState>(File.ReadAllText(path));
                return string.IsNullOrEmpty(state?.lastSyncedVersion) ? null : state.lastSyncedVersion;
            }
            catch
            {
                // 记录损坏等价于没有记录：下一轮同步会把它整个重写。
                return null;
            }
        }

        internal static void WriteState(string version, List<string> syncedTargets)
        {
            try
            {
                var path = StateFilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var state = new SyncState
                {
                    schemaVersion = StateSchemaVersion,
                    lastSyncedVersion = version,
                    lastSyncedAt = DateTime.UtcNow.ToString("O"),
                    lastSyncedTargets = syncedTargets ?? new List<string>()
                };

                File.WriteAllText(path, JsonConvert.SerializeObject(state, Formatting.Indented), SkillsCommon.Utf8NoBom);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to write install_sync.json: " + ex.Message);
            }
        }
    }
}

// Producer:Betsy
