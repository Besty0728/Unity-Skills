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

            return _currentTask;
        }

        public static void EndTask()
        {
            if (_currentTask == null) return;

            // Only add to history if there are snapshots or it was a meaningful task
            if (_history == null) LoadHistory();
            
            _history.tasks.Add(_currentTask);
            _currentTask = null;
            
            SaveHistory();
        }

        /// <summary>
        /// Captures the state of an object/component BEFORE modification.
        /// </summary>
        public static void SnapshotObject(UnityEngine.Object obj)
        {
            if (_currentTask == null || obj == null) return;

            // Get GlobalObjectId for persistence
            string gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();

            // Check if already snapshotted in this task (we only want the INITIAL state before this task's changes)
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            string json = EditorJsonUtility.ToJson(obj);
            
            _currentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = json,
                objectName = obj.name,
                typeName = obj.GetType().Name
            });
        }

        /// <summary>
        /// Reverts a specific task (and ideally all subsequent ones if we enforce linear history, 
        /// but here we implement a simple "restore state" which might be destructive).
        /// Returns true if successful.
        /// </summary>
        public static bool RevertTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null) return false;

            // Undo snapshots in reverse order (though order doesn't strictly matter for non-overlapping props)
            // Ideally we should start a new undo group for this revert operation
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Revert Task: {task.tag}");
            int undoGroup = Undo.GetCurrentGroup();

            foreach (var snapshot in task.snapshots)
            {
                if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                    continue;

                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                
                // If object is null (maybe deleted?), we can't easily restore it purely from JSON 
                // unless we re-instantiate, which is complex. 
                // For now, we only support restoring modified objects that still exist.
                // Improvement: Support re-creation if we had a prefab reference or if it's a simple GO.
                if (obj == null) 
                {
                    Debug.LogWarning($"[UnitySkills] Could not find object {snapshot.objectName} ({snapshot.globalObjectId}) to revert.");
                    continue;
                }

                Undo.RecordObject(obj, "Revert Workflow");
                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                
                // If it's a transform or similar, we might need to verify dirty state
                EditorUtility.SetDirty(obj);
            }

            Undo.CollapseUndoOperations(undoGroup);
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
