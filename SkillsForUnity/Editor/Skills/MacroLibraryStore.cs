using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Named macro library backing the macro_save / macro_list / macro_get / macro_delete /
    /// macro_run skills: macro files in the MacroFileStore schema, one file per macro,
    /// addressed by name, in one of two scopes — <c>project</c>
    /// (<c>Library/UnitySkillsMacros</c>, never committed) or <c>global</c>
    /// (<c>~/.unity_skills/macros</c>, shared across every Unity project on the machine, the
    /// cross-project reuse story). Pure file/naming layer — session access and batch execution
    /// live in the callers. Name validation is strict (reject, not sanitize): a macro name
    /// doubles as the file name, so path separators, reserved device names and other invalid
    /// characters are structured errors. Scope-less overloads default to project.
    /// </summary>
    internal static class MacroLibraryStore
    {
        internal const string DirName = "UnitySkillsMacros";
        internal const string ScopeProject = "project";
        internal const string ScopeGlobal = "global";
        private const int MaxNameLength = 64;

        // Windows reserved device names — invalid as a bare file name even with an extension.
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Test seam: when set, used verbatim instead of &lt;project&gt;/Library/UnitySkillsMacros.</summary>
        internal static string OverrideDirForTests;

        /// <summary>Test seam for the global scope: when set, used verbatim instead of ~/.unity_skills/macros.</summary>
        internal static string OverrideGlobalDirForTests;

        /// <summary>
        /// Normalizes a caller-supplied scope: null/blank → project; "project"/"global"
        /// case-insensitively; anything else is a structured error listing the allowed values.
        /// </summary>
        internal static bool TryNormalizeScope(string raw, out string scope, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                scope = ScopeProject;
                return true;
            }
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, ScopeProject, StringComparison.OrdinalIgnoreCase))
            {
                scope = ScopeProject;
                return true;
            }
            if (string.Equals(trimmed, ScopeGlobal, StringComparison.OrdinalIgnoreCase))
            {
                scope = ScopeGlobal;
                return true;
            }
            scope = null;
            error = $"Invalid scope '{raw}'. Allowed: '{ScopeProject}' (Library/UnitySkillsMacros, this project only) or '{ScopeGlobal}' (~/.unity_skills/macros, shared across projects).";
            return false;
        }

        /// <summary>Returns &lt;project&gt;/Library/UnitySkillsMacros (mirrors SkillTelemetryService's dir resolution).</summary>
        internal static string ResolveDir() => ResolveDir(ScopeProject);

        /// <summary>Scope → directory. scope must already be normalized (see TryNormalizeScope).</summary>
        internal static string ResolveDir(string scope)
        {
            if (scope == ScopeGlobal)
            {
                if (!string.IsNullOrEmpty(OverrideGlobalDirForTests))
                    return OverrideGlobalDirForTests;

                // Same family root as the schema disk cache (~/.unity_skills/cache) — one
                // per-user home for cross-project UnitySkills state, no hardcoded drive.
                try
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home))
                        return Path.Combine(home, ".unity_skills", "macros");
                }
                catch { /* fall through to temp */ }
                return Path.Combine(Path.GetTempPath(), ".unity_skills", "macros");
            }

            if (!string.IsNullOrEmpty(OverrideDirForTests))
                return OverrideDirForTests;

            try
            {
                var dataPath = Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var projectRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                    return Path.Combine(projectRoot, "Library", DirName);
                }
            }
            catch { /* Unity API not ready; fall through */ }

            try { return Path.Combine(Application.persistentDataPath, DirName); }
            catch { return Path.Combine(Path.GetTempPath(), DirName); }
        }

        /// <summary>
        /// Strict macro-name validation: non-empty, ≤64 chars, no path separators or other
        /// invalid file-name characters, no leading/trailing whitespace or trailing dot, not a
        /// relative-path token ("."/"..") or a Windows reserved device name.
        /// </summary>
        internal static bool TryValidateName(string name, out string error)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Macro name is required (non-empty).";
                return false;
            }
            if (name.Length > MaxNameLength)
            {
                error = $"Macro name is too long ({name.Length} chars, max {MaxNameLength}).";
                return false;
            }
            if (name != name.Trim())
            {
                error = "Macro name must not start or end with whitespace.";
                return false;
            }
            if (name == "." || name == ".." || name.EndsWith(".", StringComparison.Ordinal))
            {
                error = "Macro name must not be '.'/'..' or end with a dot.";
                return false;
            }
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "Macro name contains characters that are invalid in a file name "
                    + "(path separators, quotes, control characters, ...).";
                return false;
            }
            if (ReservedNames.Contains(name))
            {
                error = $"Macro name '{name}' is a reserved device name on Windows.";
                return false;
            }
            error = null;
            return true;
        }

        internal static string PathFor(string name) => PathFor(name, ScopeProject);
        internal static string PathFor(string name, string scope) => Path.Combine(ResolveDir(scope), name + ".json");

        internal static bool Exists(string name) => Exists(name, ScopeProject);
        internal static bool Exists(string name, string scope) => File.Exists(PathFor(name, scope));

        /// <summary>
        /// Resolves which scope holds a macro when the caller did not pin one:
        /// project first, then global (a project macro shadows a same-named global one).
        /// Returns false when the macro exists in neither scope.
        /// </summary>
        internal static bool TryResolveExistingScope(string name, out string scope)
        {
            if (Exists(name, ScopeProject)) { scope = ScopeProject; return true; }
            if (Exists(name, ScopeGlobal))  { scope = ScopeGlobal;  return true; }
            scope = null;
            return false;
        }

        /// <summary>
        /// Writes a macro (MacroFileStore schema JObject) under name. Fails on an invalid name,
        /// or on an existing macro unless overwrite is set.
        /// </summary>
        internal static bool TrySave(string name, JObject macroJson, bool overwrite, out string error)
            => TrySave(name, macroJson, overwrite, ScopeProject, out error);

        internal static bool TrySave(string name, JObject macroJson, bool overwrite, string scope, out string error)
        {
            if (!TryValidateName(name, out error))
                return false;
            if (macroJson == null)
            {
                error = "Macro content is required.";
                return false;
            }
            var path = PathFor(name, scope);
            if (!overwrite && File.Exists(path))
            {
                error = $"A macro named '{name}' already exists in the {scope} scope. Pass overwrite=true to replace it.";
                return false;
            }
            try
            {
                Directory.CreateDirectory(ResolveDir(scope));
                File.WriteAllText(path, macroJson.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>All macro names in the library, sorted; empty list when the directory does not exist.</summary>
        internal static List<string> ListNames() => ListNames(ScopeProject);

        internal static List<string> ListNames(string scope)
        {
            var names = new List<string>();
            var dir = ResolveDir(scope);
            if (!Directory.Exists(dir))
                return names;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                    names.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch { /* unreadable dir counts as empty */ }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>Loads one macro by name; error distinguishes invalid name / missing file / parse failure.</summary>
        internal static bool TryLoad(string name, out MacroFile file, out long fileSizeBytes, out string error)
            => TryLoad(name, ScopeProject, out file, out fileSizeBytes, out error);

        internal static bool TryLoad(string name, string scope, out MacroFile file, out long fileSizeBytes, out string error)
        {
            file = null;
            fileSizeBytes = 0;
            if (!TryValidateName(name, out error))
                return false;
            var path = PathFor(name, scope);
            if (!File.Exists(path))
            {
                error = $"No macro named '{name}' exists in the {scope} scope.";
                return false;
            }
            try
            {
                fileSizeBytes = new FileInfo(path).Length;
            }
            catch { /* size is informational */ }
            return MacroFileStore.TryLoadFile(path, out file, out error);
        }

        internal static bool TryDelete(string name, out string error)
            => TryDelete(name, ScopeProject, out error);

        internal static bool TryDelete(string name, string scope, out string error)
        {
            if (!TryValidateName(name, out error))
                return false;
            var path = PathFor(name, scope);
            if (!File.Exists(path))
            {
                error = $"No macro named '{name}' exists in the {scope} scope.";
                return false;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// $param names a run of these steps would fail on: declared by a bare {"$param":"x"}
        /// slot (no inline default) and not covered by effectiveParams. Order follows first
        /// occurrence in the steps.
        /// </summary>
        internal static List<string> ComputeMissingParams(JArray steps, JObject effectiveParams)
        {
            var missing = new List<string>();
            foreach (var decl in SkillsHttpServer.CollectBatchParamDeclarations(steps))
            {
                if (decl.HasDefault)
                    continue;
                if (effectiveParams != null && effectiveParams.TryGetValue(decl.Name, StringComparison.Ordinal, out _))
                    continue;
                missing.Add(decl.Name);
            }
            return missing;
        }
    }
}
