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
        private static string _currentSessionId;

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
        public static string CurrentSessionId => _currentSessionId;
        public static bool HasActiveSession => !string.IsNullOrEmpty(_currentSessionId);

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
        /// Records a newly created component for undo tracking.
        /// Stores additional info (parent GameObject, component type) for reliable deletion.
        /// </summary>
        public static void SnapshotCreatedComponent(Component comp)
        {
            if (_currentTask == null || comp == null) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString();
            string parentGid = GlobalObjectId.GetGlobalObjectIdSlow(comp.gameObject).ToString();

            // Check if already snapshotted in this task
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            _currentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = "",  // New objects don't need original state
                objectName = comp.name,
                typeName = comp.GetType().Name,
                type = SnapshotType.Created,
                componentTypeName = comp.GetType().FullName,
                parentGameObjectId = parentGid
            });
        }


        /// <summary>
        /// Undoes a specific task.
        /// Handle deletion of objects that were marked as 'Created' during the task.
        /// </summary>
        public static bool UndoTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Undo Task: {task.tag}");
            int undoGroup = Undo.GetCurrentGroup();

            // Handle snapshots in reverse order (LIFO)
            var snapshots = new List<ObjectSnapshot>(task.snapshots);
            snapshots.Reverse();

            foreach (var snapshot in snapshots)
            {
                if (snapshot.type == SnapshotType.Created)
                {
                    // For components: use parentGameObjectId and componentTypeName for reliable deletion
                    if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                        !string.IsNullOrEmpty(snapshot.parentGameObjectId))
                    {
                        if (GlobalObjectId.TryParse(snapshot.parentGameObjectId, out GlobalObjectId parentGid))
                        {
                            var parentObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parentGid);
                            if (parentObj is GameObject go)
                            {
                                var compType = Type.GetType(snapshot.componentTypeName) ??
                                               ComponentSkills.FindComponentType(snapshot.componentTypeName);
                                if (compType != null)
                                {
                                    var comp = go.GetComponent(compType);
                                    if (comp != null)
                                    {
                                        Undo.DestroyObjectImmediate(comp);
                                        continue;
                                    }
                                }
                            }
                        }
                    }

                    // Fallback: try to find by GlobalObjectId directly
                    if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                        continue;

                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                    if (obj == null) continue;

                    // This was a NEW object created by AI, so we delete it to undo
                    if (obj is GameObject go2) Undo.DestroyObjectImmediate(go2);
                    else if (obj is Component comp2) Undo.DestroyObjectImmediate(comp2);
                    else Undo.DestroyObjectImmediate(obj);
                }
                else
                {
                    // This was an existing object that was modified
                    if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                        continue;

                    var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                    if (obj == null) continue;

                    Undo.RecordObject(obj, "Undo Workflow Modification");
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                    EditorUtility.SetDirty(obj);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Also update history to mark it as undone (optional UX improvement)
            SaveHistory();
            return true;
        }

        /// <summary>
        /// Alias for UndoTask (backward compatibility).
        /// </summary>
        public static bool RevertTask(string taskId)
        {
            return UndoTask(taskId);
        }

        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            _history.tasks.RemoveAll(t => t.id == taskId);
            SaveHistory();
        }

        #region Session Management (Conversation-Level Undo)

        /// <summary>
        /// Starts a new session (conversation-level). All tasks created during this session
        /// will be grouped together and can be undone as a whole.
        /// </summary>
        public static string BeginSession(string sessionTag = null)
        {
            // End any existing session first
            if (HasActiveSession)
            {
                EndSession();
            }

            _currentSessionId = Guid.NewGuid().ToString();

            // Auto-start a task for this session
            BeginTask(sessionTag ?? "Session", $"Session started at {DateTime.Now:HH:mm:ss}");
            _currentTask.sessionId = _currentSessionId;

            Debug.Log($"[UnitySkills] Session started: {_currentSessionId}");
            return _currentSessionId;
        }

        /// <summary>
        /// Ends the current session and saves all recorded changes.
        /// </summary>
        public static void EndSession()
        {
            if (!HasActiveSession) return;

            // End current task if any
            if (_currentTask != null)
            {
                _currentTask.sessionId = _currentSessionId;
                EndTask();
            }

            Debug.Log($"[UnitySkills] Session ended: {_currentSessionId}");
            _currentSessionId = null;
        }

        /// <summary>
        /// Undoes all changes made during a specific session.
        /// </summary>
        public static bool UndoSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            // Find all tasks belonging to this session
            var sessionTasks = History.tasks
                .Where(t => t.sessionId == sessionId)
                .OrderByDescending(t => t.timestamp)
                .ToList();

            if (sessionTasks.Count == 0)
            {
                Debug.LogWarning($"[UnitySkills] No tasks found for session: {sessionId}");
                return false;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Undo Session");
            int undoGroup = Undo.GetCurrentGroup();

            // Collect all snapshots from all tasks in reverse order
            var allSnapshots = new List<ObjectSnapshot>();
            foreach (var task in sessionTasks)
            {
                allSnapshots.AddRange(task.snapshots);
            }

            // Remove duplicates (keep first occurrence which has original state)
            var uniqueSnapshots = new List<ObjectSnapshot>();
            var seenIds = new HashSet<string>();
            foreach (var snapshot in allSnapshots)
            {
                if (!seenIds.Contains(snapshot.globalObjectId))
                {
                    seenIds.Add(snapshot.globalObjectId);
                    uniqueSnapshots.Add(snapshot);
                }
            }

            // Process in reverse order (LIFO)
            uniqueSnapshots.Reverse();

            int undoneCount = 0;
            foreach (var snapshot in uniqueSnapshots)
            {
                if (UndoSnapshot(snapshot))
                    undoneCount++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Remove session tasks from history
            _history.tasks.RemoveAll(t => t.sessionId == sessionId);
            SaveHistory();

            Debug.Log($"[UnitySkills] Session undone: {sessionId}, {undoneCount} changes reverted");
            return true;
        }

        /// <summary>
        /// Gets all sessions from history.
        /// </summary>
        public static List<SessionInfo> GetSessions()
        {
            var sessions = History.tasks
                .Where(t => !string.IsNullOrEmpty(t.sessionId))
                .GroupBy(t => t.sessionId)
                .Select(g => new SessionInfo
                {
                    sessionId = g.Key,
                    taskCount = g.Count(),
                    totalChanges = g.Sum(t => t.snapshots.Count),
                    startTime = DateTimeOffset.FromUnixTimeSeconds(g.Min(t => t.timestamp)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    endTime = DateTimeOffset.FromUnixTimeSeconds(g.Max(t => t.timestamp)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    tags = g.Select(t => t.tag).Distinct().ToList()
                })
                .OrderByDescending(s => s.startTime)
                .ToList();

            return sessions;
        }

        #endregion

        #region Internal Undo Helpers

        /// <summary>
        /// Undoes a single snapshot. Returns true if successful.
        /// </summary>
        private static bool UndoSnapshot(ObjectSnapshot snapshot)
        {
            try
            {
                if (snapshot.type == SnapshotType.Created)
                {
                    return UndoCreatedObject(snapshot);
                }
                else
                {
                    return UndoModifiedObject(snapshot);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Failed to undo snapshot {snapshot.objectName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes an object that was created during the task/session.
        /// </summary>
        private static bool UndoCreatedObject(ObjectSnapshot snapshot)
        {
            // For components: use parentGameObjectId and componentTypeName for reliable deletion
            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                if (GlobalObjectId.TryParse(snapshot.parentGameObjectId, out GlobalObjectId parentGid))
                {
                    var parentObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parentGid);
                    if (parentObj is GameObject go)
                    {
                        var compType = Type.GetType(snapshot.componentTypeName) ??
                                       ComponentSkills.FindComponentType(snapshot.componentTypeName);
                        if (compType != null)
                        {
                            var comp = go.GetComponent(compType);
                            if (comp != null)
                            {
                                Undo.DestroyObjectImmediate(comp);
                                return true;
                            }
                        }
                    }
                }
            }

            // Fallback: try to find by GlobalObjectId directly
            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                return false;

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (obj == null) return false;

            // Delete the created object
            if (obj is GameObject go2)
                Undo.DestroyObjectImmediate(go2);
            else if (obj is Component comp2)
                Undo.DestroyObjectImmediate(comp2);
            else
                Undo.DestroyObjectImmediate(obj);

            return true;
        }

        /// <summary>
        /// Restores an object to its original state before modification.
        /// </summary>
        private static bool UndoModifiedObject(ObjectSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.originalJson))
                return false;

            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                return false;

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (obj == null) return false;

            Undo.RecordObject(obj, "Undo Workflow Modification");
            EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
            EditorUtility.SetDirty(obj);
            return true;
        }

        #endregion

        public static void ClearHistory()
        {
            _history = new WorkflowHistoryData();
            SaveHistory();
        }
    }
}
