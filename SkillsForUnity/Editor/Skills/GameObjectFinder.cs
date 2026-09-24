using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;

namespace UnitySkills.Internal
{
    /// <summary>
    /// Compatibility layer: Unity 6+ uses FindObjectsByType, older versions fall back to FindObjectsOfType.
    /// </summary>
    internal static class FindHelper
    {
        internal static T[] FindAll<T>(bool includeInactive = false) where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType<T>(FindObjectsInactive.Include)
                : Object.FindObjectsByType<T>(FindObjectsInactive.Exclude);
#elif UNITY_6000_0_OR_NEWER || UNITY_2022_2_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                : Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return includeInactive
                ? Resources.FindObjectsOfTypeAll<T>()
                : Object.FindObjectsOfType<T>();
#endif
        }

        internal static Object[] FindAll(System.Type type, bool includeInactive = false)
        {
            if (type == null)
                return System.Array.Empty<Object>();

#if UNITY_6000_4_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType(type, FindObjectsInactive.Include)
                : Object.FindObjectsByType(type, FindObjectsInactive.Exclude);
#elif UNITY_6000_0_OR_NEWER || UNITY_2022_2_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None)
                : Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return includeInactive
                ? Resources.FindObjectsOfTypeAll(type)
                : Object.FindObjectsOfType(type);
#endif
        }
    }
}

namespace UnitySkills
{
    /// <summary>
    /// Parameter validation helpers: return an error object on failure, null when valid.
    /// </summary>
    public static class Validate
    {
        // Parameter errors from every skill across the whole package funnel through these few helper methods,
        // which is why the structured fields added here give hundreds of skills a precise errorCode and a
        // usable retryStrategy without touching their own code.
        // SkillRouter's TryGetErrorContext reads them as-is; SkillErrorClassifier only fills in what's missing.

        private static object MissingParam(string message, string paramName) => new
        {
            error = message,
            errorCode = SkillErrorCode.MissingParam.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            suggestedFixes = new[]
            {
                new
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = $"Pass '{paramName}'. dryRun returns the full parameter schema without executing."
                }
            }
        };

        private static object InvalidParam(string message, string reason) => new
        {
            error = message,
            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            suggestedFixes = new[]
            {
                new { action = "fix_param", reason }
            }
        };

        /// <summary>
        /// Checks whether a string parameter was provided. Returns an error object if empty, null if valid.
        /// Usage: if (Validate.Required(x, "x") is object err) return err;
        /// </summary>
        public static object Required(string value, string paramName) =>
            string.IsNullOrEmpty(value) ? MissingParam($"{paramName} is required", paramName) : null;

        /// <summary>
        /// The nullable-value-type version of <see cref="Required(string,string)"/>, for setters whose payload is a number.
        ///
        /// <para>A parameter declared as <c>float x = 1f</c> can't tell "the caller passed 1" apart from "the
        /// caller passed nothing", so omitting it silently overwrites the object with the CLR default value while
        /// the response still reports success. Changing it to <c>float? x = null</c> paired with a
        /// <c>RequiresInput</c> entry makes the schema mark it required and dryRun reject an empty request body,
        /// and this guard is what catches the class of calls made directly in-process.</para>
        /// </summary>
        public static object Required<T>(T? value, string paramName) where T : struct =>
            value.HasValue ? null : MissingParam($"{paramName} is required", paramName);

        /// <summary>
        /// Checks that a JSON array parameter was provided and is non-empty.
        /// Usage: if (Validate.RequiredJsonArray(items, "items") is object err) return err;
        /// </summary>
        public static object RequiredJsonArray(string jsonArray, string paramName)
        {
            if (string.IsNullOrEmpty(jsonArray))
                return MissingParam($"{paramName} is required", paramName);
            var trimmed = jsonArray.Trim();
            if (trimmed == "[]" || trimmed == "null")
                return InvalidParam($"{paramName} must be a non-empty array",
                    $"'{paramName}' is a JSON array string — send at least one element, e.g. [\"first\"].");
            return null;
        }

        /// <summary>
        /// Validates that a numeric value falls within a closed interval.
        /// Usage: if (Validate.InRange(count, 1, 100, "count") is object err) return err;
        /// </summary>
        public static object InRange(float value, float min, float max, string paramName)
        {
            if (value < min || value > max)
                return InvalidParam($"{paramName} must be between {min} and {max}, got {value}",
                    $"Clamp '{paramName}' into [{min}, {max}] and retry.");
            return null;
        }

