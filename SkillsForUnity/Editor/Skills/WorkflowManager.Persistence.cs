using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {
        /// <summary>
        /// Rebuilds the history from the log (<see cref="WorkflowStore"/>). A segment that cannot be read, or whose blob
        /// references cannot be rebuilt, puts the session in recovery mode: the history in hand then references fewer blobs
        /// than the store holds, so eviction and GC wait until <see cref="ClearHistory"/>.
        /// </summary>
        public static void LoadHistory()
        {
            ReloadFromLog();
            ScheduleMaintenance(sizeCheck: true);
        }

        private static void ReloadFromLog()
        {
            _history = WorkflowStore.Replay(out bool unreadable);
            _historyRecoveryMode = unreadable;
            _history.EnsureDefaults();
            SanitizeHistory();
            AdoptPendingAgentTasks();
        }

        /// <summary>
        /// A task still waiting for its agent is the object its caller holds; the replay rebuilt an equal copy of it, which
        /// it replaces, so the late binding reaches both.
        /// </summary>
        private static void AdoptPendingAgentTasks()
        {
            foreach (var pending in _pendingAgentTasks)
            {
                int index = _history.tasks.FindIndex(task => task.id == pending.id);
                if (index >= 0)
                    _history.tasks[index] = pending;
            }
        }

        #region Log records

        /// <summary>The records of one history change, appended together; nothing is written when a payload failed to store.</summary>
        private sealed class LogBatch
        {
            internal readonly List<string> Lines = new List<string>();
            internal readonly List<WorkflowTask> Written = new List<WorkflowTask>();
            internal bool Failed;
        }

        private static void AddTaskRecord(LogBatch batch, string stack, WorkflowTask task, bool isNew = false)
        {
            string line = WorkflowStore.BuildTaskLine(stack, task, isNew);
            if (line == null)
            {
                batch.Failed = true;
                return;
            }
            batch.Lines.Add(line);
            batch.Written.Add(task);
        }

        /// <summary>The task's state in <paramref name="list"/> after a change: removed from it when it has no snapshot left.</summary>
        private static void AddStateRecord(LogBatch batch, string stack, List<WorkflowTask> list, WorkflowTask task)
        {
            if (task.snapshots.Count == 0)
            {
                list.Remove(task);
                batch.Lines.Add(WorkflowStore.BuildDropLine(stack, task.id));
            }
            else
            {
                AddTaskRecord(batch, stack, task);
            }
        }

        /// <summary>
        /// Adds a checkpoint's record to the batch and merges it into the in-memory history when that is loaded. The merge is the
        /// one replay applies (<see cref="WorkflowStore.MergeCheckpoint"/>), so a position holds one checkpoint either way.
        /// </summary>
        private static void AddCheckpointRecord(LogBatch batch, WorkflowCheckpoint checkpoint)
        {
            batch.Lines.Add(WorkflowStore.BuildCheckpointLine(checkpoint));
            if (_history == null)
                return;

            int index = _history.checkpoints.FindIndex(existing => existing.id == checkpoint.id);
            if (index < 0)
                _history.checkpoints.Add(checkpoint);
            else
                _history.checkpoints[index] = WorkflowStore.MergeCheckpoint(_history.checkpoints[index], checkpoint);
        }

        /// <summary>
        /// Appends the batch, plus an agent record for each pending agent bound now, in one write, then releases the written
        /// snapshots' payloads from memory. False when nothing was written.
        /// </summary>
        private static bool Commit(LogBatch batch)
        {
            if (batch.Failed)
            {
                SkillsLogger.LogError("Failed to save workflow history: a snapshot payload could not be stored, so this " +
                                      "change is kept in memory for this session only.");
                return false;
            }

            AddPendingAgentRecords(batch);
            if (batch.Lines.Count == 0)
                return true;
            if (!WorkflowStore.Append(batch.Lines))
                return false;

            foreach (var task in batch.Written)
            {
                if (task.snapshots == null) continue;
                foreach (var snapshot in task.snapshots)
                    WorkflowStore.ReleasePayload(snapshot);
            }

            if (WorkflowStore.NeedsSeal)
                ScheduleMaintenance(sizeCheck: false);
            else if (WorkflowAutoCleanConfig.Enabled && WorkflowStore.GrewPastSizeCheck(CapacityBytes))
                ScheduleMaintenance(sizeCheck: true);
            return true;
        }

        #endregion

        #region Payloads

        /// <summary>
        /// Reads a stub's payload back from the store; a snapshot that was never released has nothing to read. Its header
        /// stays authoritative over the payload's copy of the same fields. Fails with the reason when the blob is missing or
        /// damaged (a damaged one is quarantined).
        /// </summary>
        internal static bool TryHydrate(ObjectSnapshot snapshot, out string error)
        {
            error = null;
            if (snapshot == null || !snapshot.payloadReleased)
                return true;

            var header = WorkflowStore.CaptureHeader(snapshot);
            if (!WorkflowStore.TryReadPayload(snapshot.payloadHash, out string json, out error))
                return false;
            try
            {
                JsonUtility.FromJsonOverwrite(json, snapshot);
                SanitizeSnapshot(snapshot, "a snapshot payload");
            }
            catch (Exception ex)
            {
                error = $"Backup payload {snapshot.payloadHash} could not be read back: {ex.Message}";
                snapshot.payloadReleased = false;
                WorkflowStore.ReleasePayload(snapshot);
                WorkflowStore.ApplyHeader(header, snapshot);
                return false;
            }
            WorkflowStore.ApplyHeader(header, snapshot);
            snapshot.payloadReleased = false;
            return true;
        }

        private static void SanitizeHistory()
        {
            foreach (var task in _history.tasks)
            {
                if (task?.snapshots == null) continue;
                foreach (var snapshot in task.snapshots)
                    if (snapshot != null)
                        SanitizeSnapshot(snapshot, "the workflow history");
            }
        }

        private static void SanitizeSnapshot(ObjectSnapshot snapshot, string source)
        {
            if (!string.IsNullOrEmpty(snapshot.assetPath) && Validate.SafePath(snapshot.assetPath, "assetPath") is object)
            {
                SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe assetPath from {source}: {snapshot.assetPath}");
                snapshot.assetPath = null;
                snapshot.fileHash = null;
                snapshot.metaFileHash = null;
                snapshot.previousAssetPath = null;
                snapshot.directoryEntries?.Clear();
                snapshot.fileRefs = null;
            }

            if (!string.IsNullOrEmpty(snapshot.previousAssetPath) &&
                Validate.SafePath(snapshot.previousAssetPath, "previousAssetPath") is object)
            {
                SkillsLogger.LogWarning($"WorkflowManager: stripped unsafe previousAssetPath from {source}: {snapshot.previousAssetPath}");
                snapshot.previousAssetPath = null;
            }
        }

        #endregion

        /// <summary>
        /// Removes a task from the history; its blobs are reclaimed when the log segments naming them are evicted.
        /// </summary>
        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            var batch = new LogBatch();
            var task = _history.tasks.FirstOrDefault(t => MatchesTaskId(t, taskId));
            if (task != null)
            {
                _history.tasks.Remove(task);
                batch.Lines.Add(WorkflowStore.BuildDropLine(WorkflowStore.TasksStack, task.id));
            }

            Commit(batch);
        }

        /// <summary>
        /// Blob hashes the log may not name yet: the task being recorded (its asset blobs are stored as it captures), and,
        /// with <paramref name="includeHistory"/>, every task in memory.
        /// </summary>
        private static HashSet<string> CollectLiveHashes(bool includeHistory)
        {
            // Case-insensitive: store entries are uppercased when enumerated, so a snapshot hash that
            // differs only in case must also count as a reference.
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTaskHashes(_currentTask, hashes);
            if (includeHistory && _history != null)
            {
                foreach (var task in _history.tasks)
                    AddTaskHashes(task, hashes);
            }
            return hashes;
        }

        private static void AddTaskHashes(WorkflowTask task, HashSet<string> hashes)
        {
            if (task?.snapshots == null) return;
            foreach (var snapshot in task.snapshots)
            {
                if (snapshot == null) continue;
                var header = WorkflowStore.CaptureHeader(snapshot);
                if (!string.IsNullOrEmpty(header.Payload)) hashes.Add(header.Payload);
                if (!string.IsNullOrEmpty(header.FileHash)) hashes.Add(header.FileHash);
                if (!string.IsNullOrEmpty(header.MetaFileHash)) hashes.Add(header.MetaFileHash);
                if (header.Files != null)
                    hashes.UnionWith(header.Files.Where(hash => !string.IsNullOrEmpty(hash)));
            }
        }

        #region Maintenance and cleanup

        private static long CapacityBytes => CapacityBytesForTests > 0
            ? CapacityBytesForTests
            : Math.Max(0, WorkflowAutoCleanConfig.CapacityMB) * 1024L * 1024L;

        /// <summary>
        /// Queues one maintenance pass (seal the full active segment, delete an expired old backup, and with
        /// <paramref name="sizeCheck"/> measure the store and evict and collect if needed). A pass that outlives a reset, a
        /// clear or a switch of store root does nothing.
        /// </summary>
        private static void ScheduleMaintenance(bool sizeCheck)
        {
            _maintenanceSizeCheck |= sizeCheck;
            if (_maintenanceScheduled)
                return;

            _maintenanceScheduled = true;
            int generation = _maintenanceGeneration;
            string root = WorkflowStore.Root;
            EditorApplication.delayCall += () =>
            {
                if (generation != _maintenanceGeneration)
                    return;
                _maintenanceScheduled = false;
                if (root == WorkflowStore.Root)
                    RunMaintenance();
            };
        }

        /// <summary>The deferred pass <see cref="ScheduleMaintenance"/> queues; internal so tests can run it in place.</summary>
        internal static void RunMaintenance()
        {
            bool sealedSegment = WorkflowStore.SealIfNeeded(_history?.nextSeq ?? 0, out bool unreadable);
            if (unreadable)
                _historyRecoveryMode = true;
            WorkflowStore.CleanUpLegacyIfDue();

            bool sizeCheck = sealedSegment || _maintenanceSizeCheck;
            _maintenanceSizeCheck = false;
            if (sizeCheck && !_historyRecoveryMode && WorkflowAutoCleanConfig.Enabled)
                RunCleanup(force: false);
        }

        /// <summary>
        /// Runs eviction and blob GC now (the History tab's "Clean Now"); without <paramref name="force"/>, only when
        /// auto-cleanup is enabled. Skipped in recovery mode. Reports what it removed.
        /// </summary>
        public static WorkflowTrimReport TrimHistoryIfNeeded(bool force = false)
        {
            if (_history == null) LoadHistory();
            if (!force && !WorkflowAutoCleanConfig.Enabled)
                return new WorkflowTrimReport();

            if (_historyRecoveryMode)
            {
                if (force)
                {
                    SkillsLogger.LogWarning(
                        "Workflow cleanup skipped: a history log segment failed to load this session, so the set of " +
                        "referenced backups is incomplete. Clear the history to re-enable cleanup.");
                }
                return new WorkflowTrimReport();
            }
            return RunCleanup(force);
        }

        /// <summary>
        /// Evicts the old segments the limits call for, rebuilds the history from what is left (a task whose last record was
        /// evicted is gone), then deletes every blob nothing names any more.
        /// </summary>
        private static WorkflowTrimReport RunCleanup(bool force)
        {
            CleanupRunsForTests++;
            var report = new WorkflowTrimReport();
            int tasksBefore = _history == null ? 0 : _history.tasks.Count;

            var eviction = WorkflowStore.EvictSegments(CapacityBytes, Math.Max(0, WorkflowAutoCleanConfig.MaxAgeDays),
                CollectLiveHashes(includeHistory: false), force);
            if (eviction.Unreadable)
            {
                _historyRecoveryMode = true;
                return report;
            }
            if (!eviction.NeedsGc)
                return report;

            report.removedSegments = eviction.RemovedSegments;
            report.reclaimedBytes = eviction.RemovedSegmentBytes;
            if (eviction.RemovedSegments > 0 && _history != null)
            {
                ReloadFromLog();
                report.removedTasks = Math.Max(0, tasksBefore - _history.tasks.Count);
                if (_historyRecoveryMode)
                    return report;
            }

            if (!WorkflowStore.CollectGarbage(CollectLiveHashes(includeHistory: true), out int blobs, out long blobBytes))
            {
                _historyRecoveryMode = true;
                return report;
            }
            report.reclaimedFileEntries = blobs;
            report.reclaimedBytes += blobBytes;
            return report;
        }

        #endregion

        public static void ClearHistory()
        {
            // Ids keep counting: an s<seq> an agent still holds must never name a task recorded after the clear.
            long nextSeq = History.nextSeq;
            _maintenanceGeneration++;
            _maintenanceScheduled = false;
            _maintenanceSizeCheck = false;
            _pendingAgentTasks.Clear();
            // The blobs of the task still being recorded are preserved: it was never part of the history the
            // user asked to clear in the first place. Everything else goes, whatever its age — this skill promises
            // to clear the file store — which also resynchronizes the store with the history and clears recovery mode.
            WorkflowStore.Clear(nextSeq, CollectLiveHashes(includeHistory: false));
            _history = new WorkflowHistoryData { nextSeq = nextSeq };
            _historyRecoveryMode = false;
        }

        /// <summary>
        /// Bytes of the history log on disk: every segment, its reference list and the head; 0 when there is none.
        /// </summary>
        public static long GetHistoryFileSizeBytes() => WorkflowStore.GetLogBytes();

        /// <summary>The pre-3.0 history moved aside into workflow_legacy/, or null when there is none.</summary>
        public static WorkflowLegacyBackupInfo GetLegacyBackupInfo() => WorkflowStore.GetLegacyBackupInfo();

        /// <summary>Deletes workflow_legacy/ (it is also deleted on its own 14 days after the move). False when that failed.</summary>
        public static bool DeleteLegacyBackup() => WorkflowStore.DeleteLegacyBackup();
    }
}

// Producer:Betsy
