using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    public static class WorkflowManager
    {
        private static WorkflowHistoryData _history;
        private static WorkflowTask _currentTask;
        
        // Path to store the history file (Library folder persists but is local)
        private static string HistoryFilePath => Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_history.json");

        public static WorkflowHistoryData History
        {
            get
            {
                if (_history == null)
                    LoadHistory();
                return _history;
            }
        }

        public static WorkflowTask CurrentTask => _currentTask;
        public static bool IsRecording => _currentTask != null;

        public static void LoadHistory()
        {
            if (File.Exists(HistoryFilePath))
            {
                try
                {
                    string json = File.ReadAllText(HistoryFilePath);
                    _history = JsonUtility.FromJson<WorkflowHistoryData>(json);
                    
                    // Cleanup any null tasks if they somehow got in
                    _history.tasks.RemoveAll(t => t == null);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[UnitySkills] Failed to load workflow history: {e.Message}");
                    _history = new WorkflowHistoryData();
                }
            }
            else
            {
                _history = new WorkflowHistoryData();
            }
        }

        public static void SaveHistory()
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonUtility.ToJson(_history, true);
                File.WriteAllText(HistoryFilePath, json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnitySkills] Failed to save workflow history: {e.Message}");
            }
        }

        public static WorkflowTask BeginTask(string tag, string description)
        {
            if (_currentTask != null)
                EndTask(); // Auto-close previous task if open

            _currentTask = new WorkflowTask
            {
                id = Guid.NewGuid().ToString(),
                tag = tag,
                description = description,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                snapshots = new List<ObjectSnapshot>()
            };

            // Hook into Undo system to automatically track changes during the task
            Undo.postprocessModifications += OnUndoPostprocess;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;

            return _currentTask;
        }

        public static void EndTask()
        {
            if (_currentTask == null) return;

            // Unhook hooks
            Undo.postprocessModifications -= OnUndoPostprocess;
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;

            // Only add to history if there are snapshots or it was a meaningful task
            if (_history == null) LoadHistory();
            
            _history.tasks.Add(_currentTask);
            _currentTask = null;
            
            SaveHistory();
        }

        private static UndoPropertyModification[] OnUndoPostprocess(UndoPropertyModification[] modifications)
        {
            if (_currentTask == null) return modifications;

            foreach (var mod in modifications)
            {
                if (mod.currentValue != null && mod.currentValue.target != null)
                {
                    // If we detect a created object in the undo system, track it
                    // Note: This is an approximation. Truly new objects are often handled via RegisterCreatedObjectUndo.
                    SnapshotObject(mod.currentValue.target);
                }
            }
            return modifications;
        }

        private static void OnUndoRedoPerformed()
        {
            // Sync current task if needed, though usually we care about the "Pre-state"
        }

        /// <summary>
        /// Captures the state of an object/component BEFORE modification.
        /// Supports both scene objects and project assets (Materials, etc.).
        /// </summary>
        public static void SnapshotObject(UnityEngine.Object obj, SnapshotType type = SnapshotType.Modified)
        {
            if (_currentTask == null || obj == null) return;

            // Get GlobalObjectId for persistence
            string gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();

            // Check if already snapshotted in this task
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            string json = "";
            string assetPath = "";
            
            try 
            { 
                json = EditorJsonUtility.ToJson(obj);
                // For assets, also store the asset path for better restoration
                assetPath = AssetDatabase.GetAssetPath(obj);
            } 
            catch { }
            
            _currentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = json,
                objectName = obj.name,
                typeName = obj.GetType().Name,
                type = type,
                assetPath = assetPath
            });
            
            // Incremental save for robustness (in case of crash)
            if (_currentTask.snapshots.Count % 10 == 0)
            {
                SaveHistory();
            }
        }


        /// <summary>
        /// Reverts a specific task.
        /// Handle deletion of objects that were marked as 'Created' during the task.
        /// </summary>
        public static bool RevertTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Revert Task: {task.tag}");
            int undoGroup = Undo.GetCurrentGroup();

            // Handle snapshots in reverse order (LIFO)
            var snapshots = new List<ObjectSnapshot>(task.snapshots);
            snapshots.Reverse();

            foreach (var snapshot in snapshots)
            {
                if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                    continue;

                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                
                if (obj == null) continue;

                if (snapshot.type == SnapshotType.Created)
                {
                    // This was a NEW object created by AI, so we delete it to revert
                    if (obj is GameObject go) Undo.DestroyObjectImmediate(go);
                    else if (obj is Component comp) Undo.DestroyObjectImmediate(comp);
                    else Undo.DestroyObjectImmediate(obj);
                }
                else
                {
                    // This was an existing object that was modified
                    Undo.RecordObject(obj, "Revert Workflow Modification");
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                    EditorUtility.SetDirty(obj);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            
            // Also update history to mark it as reverted (optional UX improvement)
            SaveHistory(); 
            return true;
        }

        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            _history.tasks.RemoveAll(t => t.id == taskId);
            SaveHistory();
        }

        public static void ClearHistory()
        {
            _history = new WorkflowHistoryData();
            SaveHistory();
        }
    }
}
