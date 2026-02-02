using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Debug Enhancement Skills - Console control, error handling.
    /// </summary>
    public static class DebugEnhanceSkills
    {
        [UnitySkill("console_get_logs", "Get recent console log entries")]
        public static object ConsoleGetLogs(string filter = null, int limit = 50)
        {
            // Use reflection to access internal LogEntries
            var logEntriesType = System.Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return new { success = false, error = "Cannot access LogEntries" };

            var getCountMethod = logEntriesType.GetMethod("GetCount", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var getEntryMethod = logEntriesType.GetMethod("GetEntryInternal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var startMethod = logEntriesType.GetMethod("StartGettingEntries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var endMethod = logEntriesType.GetMethod("EndGettingEntries", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

            if (getCountMethod == null || startMethod == null)
                return new { success = false, error = "LogEntries API not available" };

            var logEntryType = System.Type.GetType("UnityEditor.LogEntry, UnityEditor");
            if (logEntryType == null)
                return new { success = false, error = "LogEntry type not found" };

            var logs = new List<object>();
            int count = (int)getCountMethod.Invoke(null, null);
            int errorCount = 0, warningCount = 0, logCount = 0;

            startMethod.Invoke(null, null);
            try
            {
                var entry = System.Activator.CreateInstance(logEntryType);
                var messageField = logEntryType.GetField("message");
                var modeField = logEntryType.GetField("mode");

                for (int i = count - 1; i >= 0 && logs.Count < limit; i--)
                {
                    getEntryMethod.Invoke(null, new object[] { i, entry });
                    var message = messageField.GetValue(entry) as string ?? "";
                    var mode = (int)modeField.GetValue(entry);

                    // Mode: 0=Log, 1=Warning, 256+=Error
                    string type = mode >= 256 ? "Error" : (mode == 1 ? "Warning" : "Log");
                    
                    if (type == "Error") errorCount++;
                    else if (type == "Warning") warningCount++;
                    else logCount++;

                    if (!string.IsNullOrEmpty(filter) && !message.ToLower().Contains(filter.ToLower()))
                        continue;

                    // Truncate very long messages
                    if (message.Length > 500) message = message.Substring(0, 500) + "...";

                    logs.Add(new { type, message = message.Split('\n')[0] }); // First line only
                }
            }
            finally
            {
                endMethod.Invoke(null, null);
            }

            return new
            {
                success = true,
                total = count,
                errors = errorCount,
                warnings = warningCount,
                logs = logCount,
                entries = logs
            };
        }

        [UnitySkill("console_clear", "Clear the Unity console")]
        public static object ConsoleClear()
        {
            var logEntriesType = System.Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntriesType == null)
                return new { success = false, error = "Cannot access LogEntries" };

            var clearMethod = logEntriesType.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (clearMethod != null)
            {
                clearMethod.Invoke(null, null);
                return new { success = true, message = "Console cleared" };
            }
            return new { success = false, error = "Clear method not found" };
        }

        [UnitySkill("debug_log", "Write a message to the Unity console")]
        public static object DebugLog(string message, string type = "Log")
        {
            switch (type.ToLower())
            {
                case "warning":
                    Debug.LogWarning($"[UnitySkills] {message}");
                    break;
                case "error":
                    Debug.LogError($"[UnitySkills] {message}");
                    break;
                default:
                    Debug.Log($"[UnitySkills] {message}");
                    break;
            }
            return new { success = true, type, message };
        }

        [UnitySkill("editor_set_pause_on_error", "Enable or disable 'Error Pause' in Play mode")]
        public static object EditorSetPauseOnError(bool enabled = true)
        {
            // Access Console window flags via reflection
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null)
                return new { success = false, error = "ConsoleWindow not found" };

            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField == null)
            {
                // Alternative: EditorPrefs
                EditorPrefs.SetBool("DeveloperMode_ErrorPause", enabled);
                return new { success = true, enabled, note = "Set via EditorPrefs" };
            }

            // Flag 256 = ErrorPause
            int flags = (int)flagField.GetValue(null);
            if (enabled)
                flags |= 256;
            else
                flags &= ~256;
            flagField.SetValue(null, flags);

            return new { success = true, enabled };
        }
    }
}
