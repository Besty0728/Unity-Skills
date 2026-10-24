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

        public static void LoadHistory()
        {
            // Crash recovery: if the main file is missing but a .tmp exists, promote the .tmp to the main file
            string tmpPath = HistoryFilePath + ".tmp";
            if (!File.Exists(HistoryFilePath) && File.Exists(tmpPath))
            {
                try { File.Move(tmpPath, HistoryFilePath); }
                catch { /* If promotion fails, fall back to the .bak below */ }
            }

            string backupPath = HistoryFilePath + ".bak";
            _history = null;
            _historyRecoveryMode = false;
            _lastSavedHistoryChars = 0;
            _sizeGateChars = 0;
            bool recoveredFromBackup = false;

            if (File.Exists(HistoryFilePath))
            {
                if (!TryLoadHistoryFrom(HistoryFilePath, out string mainError))
                {
                    _historyRecoveryMode = true;
                    string quarantined = QuarantineHistoryFile(HistoryFilePath);
                    SkillsLogger.LogError(
                        $"Failed to load workflow history: {mainError}. Kept the unreadable file as " +
                        $"{quarantined ?? "<quarantine failed>"}; file store cleanup is disabled for this session " +
                        "so the backups of the lost tasks are not reclaimed. Clear the history to re-enable it.");
                }
            }

            if (_history == null && File.Exists(backupPath))
            {
                if (TryLoadHistoryFrom(backupPath, out string backupError))
                {
                    // The backup lags one save behind current, so what was recorded after it is still gone.
                    _historyRecoveryMode = true;
                    recoveredFromBackup = true;
                    SkillsLogger.LogWarning(
                        $"Recovered workflow history from {Path.GetFileName(backupPath)}; tasks recorded after the last save are gone.");
                }
                else
                {
                    _historyRecoveryMode = true;
                    SkillsLogger.LogError($"Workflow history backup is unreadable as well: {backupError}");
                }
            }

            _history ??= new WorkflowHistoryData();
            _history.EnsureDefaults();
            MigrateHistorySchema();
            if (recoveredFromBackup)
                SaveHistory();
            TrimHistoryIfNeeded();
        }

        /// <summary>
        /// Parses the history file into <see cref="_history"/>. On failure, _history stays null,
        /// letting the caller try the next candidate file, with the failure reason surfaced via <paramref name="error"/>.
        /// </summary>
        private static bool TryLoadHistoryFrom(string path, out string error)
        {
            error = null;
            try
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var data = JsonUtility.FromJson<WorkflowHistoryData>(json);
                if (data == null)
                {
                    error = "file is empty or not a workflow history document";
                    return false;
                }

                _history = data;
                _lastSavedHistoryChars = json.Length;
                _history.EnsureDefaults();
                SanitizeHistory();
                return true;
            }
            catch (Exception e)
            {
                _history = null;
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// Moves an unreadable history file aside under a timestamped name, so the next save doesn't overwrite it.
        /// Returns the quarantined file name, or null if the move failed.
        /// </summary>
        private static string QuarantineHistoryFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string baseName = Path.GetFileNameWithoutExtension(path);
                string quarantinePath = Path.Combine(dir,
                    $"{baseName}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
                if (File.Exists(quarantinePath))
                    File.Delete(quarantinePath);
                File.Move(path, quarantinePath);
                return Path.GetFileName(quarantinePath);
            }
            catch (Exception e)
            {
                SkillsLogger.LogWarning($"Failed to quarantine unreadable workflow history: {e.Message}");
                return null;
            }
        }

        public static void SaveHistory()
        {
            try
            {
                _history ??= new WorkflowHistoryData();
                _history.EnsureDefaults();
                BindPendingAgents();
                // Compact: indentation made up a large share of the file that every tracked write rewrites.
                string json = JsonUtility.ToJson(_history, false);
                // The file being replaced is kept as .bak: if the main file becomes unreadable, LoadHistory falls back to it.
                LibraryStateFile.WriteAllText(HistoryFilePath, json, HistoryFilePath + ".bak");
                _lastSavedHistoryChars = json.Length;
            }
            catch (Exception e)
            {
                SkillsLogger.LogError($"Failed to save workflow history: {e.Message}");
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

        /// <summary>
        /// Queues one deferred trim if none is pending and the last one ran more than <see cref="TrimIntervalSeconds"/> ago;
        /// it trims only when <see cref="ExceedsTrimGate"/> holds. A deferred run that outlives a reset, a clear or a switch
        /// of history file does nothing.
        /// </summary>
        private static void ScheduleTrim()
        {
            if (_trimScheduled || (DateTime.UtcNow - _lastTrimUtc).TotalSeconds < TrimIntervalSeconds)
                return;

            _trimScheduled = true;
            int generation = _trimGeneration;
            string historyPath = HistoryFilePath;
            EditorApplication.delayCall += () =>
            {
                // A reset or clear has already released the slot (and may have queued a newer run).
                if (generation != _trimGeneration)
                    return;

                _trimScheduled = false;
                if (_history == null || historyPath != HistoryFilePath || !ExceedsTrimGate())
                    return;

                _lastTrimUtc = DateTime.UtcNow;
                if (TrimHistoryIfNeeded().removedTasks > 0)
                    SaveHistory();
                _sizeGateChars = (long)(_lastSavedHistoryChars * TrimGateFactor);
            };
        }

        /// <summary>
        /// The deferred trim's cheap pre-check: the task count, the age of the oldest task or the size of the last saved
        /// file is more than 10% past its limit. Nothing here walks the snapshots or the file store; the file store is
        /// pruned by the trim this lets through and by the one every history load runs.
        /// </summary>
        internal static bool ExceedsTrimGate()
        {
            if (_history == null || _historyRecoveryMode || !WorkflowAutoCleanConfig.Enabled)
                return false;

            int maxTasks = WorkflowAutoCleanConfig.MaxTasks;
            if (maxTasks > 0 && Math.Max(_history.tasks.Count, _history.undoneStack.Count) > maxTasks * TrimGateFactor)
                return true;

            long maxHistoryBytes = WorkflowAutoCleanConfig.MaxHistoryMB * 1024L * 1024L;
            if (maxHistoryBytes > 0 && _lastSavedHistoryChars > Math.Max(maxHistoryBytes * TrimGateFactor, _sizeGateChars))
                return true;

            int maxAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays;
            if (maxAgeDays <= 0)
                return false;
            long cutoff = DateTimeOffset.Now.AddDays(-maxAgeDays * TrimGateFactor).ToUnixTimeSeconds();
            return _history.tasks.Any(t => t?.timestamp < cutoff) || _history.undoneStack.Any(t => t?.timestamp < cutoff);
        }

        /// <summary>
        /// Gets the list of tasks available to redo (already undone).
        /// </summary>
        public static List<WorkflowTask> GetUndoneStack()
        {
            return History.undoneStack;
        }

        /// <summary>
        /// Clears the undone stack (called when new changes occur after an undo).
        /// </summary>
        public static void ClearUndoneStack()
        {
            if (_history != null)
            {
                _history.undoneStack.Clear();
                SaveHistory();
            }
        }

        public static void DeleteTask(string taskId)
        {
            if (_history == null) LoadHistory();
            var task = _history.tasks.FirstOrDefault(t => MatchesTaskId(t, taskId));
            if (task != null)
            {
                _history.tasks.Remove(task);
            }
            else
            {
                // A task that's already been undone lives in undoneStack, not in tasks. Leaving it there
                // after a "delete" would make workflow_redo_task's default behavior (take the last item in
                // undoneStack) resurrect an object the caller believes is already gone.
                var undoneTask = _history.undoneStack.FirstOrDefault(t => MatchesTaskId(t, taskId));
                if (undoneTask != null)
                    _history.undoneStack.Remove(undoneTask);
            }

            SaveHistory();

            if (_historyRecoveryMode)
                return;

            var referencedHashes = CollectReferencedHashes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out _, out _);
        }

        /// <summary>
        /// Collects the task currently being recorded, plus every file hash referenced by all active tasks
        /// and undone tasks.
        /// </summary>
        private static HashSet<string> CollectReferencedHashes()
        {
            // Case-insensitive: store entries are uppercased when enumerated, so a snapshot hash that
            // differs only in case must also count as a reference.
            var referencedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The task currently being recorded isn't in _history yet — history is lazily loaded, and both
            // LoadHistory and EndTask reclaim before appending it — so it relies on this line for protection.
            AddTaskHashes(_currentTask, referencedHashes);

            if (_history == null) return referencedHashes;

            foreach (var task in _history.tasks.Concat(_history.undoneStack))
            {
                AddTaskHashes(task, referencedHashes);
            }
            return referencedHashes;
        }

        private static void AddTaskHashes(WorkflowTask task, HashSet<string> hashes)
        {
            if (task?.snapshots == null) return;
            foreach (var snapshot in task.snapshots)
            {
                AddSnapshotHashes(snapshot, hashes);
            }
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

        #region Auto-Cleanup

        /// <summary>
        /// Trims the workflow history and the content-addressed file store according to
        /// WorkflowAutoCleanConfig's settings. Called automatically after EndTask and LoadHistory.
        /// </summary>
        public static WorkflowTrimReport TrimHistoryIfNeeded(bool force = false)
        {
            var report = new WorkflowTrimReport();
            if (_history == null) LoadHistory();
            TrimRunsForTests++;
            if (!force && !WorkflowAutoCleanConfig.Enabled)
                return report;

            if (_historyRecoveryMode)
            {
                if (force)
                {
                    SkillsLogger.LogWarning(
                        "Workflow cleanup skipped: the history file failed to load this session, so the set of " +
                        "referenced backups is incomplete. Clear the history to re-enable cleanup.");
                }
                return report;
            }

            var now = DateTimeOffset.Now;
            int maxAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays;
            int maxTasks = WorkflowAutoCleanConfig.MaxTasks;
            long maxHistoryBytes = WorkflowAutoCleanConfig.MaxHistoryMB * 1024L * 1024L;

            int beforeCount = _history.tasks.Count + _history.undoneStack.Count;

            // Delete tasks older than MaxTaskAgeDays
            if (maxAgeDays > 0)
            {
                long cutoff = now.AddDays(-maxAgeDays).ToUnixTimeSeconds();
                _history.tasks.RemoveAll(t => t?.timestamp < cutoff);
                _history.undoneStack.RemoveAll(t => t?.timestamp < cutoff);
            }

            // Delete oldest-first until the task count is below MaxTasks
            if (maxTasks > 0)
            {
                while (_history.tasks.Count > maxTasks)
                    _history.tasks.RemoveAt(0);
                while (_history.undoneStack.Count > maxTasks)
                    _history.undoneStack.RemoveAt(0);
            }

            // Delete oldest-first until the estimated serialized size is below MaxHistoryMB
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

            // Reclaim unreferenced file-store entries
            var referencedHashes = CollectReferencedHashes();
            long beforeBytes = WorkflowFileStore.GetStoreSizeBytes();
            WorkflowFileStore.CollectGarbage(referencedHashes, out int reclaimedCount, out _);
            report.reclaimedFileEntries = reclaimedCount;

            // Trim the file store by storage age and total size
            int storeMaxAgeDays = WorkflowAutoCleanConfig.StoreMaxAgeDays;
            long maxStoreBytes = WorkflowAutoCleanConfig.MaxStoreMB > 0
                ? WorkflowAutoCleanConfig.MaxStoreMB * 1024L * 1024L
                : 0;
            if (storeMaxAgeDays > 0 || maxStoreBytes > 0)
            {
                DateTime? storeCutoff = storeMaxAgeDays > 0
                    ? now.AddDays(-storeMaxAgeDays).UtcDateTime
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
            long size = 64; // Task metadata overhead
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
                        64; // Per-snapshot overhead
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
            // Ids keep counting: an s<seq> an agent still holds must never name a task recorded after the clear.
            _history = new WorkflowHistoryData { nextSeq = History.nextSeq };
            _trimGeneration++;
            _trimScheduled = false;
            _sizeGateChars = 0;
            _pendingAgentTasks.Clear();
            // The blob for the task still being recorded is preserved: it was never part of the history the
            // user asked to clear in the first place.
            // Everything else gets cleaned out — this skill promises to clear the file store, so the
            // "recently written" grace period doesn't apply here — which also resynchronizes the file
            // store with history and clears recovery mode.
            WorkflowFileStore.CollectGarbage(CollectReferencedHashes(), out _, out _, includeRecentWrites: true);
            _historyRecoveryMode = false;
            SaveHistory();
        }

        /// <summary>
        /// Returns the workflow history JSON file's on-disk size (bytes); returns 0 if the file doesn't exist.
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
