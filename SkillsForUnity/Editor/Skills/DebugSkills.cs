using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
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
        // Unity LogEntry mode bits (from UnityCsReference)
        // Error=1, Assert=2, Log=4, Fatal=16,
        // DontPreprocessCondition=32, AssetImportError=64, AssetImportWarning=128,
        // ScriptingError=256, ScriptingWarning=512, ScriptingLog=1024,
        // ScriptCompileError=2048, ScriptCompileWarning=4096,
        // ScriptingException=131072
        private const int ModeError               = 1;
        private const int ModeAssert              = 2;
        private const int ModeLog                 = 4;
        private const int ModeFatal               = 16;
        private const int ModeAssetImportError    = 64;
        private const int ModeAssetImportWarning  = 128;
        private const int ModeScriptingError      = 256;
        private const int ModeScriptingWarning    = 512;
        private const int ModeScriptingLog        = 1024;
        private const int ModeScriptCompileError  = 2048;
        private const int ModeScriptCompileWarning = 4096;
        private const int ModeScriptingException  = 131072;

        internal const int ErrorModeMask   = ModeError | ModeAssert | ModeFatal | ModeAssetImportError | ModeScriptingError | ModeScriptCompileError | ModeScriptingException;
        internal const int WarningModeMask = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;
        internal const int LogModeMask     = ModeLog | ModeScriptingLog;

        // Cached reflection members (initialized on first use, cleared on failure to allow retry)
        private static System.Type _logEntriesType;
        private static System.Type _logEntryType;
        private static MethodInfo  _getCountMethod;
        private static MethodInfo  _getEntryMethod;
        private static MethodInfo  _startMethod;
        private static MethodInfo  _endMethod;
        private static FieldInfo   _modeField;
        private static FieldInfo   _messageField;
        private static FieldInfo   _fileField;
        private static FieldInfo   _lineField;

        internal static bool EnsureReflection()
        {
            if (_getEntryMethod != null) return true;

            var asm = System.Reflection.Assembly.GetAssembly(typeof(SceneView));
            _logEntriesType = asm?.GetType("UnityEditor.LogEntries");
            _logEntryType   = asm?.GetType("UnityEditor.LogEntry");

            if (_logEntriesType == null || _logEntryType == null)
            {
                SkillsLogger.LogError("DebugSkills: UnityEditor.LogEntries or LogEntry type not found.");
                return false;
            }

            var staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            _getCountMethod = _logEntriesType.GetMethod("GetCount",             staticFlags);
            _getEntryMethod = _logEntriesType.GetMethod("GetEntryInternal",     staticFlags);
            _startMethod    = _logEntriesType.GetMethod("StartGettingEntries",  staticFlags);
            _endMethod      = _logEntriesType.GetMethod("EndGettingEntries",    staticFlags);

            var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _modeField    = _logEntryType.GetField("mode",    instFlags);
            _messageField = _logEntryType.GetField("message", instFlags);
            _fileField    = _logEntryType.GetField("file",    instFlags);
            _lineField    = _logEntryType.GetField("line",    instFlags);

            bool ok = _getCountMethod != null && _getEntryMethod != null &&
                      _startMethod    != null && _endMethod      != null &&
                      _modeField      != null && _messageField   != null &&
                      _fileField      != null && _lineField      != null;

            if (!ok)
            {
                SkillsLogger.LogError("DebugSkills: Failed to reflect required members of LogEntries/LogEntry.");
                // Clear everything so the next call will retry
                _logEntriesType = null;
                _logEntryType   = null;
                _getCountMethod = null;
                _getEntryMethod = null;
                _startMethod    = null;
                _endMethod      = null;
                _modeField      = null;
                _messageField   = null;
                _fileField      = null;
                _lineField      = null;
            }

            return ok;
        }

        internal static List<object> ReadLogEntries(int targetMask, string filter, int limit)
        {
            var results = new List<object>();
            if (!EnsureReflection()) return results;

            var entry = System.Activator.CreateInstance(_logEntryType);
            _startMethod.Invoke(null, null);
            try
            {
                int count = (int)_getCountMethod.Invoke(null, null);
                int found = 0;
                for (int i = count - 1; i >= 0 && found < limit; i--)
                {
                    _getEntryMethod.Invoke(null, new object[] { i, entry });
                    int mode = (int)_modeField.GetValue(entry);
                    if ((mode & targetMask) == 0) continue;

                    string msg  = (string)_messageField.GetValue(entry);
                    if (!string.IsNullOrEmpty(filter) && !msg.Contains(filter)) continue;

                    string file = (string)_fileField.GetValue(entry);
                    int    line = (int)_lineField.GetValue(entry);

                    string logType = (mode & ErrorModeMask)   != 0 ? "Error"
                                   : (mode & WarningModeMask) != 0 ? "Warning"
                                   : "Log";

                    results.Add(new
                    {
                        type    = logType,
                        message = msg.Length > 500 ? msg.Substring(0, 500) + "..." : msg,
                        file,
                        line
                    });
                    found++;
                }
            }
            finally
            {
                _endMethod.Invoke(null, null);
            }

            return results;
        }

        [UnitySkill("debug_get_errors", "Get only active errors and exceptions from the console logs.")]
        public static object DebugGetErrors(int limit = 50) => DebugGetLogs("Error", null, limit);

        [UnitySkill("debug_get_logs", "Get console logs filtered by type (Error/Warning/Log) and content.")]
        public static object DebugGetLogs(string type = "Error", string filter = null, int limit = 50)
        {
            int targetMask = 0;
            if (type.Contains("Error"))   targetMask |= ErrorModeMask;
            if (type.Contains("Warning")) targetMask |= WarningModeMask;
            if (type.Contains("Log"))     targetMask |= LogModeMask;
            if (targetMask == 0)          targetMask = ErrorModeMask;

            var results = ReadLogEntries(targetMask, filter, limit);
            return new { count = results.Count, logs = results };
        }

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
            // 1. Refresh AssetDatabase
            AssetDatabase.Refresh();

            // 2. Request Script Compilation (Target specific or all)
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
            if (!EnsureReflection())
                return new { error = "Reflection initialization failed" };

            var entry = System.Activator.CreateInstance(_logEntryType);
            _startMethod.Invoke(null, null);
            try
            {
                int count = (int)_getCountMethod.Invoke(null, null);
                if (entryIndex < 0 || entryIndex >= count)
                    return new { error = $"Index {entryIndex} out of range (0-{count - 1})" };

                _getEntryMethod.Invoke(null, new object[] { entryIndex, entry });
                var msg   = (string)_messageField.GetValue(entry);
                var lines = msg.Split('\n');
                return new { index = entryIndex, message = lines[0], stackTrace = string.Join("\n", lines.Skip(1)) };
            }
            finally
            {
                _endMethod.Invoke(null, null);
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