        /// <summary>
        /// Validates that an integer falls within a closed interval.
        /// </summary>
        public static object InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                return InvalidParam($"{paramName} must be between {min} and {max}, got {value}",
                    $"Clamp '{paramName}' into [{min}, {max}] and retry.");
            return null;
        }

        /// <summary>
        /// Validates asset path safety: blocks path traversal, and restricts to under Assets/ or Packages/.
        /// Usage: if (Validate.SafePath(path, "path") is object err) return err;
        /// </summary>
        public static object SafePath(string path, string paramName, bool isDelete = false)
        {
            if (string.IsNullOrEmpty(path))
                return MissingParam($"{paramName} is required", paramName);

            var normalized = path.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            if (normalized.StartsWith("./")) normalized = normalized.Substring(2);

            // Block path traversal.
            if (normalized.Contains(".."))
                return InvalidParam($"Path traversal not allowed: {path}",
                    "Send a normalized project-relative path with no '..' segments.");

            // Restrict to under Assets/ or Packages/.
            if (!normalized.StartsWith("Assets/") && !normalized.StartsWith("Packages/") &&
                normalized != "Assets" && normalized != "Packages")
                return InvalidParam($"Path must start with Assets/ or Packages/: {path}",
                    "Paths are project-relative: prefix with 'Assets/' (or 'Packages/'), not an absolute disk path.");

            // Forbid deleting the root folder.
            if (isDelete && (normalized == "Assets" || normalized == "Assets/" ||
                            normalized == "Packages" || normalized == "Packages/"))
                return InvalidParam("Cannot delete root Assets or Packages folder",
                    "Target a specific asset or subfolder instead of the project root.");

            return null;
        }

        /// <summary>
        /// Validates both the safety and existence of an asset path.
        /// Usage: if (Validate.SafePathExists(path, "path") is object err) return err;
        /// </summary>
        public static object SafePathExists(string path, string paramName)
        {
            var safeErr = SafePath(path, paramName);
            if (safeErr != null) return safeErr;
            if (!SkillsCommon.PathExists(path))
                return new
                {
                    error = $"Path does not exist: {path}",
                    errorCode = SkillErrorCode.TargetNotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFindAndRetry,
                    relatedSkills = new[] { "asset_find", "asset_get_info" },
                    suggestedFixes = new[]
                    {
                        new
                        {
                            action = "find_target",
                            skill = "asset_find",
                            reason = "Resolve the real project path first — asset paths are case-sensitive and must start with Assets/ or Packages/."
                        }
                    }
                };
            return null;
        }

        /// <summary>
        /// Ensures the parent directory of a file path exists.
        /// </summary>
        public static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// Unified GameObject lookup utility, supporting lookup by name, entityId, legacy instanceId,
    /// Hierarchy path, tag, or component type, with a step-by-step fallback search strategy.
    /// </summary>
    public static class GameObjectFinder
    {
        private sealed class SceneObjectCache
        {
            public readonly List<GameObject> Objects = new List<GameObject>();
            public readonly Dictionary<string, string> PathsByEntityId =
                new Dictionary<string, string>(System.StringComparer.Ordinal);
            public readonly Dictionary<string, int> DepthsByEntityId =
                new Dictionary<string, int>(System.StringComparer.Ordinal);
            public readonly Dictionary<string, GameObject> PathLookup =
                new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
        }

        // Request-level cache of scene traversal metadata, invalidated by InvalidateCache() at the end of each request.
        private static SceneObjectCache _cachedSceneData;
        private static bool _cacheValid = false;

        // Lookups that bind a loose reference (case-insensitive name, word/substring fallback, a path that only matched
        // case-insensitively, several same-named objects) record one line here; the router drains them into the response
        // as resolutionNotes, so the caller can see which object a fuzzy reference actually landed on.
        private const int MaxResolutionNotes = 20;
        private const int MaxAmbiguityCandidates = 10;
        [System.ThreadStatic] private static List<string> _resolutionNotes;
        [System.ThreadStatic] private static int _omittedResolutionNotes;

        internal static void AddResolutionNote(string note)
        {
            if (string.IsNullOrEmpty(note))
                return;
            if (_resolutionNotes == null)
                _resolutionNotes = new List<string>();
            if (_resolutionNotes.Contains(note))
                return;
            if (_resolutionNotes.Count >= MaxResolutionNotes)
            {
                _omittedResolutionNotes++;
                return;
            }
            _resolutionNotes.Add(note);
        }

        /// <summary>
        /// Returns the resolution notes recorded on this thread since the last drain, and clears them. Never null.
        /// </summary>
        internal static List<string> DrainResolutionNotes()
        {
            var notes = _resolutionNotes ?? new List<string>();
            if (_omittedResolutionNotes > 0)
                notes.Add($"(+{_omittedResolutionNotes} more resolution note(s) omitted)");
            _resolutionNotes = null;
            _omittedResolutionNotes = 0;
            return notes;
        }

        /// <summary>
        /// Invalidates the scene object cache; should be called at the end of every request cycle.
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedSceneData = null;
            _cacheValid = false;
        }

        /// <summary>
        /// Builds and caches scene traversal metadata once per request.
        /// </summary>
        private static SceneObjectCache GetOrBuildSceneCache()
        {
            if (_cachedSceneData != null && _cacheValid)
            {
                // The managed wrapper object Unity leaves behind after DestroyImmediate still sits in the list,
                // but compares equal to null. If detected, rebuild the cache so subsequent lookups see the
                // replacement object instead of dereferencing a destroyed wrapper (common during undo/redo and
                // test fixture teardown).
                if (_cachedSceneData.Objects.All(gameObject => gameObject != null))
                    return _cachedSceneData;

                InvalidateCache();
            }

            var cache = new SceneObjectCache();
            var roots = GetLoadedSceneRoots();
            var stack = new Stack<(Transform transform, string path, string sceneName, int depth)>();
            foreach (var root in roots)
                stack.Push((root.transform, root.name, root.scene.name, 0));

            while (stack.Count > 0)
            {
                var (transform, path, sceneName, depth) = stack.Pop();
                var gameObject = transform.gameObject;
                var entityId = UnityObjectIdUtility.GetEntityId(gameObject);

                cache.Objects.Add(gameObject);
                if (!string.IsNullOrEmpty(entityId))
                {
                    cache.PathsByEntityId[entityId] = path;
                    cache.DepthsByEntityId[entityId] = depth;
                }
                AddPathLookup(cache.PathLookup, path, gameObject);

                if (!string.IsNullOrEmpty(sceneName))
                    AddPathLookup(cache.PathLookup, sceneName + "/" + path, gameObject);

                foreach (Transform child in transform)
                    stack.Push((child, path + "/" + child.name, sceneName, depth + 1));
            }

            _cachedSceneData = cache;
            _cacheValid = true;
            return cache;
        }

        /// <summary>
        /// Efficiently enumerates all GameObjects in the scene via root-node traversal (faster than FindObjectsOfType).
        /// Results are cached per request to avoid repeated traversal within the same skill execution.
        /// </summary>
        private static IEnumerable<GameObject> GetAllSceneObjects()
        {
            return GetOrBuildSceneCache().Objects;
        }

        /// <summary>
        /// Gets the cached list of scene objects for the current request.
        /// </summary>
        public static IReadOnlyList<GameObject> GetSceneObjects()
        {
            return GetOrBuildSceneCache().Objects;
        }

        /// <summary>
        /// Gets a scene object's depth in the hierarchy (via cache). For non-scene objects, falls back to walking up parents one by one.
        /// </summary>
        public static int GetDepth(GameObject go)
        {
            if (go == null)
                return 0;

            var entityId = UnityObjectIdUtility.GetEntityId(go);
            if (_cachedSceneData != null && _cacheValid &&
                !string.IsNullOrEmpty(entityId) &&
                _cachedSceneData.DepthsByEntityId.TryGetValue(entityId, out var depth))
                return depth;

            depth = 0;
            var parent = go.transform.parent;
            while (parent != null)
            {
                depth++;
                parent = parent.parent;
            }

            if (_cachedSceneData != null && _cacheValid && !string.IsNullOrEmpty(entityId))
                _cachedSceneData.DepthsByEntityId[entityId] = depth;

            return depth;
        }

        private static void AddPathLookup(Dictionary<string, GameObject> lookup, string path, GameObject go)
        {
            if (string.IsNullOrEmpty(path) || lookup.ContainsKey(path))
                return;

            lookup[path] = go;
        }

        /// <summary>
        /// Adds a GameObject created during the current request, with its children, to the request cache, so later lookups
        /// in the same request see it without a full rebuild. No-op when no cache has been built yet (the next build walks the live scene).
        /// </summary>
        internal static void RegisterCreated(GameObject go)
        {
            if (go == null || _cachedSceneData == null || !_cacheValid)
                return;

            var cache = _cachedSceneData;
            if (cache.Objects.Contains(go))
                return;

            int depth = 0;
            for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                depth++;

            var sceneName = go.scene.name;
            var stack = new Stack<(Transform transform, string path, int depth)>();
            stack.Push((go.transform, GetPath(go), depth));
            while (stack.Count > 0)
            {
                var (transform, path, level) = stack.Pop();
                var current = transform.gameObject;
                var entityId = UnityObjectIdUtility.GetEntityId(current);

                cache.Objects.Add(current);
                if (!string.IsNullOrEmpty(entityId))
                {
                    cache.PathsByEntityId[entityId] = path;
                    cache.DepthsByEntityId[entityId] = level;
                }
                AddPathLookup(cache.PathLookup, path, current);
                if (!string.IsNullOrEmpty(sceneName))
                    AddPathLookup(cache.PathLookup, sceneName + "/" + path, current);

                foreach (Transform child in transform)
                    stack.Push((child, path + "/" + child.name, level + 1));
            }
        }

        internal static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var parts = path
                .Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            return parts.Length == 0 ? null : string.Join("/", parts);
        }

        private static IEnumerable<GameObject> GetLoadedSceneRoots()
        {
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                    yield return root;
            }

            foreach (var root in GetDontDestroyOnLoadRoots())
                yield return root;
        }

        /// <summary>
        /// Scene name Unity assigns to the DontDestroyOnLoad pseudo-scene (Play mode only).
        /// SceneManager never lists it, so it cannot be reached through sceneCount/GetSceneAt.
        /// </summary>
        internal const string DontDestroyOnLoadSceneName = "DontDestroyOnLoad";

        /// <summary>
        /// Root GameObjects of the DontDestroyOnLoad pseudo-scene. Empty outside Play mode,
        /// where that scene does not exist. Assets are excluded by the scene validity check.
        /// </summary>
        internal static List<GameObject> GetDontDestroyOnLoadRoots()
        {
            var roots = new List<GameObject>();
            foreach (var go in Internal.FindHelper.FindAll<GameObject>(includeInactive: true))
            {
                if (go == null)
                    continue;

                var scene = go.scene;
                if (scene.IsValid() && scene.name == DontDestroyOnLoadSceneName && go.transform.parent == null)
                    roots.Add(go);
            }
            return roots;
        }

        /// <summary>
        /// Finds a GameObject using a flexible set of parameters, falling back step by step.
        /// Priority order: entityId &gt; instanceId &gt; path &gt; name (exact) &gt; name (whole word, then contains) &gt; tag &gt; component type.
        /// Non-exact resolutions are recorded for <see cref="DrainResolutionNotes"/>.
        /// </summary>
        /// <param name="name">Simple name (exact match first, falls back to whole-word, then contains match; when that fallback is ambiguous the first match is returned — <see cref="FindOrError"/> rejects it instead)</param>
        /// <param name="instanceId">Legacy Unity instance ID</param>
        /// <param name="path">Hierarchy path, e.g. "Parent/Child/Target"</param>
        /// <param name="tag">Look up by tag, e.g. "MainCamera", "Player"</param>
        /// <param name="componentType">Find the first object carrying this component, e.g. "Camera"</param>
        /// <param name="entityId">Unity EntityId, represented as a decimal ulong string</param>
        /// <returns>The found GameObject, or null if not found</returns>
        public static GameObject Find(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null, string entityId = null)
        {
            var go = Resolve(name, instanceId, path, tag, componentType, entityId, out var ambiguousMatches);
            // Find has no error channel, so an ambiguous word/substring match keeps its historical first-match
            // result; the note makes that choice visible instead of silent. FindOrError rejects it outright.
            if (ambiguousMatches != null)
                AddResolutionNote($"name '{name}' has no exact match and {ambiguousMatches.Count} objects contain it; used the first, '{go.name}' (path: {GetPath(go)})");
            return go;
        }

        /// <summary>
        /// The shared lookup behind <see cref="Find"/> and <see cref="FindOrError"/>. When the name's word/substring
        /// fallback matches several objects, <paramref name="ambiguousMatches"/> lists them and the first is returned.
        /// </summary>
        private static GameObject Resolve(string name, int instanceId, string path, string tag, string componentType, string entityId,
            out List<GameObject> ambiguousMatches)
        {
            ambiguousMatches = null;

            // Priority 1: EntityId (most precise, and compatible with Unity 6000.5).
            if (!string.IsNullOrWhiteSpace(entityId))
            {
                var obj = UnityObjectIdUtility.EntityIdToObject(entityId);
                if (obj is GameObject go)
                    return go;
                if (obj is Component component)
                    return component.gameObject;
            }

            // Priority 2: legacy instance ID.
            if (instanceId != 0)
            {
                var obj = UnityObjectIdUtility.ObjectIdToObject(instanceId);
                if (obj is GameObject go)
                    return go;
                if (obj is Component component)
                    return component.gameObject;
            }

            // Priority 3: Hierarchy path (can locate nested objects).
            bool pathMissed = false;
            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null)
                {
                    NoteInexactPath(path, go);
                    return go;
                }
                pathMissed = true;
            }

            // Priority 4: look up by simple name, exact match first, then whole-word, then substring.
            if (!string.IsNullOrEmpty(name))
            {
                var go = FindByExactName(name);
                if (go == null)
                {
                    var matches = FindByNameFallback(name, out var strategy);
                    if (matches.Count > 1)
                    {
                        ambiguousMatches = matches;
                        return matches[0];
                    }
                    if (matches.Count == 1)
                    {
                        go = matches[0];
                        AddResolutionNote($"name '{name}' has no exact match; resolved by {strategy} to '{go.name}' (path: {GetPath(go)})");
                    }
                }

                if (go != null)
                {
                    if (pathMissed)
                        AddResolutionNote($"path '{path}' not found; resolved by name '{name}' instead (path: {GetPath(go)})");
                    return go;
                }
            }

            // Priority 5: look up by tag.
            if (!string.IsNullOrEmpty(tag))
            {
                var go = GetAllSceneObjects().FirstOrDefault(candidate =>
                {
                    try { return candidate.CompareTag(tag); }
                    catch { return false; }
                });
                if (go != null)
                    return go;
            }

            // Priority 6: look up by component type.
            if (!string.IsNullOrEmpty(componentType))
            {
                var go = FindByComponent(componentType);
                if (go != null)
                    return go;
            }

            return null;
        }

        /// <summary>
        /// The exact-name tier: a case-sensitive match wins, otherwise the first case-insensitive one. Records a note when
        /// the match differs in case or when several objects share the name (the first in hierarchy order is used).
        /// </summary>
        private static GameObject FindByExactName(string name)
        {
            GameObject first = null;
            GameObject sameCase = null;
            int count = 0;
            foreach (var candidate in GetAllSceneObjects())
            {
                if (!candidate.name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                if (first == null)
                    first = candidate;
                if (sameCase == null && string.Equals(candidate.name, name, System.StringComparison.Ordinal))
                    sameCase = candidate;
            }

            var go = sameCase ?? first;
            if (go == null)
                return null;

            if (sameCase == null)
                AddResolutionNote($"name '{name}' matched '{go.name}' case-insensitively (path: {GetPath(go)})");
            if (count > 1)
                AddResolutionNote($"name '{name}' matches {count} objects; used '{GetPath(go)}' — pass path or entityId to target another");
            return go;
        }

        /// <summary>
        /// The fallback tiers for a name with no exact match: objects having it as a whole word ("Room" in "Room_Light"),
        /// or failing that, objects containing it as a substring. Returns every match of the first tier that has any.
        /// </summary>
        private static List<GameObject> FindByNameFallback(string name, out string strategy)
        {
            var objects = GetAllSceneObjects();
            var wholeWord = objects
                .Where(go => go.name.Split(' ', '_', '-').Any(
                    word => word.Equals(name, System.StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (wholeWord.Count > 0)
            {
                strategy = "whole-word match";
                return wholeWord;
            }

            strategy = "substring";
            return objects
                .Where(go => go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private static void NoteInexactPath(string requestedPath, GameObject go)
        {
            var normalized = NormalizePathKey(requestedPath);
            var actual = GetPath(go);
            if (string.Equals(normalized, actual, System.StringComparison.Ordinal))
                return;

            var sceneName = go.scene.name;
            if (!string.IsNullOrEmpty(sceneName) &&
                string.Equals(normalized, sceneName + "/" + actual, System.StringComparison.Ordinal))
                return;

            AddResolutionNote($"path '{requestedPath}' matched '{actual}' case-insensitively");
        }

        /// <summary>
        /// Finds a GameObject by Hierarchy path, e.g. "Canvas/Panel/Button".
        /// </summary>
        public static GameObject FindByPath(string path)
        {
            var normalizedPath = NormalizePathKey(path);
            if (string.IsNullOrEmpty(normalizedPath))
                return null;

            var cache = GetOrBuildSceneCache();
            if (cache.PathLookup.TryGetValue(normalizedPath, out var cachedGo))
                return cachedGo;

            var parts = normalizedPath.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            foreach (var scene in Enumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt)
                .Where(scene => scene.IsValid() && scene.isLoaded))
            {
                var rootObjects = scene.GetRootGameObjects();
                int partIndex = 0;

                if (parts.Length > 1 && scene.name.Equals(parts[0], System.StringComparison.OrdinalIgnoreCase))
                    partIndex = 1;

                if (partIndex >= parts.Length)
                    continue;

                var current = rootObjects.FirstOrDefault(go =>
                    go.name.Equals(parts[partIndex], System.StringComparison.OrdinalIgnoreCase));
                if (current == null)
                    continue;

                partIndex++;
                while (partIndex < parts.Length && current != null)
                {
                    current = FindDirectChild(current, parts[partIndex]);
                    partIndex++;
                }

                if (current != null)
                    return current;
            }

            return null;
        }

        private static GameObject FindDirectChild(GameObject parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName))
                return null;

            var exact = parent.transform.Find(childName);
            if (exact != null)
                return exact.gameObject;

            foreach (Transform child in parent.transform)
            {
                if (child.name.Equals(childName, System.StringComparison.OrdinalIgnoreCase))
                    return child.gameObject;
            }

            return null;
        }

        /// <summary>
        /// Finds a GameObject by name, case-insensitive.
        /// </summary>
        public static GameObject FindByNameCaseInsensitive(string name)
        {
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Finds a GameObject whose name contains the given substring.
        /// </summary>
        public static GameObject FindByNameContains(string name)
        {
            // Prefer a whole-word match first.
            var exactWord = GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Split(' ', '_', '-').Any(
                    word => word.Equals(name, System.StringComparison.OrdinalIgnoreCase)));
            if (exactWord != null)
                return exactWord;

            // If no whole-word match, fall back to substring containment.
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Finds the first GameObject carrying the given component type.
        /// </summary>
        public static GameObject FindByComponent(string componentType)
        {
            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null) return null;

            return GetAllSceneObjects().FirstOrDefault(go => go.GetComponent(type) != null);
        }

        /// <summary>
        /// Finds all GameObjects matching the given criteria.
        /// </summary>
        public static List<GameObject> FindAll(string name = null, string tag = null, string componentType = null, bool includeInactive = false)
        {
            IEnumerable<GameObject> results;

            results = GetAllSceneObjects();

            if (!includeInactive)
                results = results.Where(go => go.activeInHierarchy);

            if (!string.IsNullOrEmpty(tag))
            {
                results = results.Where(go =>
                {
                    try { return go.CompareTag(tag); }
                    catch { return false; }
                });
            }

            if (!string.IsNullOrEmpty(name))
            {
                results = results.Where(go => 
                    go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrEmpty(componentType))
            {
                var type = ComponentSkills.FindComponentType(componentType);
                if (type != null)
                    results = results.Where(go => go.GetComponent(type) != null);
            }

            return results.ToList();
        }

        /// <summary>
        /// Gets a GameObject's full Hierarchy path.
        /// </summary>
        public static string GetPath(GameObject go)
        {
            if (go == null)
                return null;

            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// Gets the full Hierarchy path via the request-level cache. Prefer this for large-scale read-only traversal.
        /// </summary>
        public static string GetCachedPath(GameObject go)
        {
            if (go == null)
                return null;

            var entityId = UnityObjectIdUtility.GetEntityId(go);
            var cache = GetOrBuildSceneCache();
            if (!string.IsNullOrEmpty(entityId) &&
                cache.PathsByEntityId.TryGetValue(entityId, out var cachedPath))
                return cachedPath;

            var path = GetPath(go);
            if (!string.IsNullOrEmpty(entityId))
                cache.PathsByEntityId[entityId] = path;
            return path;
        }

        /// <summary>
        /// Finds an object, returning an error with close-match suggestions when it can't be found, or with every
        /// candidate when a name has no exact match and its whole-word/substring fallback matches more than one object.
        /// </summary>
        public static (GameObject go, object error) FindOrError(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null, string entityId = null)
        {
            var go = Resolve(name, instanceId, path, tag, componentType, entityId, out var ambiguousMatches);
            if (ambiguousMatches != null)
                return (null, BuildAmbiguousNameError(name, ambiguousMatches));

            if (go == null)
            {
                var identifier = !string.IsNullOrEmpty(entityId) ? $"entityId {entityId}" :
                    instanceId != 0 ? $"instanceId {instanceId}" :
                    !string.IsNullOrEmpty(path) ? $"path '{path}'" :
                    !string.IsNullOrEmpty(tag) ? $"tag '{tag}'" :
                    !string.IsNullOrEmpty(componentType) ? $"component '{componentType}'" :
                    $"name '{name}'";

                var suggestions = GetSuggestions(name, tag, componentType);

                return (null, new {
                    error = $"GameObject not found: {identifier}",
                    suggestions = suggestions.Any() ? suggestions : null,
                    errorCode = SkillErrorCode.TargetNotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFindAndRetry,
                    relatedSkills = new[] { "gameobject_find", "scene_get_hierarchy" },
                    suggestedFixes = BuildNotFoundFixes(identifier, suggestions)
                });
            }
            return (go, null);
        }

        /// <summary>
        /// A name with no exact match whose word/substring fallback hits several objects: picking one would silently bind
        /// the wrong target ("Room" -> "RoomLight"), so the caller gets every candidate's path and entityId to choose from.
        /// </summary>
        private static object BuildAmbiguousNameError(string name, List<GameObject> matches)
        {
            var candidates = matches.Take(MaxAmbiguityCandidates).Select(go => new
            {
                name = go.name,
                path = GetPath(go),
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go)
            }).ToArray();

            var listed = string.Join(", ", candidates.Select(candidate => $"'{candidate.path}'"));
            var more = matches.Count > candidates.Length ? $" (+{matches.Count - candidates.Length} more)" : string.Empty;

            return new
            {
                error = $"GameObject name '{name}' is ambiguous: no object has exactly that name and {matches.Count} objects contain it: {listed}{more}. Retry with the exact path or entityId of the intended object.",
                errorCode = SkillErrorCode.TargetNotFound.ToWireString(),
                retryStrategy = SkillErrorResponse.RetryFindAndRetry,
                candidates,
                relatedSkills = new[] { "gameobject_find", "scene_get_hierarchy" },
                suggestedFixes = candidates.Take(3).Select(candidate => (object)new
                {
                    action = "fix_param",
                    reason = $"Target '{candidate.path}' explicitly: pass its path or entityId {candidate.entityId}."
                }).ToArray()
            };
        }

        /// <summary>
        /// Converts close-match candidates into suggestedFixes. Without this, computed candidates would still get
        /// dropped by the router — it only reads the error string.
        /// </summary>
        private static object[] BuildNotFoundFixes(string identifier, string[] suggestions)
        {
            var fixes = new List<object>();

            foreach (var candidate in suggestions.Take(3))
            {
                fixes.Add(new
                {
                    action = "find_target",
                    skill = "gameobject_find",
                    reason = $"Close match already in an open scene: {candidate}"
                });
            }

            fixes.Add(new
            {
                action = "find_target",
                skill = "scene_get_hierarchy",
                reason = $"Nothing matched {identifier}. List the open scenes' hierarchy, then retry with the exact path or the entityId it returns."
            });

            return fixes.ToArray();
        }

        /// <summary>
        /// Finds a GameObject and its required component, returning an error if either step fails.
        /// </summary>
        public static (T component, object error) FindComponentOrError<T>(string name = null, int instanceId = 0, string path = null, string entityId = null) where T : Component
        {
            var (go, err) = FindOrError(name, instanceId, path, entityId: entityId);
            if (err != null) return (null, err);
            var comp = go.GetComponent<T>();
            if (comp == null) return (null, new { error = $"No {typeof(T).Name} component on {go.name}" });
            return (comp, null);
        }

        /// <summary>
        /// Provides close-match candidate suggestions when a lookup fails.
        /// </summary>
        private static string[] GetSuggestions(string name, string tag, string componentType)
        {
            var suggestions = new List<string>();

            if (!string.IsNullOrEmpty(name))
            {
                // Fuzzy-match using the first 3 characters of the name.
                var similar = GetAllSceneObjects()
                    .Where(go => go.name.IndexOf(name.Substring(0, System.Math.Min(3, name.Length)),
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(5)
                    .Select(go => $"'{go.name}' (path: {GetPath(go)})");
                suggestions.AddRange(similar);
            }

            if (!string.IsNullOrEmpty(componentType))
            {
                // Also add objects that actually carry this component.
                var type = ComponentSkills.FindComponentType(componentType);
                if (type != null)
                {
                    var withComp = GetAllSceneObjects()
                        .Where(candidate => candidate.GetComponent(type) != null)
                        .Take(3)
                        .Select(candidate => $"'{candidate.name}' has {type.Name}");
                    suggestions.AddRange(withComp);
                }
            }

            return suggestions.Take(5).ToArray();
        }

        /// <summary>
        /// Smart lookup that tries several strategies in sequence, for AI callers unsure of the exact name.
        /// </summary>
        public static GameObject SmartFind(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;

            // Try as an exact name.
            var go = FindByNameCaseInsensitive(query);
            if (go != null) return go;

            // Try as a path.
            go = FindByPath(query);
            if (go != null) return go;

            // Try as a tag.
            go = Find(tag: query);
            if (go != null) return go;

            // Various ways of referring to "Main Camera".
            if (query.Equals("camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("main camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("maincamera", System.StringComparison.OrdinalIgnoreCase))
            {
                go = Camera.main?.gameObject;
                if (go != null) return go;

                // If there's no Camera.main, fall back to any camera in the scene.
                var cam = GetAllSceneObjects()
                    .Select(candidate => candidate.GetComponent<Camera>())
                    .FirstOrDefault(component => component != null);
                if (cam != null) return cam.gameObject;
            }

            // Various ways of referring to "Player".
            if (query.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                go = Find(tag: "Player");
                if (go != null) return go;
            }

            // Case-insensitive substring containment.
            go = FindByNameContains(query);
            if (go != null) return go;

            // Last resort: try as a component type name.
            go = FindByComponent(query);
            return go;
        }
    }

    /// <summary>
    /// Utility methods shared across skill modules.
    /// </summary>
    public static class SkillsCommon
    {
        /// <summary>UTF-8 encoding without a BOM.</summary>
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Shared JSON settings: Unicode is emitted directly and readably, without escaping.</summary>
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.Default
        };

        /// <summary>
        /// Same as <see cref="JsonSettings"/>, but null members are dropped rather than written as <c>null</c>.
        /// Reserved for <c>?wire=v2</c> manifest payloads, where "field absent" means "default / not applicable".
        /// All other responses must keep emitting explicit nulls — never redirect an existing path to this instance.
        /// </summary>
        public static readonly JsonSerializerSettings JsonSettingsOmitNull = new JsonSerializerSettings
        {
            StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.Default,
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
        };

        /// <summary>
        /// Gets all loaded types across every non-dynamic assembly.
        /// </summary>
        public static System.Collections.Generic.IEnumerable<System.Type> GetAllLoadedTypes()
        {
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Type.EmptyTypes; } });
        }

        /// <summary>
        /// Counts a mesh's triangles without allocating the full triangles array.
        /// </summary>
        public static int GetTriangleCount(UnityEngine.Mesh mesh)
        {
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += (int)mesh.GetIndexCount(i);
            return count / 3;
        }

        /// <summary>Returns true if the path exists (file or directory).</summary>
        public static bool PathExists(string path) =>
            !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

        /// <summary>Case-insensitive Levenshtein distance, for "did you mean" suggestions.</summary>
        internal static int EditDistance(string left, string right)
        {
            left = (left ?? string.Empty).ToLowerInvariant();
            right = (right ?? string.Empty).ToLowerInvariant();
            if (left.Length == 0) return right.Length;
            if (right.Length == 0) return left.Length;

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= right.Length; j++)
                {
                    int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = System.Math.Min(System.Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        /// <summary>All layer names currently defined in the Tags &amp; Layers window (built-in + user-defined).</summary>
        public static string[] DefinedLayerNames()
        {
            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                var n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) names.Add(n);
            }
            return names.ToArray();
        }

        /// <summary>
        /// Determines whether <paramref name="tag"/> is already registered in TagManager. An unregistered
        /// tag makes both GameObject.tag's setter and GameObject.FindGameObjectsWithTag fail -- the former
        /// silently (no throw, no effect), the latter by throwing UnityException -- so any tag write
        /// or tag-filtered read must pass this check first, rather than discovering the problem on impact.
        /// </summary>
        public static bool IsTagDefined(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return false;
            return InternalEditorUtility.tags.Contains(tag);
        }

        /// <summary>
        /// Case-insensitive Levenshtein nearest match, for a "did you mean" suggestion: null when
        /// nothing is close enough. The threshold scales with the input's length (at least 2, else
        /// a third of it) -- the same bound <c>BatchExecutor.SuggestField</c> used before this was
        /// extracted for reuse by other "unknown value, did you mean" error paths.
        /// </summary>
        public static string ClosestMatch(string input, IEnumerable<string> candidates)
        {
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (var candidate in candidates)
            {
                int distance = EditDistance(input, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best != null && bestDistance <= System.Math.Max(2, (input?.Length ?? 0) / 3) ? best : null;
        }

        // -----------------------------------------------------------------
        // Unified type lookup (cached, shared by every ReflectionHelper)
        // -----------------------------------------------------------------

        private static readonly Dictionary<string, System.Type> _findTypeCache =
            new Dictionary<string, System.Type>();

        /// <summary>
        /// Looks up a type by fully-qualified name across all loaded assemblies.
        /// The result is cached (including a null miss), so subsequent lookups are O(1).
        /// </summary>
        public static System.Type FindTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_findTypeCache.TryGetValue(fullName, out var cached)) return cached;

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) { _findTypeCache[fullName] = t; return t; }
                }
                catch { /* skip assemblies that fail to enumerate */ }
            }

            _findTypeCache[fullName] = null;
            return null;
        }
    }
}

// Producer:Betsy
