using UnityEngine;
using UnityEditor;
using System.Linq;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Skills related to the Console panel: log reading / capture / export and console toggles.
    /// </summary>
    public static class ConsoleSkills
    {
        // Console flag bitmasks; values match Unity's internal ConsoleWindow flags.
        private const int FlagClearOnPlay = 16;
        private const int FlagCollapse = 32;
        private const int FlagErrorPause = 256;

        /// <summary>
        /// Registers setting readers/writers so console flag changes can actually be rolled back via workflow undo/redo.
        /// Runs on domain load.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterSettingRestorers()
        {
            WorkflowSettingRestorerRegistry.Register("console.pauseOnError",
                () => JsonConvert.SerializeObject(GetConsoleFlagValue(FlagErrorPause, "DeveloperMode_ErrorPause")),
                json => { ConsoleSetPauseOnError(JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register("console.collapse",
                () => JsonConvert.SerializeObject(GetConsoleFlagValue(FlagCollapse, "UnitySkills_Console_Collapse")),
                json => { ConsoleSetCollapse(JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register("console.clearOnPlay",
                () => JsonConvert.SerializeObject(GetConsoleFlagValue(FlagClearOnPlay, "UnitySkills_Console_ClearOnPlay")),
                json => { ConsoleSetClearOnPlay(JsonConvert.DeserializeObject<bool>(json)); return true; });
        }

        /// <summary>
        /// Reads the current state of a console flag. The data source matches the write path
        /// (ConsoleWindow's s_ConsoleFlags field); falls back to EditorPrefs if unavailable.
        /// </summary>
        private static bool GetConsoleFlagValue(int flag, string prefFallbackKey)
        {
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            var flagField = consoleType?.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField != null)
            {
                int flags = (int)flagField.GetValue(null);
                return (flags & flag) != 0;
            }
            return EditorPrefs.GetBool(prefFallbackKey, false);
        }

        [UnitySkill("console_start_capture", "Start capturing console logs",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "capture", "logs", "start" },
            Outputs = new[] { "message" })]
        public static object ConsoleStartCapture()
        {
            // Capture remains a compatibility marker for callers that bracket a Play run. Managed
            // history is always collected, so starting a session never discards background evidence.
            ConsoleLogHistoryService.StartCaptureSession();
            return new { success = true, message = "Console capture session started", cursor = ConsoleLogHistoryService.GetCurrentSeq() };
        }

        [UnitySkill("console_stop_capture", "Stop capturing console logs",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "capture", "logs", "stop" },
            Outputs = new[] { "message", "capturedCount" })]
        public static object ConsoleStopCapture()
        {
            int count = ConsoleLogHistoryService.StopCaptureSession();
            return new { success = true, message = "Console capture stopped", capturedCount = count };
        }

        [UnitySkill("console_get_logs", "Get continuously captured Unity Console logs. Filter type/content/time, use stable cursors for fixed snapshots, and collapse duplicate full type/message/stack groups by default.",
            Category = SkillCategory.Console, Operation = SkillOperation.Query,
            Tags = new[] { "console", "logs", "filter", "errors" },
            Outputs = new[] { "count", "logs", "cursor", "nextCursor", "totalMatched", "hasMore", "complete", "dropped", "coverage" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ConsoleGetLogs(string type = "All", string filter = null, int limit = 100,
            bool collapse = true, string fromUtc = null, string toUtc = null, string groupId = null,
            string cursor = null, bool includeStackTrace = false)
        {
            const int allTypes = DebugSkills.ErrorModeMask | DebugSkills.WarningModeMask | DebugSkills.LogModeMask;
            if (!DebugSkills.TryParseLogTypeMask(type, allTypes, out var targetMask))
                return SkillParamUtil.InvalidValueError(type, "type", DebugSkills.LogTypeNames);
            if (limit < 1 || limit > ConsoleLogHistoryService.MaxReadLimit)
                return SkillParamUtil.InvalidValueError(limit.ToString(), "limit", new[] { "1-1000" });
            if (!IsValidUtc(fromUtc))
                return SkillParamUtil.InvalidValueError(fromUtc, "fromUtc", new[] { "ISO-8601 UTC" });
            if (!IsValidUtc(toUtc))
                return SkillParamUtil.InvalidValueError(toUtc, "toUtc", new[] { "ISO-8601 UTC" });
            if (HasInvertedUtcRange(fromUtc, toUtc))
                return SkillParamUtil.InvalidValueError(toUtc, "toUtc", new[] { "later than fromUtc" });

            // Native entries predating this service do not expose timestamps or full stacks. Import
            // them once as explicitly unknown-time records, without fabricating a timestamp or duplicating managed callbacks.
            ConsoleLogHistoryService.ImportNativeEntries(DebugSkills.ReadLogEntries(allTypes, null, 10000));
            bool capturing = ConsoleLogHistoryService.IsCapturing;
            long captureStart = capturing ? ConsoleLogHistoryService.CaptureStartSeq : 0;
            var page = ConsoleLogHistoryService.ReadPage(targetMask, filter, limit, collapse, fromUtc, toUtc, groupId, cursor, includeStackTrace, captureStart);
            if (!string.IsNullOrEmpty(page.errorParameter))
                return SkillParamUtil.InvalidValueError(cursor, page.errorParameter, new[] { "a cursor returned by console_get_logs" });
            return new { page.count, page.logs, source = capturing ? "capture_session" : "history", page.cursor, page.nextCursor, page.totalMatched, page.hasMore, page.complete, page.dropped, page.coverage };
        }

        [UnitySkill("console_clear", "Clear the Unity console",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "clear", "logs" },
            Outputs = new[] { "message" })]
        public static object ConsoleClear()
        {
            var assembly = System.Reflection.Assembly.GetAssembly(typeof(SceneView));
            var logEntries = assembly.GetType("UnityEditor.LogEntries");
            var clearMethod = logEntries.GetMethod("Clear");
            clearMethod.Invoke(null, null);

            ConsoleLogHistoryService.Clear();
            return new { success = true, message = "Console cleared" };
        }

        [UnitySkill("console_log", "Write a message to the console",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "log", "debug", "message" },
            Outputs = new[] { "logged", "warning" },
            RequiresInput = new[] { "message" })]
        public static object ConsoleLog(string message, string type = "log")
        {
            string normalized = type?.ToLower() ?? "log";
            string warning = null;
            switch (normalized)
            {
                case "warning":
                    Debug.LogWarning(message);
                    break;
                case "error":
                    Debug.LogError(message);
                    break;
                case "log":
                    Debug.Log(message);
                    break;
                default:
                    // An unrecognized type (e.g. "Fatal") is still written out as Log on a best-effort basis, but must return a warning:
                    // silently downgrading would hide a misspelled or made-up value, letting the caller believe the type actually took effect.
                    warning = $"Unrecognized type '{type}'; valid values are Log, Warning, Error. Logged as Log.";
                    Debug.Log(message);
                    break;
            }
            return new { success = true, logged = message, warning };
        }


        [UnitySkill("console_set_pause_on_error", "Enable or disable Error Pause in Play mode", TracksWorkflow = true,
            Category = SkillCategory.Console, Operation = SkillOperation.Modify,
            Tags = new[] { "console", "pause", "error", "playmode" },
            Outputs = new[] { "enabled" })]
        public static object ConsoleSetPauseOnError(bool enabled = true)
        {
            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("console.pauseOnError",
                    JsonConvert.SerializeObject(GetConsoleFlagValue(FlagErrorPause, "DeveloperMode_ErrorPause")),
                    "Console: Error Pause");

            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null) return new { error = "ConsoleWindow not found" };
            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField == null) { EditorPrefs.SetBool("DeveloperMode_ErrorPause", enabled); return new { success = true, enabled, note = "Set via EditorPrefs" }; }
            int flags = (int)flagField.GetValue(null);
            flags = enabled ? flags | 256 : flags & ~256;
            flagField.SetValue(null, flags);
            return new { success = true, enabled };
        }

        [UnitySkill("console_export", "Export retained console log history to a file, including timestamps and stack traces when available. Reports whether bounded retention truncated the export.",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "export", "file", "logs" },
            Outputs = new[] { "path", "count", "source" })]
        public static object ConsoleExport(string savePath = "Assets/console_log.txt")
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

            bool capturing = ConsoleLogHistoryService.IsCapturing;
            long captureStart = capturing ? ConsoleLogHistoryService.CaptureStartSeq : 0;
            var logs = ConsoleLogHistoryService.SnapshotAll(includeStackTrace: true, out var dropped, out var complete, captureStart);
            var lines = logs.Select(log => string.Concat("[", log.utc ?? "unknown-time", "] [", log.type, "] ", log.message,
                string.IsNullOrEmpty(log.stackTrace) ? string.Empty : System.Environment.NewLine + log.stackTrace));
            System.IO.File.WriteAllLines(savePath, lines);
            return new { success = true, path = savePath, count = logs.Count, source = capturing ? "capture_session" : "history", complete, dropped, truncated = !complete };
        }

        [UnitySkill("console_get_stats", "Get statistics for retained console log history, including bounded-retention completeness.",
            Category = SkillCategory.Console, Operation = SkillOperation.Query,
            Tags = new[] { "console", "stats", "count", "summary" },
            Outputs = new[] { "total", "logs", "warnings", "errors", "source", "complete", "dropped" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ConsoleGetStats()
        {
            bool capturing = ConsoleLogHistoryService.IsCapturing;
            long captureStart = capturing ? ConsoleLogHistoryService.CaptureStartSeq : 0;
            var entries = ConsoleLogHistoryService.SnapshotAll(includeStackTrace: false, out var dropped, out var complete, captureStart);
            int errors = entries.Count(entry => entry.type == "Error");
            int warnings = entries.Count(entry => entry.type == "Warning");
            int exceptions = entries.Count(entry => entry.type == "Exception");
            int asserts = entries.Count(entry => entry.type == "Assert");
            int logs = entries.Count - errors - warnings - exceptions - asserts;
            return new { success = true, total = entries.Count, source = capturing ? "capture_session" : "history", logs, warnings, errors, exceptions, asserts, complete, dropped, truncated = !complete };
        }

        private static bool IsValidUtc(string value)
        {
            return string.IsNullOrWhiteSpace(value) || TryParseUtc(value, out _);
        }

        private static bool HasInvertedUtcRange(string fromUtc, string toUtc)
        {
            if (string.IsNullOrWhiteSpace(fromUtc) || string.IsNullOrWhiteSpace(toUtc)) return false;
            TryParseUtc(fromUtc, out var from);
            TryParseUtc(toUtc, out var to);
            return from >= to;
        }

        private static bool TryParseUtc(string value, out System.DateTime utc)
        {
            utc = default(System.DateTime);
            string candidate = value?.Trim();
            if (string.IsNullOrEmpty(candidate) || candidate.IndexOf('T') < 0 ||
                !(candidate.EndsWith("Z", System.StringComparison.OrdinalIgnoreCase) || candidate.EndsWith("+00:00", System.StringComparison.Ordinal) || candidate.EndsWith("-00:00", System.StringComparison.Ordinal)))
                return false;
            if (!System.DateTimeOffset.TryParse(candidate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed) || parsed.Offset != System.TimeSpan.Zero)
                return false;
            utc = parsed.UtcDateTime;
            return true;
        }

        [UnitySkill("console_set_collapse", "Set console log collapse mode", TracksWorkflow = true,
            Category = SkillCategory.Console, Operation = SkillOperation.Modify,
            Tags = new[] { "console", "collapse", "settings" },
            Outputs = new[] { "setting", "enabled" })]
        public static object ConsoleSetCollapse(bool enabled)
        {
            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("console.collapse",
                    JsonConvert.SerializeObject(GetConsoleFlagValue(FlagCollapse, "UnitySkills_Console_Collapse")),
                    "Console: Collapse");

            return SetConsoleFlag(FlagCollapse, enabled, "Collapse");
        }

        [UnitySkill("console_set_clear_on_play", "Set clear on play mode", TracksWorkflow = true,
            Category = SkillCategory.Console, Operation = SkillOperation.Modify,
            Tags = new[] { "console", "clear", "playmode", "settings" },
            Outputs = new[] { "setting", "enabled" })]
        public static object ConsoleSetClearOnPlay(bool enabled)
        {
            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("console.clearOnPlay",
                    JsonConvert.SerializeObject(GetConsoleFlagValue(FlagClearOnPlay, "UnitySkills_Console_ClearOnPlay")),
                    "Console: Clear On Play");

            return SetConsoleFlag(FlagClearOnPlay, enabled, "ClearOnPlay");
        }

        private static object SetConsoleFlag(int flag, bool enabled, string name)
        {
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null) return new { error = "ConsoleWindow not found" };

            // Unity 6+: prefer the ConsoleWindow.SetConsoleFlag method.
            var setFlagMethod = consoleType.GetMethod("SetConsoleFlag", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (setFlagMethod != null)
            {
                try { setFlagMethod.Invoke(null, new object[] { flag, enabled }); return new { success = true, setting = name, enabled }; }
                catch { /* fall through */ }
            }

            // Older versions: modify the s_ConsoleFlags field directly.
            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField != null)
            {
                int flags = (int)flagField.GetValue(null);
                flags = enabled ? flags | flag : flags & ~flag;
                flagField.SetValue(null, flags);
                return new { success = true, setting = name, enabled };
            }

            // Fall back further: the LogEntries API.
            var logEntriesType = System.Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType != null)
            {
                var setMethod = logEntriesType.GetMethod("SetConsoleFlag", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (setMethod != null)
                {
                    try { setMethod.Invoke(null, new object[] { flag, enabled }); return new { success = true, setting = name, enabled }; }
                    catch { /* fall through */ }
                }
            }

            // Last resort: write to EditorPrefs only.
            EditorPrefs.SetBool("UnitySkills_Console_" + name, enabled);
            return new { success = true, setting = name, enabled, note = "Set via EditorPrefs fallback" };
        }
    }
}

// Producer:Betsy
