using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

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
            // Crash recovery: if main file is missing but .tmp exists, promote it
            string tmpPath = HistoryFilePath + ".tmp";
            if (!File.Exists(HistoryFilePath) && File.Exists(tmpPath))
            {
                try { File.Move(tmpPath, HistoryFilePath); }
                catch { /* If promotion fails, start fresh below */ }
            }

            if (File.Exists(HistoryFilePath))
            {
                try
                {
                    string json = File.ReadAllText(HistoryFilePath, System.Text.Encoding.UTF8);
                    _history = JsonUtility.FromJson<WorkflowHistoryData>(json);

                    if (_history == null)
                    {
                        _history = new WorkflowHistoryData();
                        _history.EnsureDefaults();
                        MigrateHistorySchema();
                        return;
                    }

                    _history.EnsureDefaults();
                    MigrateHistorySchema();
                    SanitizeHistory();
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

            _history.EnsureDefaults();
            MigrateHistorySchema();
            TrimHistoryIfNeeded();
        }

        public static void SaveHistory()
        {
            try
            {
                string dir = Path.GetDirectoryName(HistoryFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                _history ??= new WorkflowHistoryData();
                _history.EnsureDefaults();
                _history.schemaVersion = WorkflowHistoryData.CurrentSchemaVersion;

                string json = JsonUtility.ToJson(_history, true);
                string tmpPath = HistoryFilePath + ".tmp";
                File.WriteAllText(tmpPath, json, SkillsCommon.Utf8NoBom);
                if (File.Exists(HistoryFilePath))
                    File.Delete(HistoryFilePath);
                File.Move(tmpPath, HistoryFilePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"{SkillsLogger.PREFIX_ERROR} Failed to save workflow history: {e.Message}");
            }
        }

        private static void SanitizeHistory()
        {
            if (_history == null) return;

            SanitizeTaskCollection(_history.tasks, "tasks");
            SanitizeTaskCollection(_history.undoneStack, "undoneStack");
        }

        private static void SanitizeTaskCollection(List<WorkflowTask> tasks, string source)
        {
            if (tasks == null) return;

            foreach (var task in tasks)
            {
                if (task?.snapshots == null) continue;

                foreach (var snapshot in task.snapshots)
                {
                    if (snapshot == null) continue;

                    if (!string.IsNullOrEmpty(snapshot.assetPath))
                    {
                        if (Validate.SafePath(snapshot.assetPath, "assetPath") is object)
                        {
                            SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe assetPath from {source}: {snapshot.assetPath}");
                            snapshot.assetPath = null;
                            snapshot.fileHash = null;
                            snapshot.previousAssetPath = null;
                            snapshot.assetBytesBase64 = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(snapshot.previousAssetPath) &&
                        Validate.SafePath(snapshot.previousAssetPath, "previousAssetPath") is object)
                    {
                        SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe previousAssetPath from {source}: {snapshot.previousAssetPath}");
                        snapshot.previousAssetPath = null;
                    }
                }
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
            _currentTask.EnsureSnapshotIndex();

            return _currentTask;
        }

        public static void EndTask()
        {
            if (_currentTask == null) return;

            // Only record tasks that captured at least one snapshot. A tracked skill that failed
            // or made no changes has nothing to undo, so recording it would leave an empty
            // (changes=0) entry that clutters the history and confuses undo/redo navigation.
            if (_currentTask.snapshots.Count == 0)
            {
                _currentTask = null;
                return;
            }

            if (_history == null) LoadHistory();

            _history.tasks.Add(_currentTask);
            _currentTask = null;

            TrimHistoryIfNeeded();
            SaveHistory();
        }

        /// <summary>
        /// Registers a snapshot in the current task, deduplicating by globalObjectId.
        /// When upgradeExisting is true, replaces a previously registered snapshot for the same id.
        /// </summary>
        internal static ObjectSnapshot AddSnapshot(ObjectSnapshot snap, bool upgradeExisting = false)
        {
            if (_currentTask == null || snap == null)
                return null;

            if (string.IsNullOrEmpty(snap.globalObjectId))
            {
                _currentTask.snapshots.Add(snap);
                return snap;
            }

            _currentTask.EnsureSnapshotIndex();

            if (_currentTask.HasSnapshotId(snap.globalObjectId))
            {
                if (!upgradeExisting)
                    return null;

                _currentTask.snapshots.RemoveAll(s =>
                    !string.IsNullOrEmpty(s.globalObjectId) &&
                    s.globalObjectId == snap.globalObjectId);
            }

            _currentTask.snapshots.Add(snap);
            _currentTask.TryRegisterSnapshotId(snap.globalObjectId);
            return snap;
        }

        /// <summary>
        /// Captures the state of an object/component BEFORE modification.
        /// Supports both scene objects and project assets (Materials, scripts, etc.).
        /// Asset file backups are stored in the content-addressed WorkflowFileStore.
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

            string json = "";
            string assetPath = "";
            string fileHash = "";

            try
            {
                json = EditorJsonUtility.ToJson(obj);
                assetPath = AssetDatabase.GetAssetPath(obj);

                // Backup asset file bytes in the content-addressed store (all extensions, including .cs)
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath) && File.Exists(fullPath))
                    {
                        fileHash = WorkflowFileStore.StoreFile(assetPath, move: false);
                    }
                }
            }
            catch (Exception ex) { SkillsLogger.LogVerbose($"Snapshot serialization failed for {obj.name}: {ex.Message}"); }

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                originalJson = json,
                objectName = obj.name,
                typeName = obj.GetType().Name,
                type = type,
                assetPath = assetPath,
                fileHash = fileHash
            });
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

            AddSnapshot(new ObjectSnapshot
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
        /// Records a newly created asset (Material, Prefab, ScriptableObject, script, etc.) for undo tracking.
        /// Does not store file contents; undo of a created asset deletes the asset file.
        /// </summary>
        public static void SnapshotCreatedAsset(UnityEngine.Object asset)
        {
            if (_currentTask == null || asset == null) return;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return;

            string gid = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString();

            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = asset.name,
                typeName = asset.GetType().Name,
                type = SnapshotType.Created,
                assetPath = assetPath
            });
        }

        /// <summary>
        /// Records a settings change (e.g. console flags, gravity, quality level) for undo tracking.
        /// The setting must have a getter/setter registered in <see cref="WorkflowSettingRestorerRegistry"/>.
        /// Undo restores <paramref name="oldValueJson"/>; redo re-applies the value captured at undo time.
        /// </summary>
        /// <param name="settingKey">Stable setting key, "module.property" (e.g. "console.pauseOnError").</param>
        /// <param name="oldValueJson">JSON-encoded value BEFORE the change (captured by the caller).</param>
        /// <param name="description">Human-readable label for display.</param>
        public static ObjectSnapshot SnapshotSetting(string settingKey, string oldValueJson, string description)
        {
            if (_currentTask == null || string.IsNullOrEmpty(settingKey))
                return null;

            // Stable pseudo id so repeated changes to the same setting within one task dedupe
            // (only the first snapshot's old value matters for a full undo of the task).
            return AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = "setting:" + settingKey,
                objectName = description,
                typeName = "Setting",
                type = SnapshotType.Setting,
                settingKey = settingKey,
                settingOldValueJson = oldValueJson
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

            AddSnapshot(snapshot);
        }

        /// <summary>
        /// Records an asset move (source -> destination) for undo/redo tracking.
        /// Replaces any existing snapshot for the same global object id.
        /// </summary>
        public static ObjectSnapshot SnapshotAssetMove(string sourcePath, string destinationPath)
        {
            if (_currentTask == null) return null;
            if (Validate.SafePath(sourcePath, "sourcePath") is object ||
                Validate.SafePath(destinationPath, "destinationPath") is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Invalid asset move paths: {sourcePath} -> {destinationPath}");
                return null;
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath);
            string gid = asset != null ? GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString() : "";
            string objectName = asset != null ? asset.name : Path.GetFileNameWithoutExtension(sourcePath);
            string typeName = asset != null ? asset.GetType().Name : "DefaultAsset";

            var snap = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = objectName,
                typeName = typeName,
                type = SnapshotType.Moved,
                assetPath = destinationPath,
                previousAssetPath = sourcePath
            };

            return AddSnapshot(snap, upgradeExisting: true);
        }

        /// <summary>
        /// Records a newly created folder for undo tracking.
        /// Folder deletions are handled via AssetDatabase.DeleteAsset (empty folders only).
        /// </summary>
        public static ObjectSnapshot SnapshotCreatedFolder(string folderPath)
        {
            if (_currentTask == null) return null;
            if (Validate.SafePath(folderPath, "folderPath") is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Invalid folder path: {folderPath}");
                return null;
            }

            var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
            string gid = folderAsset != null ? GlobalObjectId.GetGlobalObjectIdSlow(folderAsset).ToString() : "";

            var snap = new ObjectSnapshot
            {
                globalObjectId = gid,
                objectName = Path.GetFileName(folderPath.TrimEnd('/', '\\')),
                typeName = "DefaultAsset",
                type = SnapshotType.Created,
                assetPath = folderPath
            };

            return AddSnapshot(snap, upgradeExisting: false);
        }

        /// <summary>
        /// Deletes an asset after backing it up to the content-addressed file store.
        /// Creates a Deleted snapshot so the operation can be undone.
        /// For folder deletions, only metadata is recorded and the folder is removed with AssetDatabase.DeleteAsset.
        /// </summary>
        public static bool DeleteAssetToTrash(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || Validate.SafePath(assetPath, "assetPath", isDelete: true) is object)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Unsafe asset delete path: {assetPath}");
                return false;
            }

            if (!WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath))
                return false;

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                return false;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            string gid = asset != null ? GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString() : "";
            string objectName = asset != null ? asset.name : Path.GetFileNameWithoutExtension(assetPath);
            string typeName = asset != null ? asset.GetType().Name : "DefaultAsset";
            bool isFolder = Directory.Exists(fullPath);

            if (!isFolder)
            {
                // Move file (and .meta) into the store so it can be restored on undo.
                string hash = WorkflowFileStore.StoreFile(assetPath, move: true);

                AddSnapshot(new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath,
                    fileHash = hash
                }, upgradeExisting: true);
            }
            else
            {
                AddSnapshot(new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath
                }, upgradeExisting: true);

                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>
        /// Undoes a specific task. Returns detailed per-snapshot results.
        /// Saves the inverse operations to undoneStack for potential redo.
        /// </summary>
        public static TaskUndoResult UndoTask(string taskId)
        {
            var result = new TaskUndoResult();
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                result.error = "Task not found";
                return result;
            }

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
                var detail = UndoSnapshot(snapshot, redoTask);
                result.details.Add(detail);
                if (detail.success) result.succeeded++;
                else result.failed++;
            }

            result.total = result.details.Count;
            result.success = result.failed == 0;

            Undo.CollapseUndoOperations(undoGroup);

            // Move task from history to undone stack
            _history.tasks.Remove(task);
            _history.undoneStack.Add(redoTask);
            SaveHistory();
            return result;
        }

        /// <summary>
        /// Redoes a previously undone task. Returns detailed per-snapshot results.
        /// </summary>
        public static TaskUndoResult RedoTask(string taskId)
        {
            var result = new TaskUndoResult();
            var task = History.undoneStack.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                result.error = "Task not found in undone stack";
                return result;
            }

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

            // Process snapshots in forward order
            foreach (var snapshot in task.snapshots)
            {
                var detail = RedoSnapshot(snapshot, newTask);
                result.details.Add(detail);
                if (detail.success) result.succeeded++;
                else result.failed++;
            }

            result.total = result.details.Count;
            result.success = result.failed == 0;

            Undo.CollapseUndoOperations(undoGroup);

            // Move task from undone stack back to history
            _history.undoneStack.Remove(task);
            _history.tasks.Add(newTask);
            SaveHistory();
            return result;
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
        public static TaskUndoResult RevertTask(string taskId)
        {
            return UndoTask(taskId);
        }

        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            var task = _history.tasks.FirstOrDefault(t => t.id == taskId);
            if (task != null)
                _history.tasks.Remove(task);

            SaveHistory();

            // Reclaim file store entries no longer referenced by any task
            var referencedHashes = CollectReferencedHashes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out _, out _);
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
        /// Undoes all changes made during a specific session. Returns detailed per-snapshot results.
        /// </summary>
        public static TaskUndoResult UndoSession(string sessionId)
        {
            var result = new TaskUndoResult();
            if (string.IsNullOrEmpty(sessionId))
            {
                result.error = "sessionId is required";
                return result;
            }

            // Find all tasks belonging to this session
            var sessionTasks = History.tasks
                .Where(t => t.sessionId == sessionId)
                .OrderByDescending(t => t.timestamp)
                .ToList();

            if (sessionTasks.Count == 0)
            {
                result.error = $"No tasks found for session: {sessionId}";
                return result;
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Undo Session");
            int undoGroup = Undo.GetCurrentGroup();

            // Collect all snapshots from all tasks in chronological order (oldest first)
            var allSnapshots = new List<ObjectSnapshot>();
            foreach (var task in sessionTasks.OrderBy(t => t.timestamp))
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

            var redoTask = new WorkflowTask
            {
                id = sessionTasks[0].id,
                tag = "Session Undo",
                description = $"Undo session {sessionId}",
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                sessionId = sessionId,
                snapshots = new List<ObjectSnapshot>()
            };

            foreach (var snapshot in uniqueSnapshots)
            {
                var detail = UndoSnapshot(snapshot, redoTask);
                result.details.Add(detail);
                if (detail.success) result.succeeded++;
                else result.failed++;
            }

            result.total = result.details.Count;
            result.success = result.failed == 0;

            Undo.CollapseUndoOperations(undoGroup);

            // Remove session tasks from history
            _history.tasks.RemoveAll(t => t.sessionId == sessionId);
            _history.undoneStack.Add(redoTask);
            SaveHistory();

            return result;
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

        #region Undo/Redo Snapshot Dispatch

        /// <summary>
        /// Undoes a single snapshot and records the inverse operation in redoTask.
        /// </summary>
        private static SnapshotUndoResult UndoSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            try
            {
                switch (snapshot.type)
                {
                    case SnapshotType.Modified:
                        result.success = UndoModifiedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Created:
                        result.success = UndoCreatedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Deleted:
                        result.success = UndoDeletedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Moved:
                        result.success = UndoMovedSnapshot(snapshot, redoTask);
                        break;
                    case SnapshotType.Setting:
                        result.success = UndoSettingSnapshot(snapshot, redoTask, out string undoSettingError);
                        if (!result.success && !string.IsNullOrEmpty(undoSettingError))
                            result.error = undoSettingError;
                        break;
                    default:
                        result.error = $"Unsupported snapshot type: {snapshot.type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
            }

            if (!result.success && string.IsNullOrEmpty(result.error))
                result.error = "Unknown failure";

            return result;
        }

        /// <summary>
        /// Redoes a single snapshot and records the inverse operation in newTask.
        /// </summary>
        private static SnapshotUndoResult RedoSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            try
            {
                switch (snapshot.type)
                {
                    case SnapshotType.Modified:
                        result.success = RedoModifiedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Created:
                        result.success = RedoCreatedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Deleted:
                        result.success = RedoDeletedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Moved:
                        result.success = RedoMovedSnapshot(snapshot, newTask);
                        break;
                    case SnapshotType.Setting:
                        result.success = RedoSettingSnapshot(snapshot, newTask, out string redoSettingError);
                        if (!result.success && !string.IsNullOrEmpty(redoSettingError))
                            result.error = redoSettingError;
                        break;
                    default:
                        result.error = $"Unsupported snapshot type: {snapshot.type}";
                        break;
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
            }

            if (!result.success && string.IsNullOrEmpty(result.error))
                result.error = "Unknown failure";

            return result;
        }

        private static bool UndoModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            return RestoreModifiedSnapshot(snapshot, redoTask, removeFromStore: false, undoLabel: "Undo Workflow Modification");
        }

        private static bool RedoModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            return RestoreModifiedSnapshot(snapshot, newTask, removeFromStore: false, undoLabel: "Redo Workflow Modification");
        }

        private static bool UndoCreatedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            // Component creation undo
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
                                redoTask.snapshots.Add(new ObjectSnapshot
                                {
                                    globalObjectId = snapshot.globalObjectId,
                                    objectName = snapshot.objectName,
                                    typeName = snapshot.typeName,
                                    type = SnapshotType.Created,
                                    componentTypeName = snapshot.componentTypeName,
                                    parentGameObjectId = snapshot.parentGameObjectId
                                });
                                Undo.DestroyObjectImmediate(comp);
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            // GameObject creation undo
            if (snapshot.typeName == "GameObject")
            {
                if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                    return false;

                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (!(obj is GameObject go))
                    return false;

                redoTask.snapshots.Add(CaptureGameObjectState(go, new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Created,
                    primitiveType = snapshot.primitiveType
                }));
                Undo.DestroyObjectImmediate(go);
                return true;
            }

            // Asset or folder creation undo
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                    return false;

                bool isFolder = Directory.Exists(fullPath);
                if (isFolder)
                {
                    if (Directory.GetFileSystemEntries(fullPath).Length > 0)
                    {
                        SkillsLogger.LogWarning($"[WorkflowManager] Cannot undo created folder, not empty: {snapshot.assetPath}");
                        return false;
                    }

                    redoTask.snapshots.Add(new ObjectSnapshot
                    {
                        globalObjectId = snapshot.globalObjectId,
                        objectName = snapshot.objectName,
                        typeName = snapshot.typeName,
                        type = SnapshotType.Deleted,
                        assetPath = snapshot.assetPath
                    });
                    AssetDatabase.DeleteAsset(snapshot.assetPath);
                    return true;
                }

                if (File.Exists(fullPath))
                {
                    string hash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: true);
                    redoTask.snapshots.Add(new ObjectSnapshot
                    {
                        globalObjectId = snapshot.globalObjectId,
                        objectName = snapshot.objectName,
                        typeName = snapshot.typeName,
                        type = SnapshotType.Deleted,
                        assetPath = snapshot.assetPath,
                        fileHash = hash
                    });
                    AssetDatabase.Refresh();
                    return true;
                }

                return false;
            }

            // Generic object creation undo
            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId genericGid))
                return false;

            var genericObj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(genericGid);
            if (genericObj == null) return false;

            if (genericObj is GameObject go2) Undo.DestroyObjectImmediate(go2);
            else if (genericObj is Component comp2) Undo.DestroyObjectImmediate(comp2);
            else Undo.DestroyObjectImmediate(genericObj);

            return true;
        }

        private static bool RedoCreatedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
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
                            var comp = Undo.AddComponent(go, compType);
                            if (comp != null && !string.IsNullOrEmpty(snapshot.originalJson))
                            {
                                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, comp);
                            }

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
                            return true;
                        }
                    }
                }
                return false;
            }

            if (snapshot.typeName == "GameObject")
            {
                var newGo = RecreateGameObject(snapshot);
                newTask.snapshots.Add(CaptureGameObjectState(newGo, new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(newGo).ToString(),
                    originalJson = EditorJsonUtility.ToJson(newGo),
                    objectName = newGo.name,
                    typeName = "GameObject",
                    type = SnapshotType.Created,
                    primitiveType = snapshot.primitiveType
                }));
                return true;
            }

            // Asset/folder branch: an asset/folder Created snapshot only reaches the redo stack via
            // UndoDeletedSnapshot (a deletion was undone, restoring the asset). Reversing that undo
            // means re-deleting the asset/folder with a fresh content-addressed backup and pushing a
            // proper Deleted inverse, which is exactly UndoCreatedSnapshot's asset/folder branch.
            // Delegating avoids the earlier bug where a restored file (no fileHash) was misclassified
            // as a folder and deleted without backup, corrupting the next undo. The component and
            // GameObject branches above are intentionally NOT self-inverse (destroy vs recreate) and
            // are handled before reaching here.
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                return UndoCreatedSnapshot(snapshot, newTask);
            }

            return false;
        }

        private static bool UndoDeletedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            if (string.IsNullOrEmpty(snapshot.assetPath))
                return false;

            if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                return false;

            bool isFolder = string.IsNullOrEmpty(snapshot.fileHash);

            if (File.Exists(fullPath) || (isFolder && Directory.Exists(fullPath)))
                return false; // Destination already exists

            if (!isFolder && !string.IsNullOrEmpty(snapshot.fileHash))
            {
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath
                });

                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.assetPath, removeFromStore: false);
            }

            if (isFolder)
            {
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath
                });

                string parentPath = Path.GetDirectoryName(snapshot.assetPath).Replace('\\', '/');
                string folderName = Path.GetFileName(snapshot.assetPath.TrimEnd('/', '\\'));
                AssetDatabase.CreateFolder(parentPath, folderName);
                return true;
            }

            return false;
        }

        private static bool RedoDeletedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            // Redo reverses the undo, it does not re-run the original operation. A Deleted snapshot
            // only ever reaches the redo stack via UndoCreatedSnapshot (undoing a creation deletes
            // the asset/folder and records a Deleted inverse). Reversing that undo means restoring
            // the asset/folder, which is exactly what UndoDeletedSnapshot does, so the redo of a
            // Deleted snapshot is identical to the undo of one.
            return UndoDeletedSnapshot(snapshot, newTask);
        }

        private static bool UndoMovedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            if (string.IsNullOrEmpty(snapshot.assetPath) || string.IsNullOrEmpty(snapshot.previousAssetPath))
                return false;

            string result = AssetDatabase.MoveAsset(snapshot.assetPath, snapshot.previousAssetPath);
            if (!string.IsNullOrEmpty(result))
                return false;

            redoTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Moved,
                assetPath = snapshot.previousAssetPath,
                previousAssetPath = snapshot.assetPath
            });
            return true;
        }

        private static bool RedoMovedSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            // A Moved snapshot is a self-inverse: undoing it moves the asset back and records a
            // snapshot with the two paths swapped. Reversing that undo is the same operation again,
            // so redo replays the undo logic against the already-swapped snapshot on the redo stack.
            return UndoMovedSnapshot(snapshot, newTask);
        }

        /// <summary>
        /// Undoes a settings change: captures the current (post-change) value into the redo snapshot,
        /// then restores the recorded old value via the setting restorer registry.
        /// </summary>
        private static bool UndoSettingSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(snapshot.settingKey))
            {
                error = "Setting snapshot has no settingKey";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.IsRegistered(snapshot.settingKey))
            {
                error = $"No restorer registered for setting '{snapshot.settingKey}'. " +
                        "The owning skill's registration runs on domain load; re-run the skill or reload if this persists.";
                return false;
            }

            // Capture the current value (what the change set it to) so redo can re-apply it.
            string redoValueJson = WorkflowSettingRestorerRegistry.TryGetCurrentValue(snapshot.settingKey);

            if (!WorkflowSettingRestorerRegistry.TryRestore(snapshot.settingKey, snapshot.settingOldValueJson))
            {
                error = $"Restorer for setting '{snapshot.settingKey}' failed to apply the old value.";
                return false;
            }

            redoTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Setting,
                settingKey = snapshot.settingKey,
                settingOldValueJson = redoValueJson
            });
            return true;
        }

        /// <summary>
        /// Redoes a settings change: re-applies the value captured when the change was undone,
        /// capturing the pre-redo value into the new snapshot so a later undo remains reversible.
        /// </summary>
        private static bool RedoSettingSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(snapshot.settingKey))
            {
                error = "Setting snapshot has no settingKey";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.IsRegistered(snapshot.settingKey))
            {
                error = $"No restorer registered for setting '{snapshot.settingKey}'. " +
                        "The owning skill's registration runs on domain load; re-run the skill or reload if this persists.";
                return false;
            }

            // Capture the current (pre-redo, i.e. old) value so a subsequent undo can restore it.
            string undoValueJson = WorkflowSettingRestorerRegistry.TryGetCurrentValue(snapshot.settingKey);

            if (snapshot.settingOldValueJson == null && undoValueJson == null)
            {
                error = $"Setting '{snapshot.settingKey}' has no redo value to re-apply.";
                return false;
            }

            if (!WorkflowSettingRestorerRegistry.TryRestore(snapshot.settingKey, snapshot.settingOldValueJson))
            {
                error = $"Restorer for setting '{snapshot.settingKey}' failed to re-apply the value.";
                return false;
            }

            newTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Setting,
                settingKey = snapshot.settingKey,
                settingOldValueJson = undoValueJson
            });
            return true;
        }

        #endregion

        #region Internal Helpers

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
                fileHash = baseSnapshot.fileHash,
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
        /// the snapshot data (via file store restore, legacy base64, or JSON overlay).
        /// </summary>
        private static bool RestoreModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask targetTask,
            bool removeFromStore, string undoLabel)
        {
            if (!GlobalObjectId.TryParse(snapshot.globalObjectId, out GlobalObjectId gid))
                return false;

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (obj == null) return false;

            // Capture current state for the target task (including file store backup)
            string currentFileHash = "";
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string currentAssetPath) && File.Exists(currentAssetPath))
                {
                    currentFileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false);
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
                fileHash = currentFileHash
            });

            // Legacy base64 backup takes priority if present (old history data)
            if (!string.IsNullOrEmpty(snapshot.assetBytesBase64) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                {
                    SkillsLogger.LogWarning($"{SkillsLogger.PREFIX_WARNING} Skipping unsafe workflow restore path: {snapshot.assetPath}");
                    return false;
                }

                File.WriteAllBytes(fullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                AssetDatabase.ImportAsset(snapshot.assetPath);
                return true;
            }

            // Restore from content-addressed file store
            if (!string.IsNullOrEmpty(snapshot.fileHash) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.assetPath, removeFromStore);
            }

            // Fallback to JSON overlay for scene objects / assets without a file backup
            if (!string.IsNullOrEmpty(snapshot.originalJson))
            {
                Undo.RecordObject(obj, undoLabel);
                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                EditorUtility.SetDirty(obj);
                return true;
            }

            return false;
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

        /// <summary>
        /// Collects all file hashes referenced by active and undone tasks.
        /// </summary>
        private static HashSet<string> CollectReferencedHashes()
        {
            var referencedHashes = new HashSet<string>(StringComparer.Ordinal);
            if (_history == null) return referencedHashes;

            foreach (var task in _history.tasks.Concat(_history.undoneStack))
            {
                if (task?.snapshots == null) continue;
                foreach (var snapshot in task.snapshots)
                {
                    if (!string.IsNullOrEmpty(snapshot?.fileHash))
                        referencedHashes.Add(snapshot.fileHash);
                }
            }
            return referencedHashes;
        }

        #endregion

        #region Auto-Cleanup

        /// <summary>
        /// Trims workflow history and the content-addressed file store according to
        /// WorkflowAutoCleanConfig settings. Called automatically at EndTask and after LoadHistory.
        /// </summary>
        public static WorkflowTrimReport TrimHistoryIfNeeded(bool force = false)
        {
            var report = new WorkflowTrimReport();
            if (_history == null) LoadHistory();
            if (!force && !WorkflowAutoCleanConfig.Enabled)
                return report;

            var now = DateTimeOffset.Now;
            int maxAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays;
            int maxTasks = WorkflowAutoCleanConfig.MaxTasks;
            long maxHistoryBytes = WorkflowAutoCleanConfig.MaxHistoryMB * 1024L * 1024L;

            int beforeCount = _history.tasks.Count + _history.undoneStack.Count;

            // Remove tasks older than MaxTaskAgeDays
            if (maxAgeDays > 0)
            {
                long cutoff = now.AddDays(-maxAgeDays).ToUnixTimeSeconds();
                _history.tasks.RemoveAll(t => t?.timestamp < cutoff);
                _history.undoneStack.RemoveAll(t => t?.timestamp < cutoff);
            }

            // Remove oldest tasks until under MaxTasks
            if (maxTasks > 0)
            {
                while (_history.tasks.Count > maxTasks)
                    _history.tasks.RemoveAt(0);
                while (_history.undoneStack.Count > maxTasks)
                    _history.undoneStack.RemoveAt(0);
            }

            // Remove oldest tasks until estimated serialized size is under MaxHistoryMB
            if (maxHistoryBytes > 0)
            {
                long currentBytes = EstimateHistorySizeBytes();
                while (currentBytes > maxHistoryBytes && (_history.tasks.Count > 0 || _history.undoneStack.Count > 0))
                {
                    var oldest = GetOldestTask(out bool fromActive);
                    if (oldest == null) break;
                    currentBytes -= EstimateTaskSizeBytes(oldest);
                    if (fromActive) _history.tasks.RemoveAt(0);
                    else _history.undoneStack.RemoveAt(0);
                }
            }

            int afterCount = _history.tasks.Count + _history.undoneStack.Count;
            report.removedTasks = beforeCount - afterCount;

            // Reclaim unreferenced file store entries
            var referencedHashes = CollectReferencedHashes();
            long beforeBytes = WorkflowFileStore.GetStoreSizeBytes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out int reclaimedCount, out _);
            report.reclaimedFileEntries = reclaimedCount;

            // Prune file store by age and total size
            int storeMaxAgeDays = WorkflowAutoCleanConfig.StoreMaxAgeDays;
            long maxStoreBytes = WorkflowAutoCleanConfig.MaxStoreMB * 1024L * 1024L;
            if (storeMaxAgeDays > 0 || maxStoreBytes > 0)
            {
                var storeCutoff = now.AddDays(-storeMaxAgeDays).DateTime;
                report.reclaimedFileEntries += WorkflowFileStore.PruneByAgeAndSize(storeCutoff, maxStoreBytes);
            }

            long afterBytes = WorkflowFileStore.GetStoreSizeBytes();
            report.reclaimedBytes = beforeBytes - afterBytes;

            if (report.removedTasks > 0 || report.reclaimedBytes > 0)
            {
                SkillsLogger.LogWorkflow($"Trimmed {report.removedTasks} tasks, reclaimed {FormatBytes(report.reclaimedBytes)} from file store");
            }

            return report;
        }

        private static long EstimateHistorySizeBytes()
        {
            long total = 0;
            foreach (var task in _history.tasks)
                total += EstimateTaskSizeBytes(task);
            foreach (var task in _history.undoneStack)
                total += EstimateTaskSizeBytes(task);
            return total;
        }

        private static long EstimateTaskSizeBytes(WorkflowTask task)
        {
            if (task?.snapshots == null) return 0;
            long size = 64; // task metadata overhead
            foreach (var s in task.snapshots)
            {
                if (s == null) continue;
                size += (s.globalObjectId?.Length ?? 0) +
                        (s.originalJson?.Length ?? 0) +
                        (s.objectName?.Length ?? 0) +
                        (s.typeName?.Length ?? 0) +
                        (s.assetPath?.Length ?? 0) +
                        (s.fileHash?.Length ?? 0) +
                        (s.previousAssetPath?.Length ?? 0) +
                        (s.assetBytesBase64?.Length ?? 0) +
                        (s.componentTypeName?.Length ?? 0) +
                        (s.parentGameObjectId?.Length ?? 0) +
                        (s.primitiveType?.Length ?? 0) +
                        (s.settingKey?.Length ?? 0) +
                        (s.settingOldValueJson?.Length ?? 0) +
                        64; // snapshot overhead
            }
            return size;
        }

        private static WorkflowTask GetOldestTask(out bool fromActive)
        {
            fromActive = true;
            WorkflowTask oldest = _history.tasks.Count > 0 ? _history.tasks[0] : null;
            WorkflowTask undoneOldest = _history.undoneStack.Count > 0 ? _history.undoneStack[0] : null;

            if (oldest == null)
            {
                oldest = undoneOldest;
                fromActive = false;
            }
            else if (undoneOldest != null && undoneOldest.timestamp < oldest.timestamp)
            {
                oldest = undoneOldest;
                fromActive = false;
            }

            return oldest;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        #endregion

        private static void MigrateHistorySchema()
        {
            if (_history == null)
                return;

            // Accept schema version 2 (legacy base64 snapshots) and upgrade to 3.
            if (_history.schemaVersion < WorkflowHistoryData.CurrentSchemaVersion)
            {
                SkillsLogger.LogVerbose(
                    $"Workflow history schema upgraded: {_history.schemaVersion} -> {WorkflowHistoryData.CurrentSchemaVersion}");
                _history.schemaVersion = WorkflowHistoryData.CurrentSchemaVersion;
            }
        }

        public static void ClearHistory()
        {
            _history = new WorkflowHistoryData();
            WorkflowFileStore.CollectGarbage(new HashSet<string>(StringComparer.Ordinal), out _, out _);
            SaveHistory();
        }

        /// <summary>
        /// Returns the on-disk size of the workflow history JSON file in bytes (0 if absent).
        /// </summary>
        public static long GetHistoryFileSizeBytes()
        {
            try
            {
                return File.Exists(HistoryFilePath) ? new FileInfo(HistoryFilePath).Length : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}

// Producer:Betsy
