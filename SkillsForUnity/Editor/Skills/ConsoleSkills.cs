using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Console 面板相关技能：日志读取 / 捕获 / 导出与控制台开关。
    /// </summary>
    public static class ConsoleSkills
    {
        private static readonly List<LogEntry> _logs = new List<LogEntry>();
        private static readonly object _logLock = new object();
        private static bool _capturing;

        // 控制台标志位掩码，取值与 Unity 内部 ConsoleWindow 的 flags 保持一致。
        private const int FlagClearOnPlay = 16;
        private const int FlagCollapse = 32;
        private const int FlagErrorPause = 256;

        /// <summary>
        /// 注册设置项的读写器，使控制台标志的改动能真正通过工作流 undo/redo 回滚。
        /// 在域加载时执行。
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
        /// 读取某个控制台标志的当前状态。数据源与写入路径保持一致
        /// （ConsoleWindow 的 s_ConsoleFlags 字段），取不到时退回 EditorPrefs。
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

        private class LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public System.DateTime time;
        }

        [UnitySkill("console_start_capture", "Start capturing console logs",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "capture", "logs", "start" },
            Outputs = new[] { "message" })]
        public static object ConsoleStartCapture()
        {
            if (!_capturing)
            {
                Application.logMessageReceived += OnLogMessage;
                _capturing = true;
            }
            lock (_logLock) { _logs.Clear(); }
            return new { success = true, message = "Console capture started" };
        }

        [UnitySkill("console_stop_capture", "Stop capturing console logs",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "capture", "logs", "stop" },
            Outputs = new[] { "message", "capturedCount" })]
        public static object ConsoleStopCapture()
        {
            if (_capturing)
            {
                Application.logMessageReceived -= OnLogMessage;
                _capturing = false;
            }
            int count;
            lock (_logLock) { count = _logs.Count; }
            return new { success = true, message = "Console capture stopped", capturedCount = count };
        }

        [UnitySkill("console_get_logs", "Get Unity Console logs. Reads existing console history directly (no setup needed). Use type=All/Error/Warning/Log to filter. When console_start_capture is active, returns captured logs with timestamps instead.",
            Category = SkillCategory.Console, Operation = SkillOperation.Query,
            Tags = new[] { "console", "logs", "filter", "errors" },
            Outputs = new[] { "count", "logs", "source" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ConsoleGetLogs(string type = "All", string filter = null, int limit = 100)
        {
            if (_capturing)
            {
                // 捕获模式：返回缓冲区里带时间戳的日志。
                lock (_logLock)
                {
                    IEnumerable<LogEntry> results = _logs;
                    if (type != "All")
                        results = results.Where(l => CapturedLogMatchesType(l.type, type));
                    if (!string.IsNullOrEmpty(filter))
                        results = results.Where(l => l.message.Contains(filter));

                    var captured = results.TakeLast(limit).Select(l => new
                    {
                        type = l.type.ToString(),
                        message = l.message,
                        time = l.time.ToString("HH:mm:ss.fff")
                    }).ToArray();
                    return new { count = captured.Length, logs = captured, source = "capture" };
                }
            }

            // 直读模式：通过反射 LogEntries 读取 Unity Console 里已有的条目。
            int targetMask = 0;
            if (type == "All" || type.Contains("Error"))   targetMask |= DebugSkills.ErrorModeMask;
            if (type == "All" || type.Contains("Warning")) targetMask |= DebugSkills.WarningModeMask;
            if (type == "All" || type.Contains("Log"))     targetMask |= DebugSkills.LogModeMask;
            if (targetMask == 0) targetMask = DebugSkills.ErrorModeMask | DebugSkills.WarningModeMask | DebugSkills.LogModeMask;

            var logs = DebugSkills.ReadLogEntries(targetMask, filter, limit);
            return new { count = logs.Count, logs, source = "console" };
        }

        private static bool CapturedLogMatchesType(LogType logType, string typeFilter)
        {
            switch (typeFilter)
            {
                case "Error":   return logType == LogType.Error || logType == LogType.Exception || logType == LogType.Assert;
                case "Warning": return logType == LogType.Warning;
                case "Log":     return logType == LogType.Log;
                default:        return true;
            }
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

            lock (_logLock) { _logs.Clear(); }
            return new { success = true, message = "Console cleared" };
        }

        [UnitySkill("console_log", "Write a message to the console",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "log", "debug", "message" },
            Outputs = new[] { "logged", "warning" })]
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
                    // 无法识别的 type（如 "Fatal"）仍按 Log 尽力写出，但必须回一条 warning：
                    // 静默降级会把拼错或凭空编造的取值瞒下来，让调用方以为 type 生效了。
                    warning = $"Unrecognized type '{type}'; valid values are Log, Warning, Error. Logged as Log.";
                    Debug.Log(message);
                    break;
            }
            return new { success = true, logged = message, warning };
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logLock)
            {
                _logs.Add(new LogEntry
                {
                    message = message,
                    stackTrace = stackTrace,
                    type = type,
                    time = System.DateTime.Now
                });

                // 只保留最近 1000 条。
                if (_logs.Count > 1000)
                    _logs.RemoveAt(0);
            }
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

        [UnitySkill("console_export", "Export console logs to a file. Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history (no setup needed).",
            Category = SkillCategory.Console, Operation = SkillOperation.Execute,
            Tags = new[] { "console", "export", "file", "logs" },
            Outputs = new[] { "path", "count", "source" })]
        public static object ConsoleExport(string savePath = "Assets/console_log.txt")
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

            // 只有 capture 分支读 _logs，因此判据必须只看 _capturing。
            // console_stop_capture 之后 _logs 仍保留上一轮捕获的条目（有意如此，
            // 这样 console_get_logs 那条同样受 _capturing 把关的分支也看不到它们），
            // 若这里改判 _logs.Count > 0，就会永远导出那份过期快照。
            if (_capturing)
            {
                lock (_logLock)
                {
                    var lines = _logs.Select(l => $"[{l.time:HH:mm:ss.fff}] [{l.type}] {l.message}");
                    System.IO.File.WriteAllLines(savePath, lines);
                    return new { success = true, path = savePath, count = _logs.Count, source = "capture" };
                }
            }

            // 直读模式：没有捕获缓冲时直接读 Unity Console。
            int allMask = DebugSkills.ErrorModeMask | DebugSkills.WarningModeMask | DebugSkills.LogModeMask;
            var entries = DebugSkills.ReadLogEntries(allMask, null, 1000);
            var directLines = entries.Select(e => { dynamic d = e; return $"[{d.type}] {d.message}"; });
            System.IO.File.WriteAllLines(savePath, directLines.Cast<string>());
            return new { success = true, path = savePath, count = entries.Count, source = "console" };
        }

        [UnitySkill("console_get_stats", "Get log statistics (count by type). Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history.",
            Category = SkillCategory.Console, Operation = SkillOperation.Query,
            Tags = new[] { "console", "stats", "count", "summary" },
            Outputs = new[] { "total", "logs", "warnings", "errors", "source" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ConsoleGetStats()
        {
            // 同 console_export 的规则：捕获停止后 _logs 只是残留快照，
            // 判据必须只看 _capturing，否则会一直报 "source: capture"。
            if (_capturing)
            {
                lock (_logLock)
                {
                    return new
                    {
                        success = true, total = _logs.Count, source = "capture",
                        logs = _logs.Count(l => l.type == LogType.Log),
                        warnings = _logs.Count(l => l.type == LogType.Warning),
                        errors = _logs.Count(l => l.type == LogType.Error),
                        exceptions = _logs.Count(l => l.type == LogType.Exception),
                        asserts = _logs.Count(l => l.type == LogType.Assert)
                    };
                }
            }

            // 直读模式：直接读 Unity Console。
            int allMask = DebugSkills.ErrorModeMask | DebugSkills.WarningModeMask | DebugSkills.LogModeMask;
            var entries = DebugSkills.ReadLogEntries(allMask, null, 10000);
            int errCount = 0, warnCount = 0, logCount = 0;
            foreach (dynamic e in entries)
            {
                switch ((string)e.type)
                {
                    case "Error":   errCount++;  break;
                    case "Warning": warnCount++; break;
                    default:        logCount++;  break;
                }
            }
            return new { success = true, total = entries.Count, source = "console", logs = logCount, warnings = warnCount, errors = errCount };
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

            // Unity 6+：优先走 ConsoleWindow.SetConsoleFlag 方法。
            var setFlagMethod = consoleType.GetMethod("SetConsoleFlag", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (setFlagMethod != null)
            {
                try { setFlagMethod.Invoke(null, new object[] { flag, enabled }); return new { success = true, setting = name, enabled }; }
                catch { /* fall through */ }
            }

            // 旧版本：直接改 s_ConsoleFlags 字段。
            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField != null)
            {
                int flags = (int)flagField.GetValue(null);
                flags = enabled ? flags | flag : flags & ~flag;
                flagField.SetValue(null, flags);
                return new { success = true, setting = name, enabled };
            }

            // 再退一步：LogEntries API。
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

            // 最后兜底：只写 EditorPrefs。
            EditorPrefs.SetBool("UnitySkills_Console_" + name, enabled);
            return new { success = true, setting = name, enabled, note = "Set via EditorPrefs fallback" };
        }
    }
}

// Producer:Betsy
