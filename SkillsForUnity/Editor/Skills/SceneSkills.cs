using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// 场景管理技能：加载、保存、新建、查询信息。
    /// </summary>
    public static class SceneSkills
    {
        [UnitySkill("scene_create", "Create a new empty scene",
            Category = SkillCategory.Scene, Operation = SkillOperation.Create,
            Tags = new[] { "new", "empty", "setup" },
            Outputs = new[] { "scenePath", "sceneName" },
            TracksWorkflow = true,
            MutatesScene = true, MutatesAssets = true, RiskLevel = "high")]
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

            // SaveScene 已把新的 .unity 写到磁盘；记为 Created，undo 时将其移入存储区即等于删除
            // （可 redo）。开销很小——无需备份文件字节。
            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath);
            if (sceneAsset != null) WorkflowManager.SnapshotCreatedAsset(sceneAsset);

            return new { success = true, scenePath, sceneName = scene.name };
        }

        [UnitySkill("scene_load", "Load an existing scene",
            Category = SkillCategory.Scene, Operation = SkillOperation.Execute,
            Tags = new[] { "open", "load", "additive" },
            Outputs = new[] { "sceneName", "scenePath" },
            RequiresInput = new[] { "scenePath" },
            MutatesScene = true, RiskLevel = "high")]
        public static object SceneLoad(string scenePath, bool additive = false)
        {
            if (!File.Exists(scenePath))
                return new { error = $"Scene not found: {scenePath}" };

            var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(scenePath, mode);

            return new { success = true, sceneName = scene.name, scenePath = scene.path };
        }

        [UnitySkill("scene_save", "Save the current scene. Undo restores the on-disk .unity file; if the scene is open, reload it (Reload Scene) to see the reverted state.",
            Category = SkillCategory.Scene, Operation = SkillOperation.Execute,
            Tags = new[] { "save", "persist", "write" },
            Outputs = new[] { "scenePath" },
            TracksWorkflow = true,
            MutatesAssets = true, RiskLevel = "high")]
        public static object SceneSave(string scenePath = null)
        {
            if (!string.IsNullOrEmpty(scenePath) && Validate.SafePath(scenePath, "scenePath") is object pathErr) return pathErr;

            var scene = SceneManager.GetActiveScene();
            var path = scenePath ?? scene.path;

            if (string.IsNullOrEmpty(path))
                return new { error = "Scene has no path. Provide scenePath parameter." };

            // 让保存可回滚。覆盖已有 .unity 属 Modified：必须在 SaveScene 覆盖文件*之前*把旧字节
            // 备份进内容寻址存储区（undo 时写回磁盘）。存到全新路径属 Created：必须在文件存在*之后*
            // 记录（undo 把新文件移入存储区，redo 再还原）。
            // undo/redo 作用于磁盘文件；当前已打开的场景需要重新加载才能反映变化。
            bool existedBefore = File.Exists(path);
            if (existedBefore)
            {
                var oldAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                if (oldAsset != null) WorkflowManager.SnapshotObject(oldAsset);
            }

            EditorSceneManager.SaveScene(scene, path);

            if (!existedBefore)
            {
                AssetDatabase.Refresh();
                var newAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                if (newAsset != null) WorkflowManager.SnapshotCreatedAsset(newAsset);
            }

            return new { success = true, scenePath = path };
        }

        [UnitySkill("scene_get_info", "Get current scene information",
            Category = SkillCategory.Scene, Operation = SkillOperation.Query,
            Tags = new[] { "info", "status", "roots" },
            Outputs = new[] { "sceneName", "scenePath", "isDirty", "rootObjectCount", "rootObjects" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                    entityId = UnityObjectIdUtility.GetEntityId(go),
                    instanceId = UnityObjectIdUtility.GetObjectId(go),
                    childCount = go.transform.childCount
                }).ToArray()
            };
        }

        [UnitySkill("scene_get_hierarchy", "Get scene hierarchy tree",
            Category = SkillCategory.Scene, Operation = SkillOperation.Query,
            Tags = new[] { "hierarchy", "tree", "structure" },
            Outputs = new[] { "sceneName", "hierarchy" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object SceneGetHierarchy(int maxDepth = 3)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var hierarchy = new object[roots.Length];
            var componentBuffer = new List<Component>(8);

            for (int i = 0; i < roots.Length; i++)
                hierarchy[i] = GetHierarchyNode(roots[i], 0, maxDepth, componentBuffer);

            return new
            {
                sceneName = scene.name,
                hierarchy
            };
        }

        private static object GetHierarchyNode(GameObject go, int depth, int maxDepth, List<Component> componentBuffer)
        {
            var childCount = go.transform.childCount;
            object[] children = null;
            if (depth < maxDepth && childCount > 0)
            {
                children = new object[childCount];
                for (int i = 0; i < childCount; i++)
                    children[i] = GetHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, componentBuffer);
            }

            // childCount 始终是真实子节点数，因此 children==null && childCount>0 表示该节点被
            // maxDepth 截断——可与真正的叶子节点（childCount 为 0）区分开。
            var node = new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                components = GetComponentTypeNames(go, componentBuffer),
                childCount,
                children
            };
            return node;
        }

        private static string[] GetComponentTypeNames(GameObject go, List<Component> componentBuffer)
        {
            componentBuffer.Clear();
            go.GetComponents(componentBuffer);

            var names = new List<string>(componentBuffer.Count);
            foreach (var component in componentBuffer)
            {
                if (component != null)
                    names.Add(component.GetType().Name);
            }

            return names.ToArray();
        }

        [UnitySkill("scene_screenshot", "Capture a screenshot of the GAME VIEW (the final composited frame of all cameras + UI; in Play mode this is the live runtime image, NOT the Scene/editor view). filename is a bare filename only (no path separators); saved under Assets/Screenshots/. Async: the PNG is written ~1 frame later, so if an immediate read fails, wait ~200ms and retry. Set returnImage=true to also get a PNG as base64 in the response, for clients without filesystem access (e.g. remote/MCP) — captured via a separate synchronous read of the last-rendered Game View frame, so it may be a moment older than the file written to disk.",
            Category = SkillCategory.Scene, Operation = SkillOperation.Execute,
            Tags = new[] { "screenshot", "capture", "image", "gameview", "playmode" },
            Outputs = new[] { "path", "width", "height", "isPlaying", "note", "imageBase64", "imageWidth", "imageHeight", "imageBytes" })]
        public static object SceneScreenshot(string filename = "screenshot.png", int width = 1920, int height = 1080, bool returnImage = false, int maxDimension = 1280)
        {
            // 剥掉所有路径成分，防止写到 Screenshots/ 之外
            filename = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(filename)) filename = "screenshot";
            if (!Path.HasExtension(filename)) filename += ".png";
            var path = Path.Combine(Application.dataPath, "Screenshots", filename);
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            int superSize = Mathf.Max(1, width / Screen.width);
            // ScreenCapture.CaptureScreenshot 抓的是 Game View 最终合成帧，并在*下一帧*才写出 PNG
            // （非同步）。此刻文件还不在磁盘上，立即 Refresh 等于空操作，因此把资产导入推迟到
            // 下一个编辑器 tick。
            ScreenCapture.CaptureScreenshot(path, superSize);
            EditorApplication.delayCall += () => AssetDatabase.Refresh();

            bool isPlaying = EditorApplication.isPlaying;
            string note = isPlaying
                ? "Game View captured (live runtime frame). The PNG is written ~1 frame later; if your immediate read fails, wait ~200ms and retry."
                : "Game View captured in Edit mode — this is the last rendered Game View frame and may be static or blank. Enter Play mode (editor_play) for a live runtime frame. The PNG is written ~1 frame later; if your immediate read fails, wait ~200ms and retry.";

            var result = new Dictionary<string, object> { ["success"] = true, ["path"] = path, ["width"] = width, ["height"] = height, ["isPlaying"] = isPlaying, ["note"] = note };
            if (returnImage)
            {
                // 磁盘上的 PNG 此时还读不到（约一帧后才写出，见上），因此 returnImage 走另一条路：
                // 同步在内存中抓取 Game View 当前后台缓冲，而不是回读 `path`。
                Texture2D liveTex = null;
                try
                {
                    liveTex = ScreenCapture.CaptureScreenshotAsTexture(superSize);
                    if (liveTex == null || liveTex.width <= 0 || liveTex.height <= 0)
                    {
                        result["note"] = note + " returnImage capture unavailable (no Game View pixels to read); read the saved PNG at 'path' instead.";
                    }
                    else
                    {
                        var pngBytes = liveTex.EncodeToPNG();
                        var imageFields = ScreenshotImageEncoder.Encode(pngBytes, liveTex.width, liveTex.height, maxDimension, out var imageError);
                        if (imageError != null) return imageError;
                        foreach (var kv in imageFields) result[kv.Key] = kv.Value;
                    }
                }
                finally
                {
                    if (liveTex != null) Object.DestroyImmediate(liveTex);
                }
            }

            return result;
        }

        [UnitySkill("scene_get_loaded", "Get list of all currently loaded scenes",
            Category = SkillCategory.Scene, Operation = SkillOperation.Query,
            Tags = new[] { "loaded", "list", "multi-scene" },
            Outputs = new[] { "count", "scenes" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("scene_unload", "Unload a loaded scene (additive)",
            Category = SkillCategory.Scene, Operation = SkillOperation.Execute,
            Tags = new[] { "unload", "close", "multi-scene" },
            Outputs = new[] { "unloaded" },
            RequiresInput = new[] { "scenePath" })]
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
                EditorSceneManager.SaveScene(sceneToUnload);
            }

            EditorSceneManager.CloseScene(sceneToUnload, true);
            return new { success = true, unloaded = sceneName };
        }

        [UnitySkill("scene_set_active", "Set the active scene (for multi-scene editing)",
            Category = SkillCategory.Scene, Operation = SkillOperation.Modify,
            Tags = new[] { "active", "focus", "multi-scene" },
            Outputs = new[] { "activeScene" },
            RequiresInput = new[] { "scenePath" })]
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
        [UnitySkill("scene_find_objects", "Search GameObjects by name pattern, tag, or component type. For advanced search (regex, layer, path) use gameobject_find.",
            Category = SkillCategory.Scene, Operation = SkillOperation.Query,
            Tags = new[] { "search", "filter", "find", "objects" },
            Outputs = new[] { "count", "objects", "instanceId", "path" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object SceneFindObjects(string namePattern = null, string tag = null, string componentType = null, int limit = 50)
        {
            IEnumerable<GameObject> objects = GameObjectFinder.GetSceneObjects();

            if (!string.IsNullOrEmpty(tag))
            {
                try { objects = objects.Where(go => go.CompareTag(tag)); }
                catch { return new { error = $"Invalid tag: {tag}" }; }
            }

            if (!string.IsNullOrEmpty(namePattern))
                objects = objects.Where(go => go.name.IndexOf(namePattern, System.StringComparison.OrdinalIgnoreCase) >= 0);

            if (!string.IsNullOrEmpty(componentType))
            {
                var type = ComponentSkills.FindComponentType(componentType);
                if (type == null) return new { error = $"Component type not found: {componentType}" };
                objects = objects.Where(go => go.GetComponent(type) != null);
            }

            var results = objects.Take(limit).Select(go => new {
                name = go.name, path = GameObjectFinder.GetCachedPath(go), entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go),
                active = go.activeInHierarchy, tag = go.tag
            }).ToArray();

            return new { success = true, count = results.Length, objects = results };
        }
    }
}

// Producer:Betsy
