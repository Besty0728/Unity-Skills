using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;

namespace UnitySkills
{
    [InitializeOnLoad]
    internal static class BatchJobService
    {
        private sealed class RuntimeJobContext
        {
            public BatchJobRecord Job;
            public bool StartedWorkflowSession;
            public List<BatchPreviewItem> ExecutableItems;
        }

        private static readonly Dictionary<string, RuntimeJobContext> RuntimeJobs =
            new Dictionary<string, RuntimeJobContext>(StringComparer.OrdinalIgnoreCase);

        static BatchJobService()
        {
            BatchPersistence.EnsureLoaded();
            ResumeJobs();
            EditorApplication.update += ProcessQueuedJobs;
        }

        internal static BatchJobRecord Start(BatchPreviewEnvelope preview, int chunkSize)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var job = new BatchJobRecord
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = preview.kind,
                status = "queued",
                progress = 0,
                currentStage = "queued",
                startedAt = now,
                updatedAt = now,
                resultSummary = "Job created and waiting to start.",
                chunkSize = Math.Max(1, chunkSize),
                totalItems = preview.items.Count(item => item.valid && item.willChange),
                preview = preview
            };

            job.logs.Add(new BatchJobLogEntry
            {
                timestamp = now,
                level = "info",
                stage = "queued",
                message = $"Created batch job for {preview.kind} with {job.totalItems} executable items."
            });

            var context = CreateContext(job);
            RuntimeJobs[job.jobId] = context;
            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static BatchJobRecord Get(string jobId)
        {
            return BatchPersistence.GetJob(jobId);
        }

        internal static BatchJobRecord[] List(int limit)
        {
            return BatchPersistence.ListJobs(limit);
        }

