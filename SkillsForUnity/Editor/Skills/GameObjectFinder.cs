using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
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
        /// <summary>
        /// Efficiently iterate all GameObjects in scene using root traversal (faster than FindObjectsOfType)
        /// </summary>
        private static IEnumerable<GameObject> GetAllSceneObjects()
        {
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var stack = new Stack<Transform>();
            foreach (var root in roots)
                stack.Push(root.transform);

            while (stack.Count > 0)
            {
                var t = stack.Pop();
                yield return t.gameObject;
                foreach (Transform child in t)
                    stack.Push(child);
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
                // Try exact match with GameObject.Find
                var go = GameObject.Find(name);
                if (go != null)
                    return go;

                // Try case-insensitive exact match
                go = FindByNameCaseInsensitive(name);
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
                try
                {
                    var go = GameObject.FindGameObjectWithTag(tag);
                    if (go != null)
                        return go;
                }
                catch { } // Tag might not exist
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

            var parts = path.Split('/');
            if (parts.Length == 0)
                return null;

            // First, find root objects
            var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            
            // Find first part in root (case-insensitive)
            var current = rootObjects.FirstOrDefault(go => 
                go.name.Equals(parts[0], System.StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return null;

            // Navigate down the hierarchy
            for (int i = 1; i < parts.Length; i++)
            {
                Transform child = null;
                
                // Try exact match first
                child = current.transform.Find(parts[i]);
                
                // Try case-insensitive match
                if (child == null)
                {
                    foreach (Transform t in current.transform)
                    {
                        if (t.name.Equals(parts[i], System.StringComparison.OrdinalIgnoreCase))
                        {
                            child = t;
                            break;
                        }
                    }
                }
                
                if (child == null)
                    return null;
                current = child.gameObject;
            }

            return current;
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

            var comp = Object.FindObjectOfType(type) as Component;
            return comp?.gameObject;
        }

        /// <summary>
        /// Find all GameObjects matching criteria
        /// </summary>
        public static List<GameObject> FindAll(string name = null, string tag = null, string componentType = null, bool includeInactive = false)
        {
            IEnumerable<GameObject> results;

            if (!string.IsNullOrEmpty(tag))
            {
                try { results = GameObject.FindGameObjectsWithTag(tag); }
                catch { results = new GameObject[0]; }
            }
            else if (includeInactive)
            {
                results = Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(go => go.scene.isLoaded); // Only scene objects, not prefabs
            }
            else
            {
                results = GetAllSceneObjects();
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
                    var withComp = Object.FindObjectsOfType(type)
                        .OfType<Component>()
                        .Take(3)
                        .Select(c => $"'{c.gameObject.name}' has {type.Name}");
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
            var go = GameObject.Find(query);
            if (go != null) return go;

            // Try as path
            go = FindByPath(query);
            if (go != null) return go;

            // Try as tag
            try { go = GameObject.FindGameObjectWithTag(query); if (go != null) return go; } catch { }

            // Try finding "Main Camera" variations
            if (query.Equals("camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("main camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("maincamera", System.StringComparison.OrdinalIgnoreCase))
            {
                go = Camera.main?.gameObject;
                if (go != null) return go;
                
                // Find any camera
                var cam = Object.FindObjectOfType<Camera>();
                if (cam != null) return cam.gameObject;
            }

            // Try finding "Player" variations
            if (query.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { go = GameObject.FindGameObjectWithTag("Player"); if (go != null) return go; } catch { }
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
