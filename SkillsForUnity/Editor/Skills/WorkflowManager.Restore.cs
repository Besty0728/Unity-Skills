using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {
        // The objects a checkpoint restore has recreated so far, keyed by their old ids. Set only while a restore runs, so a
        // later Modified item resolves an object that an earlier Deleted item in the same restore brought back.
        private static RestoredObjectMap _restoreScopeObjects;
        // The alias map a restore resolves ids through, so ids captured before an earlier recreate still find the object.
        private static Dictionary<string, string> _restoreScopeAliases;

        private static void NotifyComponentTopologyChanged(GameObject owner, Type componentType)
        {
            if (owner == null || componentType == null) return;
            try { ComponentTopologyChanged?.Invoke(owner, componentType); }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Component topology callback failed for {componentType.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Undoes the steps <paramref name="taskId"/> names (see <see cref="FindStepsForUndo"/>): their targets go back to the state
        /// before the earliest of them, and steps on other targets stay. The restore is recorded, so <see cref="RedoTask"/> can take it back.
        /// </summary>
        public static TaskUndoResult UndoTask(string taskId, string conflicts = null)
        {
            var steps = FindStepsForUndo(taskId);
            if (steps.Count == 0)
                return new TaskUndoResult { error = "Task not found" };
            return ToTaskUndoResult(RestoreSteps(steps, taskId, ResolveCallerAgent(), conflicts));
        }

        /// <summary>Undoes a restore's own step (see <see cref="RedoRestore"/>).</summary>
        public static TaskUndoResult RedoTask(string taskId, string conflicts = null) => ToTaskUndoResult(RedoRestore(taskId, conflicts));

        /// <summary>A task is addressed by its UUID or by the short s&lt;seq&gt; id the compact list shows.</summary>
        internal static bool MatchesTaskId(WorkflowTask task, string taskId)
        {
            if (task == null || string.IsNullOrEmpty(taskId))
                return false;
            if (task.id == taskId)
                return true;
            return taskId.Length > 1 && (taskId[0] == 's' || taskId[0] == 'S') && task.seq > 0 &&
                   long.TryParse(taskId.Substring(1), out long seq) && seq == task.seq;
        }

        /// <summary>
        /// The steps an undo of <paramref name="taskId"/> covers, in seq order: a step by its s&lt;seq&gt; id or UUID, every step of a
        /// group by its group id (g&lt;n&gt; or b&lt;n&gt;), or, for c&lt;n&gt;, every step of the group of the step at seq n. Empty when nothing matches.
        /// </summary>
        internal static List<WorkflowTask> FindStepsForUndo(string taskId)
        {
            if (string.IsNullOrEmpty(taskId))
                return new List<WorkflowTask>();

            var tasks = History.tasks;
            var steps = tasks.Where(task => MatchesTaskId(task, taskId)).ToList();
            if (steps.Count == 0)
                steps = tasks.Where(task => task.group == taskId).ToList();
            if (steps.Count == 0 && TryParseCheckpointSeq(taskId, out long position))
            {
                var step = tasks.FirstOrDefault(task => task.seq == position);
                if (step != null)
                {
                    steps = string.IsNullOrEmpty(step.group)
                        ? new List<WorkflowTask> { step }
                        : tasks.Where(task => task.group == step.group).ToList();
                }
            }
            return steps.OrderBy(task => task.seq).ToList();
        }

        private static bool TryParseCheckpointSeq(string id, out long seq)
        {
            seq = 0;
            return id.Length > 1 && (id[0] == 'c' || id[0] == 'C') && long.TryParse(id.Substring(1), out seq);
        }

        /// <summary>The steps of one session, every agent's, in seq order.</summary>
        internal static List<WorkflowTask> FindStepsForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return new List<WorkflowTask>();
            return History.tasks.Where(task => task.sessionId == sessionId).OrderBy(task => task.seq).ToList();
        }

        /// <summary>Undoes every step of a session, the same way <see cref="UndoTask"/> undoes a step.</summary>
        public static TaskUndoResult UndoSession(string sessionId, string conflicts = null) =>
            ToTaskUndoResult(RestoreSession(sessionId, conflicts));

        internal static RestoreOutcome RestoreSession(string sessionId, string conflicts = null)
        {
            if (string.IsNullOrEmpty(sessionId))
                return RestoreRefused(SkillErrorCode.SemanticInvalid, "sessionId is required");
            var steps = FindStepsForSession(sessionId);
            if (steps.Count == 0)
                return RestoreRefused(SkillErrorCode.NotFound, $"No tasks found for session: {sessionId}");
            return RestoreSteps(steps, "session " + sessionId, ResolveCallerAgent(), conflicts);
        }

        /// <summary>The step <see cref="RedoRestore"/> undoes: the restore step of the restore a taskId names, or none.</summary>
        internal static List<WorkflowTask> FindStepsForRedo(string taskId)
        {
            var restore = FindRestoreForRedo(taskId, ResolveCallerAgent());
            var step = restore == null ? null : History.tasks.FirstOrDefault(task => task.seq == restore.seq);
            return step == null ? new List<WorkflowTask>() : new List<WorkflowTask> { step };
        }

        /// <summary>
        /// Redoes a restore by undoing its own step, the way <see cref="UndoTask"/> undoes any step: only the targets the restore
        /// changed go back, and later steps stay. See <see cref="FindRestoreForRedo"/> for which restore a taskId names.
        /// </summary>
        internal static RestoreOutcome RedoRestore(string taskId, string conflicts = null)
        {
            var steps = FindStepsForRedo(taskId);
            if (steps.Count == 0)
                return RestoreRefused(SkillErrorCode.NotFound, "No undone tasks to redo");
            return RestoreSteps(steps, steps[0].DisplayId, ResolveCallerAgent(), conflicts);
        }

        /// <summary>
        /// The restore a redo takes back: with no id, the newest undoable restore of the caller; an s&lt;n&gt; or c&lt;n&gt; id names the
        /// restore with that seq or guard; any other id is a step, and the newest undoable restore that reverted it is taken.
        /// </summary>
        private static WorkflowRestoreRecord FindRestoreForRedo(string taskId, string caller)
        {
            var undoable = GetUndoableRestores();
            if (string.IsNullOrEmpty(taskId))
                return undoable.FirstOrDefault(restore => restore.agent == caller);

            var named = undoable.FirstOrDefault(restore =>
                string.Equals(taskId, "s" + restore.seq, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(taskId, restore.guard, StringComparison.OrdinalIgnoreCase));
            if (named != null)
                return named;

            var step = History.tasks.FirstOrDefault(task => MatchesTaskId(task, taskId));
            return step == null ? null : undoable.FirstOrDefault(restore => restore.steps.Contains(step.seq));
        }

        /// <summary>
        /// The restores that can still be undone, newest first: every restore with a recorded step that no later restore reverted.
        /// A restore without a step changed nothing, so there is nothing to undo.
        /// </summary>
        internal static List<WorkflowRestoreRecord> GetUndoableRestores()
        {
            var history = History;
            var restores = history.restores;
            var stepSeqs = new HashSet<long>(history.tasks.Select(task => task.seq));
            var undoable = new List<WorkflowRestoreRecord>();
            for (int i = restores.Count - 1; i >= 0; i--)
            {
                if (stepSeqs.Contains(restores[i].seq) && !RestoreRevertedLater(restores, i))
                    undoable.Add(restores[i]);
            }
            return undoable;
        }

        /// <summary>
        /// The seqs of steps a restore undid: every step of a whole restore that no later restore undid itself. A scoped restore only
        /// partly reverts its steps, so it undoes none of them. A restore whose own step a later restore lists is itself reverted, so its
        /// steps are live again.
        /// </summary>
        internal static HashSet<long> UndoneSteps(IList<WorkflowRestoreRecord> restores)
        {
            var undone = new HashSet<long>();
            for (int i = 0; i < restores.Count; i++)
            {
                if (RestoreRevertedLater(restores, i) || !string.IsNullOrEmpty(restores[i].scope))
                    continue;
                foreach (long step in restores[i].steps)
                    undone.Add(step);
            }
            return undone;
        }

        /// <summary>Whether a later whole restore lists this restore's step, which reverts it. A later scoped restore reverts only part of it.</summary>
        internal static bool RestoreRevertedLater(IList<WorkflowRestoreRecord> restores, int index)
        {
            for (int j = index + 1; j < restores.Count; j++)
            {
                if (restores[j].steps.Contains(restores[index].seq) && string.IsNullOrEmpty(restores[j].scope))
                    return true;
            }
            return false;
        }

        private static TaskUndoResult ToTaskUndoResult(RestoreOutcome outcome)
        {
            var result = new TaskUndoResult
            {
                total = outcome.restored + outcome.merged + outcome.deleted + outcome.recreated + outcome.failed + outcome.skipped,
                succeeded = outcome.restored + outcome.merged + outcome.deleted + outcome.recreated,
                failed = outcome.failed,
                error = outcome.error
            };
            result.success = result.error == null && result.failed == 0 && outcome.matchedSteps > 0;
            foreach (string item in outcome.failedItems)
                result.details.Add(new SnapshotUndoResult { success = false, objectName = item, error = item });
            foreach (var conflict in outcome.conflicts ?? new List<RestoreConflict>())
                result.details.Add(new SnapshotUndoResult
                {
                    success = false,
                    objectName = conflict.target,
                    error = $"{conflict.id}: {conflict.by} {conflict.what}"
                });
            return result;
        }

        /// <summary>An alias for UndoTask (backward compatibility).</summary>
        public static TaskUndoResult RevertTask(string taskId)
        {
            return UndoTask(taskId);
        }

        #region Undo/Redo Snapshot Dispatch

        /// <summary>
        /// Undoes a single snapshot, recording its inverse operation into redoTask.
        /// </summary>
        private static SnapshotUndoResult UndoSnapshot(ObjectSnapshot snapshot, WorkflowTask redoTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            int inverseCountBefore = redoTask.snapshots.Count;
            WorkflowFileStore.ClearLastIntegrityError();
            if (!TryHydrate(snapshot, out string hydrateError))
            {
                result.error = hydrateError;
                return result;
            }
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
                result.error = WorkflowFileStore.LastIntegrityError ?? "Unknown failure";

            if (!result.success && redoTask.snapshots.Count > inverseCountBefore)
            {
                redoTask.snapshots.RemoveRange(inverseCountBefore, redoTask.snapshots.Count - inverseCountBefore);
                redoTask.InvalidateSnapshotIndex();
            }

            return result;
        }

        /// <summary>
        /// Redoes a single snapshot, recording its inverse operation into newTask.
        /// </summary>
        private static SnapshotUndoResult RedoSnapshot(ObjectSnapshot snapshot, WorkflowTask newTask)
        {
            var result = new SnapshotUndoResult
            {
                globalObjectId = snapshot.globalObjectId,
                objectName = snapshot.objectName
            };

            WorkflowFileStore.ClearLastIntegrityError();
            if (!TryHydrate(snapshot, out string hydrateError))
            {
                result.error = hydrateError;
                return result;
            }
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
                result.error = WorkflowFileStore.LastIntegrityError ?? "Unknown failure";

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
            // Undo "create component"
            if (!string.IsNullOrEmpty(snapshot.componentTypeName) &&
                !string.IsNullOrEmpty(snapshot.parentGameObjectId))
            {
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectEntityId, snapshot.parentGameObjectInstanceId) is GameObject go)
                {
                    var compType = Type.GetType(snapshot.componentTypeName) ??
                                   ComponentSkills.FindComponentType(snapshot.componentTypeName);
                    if (compType != null)
                    {
                        var comp = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId) as Component;
                        if (comp != null && (comp.gameObject != go || !compType.IsInstanceOfType(comp)))
                            comp = null;

                        // A legacy snapshot with no object identity can only fall back to a type-based lookup.
                        // For a snapshot that does have an identity, failing is safer than deleting a different component of the same type.
                        if (comp == null && string.IsNullOrEmpty(snapshot.globalObjectId) && snapshot.objectInstanceId == 0 &&
                            string.IsNullOrEmpty(snapshot.entityId))
                            comp = go.GetComponent(compType);
                        if (comp != null)
                        {
                            var objectReferences = CaptureObjectReferences(comp, out bool objectReferencesCaptured);
                            redoTask.snapshots.Add(new ObjectSnapshot
                            {
                                globalObjectId = snapshot.globalObjectId,
                                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                                entityId = UnityObjectIdUtility.GetEntityId(comp),
                                originalJson = EditorJsonUtility.ToJson(comp),
                                objectReferencesCaptured = objectReferencesCaptured,
                                objectReferences = objectReferences,
                                objectName = snapshot.objectName,
                                typeName = snapshot.typeName,
                                type = SnapshotType.Deleted,
                                componentTypeName = snapshot.componentTypeName,
                                parentGameObjectId = snapshot.parentGameObjectId,
                                parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                                parentGameObjectEntityId = UnityObjectIdUtility.GetEntityId(go)
                            });
                            Undo.DestroyObjectImmediate(comp);
                            NotifyComponentTopologyChanged(go, compType);
                            return true;
                        }
                    }
                }
                return false;
            }

            // Undo "create GameObject"
            if (snapshot.typeName == "GameObject")
            {
                var obj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);
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

            // Undo "create asset or folder"
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

            // Undo "create generic object"
            var genericObj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);
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
                if (TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectEntityId, snapshot.parentGameObjectInstanceId) is GameObject go)
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
                            globalObjectId = GetGlobalObjectIdString(comp),
                            objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(comp),
                            entityId = UnityObjectIdUtility.GetEntityId(comp),
                            originalJson = "",
                            objectName = snapshot.objectName,
                            typeName = snapshot.typeName,
                            type = SnapshotType.Created,
                            componentTypeName = snapshot.componentTypeName,
                            parentGameObjectId = snapshot.parentGameObjectId,
                            parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                            parentGameObjectEntityId = UnityObjectIdUtility.GetEntityId(go)
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
                    globalObjectId = GetGlobalObjectIdString(newGo),
                    originalJson = EditorJsonUtility.ToJson(newGo),
                    objectName = newGo.name,
                    typeName = "GameObject",
                    type = SnapshotType.Created,
                    primitiveType = snapshot.primitiveType,
                    gameObjectHierarchy = CaptureGameObjectHierarchy(newGo)
                }));
                return true;
            }

            // Asset/folder branch: a Created snapshot for an asset or folder can only have entered the redo
            // stack via UndoDeletedSnapshot (a deletion was undone, restoring the asset). Reversing that
            // undo means deleting that asset/folder again — with a fresh content-addressed backup — and
            // pushing a correct Deleted inverse operation, which is exactly what UndoCreatedSnapshot's
            // asset/folder branch does. Delegating to it sidesteps an earlier bug: a restored file (with no
            // fileHash) was misjudged as a folder and deleted without a backup, corrupting the next undo.
            // The component and GameObject branches above are deliberately not self-inverse (destroy vs.
            // recreate), and are already handled before reaching this point.
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
                var parent = TryResolveObject(snapshot.parentGameObjectId, snapshot.parentGameObjectEntityId,
                    snapshot.parentGameObjectInstanceId) as GameObject;
                var componentType = Type.GetType(snapshot.componentTypeName) ?? ComponentSkills.FindComponentType(snapshot.componentTypeName);
                if (parent == null || componentType == null) return false;

                // Restoring a ParticleSystem already brought its ParticleSystemRenderer back; restore onto that one.
                var restored = ReuseAutoAddedCompanion(parent, componentType);
                if (restored != null)
                    Undo.RecordObject(restored, "Restore Workflow Component");
                else
                    restored = Undo.AddComponent(parent, componentType);
                if (restored == null) return false;
                _restoreScopeObjects?.Add(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId, restored);
                if (!string.IsNullOrEmpty(snapshot.originalJson))
                {
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, restored);
                    RestoreObjectReferences(restored, snapshot.objectReferencesCaptured,
                        snapshot.objectReferences, null);
                }
                redoTask.snapshots.Add(new ObjectSnapshot
                {
                    globalObjectId = GetGlobalObjectIdString(restored),
                    objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(restored),
                    entityId = UnityObjectIdUtility.GetEntityId(restored),
                    objectName = restored.name,
                    typeName = restored.GetType().Name,
                    type = SnapshotType.Created,
                    componentTypeName = restored.GetType().AssemblyQualifiedName,
                    parentGameObjectId = snapshot.parentGameObjectId,
                    parentGameObjectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(parent),
                    parentGameObjectEntityId = UnityObjectIdUtility.GetEntityId(parent)
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
                return false; // Target already exists

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
            // Redo reverses undo, it doesn't rerun the original operation. A Deleted snapshot can only have
            // entered the redo stack via UndoCreatedSnapshot (undoing a creation = deleting that asset/
            // folder and recording a Deleted inverse operation). Reversing that undo means restoring the
            // asset/folder, which is exactly what UndoDeletedSnapshot does, so "redo a Deleted snapshot" and
            // "undo a Deleted snapshot" are exactly the same thing.
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
            // A Moved snapshot is self-inverse: undoing it just moves the asset back, and records a
            // snapshot with the two paths swapped. Reversing that undo is the same operation again, so redo
            // is just running the undo logic again on that already-swapped snapshot on the redo stack.
            return UndoMovedSnapshot(snapshot, newTask);
        }

        /// <summary>
        /// Undoes a setting change: first captures the current (post-change) value into the redo
        /// snapshot, then restores the recorded old value via the setting restorer registry.
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

            // Capture the current value (the one that change set it to), for reapplying on redo.
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
        /// Redoes a setting change: reapplies the value captured at undo time,
        /// while also capturing the pre-redo value into the new snapshot so a later undo is still reversible.
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

            // Capture the current value (pre-redo, i.e. the old value), for the subsequent undo to restore.
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

        private static GameObject RestoreGameObjectHierarchy(List<GameObjectSnapshotData> nodes)
        {
            if (nodes == null || nodes.Count == 0) return null;
            var restored = new List<GameObject>(nodes.Count);
            var restoredObjects = _restoreScopeObjects ?? new RestoredObjectMap();
            var restoredComponents = new Dictionary<ComponentData, Component>();

            for (int i = 0; i < nodes.Count; i++)
            {
                var data = nodes[i];
                Transform parent = data.parentIndex >= 0 && data.parentIndex < restored.Count
                    ? restored[data.parentIndex].transform
                    : (TryResolveObject(data.externalParentGlobalObjectId, data.externalParentEntityId, data.externalParentInstanceId) as GameObject)?.transform;

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
                restoredObjects.Add(data.globalObjectId, data.entityId, data.objectInstanceId, go);
                restoredObjects.Add(data.transformGlobalObjectId, data.transformEntityId, data.transformInstanceId, go.transform);

                foreach (var componentData in data.components ?? new List<ComponentData>())
                {
                    var componentType = Type.GetType(componentData.typeName) ?? ComponentSkills.FindComponentType(componentData.typeName);
                    if (componentType == null || !typeof(Component).IsAssignableFrom(componentType)) continue;
                    // A companion Unity added with an earlier component (a ParticleSystem's renderer) is reused, so its
                    // JSON is restored instead of being dropped when a second AddComponent is refused.
                    var component = ReuseAutoAddedCompanion(go, componentType, restoredComponents.Values) ?? Undo.AddComponent(go, componentType);
                    if (component == null) continue;
                    NotifyComponentTopologyChanged(go, componentType);
                    restoredComponents[componentData] = component;
                    restoredObjects.Add(componentData.globalObjectId, componentData.entityId,
                        componentData.objectInstanceId, component);
                }
            }

            // References may point forward to child objects or components that don't exist yet on the first
            // pass, so deserialization must wait until the whole hierarchy has been rebuilt.
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

        /// <summary>
        /// The instance of a component Unity adds on its own (see <see cref="ParticleSkills.IsAutoAddedCompanion"/>) when
        /// restoring its owner already re-created it, and it is not yet claimed by another restored entry; otherwise null,
        /// and the caller adds the component as usual.
        /// </summary>
        private static Component ReuseAutoAddedCompanion(GameObject go, Type componentType, IEnumerable<Component> claimed = null)
        {
            if (go == null || !ParticleSkills.IsAutoAddedCompanion(componentType))
                return null;
            var existing = go.GetComponent(componentType);
            if (existing == null || (claimed != null && claimed.Contains(existing)))
                return null;
            return existing;
        }

        private static void SnapshotCreatedInverse(GameObject go, WorkflowTask inverseTask)
        {
            inverseTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = GetGlobalObjectIdString(go),
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(go),
                entityId = UnityObjectIdUtility.GetEntityId(go),
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

        private static UnityEngine.Object TryResolveObject(string globalObjectId, string entityId, int instanceId)
        {
            string requestedGlobalObjectId = globalObjectId;
            if (_restoreScopeAliases != null)
                globalObjectId = ResolveGidAlias(_restoreScopeAliases, globalObjectId);

            if (!string.IsNullOrEmpty(globalObjectId) && !IsUnsavedSceneGlobalObjectId(globalObjectId) &&
                GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid))
            {
                var persisted = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (persisted != null) return persisted;
            }

            // Ids of objects a restore recreated are looked up in the restore's own map, and the session-local ids only when
            // this session captured them.
            string scopeEntityId = _sessionIdFallbackAllowed ? entityId : null;
            int scopeInstanceId = _sessionIdFallbackAllowed ? instanceId : 0;
            var restored = _restoreScopeObjects?.Resolve(globalObjectId, scopeEntityId, scopeInstanceId);
            if (restored == null && requestedGlobalObjectId != globalObjectId)
                restored = _restoreScopeObjects?.Resolve(requestedGlobalObjectId, scopeEntityId, scopeInstanceId);
            if (restored != null) return restored;

            if (!_sessionIdFallbackAllowed)
                return null;

            // A never-saved scene's object that a restore recreated is found through the entity alias the restore recorded.
            if (_restoreScopeAliases != null && !string.IsNullOrEmpty(entityId))
            {
                string aliased = ResolveEntityAlias(_restoreScopeAliases, entityId);
                if (aliased != entityId)
                {
                    entityId = aliased;
                    instanceId = 0;
                }
            }

            // An object of a never-saved scene has no usable GlobalObjectId; the entity id is the fallback that also works
            // on 6000.4+, where the legacy instance id is always 0.
            var byEntity = string.IsNullOrEmpty(entityId) ? null : UnityObjectIdUtility.EntityIdToObject(entityId);
            if (byEntity != null) return byEntity;
            return instanceId != 0 ? UnityObjectIdUtility.ObjectIdToObject(instanceId) : null;
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
                property.objectReferenceValue = restoredObjects?.Resolve(reference.globalObjectId, reference.entityId,
                    reference.objectInstanceId) ?? TryResolveObject(reference.globalObjectId, reference.entityId,
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
            private readonly Dictionary<string, UnityEngine.Object> _byEntityId =
                new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);

            public void Add(string globalObjectId, string entityId, int instanceId, UnityEngine.Object obj)
            {
                if (obj == null) return;
                // The GlobalObjectId of a never-saved scene's object identifies none of them.
                if (!string.IsNullOrEmpty(globalObjectId) && !IsUnsavedSceneGlobalObjectId(globalObjectId)) _byGlobalId[globalObjectId] = obj;
                if (!string.IsNullOrEmpty(entityId)) _byEntityId[entityId] = obj;
                if (instanceId != 0) _byInstanceId[instanceId] = obj;
            }

            public IEnumerable<KeyValuePair<string, UnityEngine.Object>> GlobalIdEntries => _byGlobalId;
            public IEnumerable<KeyValuePair<string, UnityEngine.Object>> EntityIdEntries => _byEntityId;

            public UnityEngine.Object Resolve(string globalObjectId, string entityId, int instanceId)
            {
                if (instanceId != 0 && _byInstanceId.TryGetValue(instanceId, out var byInstance) &&
                    byInstance != null)
                    return byInstance;
                if (!string.IsNullOrEmpty(entityId) && _byEntityId.TryGetValue(entityId, out var byEntity) &&
                    byEntity != null)
                    return byEntity;
                if (!string.IsNullOrEmpty(globalObjectId) &&
                    _byGlobalId.TryGetValue(globalObjectId, out var byGlobal) && byGlobal != null)
                    return byGlobal;
                return null;
            }
        }

        /// <summary>
        /// Adds the inverse of a restore to targetTask: a Modified snapshot of the object's current state, with its file backup when it is an
        /// asset. False, with nothing added, when the references cannot be captured and the snapshot did not capture them either.
        /// </summary>
        private static bool CaptureInverseModified(ObjectSnapshot snapshot, UnityEngine.Object obj, WorkflowTask targetTask,
            out List<ObjectReferenceData> objectReferences)
        {
            // Capture the current state for the target task (including a file-store backup)
            string currentFileHash = "";
            string currentMetaHash = "";
            if (!string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string currentAssetPath) && File.Exists(currentAssetPath))
                {
                    currentFileHash = WorkflowFileStore.StoreFile(snapshot.assetPath, move: false, out currentMetaHash);
                }
            }

            objectReferences = CaptureObjectReferences(obj, out bool objectReferencesCaptured);
            if (!snapshot.objectReferencesCaptured && !objectReferencesCaptured)
                return false;
            targetTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = snapshot.globalObjectId,
                objectInstanceId = UnityObjectIdUtility.GetLegacyInstanceId(obj),
                entityId = UnityObjectIdUtility.GetEntityId(obj),
                originalJson = EditorJsonUtility.ToJson(obj),
                objectReferencesCaptured = objectReferencesCaptured,
                objectReferences = objectReferences,
                objectName = snapshot.objectName,
                typeName = snapshot.typeName,
                type = SnapshotType.Modified,
                assetPath = snapshot.assetPath,
                fileHash = currentFileHash,
                metaFileHash = currentMetaHash,
                liveTarget = obj
            });
            return true;
        }

        /// <summary>
        /// Captures a modified object's current state into targetTask, then restores the snapshot data
        /// (via one of two paths: file-store restore, or a JSON overwrite).
        /// </summary>
        private static bool RestoreModifiedSnapshot(ObjectSnapshot snapshot, WorkflowTask targetTask,
            bool removeFromStore, string undoLabel)
        {
            UnityEngine.Object obj = null;
            obj = TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId);

            // An asset deleted after it was snapshotted as Modified: the object no longer resolves, but the bytes
            // stored are enough to restore it and produce the correct inverse operation.
            if (obj == null && !string.IsNullOrEmpty(snapshot.assetPath) && !string.IsNullOrEmpty(snapshot.fileHash))
            {
                if (!WorkflowFileStore.TryGetSafeAssetFullPath(snapshot.assetPath, out string missingFullPath) ||
                    File.Exists(missingFullPath))
                    return false;

                if (!WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash, snapshot.assetPath, removeFromStore))
                    return false;
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

            if (!CaptureInverseModified(snapshot, obj, targetTask, out var objectReferences))
                return false;

            // Restore from the content-addressed file store
            if (!string.IsNullOrEmpty(snapshot.fileHash) && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                if (!WorkflowFileStore.RestoreFile(snapshot.fileHash, snapshot.metaFileHash, snapshot.assetPath, removeFromStore))
                    return false;
                // A JSON asset's live object can hold changes its file does not have yet (an unsaved material edit), and the bytes leave
                // them in place. Its JSON is its state, so it is written onto the object and saved, as a merge does.
                if (!string.IsNullOrEmpty(snapshot.originalJson) && !(obj is SceneAsset) && IsJsonAsset(snapshot.assetPath))
                {
                    Undo.RecordObject(obj, undoLabel);
                    EditorJsonUtility.FromJsonOverwrite(snapshot.originalJson, obj);
                    RestoreObjectReferences(obj, snapshot.objectReferencesCaptured,
                        snapshot.objectReferences, snapshot.objectReferencesCaptured ? null : objectReferences);
                    EditorUtility.SetDirty(obj);
                    AssetDatabase.SaveAssetIfDirty(obj);
                }
                return true;
            }

            // A scene object/asset with no file backup falls back to a JSON overwrite
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
        /// Reconstructs a GameObject from snapshot data (primitiveType, transform, components),
        /// and registers the new object with Unity's Undo system.
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

            newGo.transform.position = new Vector3(snapshot.posX, snapshot.posY, snapshot.posZ);
            newGo.transform.rotation = new Quaternion(snapshot.rotX, snapshot.rotY, snapshot.rotZ, snapshot.rotW);
            newGo.transform.localScale = new Vector3(snapshot.scaleX, snapshot.scaleY, snapshot.scaleZ);

            if (snapshot.components != null)
            {
                foreach (var compData in snapshot.components)
                {
                    if (string.IsNullOrEmpty(compData.typeName)) continue;
                    var compType = Type.GetType(compData.typeName);
                    if (compType == null) compType = ComponentSkills.FindComponentType(compData.typeName);
                    if (compType == null) continue;

                    // Skip if the component already exists (e.g. the MeshRenderer a primitive comes with)
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
    }
}

// Producer:Betsy