        internal static BatchJobRecord Cancel(string jobId)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null)
                return null;

            if (IsTerminal(job.status))
                return job;

            job.status = "cancelled";
            job.currentStage = "cancelled";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = "Job was cancelled.";
            job.logs.Add(new BatchJobLogEntry
            {
                timestamp = job.updatedAt,
                level = "warn",
                stage = "cancelled",
                message = "Cancellation requested."
            });
            BatchPersistence.UpsertJob(job);
            return job;
        }

        /// <summary>Removes runtime context for a cancelled job so chunk execution stops.</summary>
        internal static void NotifyCancelled(string jobId)
        {
            if (!string.IsNullOrEmpty(jobId) && RuntimeJobs.TryGetValue(jobId, out var context))
                FinalizeRuntimeContext(context);
        }

        internal static BatchJobRecord Wait(string jobId, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
            BatchJobRecord job;
            do
            {
                Pump(jobId);
                job = BatchPersistence.GetJob(jobId);
                if (job == null || IsTerminal(job.status))
                    return job;

                Thread.Sleep(25);
            }
            while (DateTime.UtcNow < deadline);

            return BatchPersistence.GetJob(jobId);
        }

        internal static void Pump(string jobId = null)
        {
            ProcessQueuedJobs(jobId);
        }

        private static void ResumeJobs()
        {
            foreach (var job in BatchPersistence.ListJobs(100))
            {
                if (job?.preview == null || IsTerminal(job.status))
                    continue;

                if (!RuntimeJobs.ContainsKey(job.jobId))
                    RuntimeJobs[job.jobId] = CreateContext(job);
            }
        }

        private static RuntimeJobContext CreateContext(BatchJobRecord job)
        {
            return new RuntimeJobContext
            {
                Job = job,
                ExecutableItems = job.preview.items
                    .Where(item => item.valid && item.willChange)
                    .ToList()
            };
        }

        private static void ProcessQueuedJobs()
        {
            ProcessQueuedJobs(null);
        }

        private static void ProcessQueuedJobs(string onlyJobId)
        {
            if (RuntimeJobs.Count == 0)
                return;

            foreach (var context in RuntimeJobs.Values.ToList())
            {
                if (!string.IsNullOrEmpty(onlyJobId) &&
                    !string.Equals(context.Job.jobId, onlyJobId, StringComparison.OrdinalIgnoreCase))
                    continue;

                ProcessSingleJob(context);
            }
        }

        private static void ProcessSingleJob(RuntimeJobContext context)
        {
            var job = context.Job;
            if (job == null)
                return;

            if (job.status == "cancelled")
            {
                FinishCancelledJob(context);
                return;
            }

            if (IsTerminal(job.status))
            {
                FinalizeRuntimeContext(context);
                return;
            }

            try
            {
                EnsureWorkflowSession(context);

                if (job.status == "queued" || job.status == "reconnecting")
                {
                    job.status = "running";
                    job.currentStage = "running";
                    AddLog(job, "info", "running", "Job is now running.");
                }

                if (job.totalItems == 0)
                {
                    FinishCompletedJob(context);
                    return;
                }

                var chunkStart = job.processedItems;
                if (chunkStart >= context.ExecutableItems.Count)
                {
                    FinishCompletedJob(context);
                    return;
                }

                var chunkItems = context.ExecutableItems
                    .Skip(chunkStart)
                    .Take(job.chunkSize)
                    .ToList();

                var chunkIndex = chunkStart / Math.Max(1, job.chunkSize);
                job.currentStage = $"chunk_{chunkIndex}";
                AddLog(job, "info", job.currentStage, $"Processing chunk {chunkIndex + 1} with {chunkItems.Count} items.");

                foreach (var item in chunkItems)
                {
                    var result = BatchSkills.ExecutePreviewItem(job.preview, item, chunkIndex);
                    job.items.Add(result);
                    job.processedItems++;
                }

                job.progress = job.totalItems <= 0 ? 100 : (int)Math.Round(job.processedItems * 100.0 / job.totalItems);
                job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                job.resultSummary = $"{job.processedItems}/{job.totalItems} items processed.";

                if (job.processedItems >= context.ExecutableItems.Count)
                    FinishCompletedJob(context);
                else
                    BatchPersistence.UpsertJob(job);
            }
            catch (Exception ex)
            {
                job.status = "failed";
                job.currentStage = "failed";
                job.error = ex.Message;
                job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                job.resultSummary = $"Job failed: {ex.Message}";
                AddLog(job, "error", "failed", ex.Message);
                BatchPersistence.UpsertJob(job);
                EndWorkflowSession(context);
                FinalizeRuntimeContext(context);
            }
        }

        private static void EnsureWorkflowSession(RuntimeJobContext context)
        {
            if (!string.IsNullOrEmpty(context.Job.relatedWorkflowId) && WorkflowManager.HasActiveSession)
                return;

            if (WorkflowManager.HasActiveSession)
            {
                context.Job.relatedWorkflowId = WorkflowManager.CurrentSessionId;
                return;
            }

            context.Job.relatedWorkflowId = WorkflowManager.BeginSession($"Batch:{context.Job.kind}");
            context.StartedWorkflowSession = true;
            context.Job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AddLog(context.Job, "info", "workflow", $"Workflow session started: {context.Job.relatedWorkflowId}");
        }

        private static void FinishCompletedJob(RuntimeJobContext context)
        {
            var job = context.Job;
            job.status = "completed";
            job.currentStage = "completed";
            job.progress = 100;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var report = BatchSkills.CreateReportFromJob(job);
            job.reportId = report.reportId;
            job.resultSummary = report.summary;
            BatchPersistence.UpsertReport(report);
            AddLog(job, "info", "completed", report.summary);
            BatchPersistence.UpsertJob(job);
            EndWorkflowSession(context);
            FinalizeRuntimeContext(context);
        }

        private static void FinishCancelledJob(RuntimeJobContext context)
        {
            var job = context.Job;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BatchPersistence.UpsertJob(job);
            EndWorkflowSession(context);
            FinalizeRuntimeContext(context);
        }

        private static void EndWorkflowSession(RuntimeJobContext context)
        {
            if (!context.StartedWorkflowSession)
                return;

            if (WorkflowManager.HasActiveSession &&
                string.Equals(WorkflowManager.CurrentSessionId, context.Job.relatedWorkflowId, StringComparison.OrdinalIgnoreCase))
            {
                WorkflowManager.EndSession();
            }

            context.StartedWorkflowSession = false;
        }

        private static void FinalizeRuntimeContext(RuntimeJobContext context)
        {
            if (context?.Job == null)
                return;

            RuntimeJobs.Remove(context.Job.jobId);
        }

        private static bool IsTerminal(string status)
        {
            return status == "completed" || status == "failed" || status == "cancelled";
        }

        private static void AddLog(BatchJobRecord job, string level, string stage, string message)
        {
            job.logs.Add(new BatchJobLogEntry
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                level = level,
                stage = stage,
                message = message
            });
        }
    }
}
