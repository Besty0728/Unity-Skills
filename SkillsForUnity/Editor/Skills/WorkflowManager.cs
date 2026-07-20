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
        internal static string OverrideHistoryFilePathForTests;
        private static string HistoryFilePath => OverrideHistoryFilePathForTests ??
            Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_history.json");

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

        internal static event Action<GameObject, Type> ComponentTopologyChanged;

        private static void NotifyComponentTopologyChanged(GameObject owner, Type componentType)
        {
            if (owner == null || componentType == null) return;
            try { ComponentTopologyChanged?.Invoke(owner, componentType); }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Component topology callback failed for {componentType.Name}: {ex.Message}");
            }
        }

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
                string json = JsonUtility.ToJson(_history, true);
                string tmpPath = HistoryFilePath + ".tmp";
                string backupPath = HistoryFilePath + ".bak";
                File.WriteAllText(tmpPath, json, SkillsCommon.Utf8NoBom);
                if (File.Exists(HistoryFilePath))
                {
                    File.Replace(tmpPath, HistoryFilePath, backupPath);
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                else
                {
                    File.Move(tmpPath, HistoryFilePath);
                }
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
                            snapshot.metaFileHash = null;
                            snapshot.previousAssetPath = null;
                            snapshot.assetBytesBase64 = null;
                            snapshot.directoryEntries?.Clear();
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
            _history.undoneStack.Clear();
            _currentTask = null;

            TrimHistoryIfNeeded();
            SaveHistory();
        }

        public static void AbortTask()
        {
            _currentTask = null;
        }

        internal static void TruncateCurrentTask(int snapshotCount)
        {
            if (_currentTask?.snapshots == null) return;
            snapshotCount = Mathf.Clamp(snapshotCount, 0, _currentTask.snapshots.Count);
            if (_currentTask.snapshots.Count > snapshotCount)
                _currentTask.snapshots.RemoveRange(snapshotCount, _currentTask.snapshots.Count - snapshotCount);
            _currentTask.InvalidateSnapshotIndex();
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

            bool shouldDeduplicate = WorkflowTask.ShouldDeduplicate(snap);
            if (shouldDeduplicate && _currentTask.HasSnapshot(snap.globalObjectId, snap.type))
            {
                if (!upgradeExisting)
                    return null;

                _currentTask.snapshots.RemoveAll(s =>
                    !string.IsNullOrEmpty(s.globalObjectId) &&
                    s.globalObjectId == snap.globalObjectId && s.type == snap.type);
                _currentTask.InvalidateSnapshotIndex();
            }

            _currentTask.snapshots.Add(snap);
            if (shouldDeduplicate)
                _currentTask.TryRegisterSnapshot(snap.globalObjectId, snap.type);
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

            if (type == SnapshotType.Created && obj is GameObject createdGameObject)
            {
                SnapshotCreatedGameObject(createdGameObject);
                return;
            }

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
            string metaFileHash = "";

            try
            {
                json = EditorJsonUtility.ToJson(obj);
                assetPath = AssetDatabase.GetAssetPath(obj);

                // Backup asset file bytes in the content-addressed store (all extensions, including .cs)
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath) && File.Exists(fullPath))
                    {
                        fileHash = WorkflowFileStore.StoreFile(assetPath, move: false, out string storedMetaHash);
                        metaFileHash = storedMetaHash;
                    }
                }
            }
            catch (Exception ex) { SkillsLogger.LogVerbose($"Snapshot serialization failed for {obj.name}: {ex.Message}"); }

            var objectReferences = CaptureObjectReferences(obj, out bool objectReferencesCaptured);
            AddSnapshot(new ObjectSnapshot
            {
                globalObjectId = gid,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
                originalJson = json,
                objectReferencesCaptured = objectReferencesCaptured,
                objectReferences = objectReferences,
                objectName = obj.name,
                typeName = obj.GetType().Name,
                type = type,
                assetPath = assetPath,
                fileHash = fileHash,
                metaFileHash = metaFileHash
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
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                originalJson = "",  // New objects don't need original state
                objectName = comp.name,
                typeName = comp.GetType().Name,
                type = SnapshotType.Created,
                componentTypeName = comp.GetType().FullName,
                parentGameObjectId = parentGid,
                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp.gameObject)
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
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                originalJson = EditorJsonUtility.ToJson(go),
                objectName = go.name,
                typeName = "GameObject",
                type = SnapshotType.Created,
                primitiveType = primitiveType ?? "",
                posX = t.position.x, posY = t.position.y, posZ = t.position.z,
                rotX = t.rotation.x, rotY = t.rotation.y, rotZ = t.rotation.z, rotW = t.rotation.w,
                scaleX = t.localScale.x, scaleY = t.localScale.y, scaleZ = t.localScale.z,
                components = new List<ComponentData>(),
                gameObjectHierarchy = CaptureGameObjectHierarchy(go)
            };

            // Save all components data
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                try
                {
                    var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                    snapshot.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp),
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
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
        /// Folder deletions capture every child file and folder .meta before deleting anything.
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
                string hash = WorkflowFileStore.StoreFile(assetPath, move: false, out string metaHash);
                if (string.IsNullOrEmpty(hash))
                    return false;

                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath,
                    fileHash = hash,
                    metaFileHash = metaHash
                };

                if (!AssetDatabase.DeleteAsset(assetPath))
                    return false;
                AddSnapshot(snapshot);
            }
            else
            {
                var entries = CaptureDirectoryEntries(fullPath);
                if (entries == null)
                    return false;

                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = gid,
                    objectName = objectName,
                    typeName = typeName,
                    type = SnapshotType.Deleted,
                    assetPath = assetPath,
                    isDirectory = true,
                    directoryEntries = entries
                };

                if (!AssetDatabase.DeleteAsset(assetPath))
                    return false;
                AddSnapshot(snapshot);
            }

            AssetDatabase.Refresh();
            return true;
        }

        public static bool DeleteSceneObject(UnityEngine.Object obj)
        {
            if (obj == null) return false;

            if (_currentTask == null)
            {
                Undo.DestroyObjectImmediate(obj);
                return true;
            }

            if (obj is GameObject go)
            {
                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Deleted,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(go)
                };
                Undo.DestroyObjectImmediate(go);
                AddSnapshot(snapshot);
                return true;
            }

            if (obj is Component component && !(component is Transform))
            {
                var owner = component.gameObject;
                var componentType = component.GetType();
                var objectReferences = CaptureObjectReferences(component, out bool objectReferencesCaptured);
                var snapshot = new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                    originalJson = EditorJsonUtility.ToJson(component),
                    objectReferencesCaptured = objectReferencesCaptured,
                    objectReferences = objectReferences,
                    objectName = component.name,
                    typeName = component.GetType().Name,
                    type = SnapshotType.Deleted,
                    componentTypeName = component.GetType().AssemblyQualifiedName,
                    parentGameObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component.gameObject).ToString(),
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component.gameObject)
                };
                Undo.DestroyObjectImmediate(component);
                NotifyComponentTopologyChanged(owner, componentType);
                AddSnapshot(snapshot);
                return true;
            }

            return false;
        }

        private static List<WorkflowStoredPath> CaptureDirectoryEntries(string rootFullPath)
        {
            var entries = new List<WorkflowStoredPath>();
            string normalizedRoot = Path.GetFullPath(rootFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            try
            {
                foreach (string directory in Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories))
                {
                    string metaPath = directory + ".meta";
                    string metaHash = File.Exists(metaPath)
                        ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(metaPath))
                        : null;
                    if (File.Exists(metaPath) && string.IsNullOrEmpty(metaHash))
                        return null;

                    entries.Add(new WorkflowStoredPath
                    {
                        relativePath = GetRelativePath(normalizedRoot, directory),
                        isDirectory = true,
                        metaFileHash = metaHash
                    });
                }

                foreach (string file in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fileHash = WorkflowFileStore.StoreBytes(File.ReadAllBytes(file));
                    string metaPath = file + ".meta";
                    string metaHash = File.Exists(metaPath)
                        ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(metaPath))
                        : null;
                    if (string.IsNullOrEmpty(fileHash) || (File.Exists(metaPath) && string.IsNullOrEmpty(metaHash)))
                        return null;

                    entries.Add(new WorkflowStoredPath
                    {
                        relativePath = GetRelativePath(normalizedRoot, file),
                        fileHash = fileHash,
                        metaFileHash = metaHash
                    });
                }

                string rootMetaPath = normalizedRoot + ".meta";
                string rootMetaHash = File.Exists(rootMetaPath)
                    ? WorkflowFileStore.StoreBytes(File.ReadAllBytes(rootMetaPath))
                    : null;
                if (File.Exists(rootMetaPath) && string.IsNullOrEmpty(rootMetaHash))
                    return null;

                entries.Add(new WorkflowStoredPath
                {
                    relativePath = "",
                    isDirectory = true,
                    metaFileHash = rootMetaHash
                });
                return entries;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Failed to capture directory backup: {ex.Message}");
                return null;
            }
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            var rootUri = new Uri(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(fullPath)).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Undoes a specific task. Returns detailed per-snapshot results.
        /// Saves the inverse operations to undoneStack for potential redo.
        /// </summary>
        public static TaskUndoResult UndoTask(string taskId)
        {
            var task = History.tasks.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                return new TaskUndoResult { error = "Task not found" };
            }

            return TransitionTask(task, _history.tasks, _history.undoneStack, $"Undo Task: {task.tag}");
        }

        /// <summary>
        /// Redoes a previously undone task. Returns detailed per-snapshot results.
        /// </summary>
        public static TaskUndoResult RedoTask(string taskId)
        {
            var task = History.undoneStack.FirstOrDefault(t => t.id == taskId);
            if (task == null)
            {
                return new TaskUndoResult { error = "Task not found in undone stack" };
            }

            return TransitionTask(task, _history.undoneStack, _history.tasks, $"Redo Task: {task.tag}");
        }

        private static TaskUndoResult TransitionTask(WorkflowTask sourceTask, List<WorkflowTask> sourceStack,
            List<WorkflowTask> destinationStack, string undoGroupName)
        {
            var result = new TaskUndoResult();
            var destinationTask = destinationStack.FirstOrDefault(t => t.id == sourceTask.id);
            if (destinationTask == null)
            {
                destinationTask = CloneTaskMetadata(sourceTask);
                destinationStack.Add(destinationTask);
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(undoGroupName);
            int undoGroup = Undo.GetCurrentGroup();

            for (int i = sourceTask.snapshots.Count - 1; i >= 0; i--)
            {
                var detail = UndoSnapshot(sourceTask.snapshots[i], destinationTask);
                result.details.Add(detail);
                if (!detail.success)
                {
                    result.failed++;
                    break;
                }

                result.succeeded++;
                sourceTask.snapshots.RemoveAt(i);
                sourceTask.InvalidateSnapshotIndex();
            }

            result.total = result.details.Count;
            result.success = result.failed == 0 && sourceTask.snapshots.Count == 0;
            Undo.CollapseUndoOperations(undoGroup);

            if (sourceTask.snapshots.Count == 0)
                sourceStack.Remove(sourceTask);
            if (destinationTask.snapshots.Count == 0)
                destinationStack.Remove(destinationTask);

            SaveHistory();
            return result;
        }

        private static WorkflowTask CloneTaskMetadata(WorkflowTask task)
        {
            return new WorkflowTask
            {
                id = task.id,
                tag = task.tag,
                description = task.description,
                timestamp = task.timestamp,
                sessionId = task.sessionId,
                snapshots = new List<ObjectSnapshot>()
            };
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

            // Undo whole tasks newest-first. Keeping task boundaries preserves operation order
            // when the same object was modified, moved, and deleted across the session.
            foreach (var task in sessionTasks)
            {
                var taskResult = TransitionTask(task, _history.tasks, _history.undoneStack, "Undo Session");
                result.details.AddRange(taskResult.details);
                result.succeeded += taskResult.succeeded;
                result.failed += taskResult.failed;
                if (!taskResult.success)
                    break;
            }

            result.total = result.details.Count;
            result.success = result.failed == 0 && !_history.tasks.Any(t => t.sessionId == sessionId);

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

            int inverseCountBefore = redoTask.snapshots.Count;
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

            if (!result.success && redoTask.snapshots.Count > inverseCountBefore)
            {
                redoTask.snapshots.RemoveRange(inverseCountBefore, redoTask.snapshots.Count - inverseCountBefore);
                redoTask.InvalidateSnapshotIndex();
            }

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
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectInstanceId) is GameObject go)
                {
                    var compType = Type.GetType(snapshot.componentTypeName) ??
                                   ComponentSkills.FindComponentType(snapshot.componentTypeName);
                    if (compType != null)
                    {
                        var comp = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId) as Component;
                        if (comp != null && (comp.gameObject != go || !compType.IsInstanceOfType(comp)))
                            comp = null;

                        // Legacy snapshots without an object identity can only fall back to type lookup.
                        // For identified snapshots, failing is safer than deleting a different same-type component.
                        if (comp == null && string.IsNullOrEmpty(snapshot.globalObjectId) && snapshot.objectInstanceId == 0)
                            comp = go.GetComponent(compType);
                        if (comp != null)
                        {
                            var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                            redoTask.snapshots.Add(new ObjectSnapshot
                            {
                                globalObjectId = snapshot.globalObjectId,
                                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                                originalJson = EditorJsonUtility.ToJson(comp),
                                objectReferencesCaptured = objectReferencesCaptured,
                                objectReferences = objectReferences,
                                objectName = snapshot.objectName,
                                typeName = snapshot.typeName,
                                type = SnapshotType.Deleted,
                                componentTypeName = snapshot.componentTypeName,
                                parentGameObjectId = snapshot.parentGameObjectId,
                                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go)
                            });
                            Undo.DestroyObjectImmediate(comp);
                            NotifyComponentTopologyChanged(go, compType);
                            return true;
                        }
                    }
                }
                return false;
            }

            // GameObject creation undo
            if (snapshot.typeName == "GameObject")
            {
                var obj = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId);
                if (!(obj is GameObject go))
                    return false;

                redoTask.snapshots.Add(CaptureGameObjectState(go, new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = go.name,
                    typeName = "GameObject",
                    type = SnapshotType.Deleted,
                    primitiveType = snapshot.primitiveType,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(go)
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
                    if (Directory.GetFileSystemEntries(fullPath).Length > 0 && !snapshot.deleteRecursively)
                    {
                        SkillsLogger.LogWarning($"[WorkflowManager] Cannot undo created folder, not empty: {snapshot.assetPath}");
                        return false;
                    }

                    return DeleteExistingAssetToInverse(snapshot, redoTask);
                }

                if (File.Exists(fullPath))
                {
                    return DeleteExistingAssetToInverse(snapshot, redoTask);
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
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectInstanceId) is GameObject go)
                {
                    var compType = Type.GetType(snapshot.componentTypeName) ??
                                   ComponentSkills.FindComponentType(snapshot.componentTypeName);
                    if (compType != null)
                    {
                        var comp = Undo.AddComponent(go, compType);
                        if (comp != null && !string.IsNullOrEmpty(snapshot.originalJson))
                        {
                            EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, comp);
                            RestoreObjectReferences(comp, snapshot.objectReferencesCaptured,
                                snapshot.objectReferences, null);
                        }

                        newTask.snapshots.Add(new ObjectSnapshot
                        {
                            globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                            objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                            originalJson = "",
                            objectName = snapshot.objectName,
                            typeName = snapshot.typeName,
                            type = SnapshotType.Created,
                            componentTypeName = snapshot.componentTypeName,
                            parentGameObjectId = snapshot.parentGameObjectId,
                            parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go)
                        });
                        NotifyComponentTopologyChanged(go, compType);
                        return true;
                    }
                }
                return false;
            }

            if (snapshot.typeName == "GameObject")
            {
                var newGo = snapshot.gameObjectHierarchy?.Count > 0
                    ? RestoreGameObjectHierarchy(snapshot.gameObjectHierarchy)
                    : RecreateGameObject(snapshot);
                if (newGo == null) return false;
                newTask.snapshots.Add(CaptureGameObjectState(newGo, new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(newGo).ToString(),
                    originalJson = EditorJsonUtility.ToJson(newGo),
                    objectName = newGo.name,
                    typeName = "GameObject",
                    type = SnapshotType.Created,
                    primitiveType = snapshot.primitiveType,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(newGo)
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

        private static bool DeleteExistingAssetToInverse(ObjectSnapshot snapshot, WorkflowTask inverseTask)
        {
            if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                return false;

            bool isDirectory = Directory.Exists(fullPath);
            List<WorkflowStoredPath> directoryEntries = null;
            string fileHash = null;
            string metaHash = null;
            if (isDirectory)
            {
                directoryEntries = CaptureDirectoryEntries(fullPath);
                if (directoryEntries == null) return false;
            }
            else
            {
                fileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false, out metaHash);
                if (string.IsNullOrEmpty(fileHash)) return false;
            }

            if (!AssetDatabase.DeleteAsset(snapshot.assetPath))
                return false;

            inverseTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Deleted,
                assetPath = snapshot.assetPath,
                fileHash = fileHash,
                metaFileHash = metaHash,
                isDirectory = isDirectory,
                directoryEntries = directoryEntries ?? new List<WorkflowStoredPath>()
            });
            return true;
        }

        private static bool UndoDeletedSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            if (snapshot.typeName == "GameObject" && snapshot.gameObjectHierarchy?.Count > 0)
            {
                var restored = RestoreGameObjectHierarchy(snapshot.gameObjectHierarchy);
                if (restored == null) return false;
                SnapshotCreatedInverse(restored, redoTask);
                return true;
            }

            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                var parent = TryResolveObject(snapshot.parentGameObjectId,
                    snapshot.parentGameObjectInstanceId) as GameObject;
                var componentType = Type.GetType(snapshot.componentTypeName) ?? ComponentSkills.FindComponentType(snapshot.componentTypeName);
                if (parent == null || componentType == null) return false;

                var restored = Undo.AddComponent(parent, componentType);
                if (restored == null) return false;
                if (!string.IsNullOrEmpty(snapshot.originalJson))
                {
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, restored);
                    RestoreObjectReferences(restored, snapshot.objectReferencesCaptured,
                        snapshot.objectReferences, null);
                }
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(restored).ToString(),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(restored),
                    objectName = restored.name,
                    typeName = restored.GetType().Name,
                    type = SnapshotType.Created,
                    componentTypeName = restored.GetType().AssemblyQualifiedName,
                    parentGameObjectId = snapshot.parentGameObjectId,
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(parent)
                });
                NotifyComponentTopologyChanged(parent, componentType);
                return true;
            }

            if (string.IsNullOrEmpty(snapshot.assetPath))
                return false;

            if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string fullPath))
                return false;

            bool isFolder = snapshot.isDirectory ||
                            (string.IsNullOrEmpty(snapshot.fileHash) && snapshot.directoryEntries?.Count == 0);

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

                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                    snapshot.assetPath, removeFromStore: false);
            }

            if (isFolder)
            {
                if (snapshot.directoryEntries != null && snapshot.directoryEntries.Count > 0)
                {
                    if (!RestoreDirectorySnapshot(snapshot, fullPath))
                        return false;
                }
                else
                {
                    string parentPath = Path.GetDirectoryName(snapshot.assetPath).Replace('\\', '/');
                    string folderName = Path.GetFileName(snapshot.assetPath.TrimEnd('/', '\\'));
                    AssetDatabase.CreateFolder(parentPath, folderName);
                }

                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath,
                    deleteRecursively = true
                });
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
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                originalJson = baseSnapshot.originalJson,
                objectReferencesCaptured = baseSnapshot.objectReferencesCaptured,
                objectReferences = baseSnapshot.objectReferences ?? new List<ObjectReferenceData>(),
                objectName = baseSnapshot.objectName,
                typeName = baseSnapshot.typeName,
                type = baseSnapshot.type,
                componentTypeName = baseSnapshot.componentTypeName,
                parentGameObjectId = baseSnapshot.parentGameObjectId,
                parentGameObjectInstanceId = baseSnapshot.parentGameObjectInstanceId,
                assetPath = baseSnapshot.assetPath,
                fileHash = baseSnapshot.fileHash,
                metaFileHash = baseSnapshot.metaFileHash,
                primitiveType = baseSnapshot.primitiveType,
                gameObjectHierarchy = CaptureGameObjectHierarchy(go),
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
                    var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                    result.components.Add(new ComponentData
                    {
                        typeName = comp.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(comp),
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(comp).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* Some components may not be serializable, skip safely */ }
            }

            return result;
        }

        private static List<GameObjectSnapshotData> CaptureGameObjectHierarchy(GameObject go)
        {
            var nodes = new List<GameObjectSnapshotData>();
            CaptureGameObjectNode(go, -1, nodes);
            return nodes;
        }

        private static void CaptureGameObjectNode(GameObject go, int parentIndex,
            List<GameObjectSnapshotData> nodes)
        {
            var transform = go.transform;
            var data = new GameObjectSnapshotData
            {
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                transformGlobalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(transform).ToString(),
                transformInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(transform),
                name = go.name,
                parentIndex = parentIndex,
                activeSelf = go.activeSelf,
                layer = go.layer,
                tag = go.tag,
                siblingIndex = transform.GetSiblingIndex(),
                externalParentGlobalObjectId = parentIndex < 0 && transform.parent != null
                    ? GlobalObjectId.GetGlobalObjectIdSlow(transform.parent.gameObject).ToString()
                    : null,
                externalParentInstanceId = parentIndex < 0 && transform.parent != null
                    ? UnityObjectIdUtility.GetLegacyInstanceId(transform.parent.gameObject)
                    : 0,
                posX = transform.localPosition.x,
                posY = transform.localPosition.y,
                posZ = transform.localPosition.z,
                rotX = transform.localRotation.x,
                rotY = transform.localRotation.y,
                rotZ = transform.localRotation.z,
                rotW = transform.localRotation.w,
                scaleX = transform.localScale.x,
                scaleY = transform.localScale.y,
                scaleZ = transform.localScale.z
            };
            int currentIndex = nodes.Count;
            nodes.Add(data);

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || component is Transform) continue;
                try
                {
                    var objectReferences = CaptureObjectReferences(component, out bool objectReferencesCaptured);
                    data.components.Add(new ComponentData
                    {
                        typeName = component.GetType().AssemblyQualifiedName,
                        json = EditorJsonUtility.ToJson(component),
                        globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString(),
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(component),
                        objectReferencesCaptured = objectReferencesCaptured,
                        objectReferences = objectReferences
                    });
                }
                catch { /* unsupported component serialization is non-fatal */ }
            }

            foreach (Transform child in transform)
                CaptureGameObjectNode(child.gameObject, currentIndex, nodes);
        }

        private static GameObject RestoreGameObjectHierarchy(List<GameObjectSnapshotData> nodes)
        {
            if (nodes == null || nodes.Count == 0) return null;
            var restored = new List<GameObject>(nodes.Count);
            var restoredObjects = new RestoredObjectMap();
            var restoredComponents = new Dictionary<ComponentData, Component>();

            for (int i = 0; i < nodes.Count; i++)
            {
                var data = nodes[i];
                Transform parent = data.parentIndex >= 0 && data.parentIndex < restored.Count
                    ? restored[data.parentIndex].transform
                    : (TryResolveObject(data.externalParentGlobalObjectId, data.externalParentInstanceId) as GameObject)?.transform;

                var go = new GameObject(data.name);
                if (i == 0) Undo.RegisterCreatedObjectUndo(go, "Restore Workflow GameObject");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(data.posX, data.posY, data.posZ);
                go.transform.localRotation = new Quaternion(data.rotX, data.rotY, data.rotZ, data.rotW);
                go.transform.localScale = new Vector3(data.scaleX, data.scaleY, data.scaleZ);
                go.layer = data.layer;
                try { go.tag = data.tag; } catch { }
                go.SetActive(data.activeSelf);
                go.transform.SetSiblingIndex(Mathf.Max(0, data.siblingIndex));
                restored.Add(go);
                restoredObjects.Add(data.globalObjectId, data.objectInstanceId, go);
                restoredObjects.Add(data.transformGlobalObjectId, data.transformInstanceId, go.transform);

                foreach (var componentData in data.components ?? new List<ComponentData>())
                {
                    var componentType = Type.GetType(componentData.typeName) ?? ComponentSkills.FindComponentType(componentData.typeName);
                    if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) continue;
                    var component = Undo.AddComponent(go, componentType);
                    if (component == null) continue;
                    NotifyComponentTopologyChanged(go, componentType);
                    restoredComponents[componentData] = component;
                    restoredObjects.Add(componentData.globalObjectId,
                        componentData.objectInstanceId, component);
                }
            }

            // References can point forward to children or components that did not exist during the
            // first pass, so deserialize only after the whole hierarchy has been recreated.
            foreach (var pair in restoredComponents)
            {
                var componentData = pair.Key;
                var component = pair.Value;
                if (component == null || string.IsNullOrEmpty(componentData.json)) continue;
                EditorJsonUtility.FromJsonOverwrite(componentData.json, component);
                RestoreObjectReferences(component, componentData.objectReferencesCaptured,
                    componentData.objectReferences, null, restoredObjects);
            }
            return restored[0];
        }

        private static void SnapshotCreatedInverse(GameObject go, WorkflowTask inverseTask)
        {
            inverseTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                originalJson = EditorJsonUtility.ToJson(go),
                objectName = go.name,
                typeName = "GameObject",
                type = SnapshotType.Created,
                gameObjectHierarchy = CaptureGameObjectHierarchy(go)
            });
        }

        private static bool RestoreDirectorySnapshot(ObjectSnapshot snapshot, string rootFullPath)
        {
            try
            {
                Directory.CreateDirectory(rootFullPath);
                foreach (var entry in snapshot.directoryEntries.Where(e => e != null && e.isDirectory)
                             .OrderBy(e => e.relativePath?.Length ?? 0))
                {
                    string path = string.IsNullOrEmpty(entry.relativePath)
                        ? rootFullPath
                        : Path.Combine(rootFullPath, entry.relativePath);
                    Directory.CreateDirectory(path);
                    if (!string.IsNullOrEmpty(entry.metaFileHash) &&
                        !WorkflowFileStore.RestoreBlob(entry.metaFileHash, path + ".meta"))
                        return false;
                }

                foreach (var entry in snapshot.directoryEntries.Where(e => e != null && !e.isDirectory))
                {
                    string path = Path.Combine(rootFullPath, entry.relativePath);
                    if (!WorkflowFileStore.RestoreBlob(entry.fileHash, path))
                        return false;
                    if (!string.IsNullOrEmpty(entry.metaFileHash) &&
                        !WorkflowFileStore.RestoreBlob(entry.metaFileHash, path + ".meta"))
                        return false;
                }

                AssetDatabase.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowManager] Failed to restore directory {snapshot.assetPath}: {ex.Message}");
                return false;
            }
        }

        private static UnityEngine.Object TryResolveObject(string globalObjectId, int instanceId)
        {
            if (!string.IsNullOrEmpty(globalObjectId) &&
                GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid))
            {
                var persisted = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (persisted != null) return persisted;
            }

            return instanceId != 0 ? UnityObjectIdUtility.ObjectIdToObject(instanceId) : null;
        }

        private static List<ObjectReferenceData> CaptureObjectReferences(UnityEngine.Object obj,
            out bool captureSucceeded)
        {
            captureSucceeded = false;
            var references = new List<ObjectReferenceData>();
            if (obj == null) return references;

            try
            {
                var serializedObject = new SerializedObject(obj);
                var iterator = serializedObject.GetIterator();
                bool enterChildren = true;
                while (iterator.Next(enterChildren))
                {
                    enterChildren = true;
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference ||
                        !IsRestorableObjectReferencePath(iterator.propertyPath))
                        continue;

                    var referencedObject = iterator.objectReferenceValue;
                    references.Add(new ObjectReferenceData
                    {
                        propertyPath = iterator.propertyPath,
                        globalObjectId = referencedObject != null
                            ? GlobalObjectId.GetGlobalObjectIdSlow(referencedObject).ToString()
                            : string.Empty,
                        objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(referencedObject)
                    });
                }
                captureSucceeded = true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Object reference snapshot failed: {ex.Message}");
            }

            return references;
        }

        private static bool IsRestorableObjectReferencePath(string propertyPath)
        {
            switch (propertyPath)
            {
                case "m_Script":
                case "m_GameObject":
                case "m_CorrespondingSourceObject":
                case "m_PrefabInstance":
                case "m_PrefabAsset":
                    return false;
                default:
                    return true;
            }
        }

        private static void RestoreObjectReferences(UnityEngine.Object obj, bool referencesCaptured,
            List<ObjectReferenceData> capturedReferences, List<ObjectReferenceData> legacyReferences,
            RestoredObjectMap restoredObjects = null)
        {
            var references = referencesCaptured ? capturedReferences : legacyReferences;
            if (obj == null || references == null) return;

            var serializedObject = new SerializedObject(obj);
            bool changed = false;
            foreach (var reference in references)
            {
                if (reference == null || string.IsNullOrEmpty(reference.propertyPath) ||
                    !IsRestorableObjectReferencePath(reference.propertyPath))
                    continue;
                var property = serializedObject.FindProperty(reference.propertyPath);
                if (property == null || property.propertyType != SerializedPropertyType.ObjectReference) continue;
                property.objectReferenceValue = restoredObjects?.Resolve(reference.globalObjectId,
                    reference.objectInstanceId) ?? TryResolveObject(reference.globalObjectId,
                        reference.objectInstanceId);
                changed = true;
            }

            if (changed)
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private sealed class RestoredObjectMap
        {
            private readonly Dictionary<string, UnityEngine.Object> _byGlobalId =
                new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
            private readonly Dictionary<int, UnityEngine.Object> _byInstanceId =
                new Dictionary<int, UnityEngine.Object>();

            public void Add(string globalObjectId, int instanceId, UnityEngine.Object obj)
            {
                if (obj == null) return;
                if (!string.IsNullOrEmpty(globalObjectId)) _byGlobalId[globalObjectId] = obj;
                if (instanceId != 0) _byInstanceId[instanceId] = obj;
            }

            public UnityEngine.Object Resolve(string globalObjectId, int instanceId)
            {
                if (instanceId != 0 && _byInstanceId.TryGetValue(instanceId, out var byInstance) &&
                    byInstance != null)
                    return byInstance;
                if (!string.IsNullOrEmpty(globalObjectId) &&
                    _byGlobalId.TryGetValue(globalObjectId, out var byGlobal) && byGlobal != null)
                    return byGlobal;
                return null;
            }
        }

        /// <summary>
        /// Captures the current state of a modified object into targetTask, then restores
        /// the snapshot data (via file store restore, legacy base64, or JSON overlay).
        /// </summary>
        private static bool RestoreModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask targetTask,
            bool removeFromStore, string undoLabel)
        {
            UnityEngine.Object obj = null;
            obj = TryResolveObject(snapshot.globalObjectId, snapshot.objectInstanceId);

            // Legacy deletion snapshots were recorded as Modified. The object no longer resolves,
            // but the stored bytes are sufficient to restore it and create a proper inverse.
            if (obj == null && !string.IsNullOrEmpty(snapshot.assetPath) &&
                (!string.IsNullOrEmpty(snapshot.fileHash) || !string.IsNullOrEmpty(snapshot.assetBytesBase64)))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string missingFullPath) ||
                    File.Exists(missingFullPath))
                    return false;

                bool restored;
                if (!string.IsNullOrEmpty(snapshot.assetBytesBase64))
                {
                    string parentDirectory = Path.GetDirectoryName(missingFullPath);
                    if (!string.IsNullOrEmpty(parentDirectory)) Directory.CreateDirectory(parentDirectory);
                    File.WriteAllBytes(missingFullPath, Convert.FromBase64String(snapshot.assetBytesBase64));
                    AssetDatabase.ImportAsset(snapshot.assetPath, ImportAssetOptions.ForceUpdate);
                    restored = true;
                }
                else
                {
                    restored = WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                        snapshot.assetPath, removeFromStore);
                }

                if (!restored) return false;
                targetTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = snapshot.globalObjectId,
                    objectName = snapshot.objectName,
                    typeName = snapshot.typeName,
                    type = SnapshotType.Created,
                    assetPath = snapshot.assetPath
                });
                return true;
            }

            if (obj == null) return false;

            // Capture current state for the target task (including file store backup)
            string currentFileHash = "";
            string currentMetaHash = "";
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string currentAssetPath) && File.Exists(currentAssetPath))
                {
                    currentFileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false, out currentMetaHash);
                }
            }

            var objectReferences = CaptureObjectReferences(obj, out bool objectReferencesCaptured);
            if (!snapshot.objectReferencesCaptured && !objectReferencesCaptured)
                return false;
            targetTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
                originalJson = EditorJsonUtility.ToJson(obj),
                objectReferencesCaptured = objectReferencesCaptured,
                objectReferences = objectReferences,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Modified,
                assetPath = snapshot.assetPath,
                fileHash = currentFileHash,
                metaFileHash = currentMetaHash
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
                return WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash,
                    snapshot.assetPath, removeFromStore);
            }

            // Fallback to JSON overlay for scene objects / assets without a file backup
            if (!string.IsNullOrEmpty(snapshot.originalJson))
            {
                Undo.RecordObject(obj, undoLabel);
                var legacyReferences = snapshot.objectReferencesCaptured ? null : objectReferences;
                EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                RestoreObjectReferences(obj, snapshot.objectReferencesCaptured,
                    snapshot.objectReferences, legacyReferences);
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
                        {
                            EditorJsonUtility.FromJsonOverwrite(compData.json, existing);
                            RestoreObjectReferences(existing, compData.objectReferencesCaptured,
                                compData.objectReferences, null);
                        }
                    }
                    else
                    {
                        var comp = newGo.AddComponent(compType);
                        if (comp != null && !string.IsNullOrEmpty(compData.json))
                        {
                            EditorJsonUtility.FromJsonOverwrite(compData.json, comp);
                            RestoreObjectReferences(comp, compData.objectReferencesCaptured,
                                compData.objectReferences, null);
                        }
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
                    AddSnapshotHashes(snapshot, referencedHashes);
                }
            }
            return referencedHashes;
        }

        private static void AddSnapshotHashes(ObjectSnapshot snapshot, HashSet<string> hashes)
        {
            if (snapshot == null || hashes == null) return;
            if (!string.IsNullOrEmpty(snapshot.fileHash)) hashes.Add(snapshot.fileHash);
            if (!string.IsNullOrEmpty(snapshot.metaFileHash)) hashes.Add(snapshot.metaFileHash);
            if (snapshot.directoryEntries == null) return;
            foreach (var entry in snapshot.directoryEntries)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.fileHash)) hashes.Add(entry.fileHash);
                if (!string.IsNullOrEmpty(entry.metaFileHash)) hashes.Add(entry.metaFileHash);
            }
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
            long maxStoreBytes = WorkflowAutoCleanConfig.MaxStoreMB > 0
                ? WorkflowAutoCleanConfig.MaxStoreMB * 1024L * 1024L
                : 0;
            if (storeMaxAgeDays > 0 || maxStoreBytes > 0)
            {
                DateTime? storeCutoff = storeMaxAgeDays > 0
                    ? now.AddDays(-storeMaxAgeDays).DateTime
                    : (DateTime?)null;
                report.reclaimedFileEntries += WorkflowFileStore.PruneByAgeAndSize(
                    storeCutoff, maxStoreBytes, referencedHashes);
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
                        (s.metaFileHash?.Length ?? 0) +
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

            if (_history.schemaVersion >= WorkflowHistoryData.CurrentSchemaVersion)
                return;

            int sourceVersion = _history.schemaVersion;
            var snapshots = _history.tasks.Concat(_history.undoneStack)
                .Where(t => t?.snapshots != null)
                .SelectMany(t => t.snapshots)
                .Where(s => s != null)
                .ToList();

            bool migrationSucceeded = true;
            foreach (var snapshot in snapshots)
            {
                if (!string.IsNullOrEmpty(snapshot.assetBytesBase64))
                {
                    try
                    {
                        string hash = WorkflowFileStore.StoreBytes(Convert.FromBase64String(snapshot.assetBytesBase64));
                        if (string.IsNullOrEmpty(hash) || !WorkflowFileStore.BlobExists(hash))
                        {
                            migrationSucceeded = false;
                            break;
                        }
                        snapshot.fileHash = hash;
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"Workflow base64 migration failed for {snapshot.assetPath}: {ex.Message}");
                        migrationSucceeded = false;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(snapshot.fileHash) && string.IsNullOrEmpty(snapshot.metaFileHash))
                    snapshot.metaFileHash = WorkflowFileStore.MigrateLegacyMetaHash(snapshot.fileHash);
            }

            if (!migrationSucceeded)
                return;

            foreach (var snapshot in snapshots)
                snapshot.assetBytesBase64 = null;

            _history.schemaVersion = WorkflowHistoryData.CurrentSchemaVersion;
            SaveHistory();
            SkillsLogger.LogVerbose(
                $"Workflow history schema upgraded: {sourceVersion} -> {WorkflowHistoryData.CurrentSchemaVersion}");
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

        internal static void ResetStateForTests()
        {
            _history = null;
            _currentTask = null;
            _currentSessionId = null;
        }
    }
}

// Producer:Betsy
