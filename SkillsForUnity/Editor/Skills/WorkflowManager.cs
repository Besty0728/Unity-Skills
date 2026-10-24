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

        // Set when the history file fails to load. In that state, the history in hand references fewer
        // blobs than the store actually holds, so reclaiming "unreferenced" entries would delete live
        // backups; better to leak than to pay that cost.
        // volatile: SkillsHttpServer's /health fast path reads IsHistoryRecoveryMode on the HTTP thread,
        // while writes always happen on the main thread (LoadHistory / ClearHistory).
        private static volatile bool _historyRecoveryMode;

        // Trim/GC walks the whole history and enumerates the file store several times, so it runs deferred and at most
        // once per interval instead of inside every EndTask. The generation drops a deferred run after a reset or clear.
        private const double TrimIntervalSeconds = 60;
        private static bool _trimScheduled;
        private static DateTime _lastTrimUtc = DateTime.MinValue;
        private static int _trimGeneration;
        internal static int TrimRunsForTests;

        // The deferred trim runs only when a cheap check finds a limit exceeded by more than 10%. Sizes come from the last
        // saved file; a run that cannot bring it under the limit raises the size gate by another 10%, so a history the
        // trim's own estimate finds small enough is not walked again every minute.
        private const double TrimGateFactor = 1.1;
        private static long _lastSavedHistoryChars;
        private static long _sizeGateChars;

        // GlobalObjectId of a persistent asset is fixed by its GUID and file id, so it is computed once per session.
        // Cleared whenever assets are deleted or moved, and bounded so a long session cannot grow it without limit.
        private const int MaxPersistentGidCacheEntries = 4096;
        private static readonly Dictionary<string, string> _persistentGidCache = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static int GlobalObjectIdComputationsForTests;

        // A new run of writes starts when an agent's next tracked write comes this long after its previous call.
        internal const int AutoGroupIdleGapSeconds = 30;
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

        // Path where the history file is stored (persists in the Library directory, but local to this machine only)
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

        /// <summary>
        /// True when this session's history file failed to load, suspending file-store cleanup as a
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
                snapshots = new List<ObjectSnapshot>()
            };
            _currentTask.EnsureSnapshotIndex();

            return _currentTask;
        }

        /// <summary>
        /// The router's one-call task. Unlike an explicit task, it continues its agent's current group unless the agent
        /// was idle for <see cref="AutoGroupIdleGapSeconds"/> before the call.
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
                return;
            }

            if (_history == null) LoadHistory();

            AttributeTask(_currentTask);
            _history.tasks.Add(_currentTask);
            _history.undoneStack.Clear();
            _currentTask = null;

            ScheduleTrim();
            SaveHistory();
        }

        /// <summary>
        /// Gives a finished task its seq, its agent and its group. The agent is resolved now unless the caller preset it (an
        /// async batch job's chunk); when the process resolver has not answered for the connection yet, the task is bound
        /// later by <see cref="BindPendingAgents"/>. A preset group (the job's) leaves the agent's runs alone; an explicit
        /// task is a run of its own, closed when it ends; a router task opens a run only after an idle gap.
        /// </summary>
        private static void AttributeTask(WorkflowTask task)
        {
            task.seq = _history.AllocateSeq();
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
                return;

            var activity = GetAgentActivity(task.agent);
            if (!task.autoStarted)
            {
                task.group = "g" + task.seq;
                activity.group = null;
            }
            else
            {
                long startedMs = task.startedUnixMs > 0 ? task.startedUnixMs : task.timestamp * 1000L;
                if (activity.group == null || startedMs - activity.lastCallMs >= AutoGroupIdleGapSeconds * 1000L)
                    activity.group = "g" + task.seq;
                task.group = activity.group;
            }
            SaveAgentActivity();
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
        /// telemetry does at flush time. Runs before each save and each list; a task still unanswered after the resolver's
        /// cache lifetime keeps the agent it was recorded with.
        /// </summary>
        internal static void BindPendingAgents()
        {
            if (_pendingAgentTasks.Count == 0)
                return;

            long now = NowUnixMs();
            _pendingAgentTasks.RemoveAll(task =>
            {
                if (TryResolvePendingAgent(task.pendingAgentPort, out string agent))
                    task.agent = agent;
                else if (now - task.pendingAgentSinceMs <= PendingAgentWindowMs)
                    return false;
                task.pendingAgentPort = 0;
                return true;
            });
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
            internal List<WorkflowTask> UndoneStack;
        }

        internal static BatchMark MarkBatchStart() => new BatchMark
        {
            FirstSeq = History.nextSeq,
            StartedUnixMs = NowUnixMs(),
            OpenTask = _currentTask,
            OpenTaskSnapshotCount = _currentTask?.snapshots?.Count ?? 0,
            UndoneStack = new List<WorkflowTask>(History.undoneStack)
        };

        /// <summary>
        /// After Unity's Undo rolled a transactional batch back: removes the scene snapshots the batch recorded, from the
        /// tasks it started and from the task that was already open when it began (an explicit task or session). Left
        /// behind, they would offer an undo of changes that no longer exist, which fails on the first created object and
        /// blocks the rest of that undo. Asset and setting snapshots stay: the disk and the settings are not on Unity's undo
        /// stack, so they are the only way back. When nothing of the batch is left and <paramref name="restoreRedoStack"/>
        /// holds (no step touched the workflow stacks itself), the redo stack the steps' EndTask cleared comes back.
        /// Returns how many snapshots were removed.
        /// </summary>
        internal static int RollBackBatch(BatchMark mark, bool restoreRedoStack)
        {
            if (mark == null || _history == null)
                return 0;

            int removed = 0;
            bool kept = false;
            if (mark.OpenTask?.snapshots != null && mark.OpenTask.snapshots.Count > mark.OpenTaskSnapshotCount)
            {
                var added = mark.OpenTask.snapshots.GetRange(mark.OpenTaskSnapshotCount,
                    mark.OpenTask.snapshots.Count - mark.OpenTaskSnapshotCount);
                removed += added.RemoveAll(IsRevertedByUnityUndo);
                kept |= added.Count > 0;
                mark.OpenTask.snapshots.RemoveRange(mark.OpenTaskSnapshotCount,
                    mark.OpenTask.snapshots.Count - mark.OpenTaskSnapshotCount);
                mark.OpenTask.snapshots.AddRange(added);
                mark.OpenTask.InvalidateSnapshotIndex();
                if (mark.OpenTask.snapshots.Count == 0)
                    _history.tasks.Remove(mark.OpenTask);
            }

            foreach (var task in _history.tasks.ToList())
            {
                if (task == mark.OpenTask || task.seq < mark.FirstSeq || task.startedUnixMs < mark.StartedUnixMs)
                    continue;
                removed += task.snapshots.RemoveAll(IsRevertedByUnityUndo);
                task.InvalidateSnapshotIndex();
                if (task.snapshots.Count == 0)
                    _history.tasks.Remove(task);
                else
                    kept = true;
            }

            bool restoredRedo = false;
            if (restoreRedoStack && !kept && _history.undoneStack.Count == 0 && mark.UndoneStack.Count > 0)
            {
                _history.undoneStack.AddRange(mark.UndoneStack);
                restoredRedo = true;
            }
            if (removed > 0 || restoredRedo)
                SaveHistory();
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
        /// Undoes every change made during a given session, returning per-snapshot detailed results.
        /// </summary>
        public static TaskUndoResult UndoSession(string sessionId)
        {
            var result = new TaskUndoResult();
            if (string.IsNullOrEmpty(sessionId))
            {
                result.error = "sessionId is required";
                return result;
            }

            var sessionTasks = History.tasks
                .Where(t => t.sessionId == sessionId)
                .OrderByDescending(t => t.seq)
                .ToList();

            if (sessionTasks.Count == 0)
            {
                result.error = $"No tasks found for session: {sessionId}";
                return result;
            }

            // Undo task by task, newest to oldest. Preserving task boundaries is what keeps operation order
            // correct when the same object was modified, moved, and deleted at different points in the session.
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
            _trimGeneration++;
            _trimScheduled = false;
            _lastTrimUtc = DateTime.MinValue;
            _lastSavedHistoryChars = 0;
            _sizeGateChars = 0;
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
