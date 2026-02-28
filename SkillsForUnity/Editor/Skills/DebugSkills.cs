using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace UnitySkills
{
    /// <summary>
    /// Debug skills - self-healing, active error checking, compilation control.
    /// </summary>
    public static class DebugSkills
    {
        #region LogEntries Reflection Cache

        // Cached reflection members (initialized once, reused across calls)
        private static bool _reflectionInitialized;
        private static bool _reflectionFailed;
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _getEntryMethod;
        private static FieldInfo _modeField;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;

        // Mode bit constants from Unity LogEntry.mode (may vary by version)
        // Reference: unity-mcp ReadConsole.cs + Coplay ConsoleReflectionHelper.cs
        private const int ModeBitError = 1 << 0;              // 1
        private const int ModeBitAssert = 1 << 1;             // 2
        private const int ModeBitWarning = 1 << 2;            // 4
        private const int ModeBitLog = 1 << 3;                // 8
        private const int ModeBitFatal = 1 << 4;              // 16 (Exception)
        private const int ModeBitAssetImportWarning = 1 << 8; // 256
        private const int ModeBitScriptingError = 1 << 9;     // 512
        private const int ModeBitScriptingWarning = 1 << 10;  // 1024
        private const int ModeBitScriptingLog = 1 << 11;      // 2048
        private const int ModeBitScriptCompileError = 1 << 12;// 4096
        private const int ModeBitScriptCompileWarning = 1 << 13; // 8192
        private const int ModeBitScriptingException = 1 << 18;   // 262144
        private const int ModeBitScriptingAssertion = 1 << 22;   // 4194304

        private const int ErrorMask = ModeBitError | ModeBitAssert | ModeBitFatal
            | ModeBitScriptingError | ModeBitScriptCompileError
            | ModeBitScriptingException | ModeBitScriptingAssertion;
        private const int WarningMask = ModeBitWarning | ModeBitAssetImportWarning
            | ModeBitScriptingWarning | ModeBitScriptCompileWarning;
        private const int LogMask = ModeBitLog | ModeBitScriptingLog;

        /// <summary>
        /// Initialize reflection for LogEntries/LogEntry access.
        /// Uses BindingFlags.NonPublic to handle internal APIs across Unity versions.
        /// </summary>
        private static bool InitializeReflection()
        {
            if (_reflectionInitialized) return true;
            if (_reflectionFailed) return false;

            try
            {
                const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var assembly = typeof(EditorApplication).Assembly;
                _logEntriesType = assembly.GetType("UnityEditor.LogEntries");
                if (_logEntriesType == null)
                    throw new Exception("Could not find UnityEditor.LogEntries");

                _startGettingEntriesMethod = _logEntriesType.GetMethod("StartGettingEntries", staticFlags);
                _endGettingEntriesMethod = _logEntriesType.GetMethod("EndGettingEntries", staticFlags);
                _getCountMethod = _logEntriesType.GetMethod("GetCount", staticFlags);
                _getEntryMethod = _logEntriesType.GetMethod("GetEntryInternal", staticFlags);

                if (_startGettingEntriesMethod == null) throw new Exception("LogEntries.StartGettingEntries not found");
                if (_endGettingEntriesMethod == null) throw new Exception("LogEntries.EndGettingEntries not found");
                if (_getCountMethod == null) throw new Exception("LogEntries.GetCount not found");
                if (_getEntryMethod == null) throw new Exception("LogEntries.GetEntryInternal not found");

                _logEntryType = assembly.GetType("UnityEditor.LogEntry");
                if (_logEntryType == null)
                    throw new Exception("Could not find UnityEditor.LogEntry");

                _modeField = _logEntryType.GetField("mode", instanceFlags);
                _messageField = _logEntryType.GetField("message", instanceFlags);
                _fileField = _logEntryType.GetField("file", instanceFlags);
                _lineField = _logEntryType.GetField("line", instanceFlags);

                if (_modeField == null) throw new Exception("LogEntry.mode field not found");
                if (_messageField == null) throw new Exception("LogEntry.message field not found");

                _reflectionInitialized = true;
                return true;
            }
            catch (Exception e)
            {
                _reflectionFailed = true;
                SkillsLogger.LogError($"LogEntries reflection init failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Classify log type from mode bits. Falls back to message content analysis.
        /// </summary>
        private static string ClassifyLogType(int mode, string message)
        {
            // Primary: mode bits
            if ((mode & (ModeBitFatal | ModeBitScriptingException)) != 0) return "Exception";
            if ((mode & (ModeBitError | ModeBitScriptingError | ModeBitScriptCompileError)) != 0) return "Error";
            if ((mode & (ModeBitAssert | ModeBitScriptingAssertion)) != 0) return "Assert";
            if ((mode & (ModeBitWarning | ModeBitScriptingWarning | ModeBitScriptCompileWarning | ModeBitAssetImportWarning)) != 0) return "Warning";
            if ((mode & (ModeBitLog | ModeBitScriptingLog)) != 0) return "Log";

            // Fallback: infer from message content (handles version differences)
            if (!string.IsNullOrEmpty(message))
            {
                if (message.IndexOf("LogError", StringComparison.OrdinalIgnoreCase) >= 0) return "Error";
                if (message.IndexOf("LogWarning", StringComparison.OrdinalIgnoreCase) >= 0) return "Warning";
                if (message.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0) return "Exception";
                if (message.IndexOf("Assertion", StringComparison.OrdinalIgnoreCase) >= 0) return "Assert";
                if (message.IndexOf(" error CS", StringComparison.OrdinalIgnoreCase) >= 0) return "Error";
                if (message.IndexOf(" warning CS", StringComparison.OrdinalIgnoreCase) >= 0) return "Warning";
            }

            return "Log";
        }

        /// <summary>
        /// Check if a classified type matches the requested filter.
        /// </summary>
        private static bool MatchesTypeFilter(string classifiedType, string typeFilter)
        {
            if (typeFilter.Contains("Error"))
                if (classifiedType == "Error" || classifiedType == "Exception" || classifiedType == "Assert") return true;
            if (typeFilter.Contains("Warning"))
                if (classifiedType == "Warning") return true;
            if (typeFilter.Contains("Log"))
                if (classifiedType == "Log") return true;
            return false;
        }

        /// <summary>
        /// Read log entries from Unity's internal LogEntries API via reflection.
        /// Used by debug_get_logs, debug_get_errors, and console_get_logs.
        /// </summary>
        internal static object ReadLogEntries(string type = "Error", string filter = null, int limit = 50)
        {
            if (!InitializeReflection())
                return new { error = "LogEntries reflection not available. Check Unity version compatibility.", count = 0, logs = new object[0] };

            var results = new List<object>();

            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int count = (int)_getCountMethod.Invoke(null, null);

                var logEntryInstance = Activator.CreateInstance(_logEntryType);
                int found = 0;

                // Iterate backwards (newest first)
                for (int i = count - 1; i >= 0 && found < limit; i--)
                {
                    _getEntryMethod.Invoke(null, new object[] { i, logEntryInstance });

                    string msg = (string)_messageField.GetValue(logEntryInstance);
                    if (string.IsNullOrEmpty(msg)) continue;

                    int mode = (int)_modeField.GetValue(logEntryInstance);
                    string classifiedType = ClassifyLogType(mode, msg);

                    if (!MatchesTypeFilter(classifiedType, type)) continue;

                    if (!string.IsNullOrEmpty(filter) && msg.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string file = _fileField != null ? (string)_fileField.GetValue(logEntryInstance) : "";
                    int line = _lineField != null ? (int)_lineField.GetValue(logEntryInstance) : 0;

                    // Extract first line as message, rest as stack trace
                    string[] msgLines = msg.Split('\n');
                    string firstLine = msgLines[0];

                    results.Add(new
                    {
                        type = classifiedType,
                        message = firstLine.Length > 500 ? firstLine.Substring(0, 500) + "..." : firstLine,
                        file,
                        line
                    });
                    found++;
                }
            }
            catch (Exception e)
            {
                return new { error = $"Failed to read log entries: {e.Message}", count = results.Count, logs = results };
            }
            finally
            {
                try { _endGettingEntriesMethod.Invoke(null, null); }
                catch { /* Ensure EndGettingEntries is always called */ }
            }

            return new
            {
                count = results.Count,
                logs = results
            };
        }

        #endregion

        [UnitySkill("debug_get_errors", "Get only active errors and exceptions from the console logs.")]
        public static object DebugGetErrors(int limit = 50) => ReadLogEntries("Error", null, limit);

        [UnitySkill("debug_get_logs", "Get console logs filtered by type (Error/Warning/Log) and content.")]
        public static object DebugGetLogs(string type = "Error", string filter = null, int limit = 50)
            => ReadLogEntries(type, filter, limit);

        [UnitySkill("debug_check_compilation", "Check if Unity is currently compiling scripts.")]
        public static object DebugCheckCompilation()
        {
            return new
            {
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            };
        }

        [UnitySkill("debug_force_recompile", "Force script recompilation.")]
        public static object DebugForceRecompile()
        {
            AssetDatabase.Refresh();
            CompilationPipeline.RequestScriptCompilation();
            return new { success = true, message = "Compilation requested" };
        }

        [UnitySkill("debug_get_system_info", "Get Editor and System capabilities.")]
        public static object DebugGetSystemInfo()
        {
            return new
            {
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                deviceModel = SystemInfo.deviceModel,
                processorType = SystemInfo.processorType,
                systemMemorySize = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsMemorySize = SystemInfo.graphicsMemorySize,
                editorSkin = EditorGUIUtility.isProSkin ? "Dark" : "Light"
            };
        }

        [UnitySkill("debug_get_stack_trace", "Get stack trace for a log entry by index")]
        public static object DebugGetStackTrace(int entryIndex)
        {
            if (!InitializeReflection())
                return new { error = "LogEntries reflection not available" };

            try
            {
                _startGettingEntriesMethod.Invoke(null, null);
                int count = (int)_getCountMethod.Invoke(null, null);

                if (entryIndex < 0 || entryIndex >= count)
                {
                    _endGettingEntriesMethod.Invoke(null, null);
                    return new { error = $"Index {entryIndex} out of range (0-{count - 1})" };
                }

                var entry = Activator.CreateInstance(_logEntryType);
                _getEntryMethod.Invoke(null, new object[] { entryIndex, entry });
                var msg = (string)_messageField.GetValue(entry);
                _endGettingEntriesMethod.Invoke(null, null);

                var lines = msg.Split('\n');
                return new { index = entryIndex, message = lines[0], stackTrace = string.Join("\n", lines.Skip(1)) };
            }
            catch (Exception e)
            {
                try { _endGettingEntriesMethod.Invoke(null, null); } catch { }
                return new { error = $"Failed to get stack trace: {e.Message}" };
            }
        }

        [UnitySkill("debug_get_assembly_info", "Get project assembly information")]
        public static object DebugGetAssemblyInfo()
        {
            var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Select(a => new { name = a.name, sourceFiles = a.sourceFiles.Length, defines = a.defines.Length })
                .ToArray();
            return new { success = true, count = assemblies.Length, assemblies };
        }

        [UnitySkill("debug_get_defines", "Get scripting define symbols for current platform")]
        public static object DebugGetDefines()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            return new { success = true, buildTargetGroup = group.ToString(), defines };
        }

        [UnitySkill("debug_set_defines", "Set scripting define symbols for current platform")]
        public static object DebugSetDefines(string defines)
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, defines);
            return new { success = true, buildTargetGroup = group.ToString(), defines };
        }

        [UnitySkill("debug_get_memory_info", "Get memory usage information")]
        public static object DebugGetMemoryInfo()
        {
            return new
            {
                success = true,
                totalAllocatedMB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0),
                totalReservedMB = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024.0 * 1024.0),
                totalUnusedReservedMB = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong() / (1024.0 * 1024.0),
                monoUsedSizeMB = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024.0 * 1024.0),
                monoHeapSizeMB = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024.0 * 1024.0)
            };
        }
    }
}
