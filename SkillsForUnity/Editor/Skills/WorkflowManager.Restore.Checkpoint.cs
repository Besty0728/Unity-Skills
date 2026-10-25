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
        // The changes a restore refused on, when errorCode is WorkflowConflict.
        public List<RestoreConflict> conflicts;
        // How far the journal covers the changes since the restore's cursor: full, partial or unknown.
        public string coverage = "unknown";
        // The position or the step set the restore went back to, as the caller named it.
        public string to;
        // How many steps the restore was asked to cover, whether or not any of them had a snapshot to restore.
        public int matchedSteps;
        public string guard;
        public int restored;
        public int merged;
        public int deleted;
        public int recreated;
        public int failed;
        public int skipped;
        public readonly List<string> failedItems = new List<string>();
        public readonly List<string> skippedItems = new List<string>();
        public string[] scriptChanges = Array.Empty<string>();
        public bool compileTriggered;
        public long refreshStartedUtcTicks;
        public int compilationStartsBefore;
    }

    /// <summary>
    /// What a restore would do, the dryRun payload of the restore skills (design section 7.4). Built without applying anything.
    /// A refusal sets <see cref="error"/> and leaves the rest at its default.
    /// </summary>
    internal sealed class RestorePreview
    {
        public string to;
        [JsonProperty("label", NullValueHandling = NullValueHandling.Ignore)]
        public string label;
        public string agent;
        public int steps;
        public RestorePlanCounts plan = new RestorePlanCounts();
        public List<string> items = new List<string>();
        [JsonProperty("moreItems", NullValueHandling = NullValueHandling.Ignore)]
        public int? moreItems;
        public List<RestoreConflict> conflicts = new List<RestoreConflict>();
        public string coverage = "unknown";
        public List<RestoreUnresolvable> unresolvable = new List<RestoreUnresolvable>();
        public bool mayTriggerReload;
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string error;
        [JsonProperty("errorCode", NullValueHandling = NullValueHandling.Ignore)]
        public string errorCode;
        [JsonProperty("recentCheckpoints", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> recentCheckpoints;

        public static RestorePreview Refused(string to, string agent, RestoreOutcome refusal) => new RestorePreview
        {
            to = to,
            agent = agent,
            error = refusal.error,
            errorCode = refusal.errorCode.ToWireString(),
            recentCheckpoints = refusal.recentCheckpoints
        };
    }

    internal sealed class RestorePlanCounts
    {
        public int modify;
        public int delete;
        public int recreate;
        public int move;
        public int settings;
        public int merge;
    }

    /// <summary>A planned change that cannot run: its scene is not open, or its object is gone and nothing brings it back.</summary>
    internal sealed class RestoreUnresolvable
    {
        public string target;
        public string reason;
    }

    public static partial class WorkflowManager
    {
        private const int MaxListedFailedItems = 20;
        private const int MaxListedRecentCheckpoints = 5;
        private const int MaxFailureTextLength = 200;
        private const int MaxPreviewItems = 20;

        /// <summary>
        /// Puts the scene and the project assets back to the state at a position: the state before the step at that seq.
        /// Per target, the earliest snapshot among the steps from there on holds that state (design section 3.2), so each
        /// target is restored once from it, in phase order. A failing item is reported and the rest still runs. The inverse
        /// snapshots go into a restore step, which is recorded with its guard checkpoint so the restore can be undone.
        /// </summary>
        /// <summary>The steps a restore to <paramref name="to"/> would revert, or an empty list when the position does not resolve.</summary>
        internal static List<WorkflowTask> FindStepsForRestore(string to, bool allAgents, string callerAgent)
        {
            var refusal = ResolveRestoreTo(to, allAgents, callerAgent, out var steps, out _, out _);
            return refusal == null && steps != null ? steps : new List<WorkflowTask>();
        }

        internal static RestoreOutcome RestoreTo(string to, bool allAgents, string callerAgent, string conflicts = null)
        {
            var refusal = ResolveRestoreTo(to, allAgents, callerAgent, out var steps, out long checkpointCursor, out _);
            return refusal ?? RestoreSteps(steps, to, callerAgent, conflicts, checkpointCursor);
        }

        /// <summary>
        /// The steps a restore to a position covers, and the journal cursor of the checkpoint it goes back to. Returns the refusal
        /// instead when the restore cannot run (Play Mode, compiling, or a position that does not resolve).
        /// </summary>
        private static RestoreOutcome ResolveRestoreTo(string to, bool allAgents, string callerAgent, out List<WorkflowTask> steps,
            out long checkpointCursor, out long position)
        {
            steps = null;
            checkpointCursor = 0;
            position = 0;
            var refused = RestoreRefusal();
            if (refused != null)
                return refused;

            if (!TryResolveRestorePosition(to, callerAgent, out long resolved))
            {
                var notFound = RestoreRefused(SkillErrorCode.NotFound,
                    $"Checkpoint or step not found: {to}. Use a c<n> id, an s<n> step, 'last' or a checkpoint label from workflow_list.");
                notFound.recentCheckpoints = RecentCheckpointNames();
                return notFound;
            }

            position = resolved;
            steps = History.tasks
                .Where(task => task.seq >= resolved && (allAgents || task.agent == callerAgent))
                .OrderBy(task => task.seq)
                .ToList();
            checkpointCursor = History.checkpoints.FirstOrDefault(checkpoint => checkpoint.seq == resolved)?.cursor ?? 0;
            return null;
        }

        /// <summary>
        /// The preview of <see cref="RestoreTo"/>: the same position and refusals, then <see cref="PreviewSteps"/> over the steps it covers.
        /// </summary>
        internal static RestorePreview PreviewTo(string to, bool allAgents, string callerAgent)
        {
            var refusal = ResolveRestoreTo(to, allAgents, callerAgent, out var steps, out long checkpointCursor, out long position);
            if (refusal != null)
                return RestorePreview.Refused(to, NormalizeAgent(callerAgent), refusal);

            var preview = PreviewSteps(steps, to, callerAgent, checkpointCursor);
            string label = History.checkpoints.FirstOrDefault(checkpoint => checkpoint.seq == position)?.label;
            preview.label = string.IsNullOrEmpty(label) ? null : label;
            return preview;
        }

        /// <summary>
        /// What <see cref="RestoreSteps"/> would do with <paramref name="steps"/>, applied to nothing: the same planning and conflict scan,
        /// and each work classified the way <see cref="RunRestoreWorks"/> decides it. Writes no log, opens no undo group and takes no guard.
        /// </summary>
        internal static RestorePreview PreviewSteps(List<WorkflowTask> steps, string to, string callerAgent, long checkpointCursor = 0)
        {
            string agent = NormalizeAgent(callerAgent);
            var refusal = RestoreRefusal();
            if (refusal != null)
                return RestorePreview.Refused(to, agent, refusal);

            var preview = new RestorePreview { to = to, agent = agent, steps = steps.Count };
            if (steps.Count == 0)
                return preview;

            var planning = PlanRestore(steps, callerAgent, checkpointCursor, new HashSet<string>(StringComparer.Ordinal));
            preview.coverage = planning.Scan.Coverage;
            preview.conflicts = planning.Scan.Conflicts;

            var runs = new List<RestoreWork>();
            _restoreScopeAliases = planning.GidAliases;
            try
            {
                var doomed = DoomedGameObjects(planning.Works);
                foreach (var work in planning.Works)
                {
                    _sessionIdFallbackAllowed = IsCapturedInCurrentSession(work.Item);
                    string reason = UnresolvableReason(work);
                    if (reason != null)
                        preview.unresolvable.Add(new RestoreUnresolvable { target = PreviewName(work), reason = reason });
                    else if (work.Mode != RestoreWorkMode.Already && !IsSkippedWork(work, doomed))
                        runs.Add(work);
                }
            }
            finally
            {
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
            }

            foreach (var work in runs)
            {
                string action = CountPlannedWork(preview.plan, work);
                if (preview.items.Count < MaxPreviewItems)
                    preview.items.Add($"{PreviewName(work)} ({work.Item.Snapshot.typeName}) {action}");
                preview.mayTriggerReload |= ServerAvailabilityHelper.AffectsScriptDomain(work.PathOverride ?? work.Item.Snapshot.assetPath);
            }
            if (runs.Count > MaxPreviewItems)
                preview.moreItems = runs.Count - MaxPreviewItems;
            return preview;
        }

        /// <summary>The name a preview lists a work by: its setting key, its asset path, or its object name.</summary>
        private static string PreviewName(RestoreWork work)
        {
            var snapshot = work.Item.Snapshot;
            if (snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(snapshot.settingKey))
                return snapshot.settingKey;
            string assetPath = work.PathOverride ?? snapshot.assetPath;
            if (!string.IsNullOrEmpty(assetPath))
                return assetPath;
            return snapshot.objectName ?? string.Empty;
        }

        /// <summary>Counts a planned work under the kind of change it makes, and returns the action word the preview shows.</summary>
        private static string CountPlannedWork(RestorePlanCounts counts, RestoreWork work)
        {
            if (work.Mode == RestoreWorkMode.Merge)
            {
                counts.merge++;
                return work.KeptLeaves.Count > 0 ? $"merge ({work.KeptLeaves.Count} kept)" : "merge";
            }

            switch (work.Item.Snapshot.type)
            {
                case SnapshotType.Created:
                    counts.delete++;
                    return "delete";
                case SnapshotType.Deleted:
                    counts.recreate++;
                    return "recreate";
                case SnapshotType.Moved:
                    counts.move++;
                    return "move";
                case SnapshotType.Setting:
                    counts.settings++;
                    return "set";
                default:
                    counts.modify++;
                    return "modify";
            }
        }

        /// <summary>The reason a work cannot run at all, or null when it can run. Mirrors the checks <see cref="RunRestoreWorks"/> makes first.</summary>
        private static string UnresolvableReason(RestoreWork work)
        {
            var snapshot = work.Item.Snapshot;
            if (IsSceneWork(snapshot) && TryGetClosedScene(snapshot.globalObjectId, out string scenePath))
                return $"scene {scenePath} is not open";

            // A Modified object whose chain deletes it later gets its Deleted item first, which brings it back, so it can run.
            bool objectGone = snapshot.type == SnapshotType.Modified && IsSceneWork(snapshot) &&
                              TryResolveObject(snapshot.globalObjectId, snapshot.entityId, snapshot.objectInstanceId) == null;
            bool chainBringsItBack = work.Chain != null && work.Chain.Any(item => item.Snapshot.type == SnapshotType.Deleted);
            return objectGone && !chainBringsItBack ? "object no longer exists" : null;
        }

        /// <summary>
        /// Restores the targets of <paramref name="steps"/> (in seq order) to their state before the earliest of them. Each target
        /// gets the earliest state among the steps only, so later steps on other targets stay. The caller's undo of one step and of a
        /// position both come through here.
        /// <paramref name="conflicts"/> is null (a conflict refuses the restore), "keep" (conflicting targets are skipped), or the
        /// ids of the conflicts to overwrite. <paramref name="checkpointCursor"/> is the journal cursor of the checkpoint restored to, or 0.
        /// </summary>
        internal static RestoreOutcome RestoreSteps(List<WorkflowTask> steps, string to, string callerAgent, string conflicts = null,
            long checkpointCursor = 0)
        {
            var refused = RestoreRefusal();
            if (refused != null)
                return refused;
            if (!TryParseConflictsArgument(conflicts, out var overwrite, out string conflictsError))
                return RestoreRefused(SkillErrorCode.SemanticInvalid, conflictsError);

            var history = History;
            var outcome = new RestoreOutcome { to = to, matchedSteps = steps.Count };
            if (steps.Count == 0)
                return outcome;

            // A conflict refuses the restore unless conflicts names its ids, so the planning takes the ids the caller passed (none on a refusal).
            var planning = PlanRestore(steps, callerAgent, checkpointCursor, overwrite ?? new HashSet<string>(StringComparer.Ordinal));
            var gidAliases = planning.GidAliases;
            var works = planning.Works;

            var scan = planning.Scan;
            outcome.coverage = scan.Coverage;
            if (scan.Conflicts.Count > 0)
            {
                if (overwrite == null)
                {
                    outcome.error = $"{scan.Conflicts.Count} changes since the checkpoint would be overwritten. Ask the user, then pass conflicts:\"keep\" or the ids to overwrite.";
                    outcome.errorCode = SkillErrorCode.WorkflowConflict;
                    outcome.conflicts = scan.Conflicts;
                    return outcome;
                }
                works = KeepUnconflictedWorks(works, scan.Conflicts, overwrite, outcome);
            }

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
            // The restore's own delayed change events are attributed to its caller, so a later restore does not report them as edits.
            EditorChangeTrackerService.BeginRestExecution();
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
                EditorChangeTrackerService.EndRestExecution();
            }
            restoreTask.cursorEnd = EditorChangeTrackerService.CurrentCursor;
            // The restore step is a step like any other: its inverses get the state they left behind, for a later undo of it.
            StampAfterStates(restoreTask);
            PruneUnchangedSnapshots(restoreTask);
            StampAssetFingerprints(restoreTask);

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

            // An object of a never-saved scene has no persistent id, so it is aliased by entity id, which holds for this session only.
            string session = CurrentIdSession();
            foreach (var entry in scope.EntityIdEntries)
            {
                if (entry.Value == null || !IsUnsavedSceneGlobalObjectId(GlobalObjectId.GetGlobalObjectIdSlow(entry.Value).ToString()))
                    continue;
                string newEntityId = UnityObjectIdUtility.GetEntityId(entry.Value);
                if (!string.IsNullOrEmpty(newEntityId) && newEntityId != entry.Key)
                    pairs.Add(new[] { EntityAliasKey(session, entry.Key), EntityAliasKey(session, newEntityId) });
            }
            return pairs;
        }

        private static string EntityAliasKey(string idSession, string entityId) => "entity:" + idSession + ":" + entityId;

        /// <summary>The entity id a restore's alias table maps an entity id of this session to, or the id itself.</summary>
        private static string ResolveEntityAlias(Dictionary<string, string> aliases, string entityId)
        {
            string key = EntityAliasKey(CurrentIdSession(), entityId);
            string resolved = ResolveGidAlias(aliases, key);
            return resolved == key ? entityId : resolved.Substring(resolved.LastIndexOf(':') + 1);
        }

        private static RestoreOutcome RestoreRefused(SkillErrorCode code, string message) =>
            new RestoreOutcome { error = message, errorCode = code };

        /// <summary>The refusal a restore gets while Play Mode runs or Unity compiles, or null when it may run.</summary>
        private static RestoreOutcome RestoreRefusal()
        {
            if (EditorApplication.isPlaying)
                return RestoreRefused(SkillErrorCode.SemanticInvalid, "Cannot restore while Play Mode is running. Exit Play Mode first.");
            if (ServerAvailabilityHelper.IsCompilationInProgress())
                return RestoreRefused(SkillErrorCode.Compiling, "Unity is compiling or updating assets. Retry the restore after it finishes.");
            return null;
        }

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

        /// <summary>
        /// The planning half of a restore, with no side effects: the works in run order and the conflicts they would overwrite.
        /// Shared by a restore and its preview.
        /// </summary>
        private static RestorePlanning PlanRestore(List<WorkflowTask> steps, string callerAgent, long checkpointCursor, ISet<string> overwriteIds)
        {
            var gidAliases = RestoreGidAliases(History.restores);
            var works = PlanRestoreWork(BuildRestoreChains(steps, gidAliases));
            var scan = ScanRestoreConflicts(steps, works, gidAliases, checkpointCursor, callerAgent);
            var planning = new RestorePlanning { GidAliases = gidAliases, Works = works, Scan = scan };
            AdjudicateWorks(planning, overwriteIds);
            return planning;
        }

        private sealed class RestoreItem
        {
            public WorkflowTask Step;
            public ObjectSnapshot Snapshot;
            public int Index;   // Capture order across the steps
        }

        /// <summary>What planning a restore yields: the aliases its targets resolve through, its works in run order, and the conflicts it would overwrite.</summary>
        private sealed class RestorePlanning
        {
            public Dictionary<string, string> GidAliases;
            public List<RestoreWork> Works;
            public RestoreConflictScan Scan;
        }

        private sealed class RestoreWork
        {
            public int Order;
            public RestoreItem Item;
            // The key of the live target the work writes, for conflicts; null when the target does not resolve.
            public string TargetKey;
            // The target as the conflicts and the preview name it.
            public string TargetDisplay;
            // How the work runs once its target is judged against its after-state (see AdjudicateWorks).
            public RestoreWorkMode Mode = RestoreWorkMode.Plain;
            // A Merge work's result: the JSON and references to apply, and the conflicting leaves it keeps.
            public string MergedJson;
            public List<ObjectReferenceData> MergedReferences;
            public List<RestoreConflict> KeptLeaves = new List<RestoreConflict>();
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
            int index = 0;
            foreach (var step in steps)
            {
                foreach (var snapshot in step.snapshots)
                {
                    if (snapshot == null)
                        continue;
                    string capturedIn = string.IsNullOrEmpty(snapshot.idSession) ? step.idSession : snapshot.idSession;
                    string key = RestoreChainKey(snapshot, capturedIn, gidAliases, pathKeys) ?? "unkeyed:" + (++unkeyed);
                    if (!chainsByKey.TryGetValue(key, out var chain))
                    {
                        chain = new List<RestoreItem>();
                        chainsByKey.Add(key, chain);
                        chains.Add(chain);
                    }
                    chain.Add(new RestoreItem { Step = step, Snapshot = snapshot, Index = index++ });
                }
            }
            return chains;
        }

        /// <summary>The key of a snapshot's target, or null when it names none. Asset paths are tracked through moves in step order.</summary>
        private static string RestoreChainKey(ObjectSnapshot snapshot, string capturedIdSession,
            Dictionary<string, string> gidAliases, Dictionary<string, string> pathKeys)
        {
            if (snapshot.type == SnapshotType.Setting && !string.IsNullOrEmpty(snapshot.settingKey))
                return "setting:" + snapshot.settingKey;

            // A never-saved scene gives every object the same GlobalObjectId, so its objects are told apart by entity id.
            string gid = !IsUnsavedSceneGlobalObjectId(snapshot.globalObjectId)
                ? ResolveGidAlias(gidAliases, snapshot.globalObjectId)
                : string.IsNullOrEmpty(snapshot.entityId) ? null
                : ResolveGidAlias(gidAliases, EntityAliasKey(capturedIdSession, snapshot.entityId));
            // Every asset snapshot is tracked by path, so one without a GlobalObjectId (a deleted asset) still joins its chain.
            bool moved = snapshot.type == SnapshotType.Moved;
            string path = moved ? snapshot.previousAssetPath : snapshot.assetPath;
            string pathKey = null;
            if (!string.IsNullOrEmpty(path))
            {
                pathKey = pathKeys.TryGetValue(path, out string tracked) ? tracked : !string.IsNullOrEmpty(gid) ? gid : "asset:" + path;
                if (moved && !string.IsNullOrEmpty(snapshot.assetPath))
                {
                    pathKeys.Remove(path);
                    pathKeys[snapshot.assetPath] = pathKey;
                }
            }
            return !string.IsNullOrEmpty(gid) ? gid : pathKey;
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
            // Within a phase the newest capture goes first, as a step's own undo always did: a component added after another
            // (a ParticleSystem's renderer) is removed first, and a parent deleted after its child comes back before it.
            return works.OrderBy(work => work.Order).ThenByDescending(work => work.Item.Index).ToList();
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

            // A Deleted item at or after the last move means the asset is absent at its current path. Its recreate lands at the start
            // path (PathOverride), so there is nothing to move back.
            var representative = chain.FirstOrDefault(item => item.Snapshot.type != SnapshotType.Moved);
            int lastMove = chain.FindLastIndex(item => item.Snapshot.type == SnapshotType.Moved);
            bool absentAtCurrentPath = representative?.Snapshot.type == SnapshotType.Deleted ||
                                       chain.Skip(lastMove + 1).Any(item => item.Snapshot.type == SnapshotType.Deleted);
            if (!absentAtCurrentPath && !string.IsNullOrEmpty(startPath) && !string.IsNullOrEmpty(currentPath) && currentPath != startPath)
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
                    Item = new RestoreItem { Step = chain[0].Step, Snapshot = move, Index = chain[0].Index },
                    Chain = chain
                });
            }

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
            var doomed = DoomedGameObjects(works);
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

                if (work.Mode == RestoreWorkMode.Already || IsSkippedWork(work, doomed))
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

        /// <summary>
        /// The GameObjects a restore's Created items name. A created GameObject is removed with its parent or any ancestor being
        /// removed, so the works under one are skipped rather than failed.
        /// </summary>
        private static HashSet<GameObject> DoomedGameObjects(List<RestoreWork> works)
        {
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
            return doomed;
        }

        /// <summary>
        /// Whether a work needs no run: a Deleted item whose target is back already (an earlier restore or an undo may have brought it back),
        /// or a Created item that is already absent or inside a doomed GameObject. Caller sets the session fallback for the work's item.
        /// </summary>
        private static bool IsSkippedWork(RestoreWork work, HashSet<GameObject> doomed)
        {
            var snapshot = work.Item.Snapshot;
            if (work.RecreateOnly && !IsAlreadyAbsent(snapshot, work.PathOverride))
                return true;
            return snapshot.type == SnapshotType.Created &&
                   (IsAlreadyAbsent(snapshot) || IsCoveredByDoomedAncestor(snapshot, doomed));
        }

        private static void RunRestoreWork(RestoreWork work, WorkflowTask restoreTask, RestoreOutcome outcome)
        {
            if (work.Mode == RestoreWorkMode.Merge)
            {
                RunMergeWork(work, restoreTask, outcome);
                return;
            }

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
                item = new RestoreItem { Step = item.Step, Snapshot = copy, Index = item.Index };
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

        /// <summary>
        /// A created target that no longer exists is already at the position's state, so there is nothing to undo. An asset is
        /// checked at <paramref name="pathOverride"/> when the work writes it there, else at the path its snapshot names.
        /// </summary>
        private static bool IsAlreadyAbsent(ObjectSnapshot created, string pathOverride = null)
        {
            string assetPath = pathOverride ?? created.assetPath;
            if (string.IsNullOrEmpty(assetPath))
                return TryResolveObject(created.globalObjectId, created.entityId, created.objectInstanceId) == null;
            if (!WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath))
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
