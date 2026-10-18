using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    internal static class BatchPersistence
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        private static readonly object SyncRoot = new object();
        private static BatchStorageState _state;
        private static bool _dirty;
        [ThreadStatic] private static int _transientScopeDepth;

        private static string _storageDirectoryForTests;

        private static string StorageDirectory => _storageDirectoryForTests ?? Path.Combine(Application.dataPath, "../Library/UnitySkills");
        private static string StoragePath => Path.Combine(StorageDirectory, "batch_state.json");
        // The generation each save replaces. The loader falls back to it only when the current file is unreadable.
        private static string BackupPath => StoragePath + ".bak";
        // An unreadable current file is copied here before anything overwrites it, so the evidence survives.
        private static string CorruptPath => StoragePath + ".corrupt";

        internal static BatchStorageState State
        {
            get
            {
                EnsureLoaded();
                return _state;
            }
        }

        internal static void EnsureLoaded()
        {
            if (_state != null)
                return;

            lock (SyncRoot)
            {
                if (_state != null)
                    return;

                // An asset import worker shares this Library folder but not the job table: it never parses the file
                // the main editor may be rewriting, and Save never lets it write its copy over the editor's.
                bool recovered = false;
                _state = ImportWorkerGate.IsWorkerProcess ? new BatchStorageState() : LoadStateLocked(out recovered);
                EnsureDefaults();
                if (recovered)
                    PersistRecoveredStateLocked();
                PruneExpiredPreviews();
                NormalizeRunningJobsAfterReload();
            }
        }

        /// <summary>
        /// Reads the current file. Save swaps whole documents in by rename, so an unreadable current file means outside
        /// damage: its bytes are kept as batch_state.json.corrupt and the previous generation is used instead.
        /// </summary>
        private static BatchStorageState LoadStateLocked(out bool recoveredFromBackup)
        {
            recoveredFromBackup = false;
            string path = StoragePath;
            if (!File.Exists(path))
            {
                // An editor kill between the two renames of a backed-up swap leaves the next generation, complete,
                // under the temp name. A deleted state file with no temp file is a deliberate reset: start empty.
                string tmp = path + LibraryStateFile.TempSuffix;
                return File.Exists(tmp) && TryReadState(tmp, out var promoted, out _) ? promoted : new BatchStorageState();
            }

            if (TryReadState(path, out var current, out string error))
                return current;

            bool kept = TryKeepUnreadableCopy(path);
            string keptNote = kept ? $" The unreadable file is kept as {Path.GetFileName(CorruptPath)}." : string.Empty;
            if (File.Exists(BackupPath) && TryReadState(BackupPath, out var previous, out _))
            {
                recoveredFromBackup = true;
                int failed = FailUnfinishedJobs(previous);
                SkillsLogger.LogWarning($"Failed to load batch state: {error} Restored {previous.jobs?.Count ?? 0} jobs from the previous generation; " +
                    $"{failed} unfinished jobs from it were marked failed instead of resumed.{keptNote}");
                return previous;
            }

            SkillsLogger.LogWarning($"Failed to load batch state: {error} No readable previous generation; starting empty.{keptNote}");
            return new BatchStorageState();
        }

        private static bool TryReadState(string path, out BatchStorageState state, out string error)
        {
            try
            {
                // Every save writes an object, so an empty or null document is damage, not an empty job table.
                state = JsonConvert.DeserializeObject<BatchStorageState>(File.ReadAllText(path));
                error = state == null ? "The file holds no JSON object." : null;
                return state != null;
            }
            catch (Exception ex)
            {
                state = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool TryKeepUnreadableCopy(string path)
        {
            try
            {
                File.Copy(path, CorruptPath, true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A previous generation can predate a job's last progress, so resuming its unfinished jobs could repeat work
        /// that already ran. They end as failed with the reason; finished jobs keep their results.
        /// </summary>
        private static int FailUnfinishedJobs(BatchStorageState state)
        {
            if (state?.jobs == null)
                return 0;

            const string Stage = "state_recovery";
            const string Message = "The batch state file was unreadable and this job was restored from the previous generation, " +
                "which may predate its last progress; it was not resumed. Check the project state before submitting it again.";
            int failed = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var job in state.jobs.Where(job => job != null && !IsTerminalStatus(job.status)))
            {
                job.status = "failed";
                job.currentStage = Stage;
                job.progressStage = Stage;
                job.error = Message;
                job.resultSummary = Message;
                job.updatedAt = now;
                job.logs ??= new System.Collections.Generic.List<BatchJobLogEntry>();
                job.logs.Add(new BatchJobLogEntry { timestamp = now, level = "error", stage = Stage, message = Message, code = "state_recovered_from_backup" });
                failed++;
            }
            return failed;
        }

        private static bool IsTerminalStatus(string status) =>
            status == "completed" || status == "failed" || status == "cancelled";

        /// <summary>
        /// Writes the restored generation over the unreadable file at once, without rotating that file into the backup
        /// slot, so a kill before the next regular save does not lose the recovery.
        /// </summary>
        private static void PersistRecoveredStateLocked()
        {
            try
            {
                PruneExpiredPreviews();
                LibraryStateFile.WriteAllText(StoragePath, JsonConvert.SerializeObject(_state, JsonSettings));
                _dirty = false;
            }
            catch (Exception ex)
            {
                _dirty = true;
                SkillsLogger.LogWarning($"Failed to save the restored batch state: {ex.Message}");
            }
        }

        internal static bool IsTransientScopeActive => _transientScopeDepth > 0;

        internal static IDisposable BeginTransientScope()
        {
            _transientScopeDepth++;
            return new TransientScopeHandle();
        }

        internal static void Save()
        {
            EnsureLoaded();

            lock (SyncRoot)
            {
                EnsureDefaults();
                PruneExpiredPreviews();
                if (ImportWorkerGate.IsWorkerProcess)
                    return;

                // Temp file + rename: a reload, a kill or a reader never sees half a document, and the replaced
                // generation stays behind as the backup the loader can fall back to.
                var json = JsonConvert.SerializeObject(_state, JsonSettings);
                LibraryStateFile.WriteAllText(StoragePath, json, BackupPath);
                _dirty = false;
            }
        }

        /// <summary>
        /// Flush pending writes to disk when state has been modified. Should be called at key state transitions (job completed, cancelled, failed).
        /// </summary>
        internal static void FlushIfDirty()
        {
            if (_dirty) Save();
        }

        internal static void UpsertPreview(BatchPreviewEnvelope preview)
        {
            if (IsTransientScopeActive)
                return;

            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.previews.RemoveAll(p => p.confirmToken == preview.confirmToken);
                _state.previews.Add(preview);
            }
            Save();
        }

        internal static BatchPreviewEnvelope GetPreview(string confirmToken)
        {
            EnsureLoaded();
            PruneExpiredPreviews();
            return _state.previews.FirstOrDefault(p => string.Equals(p.confirmToken, confirmToken, StringComparison.OrdinalIgnoreCase));
        }

        internal static void RemovePreview(string confirmToken)
        {
            if (IsTransientScopeActive)
                return;

            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.previews.RemoveAll(p => string.Equals(p.confirmToken, confirmToken, StringComparison.OrdinalIgnoreCase));
            }
            Save();
        }

        internal static void UpsertReport(BatchReportRecord report)
        {
            if (IsTransientScopeActive)
                return;

            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.reports.RemoveAll(r => r.reportId == report.reportId);
                CompactReport(report);
                _state.reports.Add(report);
                _state.reports = _state.reports
                    .OrderByDescending(r => r.createdAt)
                    .Take(100)
                    .ToList();
            }
            Save();
        }

        internal static BatchReportRecord GetReport(string reportId)
        {
            EnsureLoaded();
            return _state.reports.FirstOrDefault(r => string.Equals(r.reportId, reportId, StringComparison.OrdinalIgnoreCase));
        }

        internal static BatchReportRecord[] ListReports(int limit)
        {
            EnsureLoaded();
            return _state.reports
                .OrderByDescending(r => r.createdAt)
                .Take(Mathf.Max(1, limit))
                .ToArray();
        }

        internal static void UpsertJob(BatchJobRecord job)
        {
            if (IsTransientScopeActive)
                return;

            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.jobs.RemoveAll(j => j.jobId == job.jobId);
                CompactJob(job);
                _state.jobs.Add(job);
                _state.jobs = _state.jobs
                    .OrderByDescending(j => j.updatedAt)
                    .Take(100)
                    .ToList();
                _dirty = true;
            }
        }

        internal static BatchJobRecord GetJob(string jobId)
        {
            EnsureLoaded();
            return _state.jobs.FirstOrDefault(j => string.Equals(j.jobId, jobId, StringComparison.OrdinalIgnoreCase));
        }

        internal static BatchJobRecord[] ListJobs(int limit)
        {
            EnsureLoaded();
            return _state.jobs
                .OrderByDescending(j => j.updatedAt)
                .Take(Mathf.Max(1, limit))
                .ToArray();
        }

        internal static void RemoveJob(string jobId)
        {
            if (IsTransientScopeActive)
                return;

            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.jobs.RemoveAll(j => string.Equals(j.jobId, jobId, StringComparison.OrdinalIgnoreCase));
                _dirty = true;
            }
        }

        private static void NormalizeRunningJobsAfterReload()
        {
            if (_state?.jobs == null)
                return;

            foreach (var job in _state.jobs.Where(j => j != null))
            {
                if (job.status != "completed" &&
                    job.status != "failed" &&
                    job.status != "cancelled")
                {
                    job.status = "reconnecting";
                    job.currentStage = "domain_reload_recovery";
                    job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    job.logs.Add(new BatchJobLogEntry
                    {
                        timestamp = job.updatedAt,
                        level = "warn",
                        stage = "recovery",
                        message = "Job reloaded after domain reload and will resume automatically.",
                        code = "domain_reload_recovery"
                    });
                }
            }
        }

        private static void PruneExpiredPreviews()
        {
            if (_state?.previews == null)
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _state.previews.RemoveAll(p => p == null || p.expiresAt <= now);
        }

        private static void EnsureDefaults()
        {
            _state ??= new BatchStorageState();
            _state.previews ??= new System.Collections.Generic.List<BatchPreviewEnvelope>();
            _state.reports ??= new System.Collections.Generic.List<BatchReportRecord>();
            _state.jobs ??= new System.Collections.Generic.List<BatchJobRecord>();

            foreach (var preview in _state.previews.Where(preview => preview != null))
            {
                preview.items ??= new System.Collections.Generic.List<BatchPreviewItem>();
                preview.operation ??= new System.Collections.Generic.Dictionary<string, object>();
            }

            foreach (var report in _state.reports.Where(report => report != null))
            {
                report.operation ??= new System.Collections.Generic.Dictionary<string, object>();
                report.items ??= new System.Collections.Generic.List<BatchReportItemRecord>();
                report.failureGroups ??= new System.Collections.Generic.List<BatchFailureGroup>();
                report.totals ??= new BatchReportTotals();
            }

            foreach (var job in _state.jobs.Where(job => job != null))
            {
                if (job.preview != null)
                {
                    job.preview.items ??= new System.Collections.Generic.List<BatchPreviewItem>();
                    job.preview.operation ??= new System.Collections.Generic.Dictionary<string, object>();
                }
                job.items ??= new System.Collections.Generic.List<BatchReportItemRecord>();
                job.logs ??= new System.Collections.Generic.List<BatchJobLogEntry>();
                job.warnings ??= new System.Collections.Generic.List<string>();
                job.metadata ??= new System.Collections.Generic.Dictionary<string, object>();
                job.resultData ??= new System.Collections.Generic.Dictionary<string, object>();
            }
        }

        private static void CompactReport(BatchReportRecord report)
        {
            if (report == null)
                return;

            report.items ??= new System.Collections.Generic.List<BatchReportItemRecord>();
            report.failureGroups ??= new System.Collections.Generic.List<BatchFailureGroup>();

            if (report.items.Count > 200)
                report.items = report.items.Take(200).ToList();

            if (report.failureGroups.Count > 50)
                report.failureGroups = report.failureGroups.Take(50).ToList();
        }

        private static void CompactJob(BatchJobRecord job)
        {
            if (job == null)
                return;

            job.items ??= new System.Collections.Generic.List<BatchReportItemRecord>();
            job.logs ??= new System.Collections.Generic.List<BatchJobLogEntry>();
            job.progressEvents ??= new System.Collections.Generic.List<BatchJobProgressEvent>();
            job.warnings ??= new System.Collections.Generic.List<string>();
            job.resultData ??= new System.Collections.Generic.Dictionary<string, object>();

            if (job.items.Count > 100)
                job.items = job.items.Take(100).ToList();

            if (job.logs.Count > 100)
                job.logs = job.logs.Skip(job.logs.Count - 100).ToList();

            if (job.progressEvents.Count > 100)
                job.progressEvents = job.progressEvents.Skip(job.progressEvents.Count - 100).ToList();

            if (job.warnings.Count > 20)
                job.warnings = job.warnings.Take(20).ToList();
        }

        /// <summary>Points persistence at <paramref name="directory"/> with nothing loaded; Dispose restores the live state.</summary>
        internal static IDisposable BeginTestScope(string directory) => new TestScope(directory);

        /// <summary>Drops the in-memory state and loads it again, as a domain reload does.</summary>
        internal static void ReloadForTests()
        {
            lock (SyncRoot)
            {
                _state = null;
                _dirty = false;
            }
            EnsureLoaded();
        }

        private sealed class TestScope : IDisposable
        {
            private readonly string _oldDirectory;
            private readonly BatchStorageState _oldState;
            private readonly bool _oldDirty;

            internal TestScope(string directory)
            {
                lock (SyncRoot)
                {
                    _oldDirectory = _storageDirectoryForTests;
                    _oldState = _state;
                    _oldDirty = _dirty;
                    _storageDirectoryForTests = directory;
                    _state = null;
                    _dirty = false;
                }
            }

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    _storageDirectoryForTests = _oldDirectory;
                    _state = _oldState;
                    _dirty = _oldDirty;
                }
            }
        }

        private sealed class TransientScopeHandle : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                if (_transientScopeDepth > 0)
                    _transientScopeDepth--;
            }
        }
    }
}

// Producer:Betsy
