using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Workflow Skills - Bookmarks, history, undo management.
    /// Designed to help AI agents navigate and manage work sessions.
    /// </summary>
    public static class WorkflowSkills
    {
        // In-memory bookmark storage (persists until domain reload)
        private static Dictionary<string, BookmarkData> _bookmarks = new Dictionary<string, BookmarkData>();

        private class BookmarkData
        {
            public int[] selectedInstanceIds;
            public Vector3? sceneViewPosition;
            public Quaternion? sceneViewRotation;
            public float? sceneViewSize;
            public string note;
            public System.DateTime createdAt;
        }

        [UnitySkill("bookmark_set", "Save current selection and scene view position as a bookmark")]
        public static object BookmarkSet(string bookmarkName, string note = null)
        {
            if (string.IsNullOrEmpty(bookmarkName))
                return new { success = false, error = "bookmarkName is required" };

            var bookmark = new BookmarkData
            {
                selectedInstanceIds = Selection.instanceIDs,
                note = note,
                createdAt = System.DateTime.Now
            };

            // Try to capture Scene View camera position
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                bookmark.sceneViewPosition = sceneView.pivot;
                bookmark.sceneViewRotation = sceneView.rotation;
                bookmark.sceneViewSize = sceneView.size;
            }

            _bookmarks[bookmarkName] = bookmark;

            return new
            {
                success = true,
                bookmark = bookmarkName,
                selectedCount = bookmark.selectedInstanceIds.Length,
                hasSceneView = sceneView != null,
                note
            };
        }

        [UnitySkill("bookmark_goto", "Restore selection and scene view from a bookmark")]
        public static object BookmarkGoto(string bookmarkName)
        {
            if (!_bookmarks.TryGetValue(bookmarkName, out var bookmark))
                return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };

            // Restore selection
            var validIds = bookmark.selectedInstanceIds
                .Where(id => EditorUtility.InstanceIDToObject(id) != null)
                .ToArray();
            Selection.instanceIDs = validIds;

            // Restore scene view
            if (bookmark.sceneViewPosition.HasValue)
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null)
                {
                    sceneView.pivot = bookmark.sceneViewPosition.Value;
                    if (bookmark.sceneViewRotation.HasValue)
                        sceneView.rotation = bookmark.sceneViewRotation.Value;
                    if (bookmark.sceneViewSize.HasValue)
                        sceneView.size = bookmark.sceneViewSize.Value;
                    sceneView.Repaint();
                }
            }

            return new
            {
                success = true,
                bookmark = bookmarkName,
                restoredSelection = validIds.Length,
                note = bookmark.note
            };
        }

        [UnitySkill("bookmark_list", "List all saved bookmarks")]
        public static object BookmarkList()
        {
            var list = _bookmarks.Select(kv => new
            {
                name = kv.Key,
                selectedCount = kv.Value.selectedInstanceIds.Length,
                hasSceneView = kv.Value.sceneViewPosition.HasValue,
                note = kv.Value.note,
                createdAt = kv.Value.createdAt.ToString("HH:mm:ss")
            }).ToList();

            return new { success = true, count = list.Count, bookmarks = list };
        }

        [UnitySkill("bookmark_delete", "Delete a bookmark")]
        public static object BookmarkDelete(string bookmarkName)
        {
            if (_bookmarks.Remove(bookmarkName))
                return new { success = true, deleted = bookmarkName };
            return new { success = false, error = $"Bookmark '{bookmarkName}' not found" };
        }

        [UnitySkill("history_undo", "Undo the last operation (or multiple steps)")]
        public static object HistoryUndo(int steps = 1)
        {
            for (int i = 0; i < steps; i++)
            {
                Undo.PerformUndo();
            }
            return new { success = true, undoneSteps = steps };
        }

        [UnitySkill("history_redo", "Redo the last undone operation (or multiple steps)")]
        public static object HistoryRedo(int steps = 1)
        {
            for (int i = 0; i < steps; i++)
            {
                Undo.PerformRedo();
            }
            return new { success = true, redoneSteps = steps };
        }

        [UnitySkill("history_get_current", "Get the name of the current undo group")]
        public static object HistoryGetCurrent()
        {
            return new
            {
                success = true,
                currentGroup = Undo.GetCurrentGroupName(),
                groupIndex = Undo.GetCurrentGroup()
            };
        }
    }
}
