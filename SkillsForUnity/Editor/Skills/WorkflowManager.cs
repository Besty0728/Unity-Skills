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
        private static WorkflowHistoryData _history;
        private static WorkflowTask _currentTask;
        private static string _currentSessionId;

        // The label the next non-empty task records as an explicit checkpoint at its seq; cleared by that task, or by an empty one.
        private static string _pendingCheckpointLabel;

        // While a batch runs, its recorded tasks share one group, named by the first of them (see BeginBatchGroup).
        private static bool _batchGroupOpen;
        private static string _batchGroup;

        // Set when a log segment cannot be read, or its blob references rebuilt. In that state, the history in hand
        // references fewer blobs than the store actually holds, so reclaiming "unreferenced" entries would delete live
        // backups; better to leak than to pay that cost.
        // volatile: SkillsHttpServer's /health fast path reads IsHistoryRecoveryMode on the HTTP thread,
        // while writes always happen on the main thread (LoadHistory / ClearHistory / maintenance).
        private static volatile bool _historyRecoveryMode;

        // Sealing, eviction and GC read whole segments and enumerate the blob store, so they run in one deferred
        // maintenance pass, never inside a write. The generation drops a pass queued before a reset or a clear.
        private static bool _maintenanceScheduled;
        private static bool _maintenanceSizeCheck;
        private static int _maintenanceGeneration;
        internal static int CleanupRunsForTests;
        internal static long CapacityBytesForTests;

        // GlobalObjectId of a persistent asset is fixed by its GUID and file id, so it is computed once per session.
        // Cleared whenever assets are deleted or moved, and bounded so a long session cannot grow it without limit.
        private const int MaxPersistentGidCacheEntries = 4096;
        private static readonly Dictionary<string, string> _persistentGidCache = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static int GlobalObjectIdComputationsForTests;

        // A new run of writes starts when an agent's next tracked write comes after its idle gap (WorkflowCheckpointConfig).
        private const string AgentActivitySessionKey = "UnitySkills_WorkflowAgentActivity";
        private const string IdSessionKey = "UnitySkills_WorkflowIdSession";
        private static Dictionary<string, AgentActivity> _agentActivity;
        private static string _idSession;
        internal static Func<long> NowUnixMsForTests;
        internal static EditorChangeTrackerService.AgentLookup AgentLookupForTests;

        // Tasks that ended before the process resolver answered for their connection; bound at the next save or list. The
        // resolver keeps an answer for 60 s, so one that turns up later could only belong to a reused port.
        private static readonly List<WorkflowTask> _pendingAgentTasks = new List<WorkflowTask>();
        private const long PendingAgentWindowMs = 60_000;

        // Set by ResetStateForTests: from then on the activity map lives in memory only, so tests never erase or overwrite
        // the editor's real SessionState value, which is read again after the next domain reload.
        private static bool _agentActivityDetached;

        private sealed class AgentActivity
        {
            public long lastCallMs;
            public string group;
        }

        // Instance and entity ids are only meaningful in the editor session that issued them; restoring a task captured in
        // an earlier session resolves objects by GlobalObjectId alone, so a recycled id can never select an unrelated object.
        private static bool _sessionIdFallbackAllowed = true;

        /// <summary>
        /// Points the whole store (log, head and blobs, with the legacy files beside it) at <paramref name="root"/>, null =
        /// the project's Library/UnitySkills/workflow, and resets the in-memory state.
        /// </summary>
        internal static void OverrideRootForTests(string root)
        {
            WorkflowStore.SetRootOverrideForTests(root);
            ResetStateForTests();
        }

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

        /// <summary>
        /// True when a history log segment failed to load this session, suspending eviction and blob GC as a
        /// result. Cleared by ClearHistory.
        /// </summary>
        public static bool IsHistoryRecoveryMode => _historyRecoveryMode;

        internal static event Action<GameObject, Type> ComponentTopologyChanged;

        public static WorkflowTask BeginTask(string tag, string description)
        {
            if (_currentTask != null)
                EndTask(); // Automatically finish off the previous task if it's still open

            _currentTask = new WorkflowTask
            {
                id = Guid.NewGuid().ToString(),
                tag = tag,
                description = description,
                timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                idSession = CurrentIdSession(),
                startedUnixMs = NowUnixMs(),
                cursorStart = EditorChangeTrackerService.CurrentCursor,
                snapshots = new List<ObjectSnapshot>()
            };
            _currentTask.EnsureSnapshotIndex();

            return _currentTask;
        }

        /// <summary>
        /// The router's one-call task. Unlike an explicit task, it continues its agent's current group unless the agent
        /// was idle for the configured gap (<see cref="WorkflowCheckpointConfig.IdleGapSeconds"/>) before the call.
        /// </summary>
        internal static WorkflowTask BeginAutoTask(string tag, string description, string target)
        {
            var task = BeginTask(tag, description);
            task.autoStarted = true;
            task.target = target;
            return task;
        }

        public static void EndTask()
        {
            if (_currentTask == null) return;

            // Only tasks that captured at least one snapshot get recorded. If a tracked skill fails or made
            // no changes, there's nothing to undo, and recording it anyway would just leave an empty entry
            // (changes=0) that both pollutes the history and gets in the way of undo/redo navigation.
            if (_currentTask.snapshots.Count == 0)
            {
                _currentTask = null;
                _pendingCheckpointLabel = null;
                return;
            }

            // One append, with the payloads it names stored first. A history not loaded yet stays unloaded: the log is the
            // only source of truth, and the next load replays this record with the rest.
            var task = _currentTask;
            _currentTask = null;
            task.cursorEnd = EditorChangeTrackerService.CurrentCursor;
            StampAssetFingerprints(task);
            bool startsRun = AttributeTask(task);
            string checkpointLabel = _pendingCheckpointLabel;
            _pendingCheckpointLabel = null;
            var batch = new LogBatch();
            // The checkpoint sits at this step's position, so it is written before the step in the same batch.
            if (startsRun && WorkflowCheckpointConfig.AutoCheckpoints)
            {
                AddCheckpointRecord(batch, new WorkflowCheckpoint
                {
                    id = CheckpointIdFor(task.seq),
                    seq = task.seq,
                    timestampMs = NowUnixMs(),
                    agent = task.agent,
                    kind = WorkflowCheckpoint.KindAuto,
                    cursor = task.cursorStart
                });
            }
            // Written after the auto one, so an explicit label upgrades it at the same position (see MergeCheckpoint).
            if (checkpointLabel != null)
            {
                AddCheckpointRecord(batch, new WorkflowCheckpoint
                {
                    id = CheckpointIdFor(task.seq),
                    seq = task.seq,
                    timestampMs = NowUnixMs(),
                    agent = task.agent,
                    kind = WorkflowCheckpoint.KindExplicit,
                    label = checkpointLabel,
                    cursor = task.cursorStart
                });
            }
            AddTaskRecord(batch, WorkflowStore.TasksStack, task, isNew: true);
            if (_history != null)
                _history.tasks.Add(task);
            if (!Commit(batch) && _history == null)
            {
                // Not persisted: keep it for this session at least, as an unsaved history file used to.
                LoadHistory();
                _history.tasks.Add(task);
            }
        }

        /// <summary>
        /// Stamps each asset snapshot whose file exists with its fingerprint ("length:ticks" of the write time), so a later
        /// reader can tell whether the file still holds this write. Folders and missing files stay null.
        /// </summary>
        private static void StampAssetFingerprints(WorkflowTask task)
        {
            foreach (var snapshot in task.snapshots)
            {
                if (snapshot == null || string.IsNullOrEmpty(snapshot.assetPath))
                    continue;
                string fingerprint = AssetFingerprint(snapshot.assetPath);
                if (fingerprint != null)
                    snapshot.assetFingerprint = fingerprint;
            }
        }

        /// <summary>"&lt;length&gt;:&lt;lastWriteTimeUtc ticks&gt;" of an asset file, or null when the path is unsafe or no file is there.</summary>
        internal static string AssetFingerprint(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath))
                return null;
            var file = new FileInfo(fullPath);
            return file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : null;
        }

        /// <summary>
        /// Gives a finished task its seq, its agent and its group. The agent is resolved now unless the caller preset it (an
        /// async batch job's chunk); when the process resolver has not answered for the connection yet, the task is bound
        /// later by <see cref="BindPendingAgents"/>. A preset group (the job's) leaves the agent's runs alone; an explicit
        /// task is a run of its own, closed when it ends; a router task opens a run only after an idle gap. Returns whether
        /// the task opened a run after an idle gap, which is when an automatic checkpoint is due.
        /// </summary>
        private static bool AttributeTask(WorkflowTask task)
        {
            task.seq = _history == null && WorkflowStore.TryAllocateSeqWithoutReplay(out long seq) ? seq : History.AllocateSeq();
            if (string.IsNullOrEmpty(task.agent))
            {
                task.agent = ResolveCallerAgent(out int pendingPort);
                if (pendingPort > 0)
                {
                    task.pendingAgentPort = pendingPort;
                    task.pendingAgentSinceMs = NowUnixMs();
                    _pendingAgentTasks.Add(task);
                }
            }
            if (!string.IsNullOrEmpty(task.group))
                return false;
            // Only the batch's first task can open a run; the rest continue the batch's group.
            if (_batchGroup != null)
            {
                task.group = _batchGroup;
                return false;
            }

            var activity = GetAgentActivity(task.agent);
            bool startsRun = false;
            if (!task.autoStarted)
            {
                task.group = "g" + task.seq;
                activity.group = null;
            }
            else
            {
                long startedMs = task.startedUnixMs > 0 ? task.startedUnixMs : task.timestamp * 1000L;
                // An agent's first tracked write has no run yet (a fresh editor, or a new agent), so it opens one with a checkpoint.
                startsRun = activity.group == null || WorkflowCheckpointPolicy.IsIdleGap(activity.lastCallMs, startedMs,
                    WorkflowLifecycle.BusySpans, WorkflowCheckpointConfig.IdleGapSeconds);
                if (startsRun)
                    activity.group = "g" + task.seq;
                task.group = activity.group;
            }
            if (_batchGroupOpen)
            {
                _batchGroup = "b" + task.seq;
                task.group = _batchGroup;
                activity.group = _batchGroup;
            }
            SaveAgentActivity();
            return startsRun;
        }

        /// <summary>
        /// Records that the current caller just finished a call (read or write). The router calls this once per execution;
        /// the idle gap before the caller's next tracked write is measured from here.
        /// </summary>
        internal static void NoteAgentCall()
        {
            try
            {
                GetAgentActivity(ResolveCallerAgent()).lastCallMs = NowUnixMs();
                SaveAgentActivity();
            }
            catch (Exception ex)
            {
                // Grouping is bookkeeping; it must never fail the call that triggered it.
                SkillsLogger.LogVerbose($"Workflow agent activity update failed: {ex.Message}");
            }
        }

        internal static string ResolveCallerAgent() => ResolveCallerAgent(out _);

        /// <summary>
        /// The calling agent, as the change journal attributes it, folded by <see cref="NormalizeAgent"/>.
        /// <paramref name="pendingPort"/> is the connection's port when the process resolver has not answered for it yet
        /// (the id may still change), otherwise 0.
        /// </summary>
        internal static string ResolveCallerAgent(out int pendingPort)
        {
            var context = SkillsAuditLog.CaptureRequestContext();
            var lookup = AgentLookup;
            pendingPort = context.Active && !context.AgentIdIsExplicit && context.RemotePort > 0 &&
                          !lookup(context.RemotePort, out _)
                ? context.RemotePort
                : 0;
            return NormalizeAgent(EditorChangeTrackerService.ResolveRestAgent(context, lookup));
        }

        private static EditorChangeTrackerService.AgentLookup AgentLookup =>
            AgentLookupForTests ?? ClientProcessResolver.TryGetAgentId;

        /// <summary>
        /// One key per agent for the workflow history. The generic header guesses a request carries before the process
        /// resolver answers ("curl", "Python", "Unknown", "Unknown(...)") all fold into "unknown", so one agent never shows
        /// under two keys just because one of its calls was attributed before the resolver landed.
        /// </summary>
        internal static string NormalizeAgent(string agent)
        {
            if (string.IsNullOrEmpty(agent) ||
                string.Equals(agent, "curl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(agent, "Python", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(agent, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                agent.StartsWith("Unknown(", StringComparison.OrdinalIgnoreCase))
                return EditorChangeTrackerService.AgentUnknown;
            return agent;
        }

        /// <summary>
        /// Binds the agent of every task that ended before the process resolver answered for its connection, the way
        /// telemetry does at flush time. Runs with each history write and each list; a task still unanswered after the
        /// resolver's cache lifetime keeps the agent it was recorded with.
        /// </summary>
        internal static void BindPendingAgents()
        {
            if (_pendingAgentTasks.Count > 0)
                Commit(new LogBatch());
        }

        /// <summary>Binds what it can (see <see cref="BindPendingAgents"/>), adding an agent record for each to <paramref name="batch"/>.</summary>
        private static void AddPendingAgentRecords(LogBatch batch)
        {
            if (_pendingAgentTasks.Count == 0)
                return;

            long now = NowUnixMs();
            _pendingAgentTasks.RemoveAll(task =>
            {
                if (TryResolvePendingAgent(task.pendingAgentPort, out string agent))
                {
                    task.agent = agent;
                    string stack = StackHolding(task);
                    if (stack != null)
                        batch.Lines.Add(WorkflowStore.BuildAgentLine(stack, task.id, agent));
                }
                else if (now - task.pendingAgentSinceMs <= PendingAgentWindowMs)
                    return false;
                task.pendingAgentPort = 0;
                return true;
            });
        }

        /// <summary>
        /// The stack holding this very task object, so an agent record names the copy it changed; null for a detached copy
        /// (deleted). With the history unloaded, only EndTask can have logged it, into "t".
        /// </summary>
        private static string StackHolding(WorkflowTask task)
        {
            if (_history == null || _history.tasks.Contains(task))
                return WorkflowStore.TasksStack;
            return null;
        }

        /// <summary>The process resolver's answer for <paramref name="remotePort"/>, folded, once it has one.</summary>
        internal static bool TryResolvePendingAgent(int remotePort, out string agent)
        {
            agent = null;
            if (remotePort <= 0 || !AgentLookup(remotePort, out string resolved) || string.IsNullOrEmpty(resolved))
                return false;
            agent = NormalizeAgent(resolved);
            return true;
        }

        private static long NowUnixMs() => NowUnixMsForTests?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static AgentActivity GetAgentActivity(string agent)
        {
            if (_agentActivity == null && !_agentActivityDetached)
            {
                // SessionState survives a domain reload but not an editor restart, so a restart always opens a new group.
                try
                {
                    _agentActivity = JsonConvert.DeserializeObject<Dictionary<string, AgentActivity>>(
                        SessionState.GetString(AgentActivitySessionKey, string.Empty), SkillsCommon.JsonSettings);
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose($"Workflow agent activity could not be restored: {ex.Message}");
                }
            }
            _agentActivity ??= new Dictionary<string, AgentActivity>(StringComparer.Ordinal);

            if (!_agentActivity.TryGetValue(agent, out var activity))
                _agentActivity[agent] = activity = new AgentActivity();
            return activity;
        }

        private static void SaveAgentActivity()
        {
            if (_agentActivityDetached)
                return;

            try
            {
                SessionState.SetString(AgentActivitySessionKey,
                    JsonConvert.SerializeObject(_agentActivity, Formatting.None, SkillsCommon.JsonSettings));
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow agent activity could not be saved: {ex.Message}");
            }
        }

        private static string CurrentIdSession()
        {
            if (_idSession != null)
                return _idSession;

            _idSession = SessionState.GetString(IdSessionKey, string.Empty);
            if (string.IsNullOrEmpty(_idSession))
            {
                _idSession = Guid.NewGuid().ToString("N");
                SessionState.SetString(IdSessionKey, _idSession);
            }
            return _idSession;
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

        /// <summary>The seq the next recorded task will get.</summary>
        internal static long NextTaskSeq => History.nextSeq;

        /// <summary>Where the workflow history stood when a transactional batch began; see <see cref="RollBackBatch"/>.</summary>
        internal sealed class BatchMark
        {
            internal long FirstSeq;
            internal long StartedUnixMs;
            internal WorkflowTask OpenTask;
            internal int OpenTaskSnapshotCount;
        }

        internal static BatchMark MarkBatchStart() => new BatchMark
        {
            FirstSeq = History.nextSeq,
            StartedUnixMs = NowUnixMs(),
            OpenTask = _currentTask,
            OpenTaskSnapshotCount = _currentTask?.snapshots?.Count ?? 0
        };

        /// <summary>
        /// After Unity's Undo rolled a transactional batch back: removes the scene snapshots the batch recorded, from the
        /// tasks it started and from the task that was already open when it began (an explicit task or session). Left
        /// behind, they would offer an undo of changes that no longer exist, which fails on the first created object and
        /// blocks the rest of that undo. Asset and setting snapshots stay: the disk and the settings are not on Unity's undo
        /// stack, so they are the only way back. Returns how many snapshots were removed.
        /// </summary>
        internal static int RollBackBatch(BatchMark mark)
        {
            if (mark == null || _history == null)
                return 0;

            int removed = 0;
            var batch = new LogBatch();
            if (mark.OpenTask?.snapshots != null && mark.OpenTask.snapshots.Count > mark.OpenTaskSnapshotCount)
            {
                var added = mark.OpenTask.snapshots.GetRange(mark.OpenTaskSnapshotCount,
                    mark.OpenTask.snapshots.Count - mark.OpenTaskSnapshotCount);
                int openRemoved = added.RemoveAll(IsRevertedByUnityUndo);
                removed += openRemoved;
                mark.OpenTask.snapshots.RemoveRange(mark.OpenTaskSnapshotCount,
                    mark.OpenTask.snapshots.Count - mark.OpenTaskSnapshotCount);
                mark.OpenTask.snapshots.AddRange(added);
                mark.OpenTask.InvalidateSnapshotIndex();
                // A step may have ended the open task into the history; only then is it on the log.
                if (openRemoved > 0 && _history.tasks.Contains(mark.OpenTask))
                    AddStateRecord(batch, WorkflowStore.TasksStack, _history.tasks, mark.OpenTask);
            }

            foreach (var task in _history.tasks.ToList())
            {
                if (task == mark.OpenTask || task.seq < mark.FirstSeq || task.startedUnixMs < mark.StartedUnixMs)
                    continue;
                int taskRemoved = task.snapshots.RemoveAll(IsRevertedByUnityUndo);
                removed += taskRemoved;
                task.InvalidateSnapshotIndex();
                if (taskRemoved > 0 || task.snapshots.Count == 0)
                    AddStateRecord(batch, WorkflowStore.TasksStack, _history.tasks, task);
            }

            if (batch.Lines.Count > 0 || batch.Failed)
                Commit(batch);
            return removed;
        }

        // A scene object's snapshot: Unity's Undo already put the object back. A snapshot with an asset path covers a disk
        // write, and a setting snapshot a value Undo may not track; both outlive the rollback.
        private static bool IsRevertedByUnityUndo(ObjectSnapshot snapshot) =>
            snapshot != null && string.IsNullOrEmpty(snapshot.assetPath) && snapshot.type != SnapshotType.Setting;

        /// <summary>
        /// Takes the open task (an explicit task or session) off the recorder, untouched, so an async batch chunk can record
        /// into a task of its own; <see cref="ResumeTask"/> puts it back.
        /// </summary>
        internal static WorkflowTask SuspendTask()
        {
            var task = _currentTask;
            _currentTask = null;
            return task;
        }

        internal static void ResumeTask(WorkflowTask task)
        {
            if (task == null)
                return;
            if (_currentTask != null)
                EndTask();
            _currentTask = task;
        }

        internal static string NewSessionId() => Guid.NewGuid().ToString();

        #region Session Management (Conversation-Level Undo)

        /// <summary>
        /// Starts a new session (conversation-level). Every task created during this session gets grouped
        /// together, so it can be undone as a whole.
        /// </summary>
        public static string BeginSession(string sessionTag = null)
        {
            if (HasActiveSession)
            {
                EndSession();
            }

            _currentSessionId = Guid.NewGuid().ToString();

            BeginTask(sessionTag ?? "Session", $"Session started at {DateTime.Now:HH:mm:ss}");
            _currentTask.sessionId = _currentSessionId;

            SkillsLogger.LogWorkflow($"Session started: {_currentSessionId}");
            return _currentSessionId;
        }

        /// <summary>
        /// Ends the current session and saves every change recorded during it.
        /// </summary>
        public static void EndSession()
        {
            if (!HasActiveSession) return;

            if (_currentTask != null)
            {
                _currentTask.sessionId = _currentSessionId;
                EndTask();
            }

            SkillsLogger.LogWorkflow($"Session ended: {_currentSessionId}");
            _currentSessionId = null;
        }

        /// <summary>
        /// Gets every session in the history.
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

        // Budget cap for CaptureObjectReferences' traversal. For assets whose serialized data is a
        // [SerializeReference] graph, a full-depth SerializedProperty descent is unbounded: a
        // VisualTreeAsset (one per imported .uxml) would make iterator.Next(true) chase managed references
        // forever, pinning the main thread at 100% CPU with no way out short of killing the editor.
        // The managed-reference dedup below is the real cycle breaker; the node-count/time caps only exist
        // as a backstop for other pathological structures we haven't seen yet.
        private const int MaxReferenceWalkNodes = 50000;
        private const int MaxReferenceWalkDepth = 32;
        private const int MaxReferenceWalkMilliseconds = 2000;

        internal static void ResetStateForTests()
        {
            _history = null;
            _currentTask = null;
            _currentSessionId = null;
            _historyRecoveryMode = false;
            _maintenanceGeneration++;
            _maintenanceScheduled = false;
            _maintenanceSizeCheck = false;
            CapacityBytesForTests = 0;
            WorkflowStore.ResetState();
            _agentActivityDetached = true;
            _agentActivity = new Dictionary<string, AgentActivity>(StringComparer.Ordinal);
            _pendingAgentTasks.Clear();
            _persistentGidCache.Clear();
            NowUnixMsForTests = null;
            AgentLookupForTests = null;
        }
    }

    /// <summary>Keeps <see cref="WorkflowManager"/>'s asset GlobalObjectId cache from outliving a deleted or moved asset.</summary>
    internal sealed class WorkflowAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

            if ((deletedAssets?.Length ?? 0) > 0 || (movedAssets?.Length ?? 0) > 0)
                WorkflowManager.InvalidateGlobalObjectIdCache();
        }
    }
}

// Producer:Betsy
