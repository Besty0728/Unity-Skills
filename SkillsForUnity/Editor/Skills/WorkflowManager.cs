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

                    // JsonUtility.FromJson 可能返回 null
                    if (_history == null)
                    {
                        _history = new WorkflowHistoryData();
                        return;
                    }

                    // 确保列表不为 null（JsonUtility 可能不会初始化新字段）
                    if (_history.tasks == null) _history.tasks = new List<WorkflowTask>();
                    if (_history.undoneStack == null) _history.undoneStack = new List<WorkflowTask>();

                    // Cleanup any null tasks if they somehow got in
                    _history.tasks.RemoveAll(t => t == null);
                    _history.undoneStack.RemoveAll(t => t == null);
                }
                catch (Exception e)
                {
                    Debug.LogError($"{SkillsLogger.PREFIX_ERROR} Failed to load workflow history: {e.Message}");
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
                string tmpPath = HistoryFilePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                if (File.Exists(HistoryFilePath))
                    File.Delete(HistoryFilePath);
                File.Move(tmpPath, HistoryFilePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"{SkillsLogger.PREFIX_ERROR} Failed to save workflow history: {e.Message}");
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

            // Limit snapshots per task to prevent unbounded memory growth
            const int MaxSnapshotsPerTask = 500;
            if (_currentTask.snapshots.Count >= MaxSnapshotsPerTask)
            {
                SkillsLogger.LogVerbose($"Snapshot limit reached ({MaxSnapshotsPerTask}), skipping: {obj.name}");
                return;
            }

            // Get GlobalObjectId for persistence
            string gid = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();

            // Check if already snapshotted in this task
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            string json = "";
            string assetPath = "";
            string assetBytesBase64 = "";

            try
            {
                json = EditorJsonUtility.ToJson(obj);
                // For assets, also store the asset path for better restoration
                assetPath = AssetDatabase.GetAssetPath(obj);

                // Backup asset file bytes (exclude .cs scripts)
                if (!string.IsNullOrEmpty(assetPath) && !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = Path.Combine(Application.dataPath, "..", assetPath);
                    if (File.Exists(fullPath))
                    {
                        assetBytesBase64 = Convert.ToBase64String(File.ReadAllBytes(fullPath));
                    }
                }
            }
            catch (Exception ex) { SkillsLogger.LogVerbose($"Snapshot serialization failed for {obj.name}: {ex.Message}"); }

            _currentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = json,
                objectName = obj.name,
                typeName = obj.GetType().Name,
                type = type,
                assetPath = assetPath,
                assetBytesBase64 = assetBytesBase64
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
        /// Records a newly created asset (Material, Prefab, ScriptableObject, etc.) for undo tracking.
        /// </summary>
        public static void SnapshotCreatedAsset(UnityEngine.Object asset)
        {
            if (_currentTask == null || asset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString();

            // Check if already snapshotted in this task
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            string assetBytesBase64 = "";
            // Backup asset file bytes (exclude .cs scripts)
            if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                string fullPath = Path.Combine(Application.dataPath, "..", assetPath);
                if (File.Exists(fullPath))
                {
                    assetBytesBase64 = Convert.ToBase64String(File.ReadAllBytes(fullPath));
                }
            }

            _currentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = EditorJsonUtility.ToJson(asset),
                objectName = asset.name,
                typeName = asset.GetType().Name,
                type = SnapshotType.Created,
                assetPath = assetPath,
                assetBytesBase64 = assetBytesBase64
            });
        }

        /// <summary>
        /// Records a newly created GameObject for undo/redo tracking.
        /// Stores primitiveType for recreation during redo.
        /// </summary>
        public static void SnapshotCreatedGameObject(GameObject go, string primitiveType = null)
        {
            if (_currentTask == null || go == null) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();

            // Check if already snapshotted in this task
            if (_currentTask.snapshots.Any(s => s.globalObjectId == gid))
                return;

            var t = go.transform;
            var snapshot = new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = EditorJsonUtility.ToJson(go),
                objectName = go.name,
                typeName = "GameObject",
                type = SnapshotType.Created,
                primitiveType = primitiveType ?? "",
                posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                scaleX = t.localScale.x, scaleY = t.localScale.y, scaleZ = t.localScale.z,
                components = new List<ComponentData>()
            };

            // Save all components data
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                try
                {
                    snapshot.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp)
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            _currentTask.snapshots.Add(snapshot);
        }


        /// <summary>
        /// Undoes a specific task.
        /// Handle deletion of objects that were marked as 'Created' during the task.
        /// Saves the task to undoneStack for potential redo.
        /// </summary>
        public static bool UndoTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null) return false;

            // Capture current state before undo (for redo)
            var redoTask = new WorkflowTask
            {
                id = task.id,
                tag = task.tag,
                description = task.description,
                timestamp = task.timestamp,
                sessionId = task.sessionId,
                snapshots = new List<ObjectSnapshot>()
            };

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
                    // Capture current state for redo (the created object exists now)
                    if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId createdGid))
                        continue;

                    var createdObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(createdGid);
                    if (createdObj != null)
                    {
                        // Save current state for redo (including transform for GameObjects)
                        var redoSnapshot = new ObjectSnapshot
                        {
                            globalObjectId = snapshot.globalObjectId,
                            originalJson = EditorJsonUtility.ToJson(createdObj),
                            objectName = snapshot.objectName,
                            typeName = snapshot.typeName,
                            type = SnapshotType.Created,
                            componentTypeName = snapshot.componentTypeName,
                            parentGameObjectId = snapshot.parentGameObjectId,
                            assetPath = snapshot.assetPath,
                            primitiveType = snapshot.primitiveType,
                            components = new List<ComponentData>()
                        };

                        // Save transform and components for GameObjects
                        if (createdObj is GameObject go)
                        {
                            redoSnapshot = CaptureGameObjectState(go, redoSnapshot);
                        }

                        redoTask.snapshots.Add(redoSnapshot);
                    }

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
                    var obj = createdObj;
                    if (obj == null) continue;

                    // Handle created assets - delete the asset file
                    if (!string.IsNullOrEmpty(snapshot.assetPath))
                    {
                        // Save asset bytes for redo before deleting
                        string fullPath = Path.Combine(Application.dataPath, "..", snapshot.assetPath);
                        if (File.Exists(fullPath) && !snapshot.assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            var lastSnapshot = redoTask.snapshots.LastOrDefault();
                            if (lastSnapshot != null && string.IsNullOrEmpty(lastSnapshot.assetBytesBase64))
                            {
                                lastSnapshot.assetBytesBase64 = Convert.ToBase64String(File.ReadAllBytes(fullPath));
                            }
                        }
                        AssetDatabase.DeleteAsset(snapshot.assetPath);
                        continue;
                    }

                    // This was a NEW object created by AI, so we delete it to undo
                    if (obj is GameObject go2) Undo.DestroyObjectImmediate(go2);
                    else if (obj is Component comp2) Undo.DestroyObjectImmediate(comp2);
                    else Undo.DestroyObjectImmediate(obj);
                }
                else
                {
                    // This was an existing object that was modified
                    RestoreModifiedSnapshot(snapshot, redoTask, "Undo Workflow Modification");
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Move task from history to undone stack
            _history.tasks.Remove(task);
            _history.undoneStack.Add(redoTask);
            SaveHistory();
            return true;
        }

        /// <summary>
        /// Redoes a previously undone task.
        /// </summary>
        public static bool RedoTask(string taskId)
        {
            var task = History.undoneStack.FirstOrDefault(t => t.id == taskId);
            if (task == null) return false;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName($"Redo Task: {task.tag}");
            int undoGroup = Undo.GetCurrentGroup();

            // Create a new task to store original state (for future undo)
            var newTask = new WorkflowTask
            {
                id = task.id,
                tag = task.tag,
                description = task.description,
                timestamp = task.timestamp,
                sessionId = task.sessionId,
                snapshots = new List<ObjectSnapshot>()
            };

            // Process snapshots to restore the state
            foreach (var snapshot in task.snapshots)
            {
                if (snapshot.type == SnapshotType.Created)
                {
                    // Re-create the object
                    if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                        !string.IsNullOrEmpty(snapshot.parentGameObjectId))
                    {
                        // Re-add component
                        if (GlobalObjectId.TryParse(snapshot.parentGameObjectId, out GlobalObjectId parentGid))
                        {
                            var parentObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parentGid);
                            if (parentObj is GameObject go)
                            {
                                var compType = Type.GetType(snapshot.componentTypeName) ??
                                               ComponentSkills.FindComponentType(snapshot.componentTypeName);
                                if (compType != null)
                                {
                                    var comp = Undo.AddComponent(go, compType);
                                    if (comp != null && !string.IsNullOrEmpty(snapshot.originalJson))
                                    {
                                        EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, comp);
                                    }

                                    // Record for future undo
                                    newTask.snapshots.Add(new ObjectSnapshot
                                    {
                                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                                        originalJson = "",
                                        objectName = snapshot.objectName,
                                        typeName = snapshot.typeName,
                                        type = SnapshotType.Created,
                                        componentTypeName = snapshot.componentTypeName,
                                        parentGameObjectId = snapshot.parentGameObjectId
                                    });
                                }
                            }
                        }
                    }
                    else if (snapshot.typeName == "GameObject")
                    {
                        // Re-create GameObject using stored primitiveType, transform, and components
                        var newGo = RecreateGameObject(snapshot);

                        // Record for future undo
                        var newSnapshot = CaptureGameObjectState(newGo, new ObjectSnapshot
                        {
                            globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(newGo).ToString(),
                            originalJson = EditorJsonUtility.ToJson(newGo),
                            objectName = newGo.name,
                            typeName = "GameObject",
                            type = SnapshotType.Created,
                            primitiveType = snapshot.primitiveType
                        });

                        newTask.snapshots.Add(newSnapshot);
                    }
                    else if (!string.IsNullOrEmpty(snapshot.assetPath) && !string.IsNullOrEmpty(snapshot.assetBytesBase64))
                    {
                        // Re-create asset from backup bytes
                        string fullPath = Path.Combine(Application.dataPath, "..", snapshot.assetPath);
                        string dir = Path.GetDirectoryName(fullPath);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                        File.WriteAllBytes(fullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                        AssetDatabase.ImportAsset(snapshot.assetPath);

                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(snapshot.assetPath);
                        if (asset != null)
                        {
                            newTask.snapshots.Add(new ObjectSnapshot
                            {
                                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString(),
                                originalJson = EditorJsonUtility.ToJson(asset),
                                objectName = asset.name,
                                typeName = snapshot.typeName,
                                type = SnapshotType.Created,
                                assetPath = snapshot.assetPath,
                                assetBytesBase64 = snapshot.assetBytesBase64
                            });
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"{SkillsLogger.PREFIX_WARNING} Cannot recreate object: {snapshot.objectName} (type: {snapshot.typeName})");
                    }
                }
                else
                {
                    // Restore modified object to its post-modification state
                    RestoreModifiedSnapshot(snapshot, newTask, "Redo Workflow Modification");
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            // Move task from undone stack back to history
            _history.undoneStack.Remove(task);
            _history.tasks.Add(newTask);
            SaveHistory();
            return true;
        }

        /// <summary>
        /// Gets the list of undone tasks that can be redone.
        /// </summary>
        public static List<WorkflowTask> GetUndoneStack()
        {
            return History.undoneStack;
        }

        /// <summary>
        /// Clears the undo stack (called when new changes are made after undo).
        /// </summary>
        public static void ClearUndoneStack()
        {
            if (_history != null)
            {
                _history.undoneStack.Clear();
                SaveHistory();
            }
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

            Debug.Log($"{SkillsLogger.PREFIX_WORKFLOW} Session started: <b>{_currentSessionId}</b>");
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

            Debug.Log($"{SkillsLogger.PREFIX_WORKFLOW} Session ended: <b>{_currentSessionId}</b>");
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
                Debug.LogWarning($"{SkillsLogger.PREFIX_WARNING} No tasks found for session: {sessionId}");
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

            Debug.Log($"{SkillsLogger.PREFIX_SUCCESS} Session undone: <b>{sessionId}</b>, {undoneCount} changes reverted");
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
                Debug.LogWarning($"{SkillsLogger.PREFIX_WARNING} Failed to undo snapshot {snapshot.objectName}: {ex.Message}");
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

        /// <summary>
        /// Captures transform and component data from a live GameObject into a new ObjectSnapshot,
        /// copying base fields from the provided baseSnapshot.
        /// </summary>
        private static ObjectSnapshot CaptureGameObjectState(GameObject go, ObjectSnapshot baseSnapshot)
        {
            var t = go.transform;
            var result = new ObjectSnapshot
            {
                globalObjectId = baseSnapshot.globalObjectId,
                originalJson = baseSnapshot.originalJson,
                objectName = baseSnapshot.objectName,
                typeName = baseSnapshot.typeName,
                type = baseSnapshot.type,
                componentTypeName = baseSnapshot.componentTypeName,
                parentGameObjectId = baseSnapshot.parentGameObjectId,
                assetPath = baseSnapshot.assetPath,
                assetBytesBase64 = baseSnapshot.assetBytesBase64,
                primitiveType = baseSnapshot.primitiveType,
                posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                scaleX = t.localScale.x, scaleY = t.localScale.y, scaleZ = t.localScale.z,
                components = new List<ComponentData>()
            };

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                try
                {
                    result.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp)
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            return result;
        }

        /// <summary>
        /// Captures the current state of a modified object into targetTask, then restores
        /// the snapshot data (via asset-bytes file write or JSON overlay).
        /// </summary>
        private static bool RestoreModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask targetTask, string undoLabel)
        {
            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                return false;

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (obj == null) return false;

            // Capture current state for the target task (including asset bytes)
            string currentAssetBytes = "";
            if (!string.IsNullOrEmpty(snapshot.assetPath) && !snapshot.assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                string fullPath = Path.Combine(Application.dataPath, "..", snapshot.assetPath);
                if (File.Exists(fullPath))
                {
                    currentAssetBytes = Convert.ToBase64String(File.ReadAllBytes(fullPath));
                }
            }

            targetTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                originalJson = EditorJsonUtility.ToJson(obj),
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Modified,
                assetPath = snapshot.assetPath,
                assetBytesBase64 = currentAssetBytes
            });

            // Restore from asset bytes backup if available
            if (!string.IsNullOrEmpty(snapshot.assetBytesBase64) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                string fullPath = Path.Combine(Application.dataPath, "..", snapshot.assetPath);
                File.WriteAllBytes(fullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                AssetDatabase.ImportAsset(snapshot.assetPath);
            }
            else
            {
                Undo.RecordObject(obj, undoLabel);
                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                EditorUtility.SetDirty(obj);
            }

            return true;
        }

        /// <summary>
        /// Recreates a GameObject from snapshot data (primitiveType, transform, components).
        /// Registers the new object with Unity's Undo system.
        /// </summary>
        private static GameObject RecreateGameObject(ObjectSnapshot snapshot)
        {
            GameObject newGo;

            if (!string.IsNullOrEmpty(snapshot.primitiveType) &&
                Enum.TryParse<PrimitiveType>(snapshot.primitiveType, out var pt))
            {
                newGo = GameObject.CreatePrimitive(pt);
            }
            else
            {
                newGo = new GameObject();
            }

            newGo.name = snapshot.objectName;

            // Restore transform from stored data
            newGo.transform.position = new Vector3(snapshot.posX, snapshot.posY, snapshot.posZ);
            newGo.transform.rotation = new Quaternion(snapshot.rotX, snapshot.rotY, snapshot.rotZ, snapshot.rotW);
            newGo.transform.localScale = new Vector3(snapshot.scaleX, snapshot.scaleY, snapshot.scaleZ);

            // Restore all components
            if (snapshot.components != null)
            {
                foreach (var compData in snapshot.components)
                {
                    if (string.IsNullOrEmpty(compData.typeName)) continue;
                    var compType = Type.GetType(compData.typeName);
                    if (compType == null) compType = ComponentSkills.FindComponentType(compData.typeName);
                    if (compType == null) continue;

                    // Skip if component already exists (e.g., MeshRenderer on primitives)
                    var existing = newGo.GetComponent(compType);
                    if (existing != null)
                    {
                        if (!string.IsNullOrEmpty(compData.json))
                            EditorJsonUtility.FromJsonOverwrite(compData.json, existing);
                    }
                    else
                    {
                        var comp = newGo.AddComponent(compType);
                        if (comp != null && !string.IsNullOrEmpty(compData.json))
                            EditorJsonUtility.FromJsonOverwrite(compData.json, comp);
                    }
                }
            }

            Undo.RegisterCreatedObjectUndo(newGo, "Redo Create " + snapshot.objectName);

            return newGo;
        }

        #endregion

        public static void ClearHistory()
        {
            _history = new WorkflowHistoryData();
            SaveHistory();
        }
    }
}
