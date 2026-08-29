using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// After a package upgrade, automatically refreshes "installed" AI tool copies to the current version, sparing
    /// the user a trip back to the panel to click install again.
    ///
    /// Triggered via [InitializeOnLoadMethod], which runs on every domain reload, so the fast path for an
    /// unchanged version only does one small file read before returning. The version record is written to
    /// Library/UnitySkills/install_sync.json (per-project, not committed to git), not to EditorPrefs — the
    /// latter is stored globally per machine, so having two projects open on the same machine would overwrite each other's record.
    ///
    /// Overwrite semantics are identical to manually clicking "Install" in the panel: the existing install
    /// mechanism has no content manifest to tell whether the user hand-edited a copy, and this service doesn't
    /// build a separate hash-check scheme — it just overwrites. Only targets detected as already installed are
    /// refreshed; new targets are never auto-installed. Everything runs on the main thread with no modal
    /// dialogs; a single target's failure only skips that target.
    /// </summary>
    public static class SkillInstallSyncService
    {
        public const int StateSchemaVersion = 1;

        // Survives across domain reloads via SessionState, cleared only on editor restart: once attempted this
        // session, it won't retry, avoiding a full file-copy pass on every recompile while sync keeps failing.
        private const string SessionAttemptedKey = "UnitySkills.InstallSync.Attempted";

        private static readonly string DefaultStateDir =
            Path.Combine(Application.dataPath, "../Library/UnitySkills");

        /// <summary>Test-only: redirects the state file to a temp directory, to avoid touching real project records.</summary>
        internal static string StateFilePathOverride;

        internal static string StateFilePath =>
            StateFilePathOverride ?? Path.Combine(DefaultStateDir, "install_sync.json");

        private static string _prefEnabled;

        // Same key pattern as SkillsHttpServer: includes InstanceId, so it's naturally isolated per project.
        internal static string PrefEnabled =>
            _prefEnabled ??= $"UnitySkills_{RegistryService.InstanceId}_AutoSyncInstalls";

        /// <summary>Whether installed AI tools are auto-synced after a package upgrade. Enabled by default.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        // ===== State model (Newtonsoft-serialized, field names are the JSON keys) =====

        public class SyncState
        {
            public int schemaVersion = StateSchemaVersion;
            public string lastSyncedVersion = "";
            public string lastSyncedAt = "";              // ISO-8601 UTC
            public List<string> lastSyncedTargets = new List<string>();
        }

        /// <summary>The result of one sync pass.</summary>
        public sealed class SyncReport
        {
            public readonly List<string> Updated = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public int SkippedNotInstalled;
            public int SkippedDuplicatePath;
        }

        // ===== Trigger =====

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

                // Deferred one tick until the editor is ready: PackageManager's package info isn't guaranteed
                // to be queryable during InitializeOnLoad, and resolving the template root directory depends on it.
                EditorApplication.delayCall += RunSyncOnce;
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] SkillInstallSyncService init failed: " + ex);
            }
        }

        /// <summary>
        /// The instant domain-reload gate, with no side effects: batchmode exclusion → version comparison →
        /// user toggle. Only worth queuing a sync if all three pass.
        ///
        /// The toggle check comes after the SessionState check (see caller): otherwise a session with the
        /// toggle off would prematurely set the "attempted" flag, and a user who flips the toggle on mid-session
        /// would have to wait for an editor restart for it to take effect.
        /// </summary>
        internal static bool ShouldSyncNow(bool batchMode)
        {
            // batchmode exclusion: headless flows like `unity test` / `run` / `build` also run
            // InitializeOnLoad; a CI build shouldn't get to rewrite the skill copies in the user's home directory.
            if (batchMode)
                return false;

            // Fast path: stop here if the version hasn't changed — the whole path only costs one small file read.
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

                // Don't write the record if any target failed — leave the retry for the next editor session
                // (this session is already blocked by SessionState and won't immediately re-run).
                if (report.Failed.Count == 0)
                    WriteState(SkillsLogger.Version, report.Updated);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("AI tool auto-sync aborted: " + ex.Message);
            }
        }

        // ===== Core logic (targets are injectable, to ease testing) =====

        /// <summary>Sync is needed when the recorded version differs from the current version (including when the record is missing).</summary>
        internal static bool NeedsSync(string recordedVersion, string currentVersion)
        {
            return !string.Equals(recordedVersion, currentVersion, StringComparison.Ordinal);
        }

        /// <summary>
        /// Runs install (= overwrite update) on each already-installed target, one by one. Uninstalled targets are always skipped, never freshly installed.
        /// </summary>
        internal static SyncReport SyncTargets(IEnumerable<SkillInstaller.InstallTarget> targets)
        {
            var report = new SyncReport();
            if (targets == null)
                return report;

            // Codex and Antigravity's project-level targets both point at the same .agents/skills directory;
            // dedupe by normalized full path so the same file isn't copied twice and double-counted in the log.
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

        // Log text follows the panel language (SkillsLocalization.Current). Strings are inlined in this file
        // rather than in Localization.cs: the Console uses Unity's built-in font, not the panel's font atlas;
        // putting them in Localization.cs would get these glyphs collected as UI characters by
        // UISkillsFontAssetBaker and forced into the atlas.
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

        // ===== State file =====

        /// <summary>Reads the version last auto-synced; returns null if the record is missing or corrupted.</summary>
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
                // A corrupted record is treated as no record: the next sync pass will rewrite it entirely.
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
