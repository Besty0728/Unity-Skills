using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Scene management skills - load, save, create, get info.
    /// </summary>
    public static class SceneSkills
    {
        [UnitySkill("scene_create", "Create a new empty scene")]
        public static object SceneCreate(string scenePath)
        {
            if (Validate.Required(scenePath, "scenePath") is object err) return err;
            if (Validate.SafePath(scenePath, "scenePath") is object pathErr) return pathErr;

            var dir = Path.GetDirectoryName(scenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.Refresh();

            return new { success = true, scenePath, sceneName = scene.name };
        }

        [UnitySkill("scene_load", "Load an existing scene")]
        public static object SceneLoad(string scenePath, bool additive = false)
        {
            if (!File.Exists(scenePath))
                return new { error = $"Scene not found: {scenePath}" };

            var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(scenePath, mode);

            return new { success = true, sceneName = scene.name, scenePath = scene.path };
        }

        [UnitySkill("scene_save", "Save the current scene")]
        public static object SceneSave(string scenePath = null)
        {
            if (!string.IsNullOrEmpty(scenePath) && Validate.SafePath(scenePath, "scenePath") is object pathErr) return pathErr;

            var scene = SceneManager.GetActiveScene();
            var path = scenePath ?? scene.path;

            if (string.IsNullOrEmpty(path))
                return new { error = "Scene has no path. Provide scenePath parameter." };

            EditorSceneManager.SaveScene(scene, path);
            return new { success = true, scenePath = path };
        }

        [UnitySkill("scene_get_info", "Get current scene information")]
        public static object SceneGetInfo()
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            return new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                isDirty = scene.isDirty,
                rootObjectCount = roots.Length,
                rootObjects = roots.Select(go => new
                {
                    name = go.name,
                    instanceId = go.GetInstanceID(),
                    childCount = go.transform.childCount
                }).ToArray()
            };
        }

        [UnitySkill("scene_get_hierarchy", "Get scene hierarchy tree")]
        public static object SceneGetHierarchy(int maxDepth = 3)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            return new
            {
                sceneName = scene.name,
                hierarchy = roots.Select(go => GetHierarchyNode(go, 0, maxDepth)).ToArray()
            };
        }

        private static object GetHierarchyNode(GameObject go, int depth, int maxDepth)
        {
            var node = new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                components = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray(),
                children = depth < maxDepth
                    ? Enumerable.Range(0, go.transform.childCount)
                        .Select(i => GetHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth))
                        .ToArray()
                    : null
            };
            return node;
        }

        [UnitySkill("scene_screenshot", "Capture a screenshot of the game view. filename is a bare filename only (no path separators); saved under Assets/Screenshots/.")]
        public static object SceneScreenshot(string filename = "screenshot.png", int width = 1920, int height = 1080)
        {
            // Strip any path components to prevent writing outside Screenshots/
            filename = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(filename)) filename = "screenshot";
            if (!Path.HasExtension(filename)) filename += ".png";
            var path = Path.Combine(Application.dataPath, "Screenshots", filename);
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            int superSize = Mathf.Max(1, width / Screen.width);
            ScreenCapture.CaptureScreenshot(path, superSize);
            AssetDatabase.Refresh();

            return new { success = true, path, width, height };
        }

        [UnitySkill("scene_get_loaded", "Get list of all currently loaded scenes")]
        public static object SceneGetLoaded()
        {
            var scenes = new System.Collections.Generic.List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(new
                {
                    name = scene.name,
                    path = scene.path,
                    isLoaded = scene.isLoaded,
                    isDirty = scene.isDirty,
                    isActive = scene == SceneManager.GetActiveScene(),
                    rootCount = scene.rootCount
                });
            }
            return new { success = true, count = scenes.Count, scenes };
        }

        [UnitySkill("scene_unload", "Unload a loaded scene (additive)")]
        public static object SceneUnload(string sceneName)
        {
            Scene sceneToUnload = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name == sceneName || scene.path.EndsWith(sceneName + ".unity"))
                {
                    sceneToUnload = scene;
                    break;
                }
            }

            if (!sceneToUnload.IsValid())
                return new { success = false, error = $"Scene '{sceneName}' not found in loaded scenes" };

            if (SceneManager.sceneCount <= 1)
                return new { success = false, error = "Cannot unload the only loaded scene" };

            if (sceneToUnload.isDirty)
            {
                // Auto-save before unload
                EditorSceneManager.SaveScene(sceneToUnload);
            }

            EditorSceneManager.CloseScene(sceneToUnload, true);
            return new { success = true, unloaded = sceneName };
        }

        [UnitySkill("scene_set_active", "Set the active scene (for multi-scene editing)")]
        public static object SceneSetActive(string sceneName)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name == sceneName || scene.path.EndsWith(sceneName + ".unity"))
                {
                    if (!scene.isLoaded)
                        return new { success = false, error = $"Scene '{sceneName}' is not loaded" };

                    SceneManager.SetActiveScene(scene);
                    return new { success = true, activeScene = scene.name };
                }
            }
            return new { success = false, error = $"Scene '{sceneName}' not found in loaded scenes" };
        }
        [UnitySkill("scene_find_objects", "Search GameObjects by name pattern, tag, or component type. For advanced search (regex, layer, path) use gameobject_find.")]
        public static object SceneFindObjects(string namePattern = null, string tag = null, string componentType = null, int limit = 50)
        {
            IEnumerable<GameObject> objects = Object.FindObjectsOfType<GameObject>();

            if (!string.IsNullOrEmpty(tag))
            {
                try { objects = objects.Where(go => go.CompareTag(tag)); }
                catch { return new { error = $"Invalid tag: {tag}" }; }
            }

            if (!string.IsNullOrEmpty(namePattern))
                objects = objects.Where(go => go.name.IndexOf(namePattern, System.StringComparison.OrdinalIgnoreCase) >= 0);

            if (!string.IsNullOrEmpty(componentType))
            {
                var type = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.Name.Equals(componentType, System.StringComparison.OrdinalIgnoreCase) && typeof(Component).IsAssignableFrom(t));
                if (type == null) return new { error = $"Component type not found: {componentType}" };
                objects = objects.Where(go => go.GetComponent(type) != null);
            }

            var results = objects.Take(limit).Select(go => new {
                name = go.name, path = GameObjectFinder.GetPath(go), instanceId = go.GetInstanceID(),
                active = go.activeInHierarchy, tag = go.tag
            }).ToArray();

            return new { success = true, count = results.Length, objects = results };
        }
    }
}
