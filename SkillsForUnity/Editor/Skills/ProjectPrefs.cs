using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Internal
{
    /// <summary>
    /// Per-project, per-user settings with an EditorPrefs-like API. The values live in
    /// <c>&lt;project&gt;/UserSettings/UnitySkills/settings.json</c> as <c>{"schema":1,"values":{"&lt;key&gt;": value}}</c>,
    /// UTF-8 without BOM. Unity's standard .gitignore excludes UserSettings/, so the file is not shared through version
    /// control; the path is the same on Windows, macOS and Linux, and the file moves with the project.
    ///
    /// Migration: the keys in <see cref="LegacyGlobalKeys"/> were machine-wide EditorPrefs before 3.0. A read returns this
    /// project's value when it has one, else the old global EditorPrefs value when that exists, else the caller's default.
    /// Writes only ever go to the project file, and the global keys are never written or deleted, so every project starts
    /// from the old global value and diverges once it is changed there. <see cref="DeleteKey"/> removes this project's
    /// value only, so an old global value applies again afterwards.
    ///
    /// The file is read lazily, once per domain, and cached; every change rewrites it through a temp file and an atomic
    /// swap. A file that exists but cannot be read or parsed (or has an unknown schema) is never written: reads fall back
    /// as if it were absent and one warning names it; changes still apply for the rest of the session, and the first one
    /// logs an error saying it was not saved. Fixing or deleting the file takes effect at the next domain reload.
    ///
    /// Main thread only, like EditorPrefs: the legacy fallback reads EditorPrefs.
    /// </summary>
    internal static class ProjectPrefs
    {
        internal const int SchemaVersion = 1;

        /// <summary>
        /// The settings that were machine-wide EditorPrefs before 3.0. Only these fall back to the old global value; a
        /// key introduced later has never existed globally and reads the caller's default when the project has none.
        /// </summary>
        private static readonly HashSet<string> LegacyGlobalKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            // Token level sources (SkillsSurfaceProfile, SkillRouter); UnitySkills_GuideMode is the pre-2.7 guide toggle.
            "UnitySkills_SurfaceProfile",
            "UnitySkills_GuideMode",
            "UnitySkills_SummaryAutoTruncate",
            "UnitySkills_SummaryPageSize",
            // Operating mode and permissions (SkillsModeManager, ConfirmationTokenService).
            "UnitySkills_OperatingMode",
            "UnitySkills_PanelApprovalRequired",
            "UnitySkills_AllowlistSkills",
            "UnitySkills_AllowlistMigratedFromGranted",
            "UnitySkills_GrantedSkills",
            "UnitySkills_RequireConfirmation",
            // Server (SkillsHttpServer), telemetry, package auto-install.
            "UnitySkills_PreferredPort",
            "UnitySkills_RequestTimeoutMinutes",
            "UnitySkills_TelemetryEnabled",
            "UnitySkills_AutoInstallPackagesOnStartup",
            // Workflow history limits (WorkflowAutoCleanConfig).
            "UnitySkills.Workflow.Enabled",
            "UnitySkills.Workflow.MaxTasks",
            "UnitySkills.Workflow.MaxHistoryMB",
            "UnitySkills.Workflow.MaxTaskAgeDays",
            "UnitySkills.Workflow.MaxStoreMB",
            "UnitySkills.Workflow.StoreMaxAgeDays",
            // Evaluation tab configuration.
            "UnitySkills_EvalPanelConfig",
        };

        private static Store _projectStore;
        private static Store _testStore;

        private static Store Active =>
            _testStore ?? (_projectStore ?? (_projectStore = new Store(SettingsFileFor(ProjectRoot()), null)));

        /// <summary>The settings file in use; null while a test keeps the values in memory.</summary>
        internal static string SettingsFilePath => Active.FilePath;

        internal static string SettingsFileFor(string projectRoot) =>
            Path.Combine(projectRoot, "UserSettings", "UnitySkills", "settings.json");

        public static bool HasKey(string key)
        {
            var store = Active;
            return store.HasProjectValue(key) || store.HasLegacyValue(key);
        }

        /// <summary>Removes this project's value; an old global value, if any, applies again.</summary>
        public static void DeleteKey(string key) => Active.Delete(key);

        public static bool GetBool(string key, bool defaultValue = false)
        {
            var store = Active;
            var token = store.ProjectValue(key);
            if (token != null && token.Type == JTokenType.Boolean) return token.Value<bool>();
            return store.LegacyBool(key, defaultValue);
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
            var store = Active;
            var token = store.ProjectValue(key) as JValue;
            if (token != null && token.Type == JTokenType.Integer)
            {
                try
                {
                    var value = Convert.ToInt64(token.Value, CultureInfo.InvariantCulture);
                    if (value >= int.MinValue && value <= int.MaxValue) return (int)value;
                }
                catch (OverflowException)
                {
                    // Out of range: treated like a value of the wrong type.
                }
            }
            return store.LegacyInt(key, defaultValue);
        }

        public static string GetString(string key, string defaultValue = "")
        {
            var store = Active;
            var token = store.ProjectValue(key);
            if (token != null && token.Type == JTokenType.String) return token.Value<string>();
            return store.LegacyString(key, defaultValue);
        }

        public static void SetBool(string key, bool value) => Active.Set(key, new JValue(value));

        public static void SetInt(string key, int value) => Active.Set(key, new JValue((long)value));

        public static void SetString(string key, string value) => Active.Set(key, new JValue(value ?? string.Empty));

        /// <summary>
        /// Test seam: until the returned scope is disposed, every call uses a fresh store. A null
        /// <paramref name="projectRoot"/> keeps the values in memory, so nothing touches the disk; otherwise they live in
        /// <c>&lt;projectRoot&gt;/UserSettings/UnitySkills/settings.json</c>. <paramref name="legacyValues"/> stands in for
        /// the old global EditorPrefs (null: there are none), and <paramref name="legacyFromEditorPrefs"/> reads the real
        /// ones instead, as the editor does. Scopes nest; dispose them in reverse order.
        /// </summary>
        internal static IDisposable UseForTests(string projectRoot = null,
            IDictionary<string, object> legacyValues = null, bool legacyFromEditorPrefs = false)
        {
            var previous = _testStore;
            _testStore = new Store(projectRoot == null ? null : SettingsFileFor(projectRoot),
                legacyFromEditorPrefs ? null : legacyValues ?? new Dictionary<string, object>());
            return new TestScope(previous);
        }

        private static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private sealed class TestScope : IDisposable
        {
            private readonly Store _previous;
            private bool _disposed;

            public TestScope(Store previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _testStore = _previous;
            }
        }

        private sealed class Store
        {
            /// <summary>Null: the values stay in memory.</summary>
            public readonly string FilePath;
            /// <summary>Null: the old global values are read from EditorPrefs.</summary>
            private readonly IDictionary<string, object> _legacyStandIn;
            private Dictionary<string, JToken> _values;
            private bool _unreadable;
            private bool _unsavedReported;
            private bool _writeFailureReported;

            public Store(string filePath, IDictionary<string, object> legacyStandIn)
            {
                FilePath = filePath;
                _legacyStandIn = legacyStandIn;
            }

            public bool HasProjectValue(string key)
            {
                EnsureLoaded();
                return _values.ContainsKey(key);
            }

            public JToken ProjectValue(string key)
            {
                EnsureLoaded();
                return _values.TryGetValue(key, out var token) ? token : null;
            }

            public void Set(string key, JToken value)
            {
                EnsureLoaded();
                if (_values.TryGetValue(key, out var existing) && JToken.DeepEquals(existing, value)) return;
                _values[key] = value;
                Save();
            }

            public void Delete(string key)
            {
                EnsureLoaded();
                if (_values.Remove(key)) Save();
            }

            public bool HasLegacyValue(string key)
            {
                if (!LegacyGlobalKeys.Contains(key)) return false;
                return _legacyStandIn != null ? _legacyStandIn.ContainsKey(key) : EditorPrefs.HasKey(key);
            }

            public bool LegacyBool(string key, bool defaultValue)
            {
                if (!LegacyGlobalKeys.Contains(key)) return defaultValue;
                if (_legacyStandIn == null) return EditorPrefs.GetBool(key, defaultValue);
                return _legacyStandIn.TryGetValue(key, out var value) && value is bool b ? b : defaultValue;
            }

            public int LegacyInt(string key, int defaultValue)
            {
                if (!LegacyGlobalKeys.Contains(key)) return defaultValue;
                if (_legacyStandIn == null) return EditorPrefs.GetInt(key, defaultValue);
                return _legacyStandIn.TryGetValue(key, out var value) && value is int i ? i : defaultValue;
            }

            public string LegacyString(string key, string defaultValue)
            {
                if (!LegacyGlobalKeys.Contains(key)) return defaultValue;
                if (_legacyStandIn == null) return EditorPrefs.GetString(key, defaultValue);
                return _legacyStandIn.TryGetValue(key, out var value) && value is string s ? s : defaultValue;
            }

            private void EnsureLoaded()
            {
                if (_values != null) return;
                _values = new Dictionary<string, JToken>(StringComparer.Ordinal);
                if (FilePath == null || !File.Exists(FilePath)) return;
                try
                {
                    var root = EvaluationState.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (root.Value<long?>("schema") != SchemaVersion || !(root["values"] is JObject values))
                        throw new InvalidDataException($"expected {{\"schema\":{SchemaVersion},\"values\":{{...}}}}");
                    foreach (var property in values.Properties())
                        _values[property.Name] = property.Value;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is JsonException || ex is InvalidDataException ||
                                           ex is FormatException || ex is InvalidCastException ||
                                           ex is OverflowException)
                {
                    _values.Clear();
                    _unreadable = true;
                    SkillsLogger.LogWarning(
                        $"Could not read the project settings file {FilePath} ({ex.Message}). Using the previous " +
                        "global values and defaults; the file is left untouched. Fix or delete it, then restart Unity.");
                }
            }

            private void Save()
            {
                if (FilePath == null) return;
                if (_unreadable)
                {
                    if (_unsavedReported) return;
                    _unsavedReported = true;
                    SkillsLogger.LogError(
                        $"A UnitySkills setting was changed for this session only and not saved: {FilePath} could not " +
                        "be read and is left untouched. Fix or delete the file, then restart Unity.");
                    return;
                }
                try
                {
                    var values = new JObject();
                    foreach (var pair in _values.OrderBy(p => p.Key, StringComparer.Ordinal))
                        values[pair.Key] = pair.Value;
                    EvaluationState.WriteTextAtomic(FilePath, Serialize(new JObject
                    {
                        ["schema"] = SchemaVersion,
                        ["values"] = values,
                    }));
                    _writeFailureReported = false;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (_writeFailureReported) return;
                    _writeFailureReported = true;
                    SkillsLogger.LogError($"Could not save UnitySkills settings to {FilePath}: {ex.Message}");
                }
            }

            private static string Serialize(JObject root)
            {
                var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
                using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented })
                    root.WriteTo(writer);
                return text + "\n";
            }
        }
    }
}

// Producer:Betsy
