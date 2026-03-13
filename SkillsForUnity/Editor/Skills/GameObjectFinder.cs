using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    /// <summary>
    /// Compatibility helper for FindObjectsByType (Unity 6+) / FindObjectsOfType fallback.
    /// </summary>
    internal static class FindHelper
    {
        internal static T[] FindAll<T>(bool includeInactive = false) where T : Object
        {
#if UNITY_6000_0_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                : Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return includeInactive
                ? Resources.FindObjectsOfTypeAll<T>()
                : Object.FindObjectsOfType<T>();
#endif
        }
    }

    /// <summary>
    /// Parameter validation helper - returns error object or null
    /// </summary>
    public static class Validate
    {
        /// <summary>
        /// Check if string parameter is provided. Returns error object if empty, null if valid.
        /// Usage: if (Validate.Required(x, "x") is object err) return err;
        /// </summary>
        public static object Required(string value, string paramName) =>
            string.IsNullOrEmpty(value) ? new { error = $"{paramName} is required" } : null;

        /// <summary>
        /// Check if a JSON array parameter is provided and non-empty.
        /// Usage: if (Validate.RequiredJsonArray(items, "items") is object err) return err;
        /// </summary>
        public static object RequiredJsonArray(string jsonArray, string paramName)
        {
            if (string.IsNullOrEmpty(jsonArray))
                return new { error = $"{paramName} is required" };
            var trimmed = jsonArray.Trim();
            if (trimmed == "[]" || trimmed == "null")
                return new { error = $"{paramName} must be a non-empty array" };
            return null;
        }

        /// <summary>
        /// Validate that a numeric value is within range (inclusive).
        /// Usage: if (Validate.InRange(count, 1, 100, "count") is object err) return err;
        /// </summary>
        public static object InRange(float value, float min, float max, string paramName)
        {
            if (value < min || value > max)
                return new { error = $"{paramName} must be between {min} and {max}, got {value}" };
            return null;
        }

        /// <summary>
        /// Validate that an integer value is within range (inclusive).
        /// </summary>
        public static object InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                return new { error = $"{paramName} must be between {min} and {max}, got {value}" };
            return null;
        }

        /// <summary>
        /// Validate asset path for safety. Prevents path traversal and restricts to Assets/Packages.
        /// Usage: if (Validate.SafePath(path, "path") is object err) return err;
        /// </summary>
        public static object SafePath(string path, string paramName, bool isDelete = false)
        {
            if (string.IsNullOrEmpty(path))
                return new { error = $"{paramName} is required" };

            // Normalize path
            var normalized = path.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            if (normalized.StartsWith("./")) normalized = normalized.Substring(2);

            // Prevent path traversal
            if (normalized.Contains(".."))
                return new { error = $"Path traversal not allowed: {path}" };

            // Restrict to Assets/ or Packages/
            if (!normalized.StartsWith("Assets/") && !normalized.StartsWith("Packages/") &&
                normalized != "Assets" && normalized != "Packages")
                return new { error = $"Path must start with Assets/ or Packages/: {path}" };

            // Prevent deleting root folders
            if (isDelete && (normalized == "Assets" || normalized == "Assets/" ||
                            normalized == "Packages" || normalized == "Packages/"))
                return new { error = "Cannot delete root Assets or Packages folder" };

            return null;
        }
    }

    /// <summary>
    /// Unified utility for finding GameObjects by multiple methods.
    /// Supports: name, instance ID, hierarchy path, tag, component type.
    /// Enhanced with intelligent fallback search strategies.
    /// </summary>
    public static class GameObjectFinder
    {
        // Request-level cache for GetAllSceneObjects - invalidated after each request via InvalidateCache()
        private static List<GameObject> _cachedSceneObjects;
        private static bool _cacheValid = false;

        /// <summary>
        /// Invalidate the scene objects cache. Should be called after each request cycle.
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedSceneObjects = null;
            _cacheValid = false;
        }

        /// <summary>
        /// Efficiently iterate all GameObjects in scene using root traversal (faster than FindObjectsOfType).
        /// Results are cached per-frame to avoid repeated traversals within the same request.
        /// </summary>
        private static IEnumerable<GameObject> GetAllSceneObjects()
        {
            if (_cachedSceneObjects != null && _cacheValid)
                return _cachedSceneObjects;

            var result = new List<GameObject>();
            var roots = GetLoadedSceneRoots();
            var stack = new Stack<Transform>();
            foreach (var root in roots)
                stack.Push(root.transform);

            while (stack.Count > 0)
            {
                var t = stack.Pop();
                result.Add(t.gameObject);
                foreach (Transform child in t)
                    stack.Push(child);
            }

            _cachedSceneObjects = result;
            _cacheValid = true;
            return result;
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
        }

        /// <summary>
        /// Find a GameObject using flexible parameters with intelligent fallback.
        /// Priority: instanceId > path > name (exact) > name (contains) > tag > component
        /// </summary>
        /// <param name="name">Simple name to search (uses GameObject.Find, then fallback to contains)</param>
        /// <param name="instanceId">Unity instance ID (most precise)</param>
        /// <param name="path">Hierarchy path like "Parent/Child/Target"</param>
        /// <param name="tag">Tag to search by (e.g., "MainCamera", "Player")</param>
        /// <param name="componentType">Find first object with this component (e.g., "Camera")</param>
        /// <returns>Found GameObject or null</returns>
        public static GameObject Find(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null)
        {
            // Priority 1: Instance ID (most precise, works regardless of selection/focus)
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj is GameObject go)
                    return go;
            }

            // Priority 2: Hierarchy path (works for nested objects)
            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null)
                    return go;
            }

            // Priority 3: Simple name search (exact match first)
            if (!string.IsNullOrEmpty(name))
            {
                var go = FindByNameCaseInsensitive(name);
                if (go != null)
                    return go;

                // Try contains match as fallback
                go = FindByNameContains(name);
                if (go != null)
                    return go;
            }

            // Priority 4: Tag search
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

            // Priority 5: Component type search
            if (!string.IsNullOrEmpty(componentType))
            {
                var go = FindByComponent(componentType);
                if (go != null)
                    return go;
            }

            return null;
        }

        /// <summary>
        /// Find a GameObject by hierarchy path (e.g., "Canvas/Panel/Button")
        /// </summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var parts = path.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
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
        /// Find GameObject by name with case-insensitive matching
        /// </summary>
        public static GameObject FindByNameCaseInsensitive(string name)
        {
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Find GameObject by name containing the search string
        /// </summary>
        public static GameObject FindByNameContains(string name)
        {
            // Prefer exact word match first
            var exactWord = GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Split(' ', '_', '-').Any(
                    word => word.Equals(name, System.StringComparison.OrdinalIgnoreCase)));
            if (exactWord != null)
                return exactWord;

            // Then try contains
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Find first GameObject with the specified component type
        /// </summary>
        public static GameObject FindByComponent(string componentType)
        {
            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null) return null;

            return GetAllSceneObjects().FirstOrDefault(go => go.GetComponent(type) != null);
        }

        /// <summary>
        /// Find all GameObjects matching criteria
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
        /// Get the full hierarchy path of a GameObject
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
        /// Find or report error with helpful suggestions
        /// </summary>
        public static (GameObject go, object error) FindOrError(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null)
        {
            var go = Find(name, instanceId, path, tag, componentType);
            if (go == null)
            {
                var identifier = instanceId != 0 ? $"instanceId {instanceId}" : 
                    !string.IsNullOrEmpty(path) ? $"path '{path}'" :
                    !string.IsNullOrEmpty(tag) ? $"tag '{tag}'" :
                    !string.IsNullOrEmpty(componentType) ? $"component '{componentType}'" :
                    $"name '{name}'";

                // Provide helpful suggestions
                var suggestions = GetSuggestions(name, tag, componentType);
                
                return (null, new { 
                    error = $"GameObject not found: {identifier}",
                    suggestions = suggestions.Any() ? suggestions : null
                });
            }
            return (go, null);
        }

        /// <summary>
        /// Get suggestions for similar objects when search fails
        /// </summary>
        private static string[] GetSuggestions(string name, string tag, string componentType)
        {
            var suggestions = new List<string>();

            if (!string.IsNullOrEmpty(name))
            {
                // Find similar names
                var similar = GetAllSceneObjects()
                    .Where(go => go.name.IndexOf(name.Substring(0, System.Math.Min(3, name.Length)),
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(5)
                    .Select(go => $"'{go.name}' (path: {GetPath(go)})");
                suggestions.AddRange(similar);
            }

            if (!string.IsNullOrEmpty(componentType))
            {
                // Find objects with similar components
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
        /// Smart find that tries multiple strategies
        /// Useful for AI that might not know exact names
        /// </summary>
        public static GameObject SmartFind(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;

            // Try as exact name
            var go = FindByNameCaseInsensitive(query);
            if (go != null) return go;

            // Try as path
            go = FindByPath(query);
            if (go != null) return go;

            // Try as tag
            go = Find(tag: query);
            if (go != null) return go;

            // Try finding "Main Camera" variations
            if (query.Equals("camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("main camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("maincamera", System.StringComparison.OrdinalIgnoreCase))
            {
                go = Camera.main?.gameObject;
                if (go != null) return go;
                
                // Find any camera
                var cam = GetAllSceneObjects()
                    .Select(candidate => candidate.GetComponent<Camera>())
                    .FirstOrDefault(component => component != null);
                if (cam != null) return cam.gameObject;
            }

            // Try finding "Player" variations
            if (query.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                go = Find(tag: "Player");
                if (go != null) return go;
            }

            // Try case-insensitive contains
            go = FindByNameContains(query);
            if (go != null) return go;

            // Try as component type
            go = FindByComponent(query);
            return go;
        }
    }
}
