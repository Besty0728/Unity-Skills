using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Editor control skills - play mode, selection, tools.
    /// </summary>
    public static class EditorSkills
    {
        [UnitySkill("editor_play", "Enter play mode")]
        public static object EditorPlay()
        {
            if (EditorApplication.isPlaying)
                return new { error = "Already in play mode" };

            EditorApplication.isPlaying = true;
            return new { success = true, mode = "playing" };
        }

        [UnitySkill("editor_stop", "Exit play mode")]
        public static object EditorStop()
        {
            if (!EditorApplication.isPlaying)
                return new { error = "Not in play mode" };

            EditorApplication.isPlaying = false;
            return new { success = true, mode = "stopped" };
        }

        [UnitySkill("editor_pause", "Pause/unpause play mode")]
        public static object EditorPause()
        {
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new { success = true, paused = EditorApplication.isPaused };
        }

        [UnitySkill("editor_select", "Select a GameObject")]
        public static object EditorSelect(string gameObjectName = null, int instanceId = 0)
        {
            GameObject go = null;

            if (instanceId != 0)
                go = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            else if (!string.IsNullOrEmpty(gameObjectName))
                go = GameObject.Find(gameObjectName);

            if (go == null)
                return new { error = "GameObject not found" };

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            return new { success = true, selected = go.name };
        }

        [UnitySkill("editor_get_selection", "Get currently selected objects")]
        public static object EditorGetSelection()
        {
            var selected = Selection.gameObjects.Select(go => new
            {
                name = go.name,
                instanceId = go.GetInstanceID()
            }).ToArray();

            return new { count = selected.Length, objects = selected };
        }

        [UnitySkill("editor_undo", "Undo the last action")]
        public static object EditorUndo()
        {
            Undo.PerformUndo();
            return new { success = true, message = "Undo performed" };
        }

        [UnitySkill("editor_redo", "Redo the last undone action")]
        public static object EditorRedo()
        {
            Undo.PerformRedo();
            return new { success = true, message = "Redo performed" };
        }

        [UnitySkill("editor_get_state", "Get current editor state")]
        public static object EditorGetState()
        {
            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                timeSinceStartup = EditorApplication.timeSinceStartup,
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString()
            };
        }

        [UnitySkill("editor_execute_menu", "Execute a Unity menu item")]
        public static object EditorExecuteMenu(string menuPath)
        {
            var result = EditorApplication.ExecuteMenuItem(menuPath);
            if (!result)
                return new { error = $"Menu item not found or failed: {menuPath}" };

            return new { success = true, executed = menuPath };
        }

        [UnitySkill("editor_get_tags", "Get all available tags")]
        public static object EditorGetTags()
        {
            return new { tags = InternalEditorUtility.tags };
        }

        [UnitySkill("editor_get_layers", "Get all available layers")]
        public static object EditorGetLayers()
        {
            var layers = Enumerable.Range(0, 32)
                .Select(i => new { index = i, name = LayerMask.LayerToName(i) })
                .Where(l => !string.IsNullOrEmpty(l.name))
                .ToArray();

            return new { layers };
        }

        [UnitySkill("editor_get_context", "Get full editor context - selected GameObjects, selected assets, active scene, focused window. Use this to get current selection without searching.")]
        public static object EditorGetContext(bool includeComponents = false, bool includeChildren = false)
        {
            // 1. Hierarchy 选中的 GameObjects
            var selectedGameObjects = Selection.gameObjects.Select(go =>
            {
                var info = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["name"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["path"] = GetGameObjectPath(go),
                    ["tag"] = go.tag,
                    ["layer"] = LayerMask.LayerToName(go.layer),
                    ["isActive"] = go.activeSelf
                };

                if (includeComponents)
                {
                    info["components"] = go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToArray();
                }

                if (includeChildren && go.transform.childCount > 0)
                {
                    var children = new System.Collections.Generic.List<object>();
                    foreach (Transform child in go.transform)
                    {
                        children.Add(new { name = child.name, instanceId = child.gameObject.GetInstanceID() });
                    }
                    info["children"] = children;
                }

                return info;
            }).ToArray();

            // 2. Project 窗口选中的资源 (通过 GUID)
            var selectedAssets = Selection.assetGUIDs.Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                return new
                {
                    guid,
                    path,
                    type = assetType?.Name ?? "Unknown",
                    isFolder = AssetDatabase.IsValidFolder(path)
                };
            }).ToArray();

            // 3. 当前活动场景
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            // 4. 焦点窗口
            var focusedWindow = EditorWindow.focusedWindow;

            return new
            {
                success = true,
                selectedGameObjects = new
                {
                    count = selectedGameObjects.Length,
                    objects = selectedGameObjects
                },
                selectedAssets = new
                {
                    count = selectedAssets.Length,
                    assets = selectedAssets
                },
                activeScene = new
                {
                    name = activeScene.name,
                    path = activeScene.path,
                    isDirty = activeScene.isDirty
                },
                focusedWindow = focusedWindow?.GetType().Name ?? "None",
                isPlaying = EditorApplication.isPlaying,
                isCompiling = EditorApplication.isCompiling
            };
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
