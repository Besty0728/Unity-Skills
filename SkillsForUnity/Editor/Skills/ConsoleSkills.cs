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

        [UnitySkill("console_get_logs", "Get captured console logs")]
        public static object ConsoleGetLogs(string filter = null, int limit = 100)
        {
            IEnumerable<LogEntry> results = _logs;

            if (!string.IsNullOrEmpty(filter))
                results = results.Where(l => l.message.Contains(filter));

            var logs = results.TakeLast(limit).Select(l => new
            {
                message = l.message,
                type = l.type.ToString(),
                time = l.time.ToString("HH:mm:ss.fff")
            }).ToArray();

            return new { count = logs.Length, logs };
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

        [UnitySkill("console_export", "Export captured logs to a file")]
        public static object ConsoleExport(string savePath = "Assets/console_log.txt")
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var lines = _logs.Select(l => $"[{l.time:HH:mm:ss.fff}] [{l.type}] {l.message}");
            System.IO.File.WriteAllLines(savePath, lines);
            return new { success = true, path = savePath, count = _logs.Count };
        }

        [UnitySkill("console_get_stats", "Get log statistics (count by type)")]
        public static object ConsoleGetStats()
        {
            return new
            {
                success = true, total = _logs.Count,
                logs = _logs.Count(l => l.type == LogType.Log),
                warnings = _logs.Count(l => l.type == LogType.Warning),
                errors = _logs.Count(l => l.type == LogType.Error),
                exceptions = _logs.Count(l => l.type == LogType.Exception),
                asserts = _logs.Count(l => l.type == LogType.Assert)
            };
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
