using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Console and debug skills.
    /// </summary>
    public static class ConsoleSkills
    {
        private static readonly List<LogEntry> _logs = new List<LogEntry>();
        private static bool _capturing;

        private class LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public System.DateTime time;
        }

        [UnitySkill("console_start_capture", "Start capturing console logs")]
        public static object ConsoleStartCapture()
        {
            if (!_capturing)
            {
                Application.logMessageReceived += OnLogMessage;
                _capturing = true;
            }
            _logs.Clear();
            return new { success = true, message = "Console capture started" };
        }

        [UnitySkill("console_stop_capture", "Stop capturing console logs")]
        public static object ConsoleStopCapture()
        {
            if (_capturing)
            {
                Application.logMessageReceived -= OnLogMessage;
                _capturing = false;
            }
            return new { success = true, message = "Console capture stopped", capturedCount = _logs.Count };
        }

        [UnitySkill("console_get_logs", "Get Unity Console logs. Reads existing console history directly (no setup needed). Use type=All/Error/Warning/Log to filter. When console_start_capture is active, returns captured logs with timestamps instead.")]
        public static object ConsoleGetLogs(string type = "All", string filter = null, int limit = 100)
        {
            if (_capturing)
            {
                // Capture mode: return buffered logs with timestamps
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

            // Direct mode: read existing entries from Unity Console via LogEntries reflection
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

        [UnitySkill("console_clear", "Clear the Unity console")]
        public static object ConsoleClear()
        {
            var assembly = System.Reflection.Assembly.GetAssembly(typeof(SceneView));
            var logEntries = assembly.GetType("UnityEditor.LogEntries");
            var clearMethod = logEntries.GetMethod("Clear");
            clearMethod.Invoke(null, null);

            _logs.Clear();
            return new { success = true, message = "Console cleared" };
        }

        [UnitySkill("console_log", "Write a message to the console")]
        public static object ConsoleLog(string message, string type = "log")
        {
            switch (type.ToLower())
            {
                case "warning":
                    Debug.LogWarning(message);
                    break;
                case "error":
                    Debug.LogError(message);
                    break;
                default:
                    Debug.Log(message);
                    break;
            }
            return new { success = true, logged = message };
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            _logs.Add(new LogEntry
            {
                message = message,
                stackTrace = stackTrace,
                type = type,
                time = System.DateTime.Now
            });

            // Keep only last 1000 entries
            if (_logs.Count > 1000)
                _logs.RemoveAt(0);
        }

        [UnitySkill("console_set_pause_on_error", "Enable or disable Error Pause in Play mode")]
        public static object ConsoleSetPauseOnError(bool enabled = true)
        {
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null) return new { error = "ConsoleWindow not found" };
            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField == null) { EditorPrefs.SetBool("DeveloperMode_ErrorPause", enabled); return new { success = true, enabled, note = "Set via EditorPrefs" }; }
            int flags = (int)flagField.GetValue(null);
            flags = enabled ? flags | 256 : flags & ~256;
            flagField.SetValue(null, flags);
            return new { success = true, enabled };
        }

        [UnitySkill("console_export", "Export console logs to a file. Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history (no setup needed).")]
        public static object ConsoleExport(string savePath = "Assets/console_log.txt")
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

            if (_capturing || _logs.Count > 0)
            {
                var lines = _logs.Select(l => $"[{l.time:HH:mm:ss.fff}] [{l.type}] {l.message}");
                System.IO.File.WriteAllLines(savePath, lines);
                return new { success = true, path = savePath, count = _logs.Count, source = "capture" };
            }

            // Direct mode: read from Unity Console when no capture buffer is available
            int allMask = DebugSkills.ErrorModeMask | DebugSkills.WarningModeMask | DebugSkills.LogModeMask;
            var entries = DebugSkills.ReadLogEntries(allMask, null, 1000);
            var directLines = entries.Select(e => { dynamic d = e; return $"[{d.type}] {d.message}"; });
            System.IO.File.WriteAllLines(savePath, directLines.Cast<string>());
            return new { success = true, path = savePath, count = entries.Count, source = "console" };
        }

        [UnitySkill("console_get_stats", "Get log statistics (count by type). Uses captured buffer when console_start_capture is active; otherwise reads directly from Unity Console history.")]
        public static object ConsoleGetStats()
        {
            if (_capturing || _logs.Count > 0)
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

            // Direct mode: read from Unity Console
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

        [UnitySkill("console_set_collapse", "Set console log collapse mode")]
        public static object ConsoleSetCollapse(bool enabled)
        {
            return SetConsoleFlag(32, enabled, "Collapse");
        }

        [UnitySkill("console_set_clear_on_play", "Set clear on play mode")]
        public static object ConsoleSetClearOnPlay(bool enabled)
        {
            return SetConsoleFlag(16, enabled, "ClearOnPlay");
        }

        private static object SetConsoleFlag(int flag, bool enabled, string name)
        {
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null) return new { error = "ConsoleWindow not found" };

            // Unity 6+: try SetConsoleFlag method
            var setFlagMethod = consoleType.GetMethod("SetConsoleFlag", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (setFlagMethod != null)
            {
                try { setFlagMethod.Invoke(null, new object[] { flag, enabled }); return new { success = true, setting = name, enabled }; }
                catch { /* fall through */ }
            }

            // Legacy: try s_ConsoleFlags field
            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField != null)
            {
                int flags = (int)flagField.GetValue(null);
                flags = enabled ? flags | flag : flags & ~flag;
                flagField.SetValue(null, flags);
                return new { success = true, setting = name, enabled };
            }

            // Fallback: LogEntries API
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

            // Final fallback: EditorPrefs
            EditorPrefs.SetBool("UnitySkills_Console_" + name, enabled);
            return new { success = true, setting = name, enabled, note = "Set via EditorPrefs fallback" };
        }
    }
}
