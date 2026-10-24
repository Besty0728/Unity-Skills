using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// What a checkpoint restore did: the guard it took, the counts per kind, and the script-domain paths it reported.
    /// A refusal or a position that does not resolve sets <see cref="error"/> and leaves everything else at its default.
    /// </summary>
    internal sealed class RestoreOutcome
    {
        public string error;
        public SkillErrorCode errorCode;
        public List<string> recentCheckpoints;
        public string guard;
        public int restored;
        public int deleted;
        public int recreated;
        public int failed;
        public int skipped;
        public readonly List<string> failedItems = new List<string>();
        public string[] scriptChanges = Array.Empty<string>();
        public bool compileTriggered;
        public long refreshStartedUtcTicks;
        public int compilationStartsBefore;
    }

    public static partial class WorkflowManager
    {
        private const int MaxListedFailedItems = 20;
        private const int MaxListedRecentCheckpoints = 5;
        private const int MaxFailureTextLength = 200;

        /// <summary>
        /// Puts the scene and the project assets back to the state at a position: the state before the step at that seq.
        /// Per target, the earliest snapshot among the steps from there on holds that state (design section 3.2), so each
        /// target is restored once from it, in phase order. A failing item is reported and the rest still runs. The inverse
        /// snapshots go into a restore step, which is recorded with its guard checkpoint so the restore can be undone.
        /// </summary>
        internal static RestoreOutcome RestoreTo(string to, bool allAgents, string callerAgent)
        {
            if (EditorApplication.isPlaying)
                return RestoreRefused(SkillErrorCode.SemanticInvalid, "Cannot restore while Play Mode is running. Exit Play Mode first.");
            if (ServerAvailabilityHelper.IsCompilationInProgress())
                return RestoreRefused(SkillErrorCode.Compiling, "Unity is compiling or updating assets. Retry the restore after it finishes.");

            var history = History;
            if (!TryResolveRestorePosition(to, callerAgent, out long position))
            {
                var notFound = RestoreRefused(SkillErrorCode.NotFound,
                    $"Checkpoint or step not found: {to}. Use a c<n> id, an s<n> step, 'last' or a checkpoint label from workflow_list.");
                notFound.recentCheckpoints = RecentCheckpointNames();
                return notFound;
            }

            var steps = history.tasks
                .Where(task => task.seq >= position && (allAgents || task.agent == callerAgent))
                .OrderBy(task => task.seq)
                .ToList();
            var outcome = new RestoreOutcome();
            if (steps.Count == 0)
                return outcome;

            var gidAliases = RestoreGidAliases(history.restores);
            var works = PlanRestoreWork(BuildRestoreChains(steps, gidAliases));

            outcome.guard = AddCheckpoint(callerAgent, WorkflowCheckpoint.KindGuard, "before restore to " + to);
            // The guard took the position the restore step gets, so both share one seq.
            var restoreTask = new WorkflowTask
            {
                id = Guid.NewGuid().ToString(),
                tag = "workflow_restore",
                description = "Restore to " + to,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                agent = callerAgent,
                target = to,
                idSession = CurrentIdSession(),
                snapshots = new List<ObjectSnapshot>()
            };
            restoreTask.seq = history.AllocateSeq();
            restoreTask.group = "g" + restoreTask.seq;

            var aliasPairs = new List<string[]>();
            outcome.compilationStartsBefore = CompilationResultService.CompilationStartCount;
            outcome.refreshStartedUtcTicks = DateTime.UtcNow.Ticks;
            restoreTask.cursorStart = EditorChangeTrackerService.CurrentCursor;
            var scope = new RestoredObjectMap();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Workflow: restore to " + to);
            int undoGroup = Undo.GetCurrentGroup();
            _restoreScopeObjects = scope;
            _restoreScopeAliases = gidAliases;
            try
            {
                // One capture for all asset work, so the whole restore triggers at most one compile.
                using (var capture = ScriptDomainImportCapture.Begin())
                {
                    bool assetWork = RunRestoreWorks(works, restoreTask, outcome);
                    if (assetWork)
                        AssetDatabase.Refresh();
                    outcome.scriptChanges = capture.Paths;
                }
                aliasPairs.AddRange(RecreatedObjectAliases(scope));
            }
            finally
            {
                _restoreScopeObjects = null;
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
                Undo.CollapseUndoOperations(undoGroup);
            }
            restoreTask.cursorEnd = EditorChangeTrackerService.CurrentCursor;

            outcome.compileTriggered = outcome.scriptChanges.Length > 0 ||
                                       CompilationResultService.CompilationStartCount != outcome.compilationStartsBefore ||
                                       ServerAvailabilityHelper.IsCompilationInProgress();

            var batch = new LogBatch();
            if (restoreTask.snapshots.Count > 0)
                AddTaskRecord(batch, WorkflowStore.TasksStack, restoreTask, isNew: true);
            // Written with the task in one append, before anything else can reload the scripts.
            var record = new WorkflowRestoreRecord
            {
                seq = restoreTask.seq,
                to = to,
                guard = outcome.guard,
                agent = callerAgent,
                steps = steps.Select(step => step.seq).ToList(),
                aliases = aliasPairs
            };
            batch.Lines.Add(WorkflowStore.BuildRestoreLine(record));
            Commit(batch);
            history.restores.Add(record);
            if (restoreTask.snapshots.Count > 0)
                history.tasks.Add(restoreTask);
            return outcome;
        }

        /// <summary>
        /// Every object the restore recreated, from its old id to the id it has now, so a later step that names the old id
        /// still chains to it. Ids that do not persist (an unsaved scene) are not recorded.
        /// </summary>
        private static List<string[]> RecreatedObjectAliases(RestoredObjectMap scope)
        {
            var pairs = new List<string[]>();
            foreach (var entry in scope.GlobalIdEntries)
            {
                if (entry.Value == null)
                    continue;
                string newGid = GlobalObjectId.GetGlobalObjectIdSlow(entry.Value).ToString();
                if (string.IsNullOrEmpty(newGid) || newGid == entry.Key || IsUnsavedSceneGlobalObjectId(newGid))
                    continue;
                pairs.Add(new[] { entry.Key, newGid });
            }
            return pairs;
        }

        private static RestoreOutcome RestoreRefused(SkillErrorCode code, string message) =>
            new RestoreOutcome { error = message, errorCode = code };

        /// <summary>
        /// A position is "s&lt;n&gt;" (the state before step n), "last" (the newest checkpoint the caller took), or a checkpoint
        /// id or label (see <see cref="FindCheckpoint"/>).
        /// </summary>
        private static bool TryResolveRestorePosition(string to, string callerAgent, out long position)
        {
            position = 0;
            if (string.Equals(to, "last", StringComparison.OrdinalIgnoreCase))
            {
                var newest = History.checkpoints
                    .Where(checkpoint => checkpoint.agent == callerAgent)
                    .OrderByDescending(checkpoint => checkpoint.seq)
                    .FirstOrDefault();
                if (newest == null)
                    return false;
                position = newest.seq;
                return true;
            }

            if (to.Length > 1 && (to[0] == 's' || to[0] == 'S') && long.TryParse(to.Substring(1), out long step) && step > 0)
            {
                position = step;
                return true;
            }

            var checkpoint = FindCheckpoint(to);
            if (checkpoint == null)
                return false;
            position = checkpoint.seq;
            return true;
        }

        private static List<string> RecentCheckpointNames() =>
            History.checkpoints
                .OrderByDescending(checkpoint => checkpoint.seq)
                .Take(MaxListedRecentCheckpoints)
                .Select(checkpoint => string.IsNullOrEmpty(checkpoint.label)
                    ? checkpoint.id
                    : checkpoint.id + " \"" + checkpoint.label + "\"")
                .ToList();

        #region Planning

        private sealed class RestoreItem
        {
            public WorkflowTask Step;
            public ObjectSnapshot Snapshot;
        }

        private sealed class RestoreWork
        {
            public int Order;
            public RestoreItem Item;
            // The target's whole chain, for the Deleted item a Modified one may need recreated first.
            public List<RestoreItem> Chain;
            // The asset path the target is at before the restore, when it is not the path its snapshot names.
            public string PathOverride;
            // A Deleted item that only brings its object back so an earlier Modified item can apply; skipped when it already resolves.
            public bool RecreateOnly;
        }

        /// <summary>
        /// Groups the snapshots of the steps by target: the global object id after alias mapping, else the asset path (followed
        /// through moves), else the setting key. Chains come out in the order their first snapshot was captured.
        /// </summary>
        private static List<List<RestoreItem>> BuildRestoreChains(List<WorkflowTask> steps, Dictionary<string, string> gidAliases)
        {
            var pathKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var chainsByKey = new Dictionary<string, List<RestoreItem>>(StringComparer.Ordinal);
            var chains = new List<List<RestoreItem>>();
            int unkeyed = 0;
            foreach (var step in steps)
            {
                foreach (var snapshot in step.snapshots)
                {
                    if (snapshot == null)
                        continue;
                    string key = RestoreChainKey(snapshot, gidAliases, pathKeys) ?? "unkeyed:" + (++unkeyed);
                    if (!chainsByKey.TryGetValue(key, out var chain))
                    {
                        chain = new List<RestoreItem>();
                        chainsByKey.Add(key, chain);
                        chains.Add(chain);
                    }
                    chain.Add(new RestoreItem { Step = step, Snapshot = snapshot });
                }
            }
            return chains;
        }

        /// <summary>The key of a snapshot's target, or null when it names none. Asset paths are tracked through moves in step order.</summary>
        private static string RestoreChainKey(ObjectSnapshot snapshot, Dictionary<string, string> gidAliases,
            Dictionary<string, string> pathKeys)
        {
            if (snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(snapshot.settingKey))
                return "setting:" + snapshot.settingKey;

            string gid = ResolveGidAlias(gidAliases, snapshot.globalObjectId);
            if (!string.IsNullOrEmpty(gid))
                return gid;

            bool moved = snapshot.type == SnapshotType.Moved;
            string path = moved ? snapshot.previousAssetPath : snapshot.assetPath;
            if (string.IsNullOrEmpty(path))
                return null;

            string key = pathKeys.TryGetValue(path, out string tracked) ? tracked : "asset:" + path;
            if (moved && !string.IsNullOrEmpty(snapshot.assetPath))
            {
                pathKeys.Remove(path);
                pathKeys[snapshot.assetPath] = key;
            }
            return key;
        }

        private static Dictionary<string, string> RestoreGidAliases(List<WorkflowRestoreRecord> restores)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var restore in restores.OrderBy(restore => restore.seq))
            {
                if (restore.aliases == null)
                    continue;
                foreach (var pair in restore.aliases)
                {
                    if (pair != null && pair.Length == 2 && pair[0] != pair[1])
                        aliases[pair[0]] = pair[1];
                }
            }
            return aliases;
        }

        private static string ResolveGidAlias(Dictionary<string, string> aliases, string globalObjectId)
        {
            // Bounded: each hop follows a recreate, so a chain this long only happens if the records are corrupt.
            for (int hop = 0; hop < 64 && !string.IsNullOrEmpty(globalObjectId) && aliases.TryGetValue(globalObjectId, out string next); hop++)
                globalObjectId = next;
            return globalObjectId;
        }

        /// <summary>
        /// Each chain yields one piece of work for its earliest state-bearing snapshot. An asset chain also gets a move back when
        /// the asset is no longer at the path it had at the position. Work runs in the design's phase order (section 6.2).
        /// Design 3.2 rows, by the earliest item:
        /// Modified (target present) -> apply its state; Modified (target deleted later in the chain) -> the Deleted item first, then
        /// apply; Deleted -> recreate only when the target is absent (skipped otherwise), then the first later Modified applies;
        /// Created -> remove when present; Moved -> move back; Setting -> apply.
        /// </summary>
        private static List<RestoreWork> PlanRestoreWork(List<List<RestoreItem>> chains)
        {
            var works = new List<RestoreWork>();
            foreach (var chain in chains)
            {
                if (chain.Any(item => !string.IsNullOrEmpty(item.Snapshot.assetPath)))
                    PlanAssetChain(chain, works);
                else
                    PlanSceneChain(chain, works);
            }
            // OrderBy is stable, so equal phases keep their capture order.
            return works.OrderBy(work => work.Order).ToList();
        }

        private static void PlanSceneChain(List<RestoreItem> chain, List<RestoreWork> works)
        {
            if (chain[0].Snapshot.type == SnapshotType.Deleted)
            {
                PlanDeletedEarliest(chain, works);
                return;
            }
            works.Add(new RestoreWork { Order = SceneRestoreOrder(chain[0].Snapshot), Item = chain[0], Chain = chain });

            // A Modified object deleted later in its chain must exist before the Modified item applies, so the deletion is
            // undone in its own phase, which runs first.
            if (chain[0].Snapshot.type != SnapshotType.Modified)
                return;
            var deleted = chain.FirstOrDefault(item => item.Snapshot.type == SnapshotType.Deleted);
            if (deleted != null)
                works.Add(new RestoreWork { Order = SceneRestoreOrder(deleted.Snapshot), Item = deleted, Chain = chain, RecreateOnly = true });
        }

        /// <summary>
        /// Earliest item Deleted: the deletion is applied only when its target is absent, since a restore or an undo may have brought
        /// the object back already. The first later Modified applies at its normal phase; its state is the one the object had right
        /// after it came back, which is the Deleted item's data, so applying it after a fresh recreate changes nothing.
        /// </summary>
        private static void PlanDeletedEarliest(List<RestoreItem> chain, List<RestoreWork> works)
        {
            works.Add(new RestoreWork { Order = SceneRestoreOrder(chain[0].Snapshot), Item = chain[0], Chain = chain, RecreateOnly = true });
            var modified = chain.FirstOrDefault(item => item.Snapshot.type == SnapshotType.Modified);
            if (modified != null)
                works.Add(new RestoreWork { Order = SceneRestoreOrder(modified.Snapshot), Item = modified, Chain = chain });
        }

        private static void PlanAssetChain(List<RestoreItem> chain, List<RestoreWork> works)
        {
            var first = chain[0].Snapshot;
            string startPath = first.type == SnapshotType.Moved ? first.previousAssetPath : first.assetPath;
            string currentPath = startPath;
            foreach (var item in chain)
            {
                if (item.Snapshot.type == SnapshotType.Moved)
                    currentPath = item.Snapshot.assetPath;
            }

            if (!string.IsNullOrEmpty(startPath) && !string.IsNullOrEmpty(currentPath) && currentPath != startPath)
            {
                var move = new ObjectSnapshot
                {
                    globalObjectId = first.globalObjectId,
                    objectName = first.objectName,
                    typeName = first.typeName,
                    type = SnapshotType.Moved,
                    assetPath = currentPath,
                    previousAssetPath = startPath
                };
                works.Add(new RestoreWork
                {
                    Order = 10,
                    Item = new RestoreItem { Step = chain[0].Step, Snapshot = move },
                    Chain = chain
                });
            }

            var representative = chain.FirstOrDefault(item => item.Snapshot.type != SnapshotType.Moved);
            if (representative == null)
                return;
            var snapshot = representative.Snapshot;
            string pathOverride = !string.IsNullOrEmpty(startPath) && snapshot.assetPath != startPath ? startPath : null;
            works.Add(new RestoreWork
            {
                Order = AssetRestoreOrder(snapshot),
                Item = representative,
                Chain = chain,
                PathOverride = pathOverride,
                RecreateOnly = snapshot.type == SnapshotType.Deleted
            });
            if (snapshot.type != SnapshotType.Deleted)
                return;
            var modified = chain.FirstOrDefault(item => item.Snapshot.type == SnapshotType.Modified);
            if (modified == null)
                return;
            string modifiedOverride = !string.IsNullOrEmpty(startPath) && modified.Snapshot.assetPath != startPath ? startPath : null;
            works.Add(new RestoreWork
            {
                Order = AssetRestoreOrder(modified.Snapshot),
                Item = modified,
                Chain = chain,
                PathOverride = modifiedOverride
            });
        }

        private static int AssetRestoreOrder(ObjectSnapshot snapshot)
        {
            switch (snapshot.type)
            {
                case SnapshotType.Deleted:
                    return 20;
                case SnapshotType.Modified:
                    return ServerAvailabilityHelper.AffectsScriptDomain(snapshot.assetPath) ? 31 : 30;
                default:
                    return 80;
            }
        }

        private static int SceneRestoreOrder(ObjectSnapshot snapshot)
        {
            switch (snapshot.type)
            {
                case SnapshotType.Deleted:
                    return string.IsNullOrEmpty(snapshot.componentTypeName) ? 40 : 50;
                case SnapshotType.Created:
                    // Components before their GameObjects: a GameObject's removal would take its components with it.
                    return string.IsNullOrEmpty(snapshot.componentTypeName) ? 71 : 70;
                case SnapshotType.Setting:
                    return 90;
                default:
                    return 60;
            }
        }

        #endregion

        #region Execution

        /// <summary>Runs the planned work in order, and returns whether any of it touched an asset.</summary>
        private static bool RunRestoreWorks(List<RestoreWork> works, WorkflowTask restoreTask, RestoreOutcome outcome)
        {
            // A created GameObject is removed with its parent or any ancestor being removed, so it is skipped rather than failed.
            var doomed = new HashSet<GameObject>();
            foreach (var work in works)
            {
                var snapshot = work.Item.Snapshot;
                if (snapshot.type != SnapshotType.Created || !string.IsNullOrEmpty(snapshot.componentTypeName) ||
                    !string.IsNullOrEmpty(snapshot.assetPath))
                    continue;
                _sessionIdFallbackAllowed = IsCapturedInCurrentSession(work.Item);
                if (TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId) is GameObject go)
                    doomed.Add(go);
            }

            bool assetWork = false;
            foreach (var work in works)
            {
                var snapshot = work.Item.Snapshot;
                _sessionIdFallbackAllowed = IsCapturedInCurrentSession(work.Item);
                if (IsSceneWork(snapshot) && TryGetClosedScene(snapshot.globalObjectId, out string scenePath))
                {
                    RecordRestoreFailure(outcome, snapshot, $"scene {scenePath} is not open; open it and restore again");
                    continue;
                }

                // A Deleted item only applies when its target is absent: an earlier restore or an undo may have brought it back.
                if (work.RecreateOnly && !IsAlreadyAbsent(snapshot))
                {
                    outcome.skipped++;
                    continue;
                }
                else if (snapshot.type == SnapshotType.Created &&
                         (IsAlreadyAbsent(snapshot) || IsCoveredByDoomedAncestor(snapshot, doomed)))
                {
                    outcome.skipped++;
                    continue;
                }

                if (!string.IsNullOrEmpty(snapshot.assetPath) || work.PathOverride != null)
                    assetWork = true;
                RunRestoreWork(work, restoreTask, outcome);
            }
            return assetWork;
        }

        private static void RunRestoreWork(RestoreWork work, WorkflowTask restoreTask, RestoreOutcome outcome)
        {
            var item = work.Item;
            if (work.PathOverride != null)
            {
                var copy = CopyHydrated(item.Snapshot, out string copyError);
                if (copy == null)
                {
                    RecordRestoreFailure(outcome, item.Snapshot, copyError);
                    return;
                }
                copy.assetPath = work.PathOverride;
                item = new RestoreItem { Step = item.Step, Snapshot = copy };
            }

            var result = UndoRestoreItem(item, restoreTask);
            if (result.success)
                Tally(outcome, item.Snapshot.type);
            else
                RecordRestoreFailure(outcome, item.Snapshot, result.error);
        }

        private static SnapshotUndoResult UndoRestoreItem(RestoreItem item, WorkflowTask restoreTask)
        {
            _sessionIdFallbackAllowed = IsCapturedInCurrentSession(item);
            return UndoSnapshot(item.Snapshot, restoreTask);
        }

        private static bool IsSceneWork(ObjectSnapshot snapshot) =>
            string.IsNullOrEmpty(snapshot.assetPath) && snapshot.type != SnapshotType.Setting;

        /// <summary>
        /// The scene path of a scene object whose scene is not open: such an object cannot be resolved, and a recreate would land in
        /// the active scene instead, so the restore reports it rather than guess.
        /// </summary>
        private static bool TryGetClosedScene(string globalObjectId, out string scenePath)
        {
            scenePath = null;
            if (string.IsNullOrEmpty(globalObjectId) || IsUnsavedSceneGlobalObjectId(globalObjectId) ||
                !GlobalObjectId.TryParse(globalObjectId, out GlobalObjectId gid) || gid.identifierType != 2)
                return false;
            string path = AssetDatabase.GUIDToAssetPath(gid.assetGUID.ToString());
            if (string.IsNullOrEmpty(path) || SceneManager.GetSceneByPath(path).isLoaded)
                return false;
            scenePath = path;
            return true;
        }

        private static bool IsCapturedInCurrentSession(RestoreItem item)
        {
            string capturedIn = string.IsNullOrEmpty(item.Snapshot.idSession) ? item.Step.idSession : item.Snapshot.idSession;
            return string.IsNullOrEmpty(capturedIn) || capturedIn == CurrentIdSession();
        }

        /// <summary>A created target that no longer exists is already at the position's state, so there is nothing to undo.</summary>
        private static bool IsAlreadyAbsent(ObjectSnapshot created)
        {
            if (string.IsNullOrEmpty(created.assetPath))
                return TryResolveObject(created.globalObjectId, created.entityId, created.objectInstanceId) == null;
            if (!WorkflowFileStore.TryGetSafeAssetFullPath(created.assetPath, out string fullPath))
                return false;
            return !File.Exists(fullPath) && !Directory.Exists(fullPath);
        }

        private static bool IsCoveredByDoomedAncestor(ObjectSnapshot created, HashSet<GameObject> doomed)
        {
            if (doomed.Count == 0)
                return false;

            bool isComponent = !string.IsNullOrEmpty(created.componentTypeName);
            var owner = isComponent
                ? TryResolveObject(created.parentGameObjectId, created.parentGameObjectEntityId, created.parentGameObjectInstanceId) as GameObject
                : TryResolveObject(created.globalObjectId, created.entityId, created.objectInstanceId) as GameObject;
            if (owner == null)
                return false;

            // A component goes with its own GameObject; a GameObject only with a proper ancestor.
            for (var transform = isComponent ? owner.transform : owner.transform.parent; transform != null; transform = transform.parent)
            {
                if (doomed.Contains(transform.gameObject))
                    return true;
            }
            return false;
        }

        /// <summary>A copy of the snapshot with its payload read back, so the copy can be edited without touching the history.</summary>
        private static ObjectSnapshot CopyHydrated(ObjectSnapshot source, out string error)
        {
            if (!TryHydrate(source, out error))
                return null;
            return JsonConvert.DeserializeObject<ObjectSnapshot>(
                JsonConvert.SerializeObject(source, SkillsCommon.JsonSettings), SkillsCommon.JsonSettings);
        }

        private static void Tally(RestoreOutcome outcome, SnapshotType type)
        {
            switch (type)
            {
                case SnapshotType.Created:
                    outcome.deleted++;
                    break;
                case SnapshotType.Deleted:
                    outcome.recreated++;
                    break;
                default:
                    outcome.restored++;
                    break;
            }
        }

        private static void RecordRestoreFailure(RestoreOutcome outcome, ObjectSnapshot snapshot, string error)
        {
            outcome.failed++;
            if (outcome.failedItems.Count >= MaxListedFailedItems)
                return;

            string name = !string.IsNullOrEmpty(snapshot.objectName) ? snapshot.objectName
                : !string.IsNullOrEmpty(snapshot.assetPath) ? snapshot.assetPath
                : snapshot.globalObjectId ?? "";
            string reason = string.IsNullOrEmpty(error) ? "unknown failure" : error;
            if (reason.Length > MaxFailureTextLength)
                reason = reason.Substring(0, MaxFailureTextLength);
            outcome.failedItems.Add($"{name} ({snapshot.typeName}): {reason}");
        }

        #endregion
    }
}

// Producer:Betsy
