using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

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

        private static string StorageDirectory => Path.Combine(Application.dataPath, "../Library/UnitySkills");
        private static string StoragePath => Path.Combine(StorageDirectory, "batch_state.json");

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

                if (!File.Exists(StoragePath))
                {
                    _state = new BatchStorageState();
                    return;
                }

                try
                {
                    _state = JsonConvert.DeserializeObject<BatchStorageState>(File.ReadAllText(StoragePath)) ?? new BatchStorageState();
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"Failed to load batch state: {ex.Message}");
                    _state = new BatchStorageState();
                }

                EnsureDefaults();
                PruneExpiredPreviews();
                NormalizeRunningJobsAfterReload();
            }
        }

        internal static void Save()
        {
            EnsureLoaded();

            lock (SyncRoot)
            {
                Directory.CreateDirectory(StorageDirectory);
                EnsureDefaults();
                PruneExpiredPreviews();
                var json = JsonConvert.SerializeObject(_state, JsonSettings);
                File.WriteAllText(StoragePath, json, new System.Text.UTF8Encoding(false));
            }
        }

        internal static void UpsertPreview(BatchPreviewEnvelope preview)
        {
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
            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.previews.RemoveAll(p => string.Equals(p.confirmToken, confirmToken, StringComparison.OrdinalIgnoreCase));
            }
            Save();
        }

        internal static void UpsertReport(BatchReportRecord report)
        {
            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.reports.RemoveAll(r => r.reportId == report.reportId);
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
            EnsureLoaded();
            lock (SyncRoot)
            {
                _state.jobs.RemoveAll(j => j.jobId == job.jobId);
                _state.jobs.Add(job);
                _state.jobs = _state.jobs
                    .OrderByDescending(j => j.updatedAt)
                    .Take(100)
                    .ToList();
            }
            Save();
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
    }
}
