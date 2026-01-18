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
    }
}
