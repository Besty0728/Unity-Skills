using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillsHttpServer
    {
        /// <summary>
        /// Routes GET /jobs and GET /jobs/{id}[/logs] directly to BatchPersistence, bypassing the skill router.
        /// Designed for high-frequency progress polling: a caller pings GET /jobs/{id} every 200-500ms to get the latest snapshot.
        /// </summary>
        private static void HandleJobsRequest(RequestJob job)
        {
            string path = job.Path ?? string.Empty;
            var qs = SkillRouter.ParseQueryString(job.QueryString);

            // GET /jobs  → list
            if (string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/jobs/", StringComparison.OrdinalIgnoreCase))
            {
                int limit = 50;
                if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                    limit = Mathf.Clamp(lp, 1, 100);

                var jobs = BatchPersistence.ListJobs(limit);
                var projected = new System.Collections.Generic.List<object>(jobs.Length);
                foreach (var r in jobs)
                {
                    projected.Add(new
                    {
                        jobId = r.jobId,
                        kind = r.kind,
                        status = r.status,
                        progress = r.progress,
                        currentStage = r.currentStage,
                        startedAt = r.startedAt,
                        updatedAt = r.updatedAt,
                        resultSummary = r.resultSummary,
                        error = r.error,
                    });
                }

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    count = projected.Count,
                    jobs = projected,
                }, _jsonSettings);
                return;
            }

            // GET /jobs/{id}[/logs]
            const string prefix = "/jobs/";
            string remainder = path.Substring(prefix.Length).TrimEnd('/');
            string jobId;
            string subResource = null;
            int slashIdx = remainder.IndexOf('/');
            if (slashIdx >= 0)
            {
                jobId = remainder.Substring(0, slashIdx);
                subResource = remainder.Substring(slashIdx + 1);
            }
            else
            {
                jobId = remainder;
            }

            if (string.IsNullOrEmpty(jobId))
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing job id in path",
                    details: new { example = "/jobs/{id}" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            var record = BatchPersistence.GetJob(jobId);
            if (record == null)
            {
                job.StatusCode = 404;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.NotFound,
                    $"Job not found: {jobId}",
                    details: new { jobId },
                    retryStrategy: SkillErrorResponse.Abort);
                return;
            }

            if (string.Equals(subResource, "progress", StringComparison.OrdinalIgnoreCase))
            {
                int offset = 0;
                if (qs.TryGetValue("offset", out var off) && int.TryParse(off, out var offp))
                    offset = Math.Max(0, offp);

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(
                    AsyncJobService.BuildProgressSnapshot(record, offset),
                    _jsonSettings);
                return;
            }

            if (string.Equals(subResource, "logs", StringComparison.OrdinalIgnoreCase))
            {
                int limit = 100;
                if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                    limit = Mathf.Clamp(lp, 1, 500);

                var logs = record.logs ?? new System.Collections.Generic.List<BatchJobLogEntry>();
                int skip = Math.Max(0, logs.Count - limit);
                var sliced = logs.Skip(skip)
                    .Select(e => new
                    {
                        timestamp = e.timestamp,
                        level = e.level,
                        stage = e.stage,
                        message = e.message,
                        code = e.code,
                    })
                    .ToArray();

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    jobId = record.jobId,
                    count = sliced.Length,
                    totalCount = logs.Count,
                    logs = sliced,
                }, _jsonSettings);
                return;
            }

            // GET /jobs/{id} (default — full status snapshot)
            int recentCount = 10;
            if (qs.TryGetValue("recentCount", out var rc) && int.TryParse(rc, out var rcp))
                recentCount = Mathf.Clamp(rcp, 1, 200);
            var recentEvents = record.progressEvents == null
                ? Array.Empty<object>()
                : record.progressEvents
                    .Skip(Math.Max(0, record.progressEvents.Count - recentCount))
                    .Select(e => new
                    {
                        timestamp = e.timestamp,
                        progress = e.progress,
                        stage = e.stage,
                        description = e.description,
                    }).ToArray();

            bool terminal = IsTerminalStatus(record.status);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                jobId = record.jobId,
                kind = record.kind,
                status = record.status,
                progress = record.progress,
                currentStage = record.currentStage,
                progressStage = record.progressStage,
                startedAt = record.startedAt,
                updatedAt = record.updatedAt,
                processedItems = record.processedItems,
                totalItems = record.totalItems,
                resultSummary = record.resultSummary,
                error = record.error,
                warnings = record.warnings,
                reportId = record.reportId,
                relatedWorkflowId = record.relatedWorkflowId,
                canCancel = record.canCancel,
                recentProgress = recentEvents,
                terminal,
            }, _jsonSettings);

            // A ?wait= long-poll exists to save the follow-up read, so its terminal answer also carries the job's
            // result (a script job's compile diagnostics, a test job's counts). The plain GET stays unchanged.
            if (job.IsInternalProbe && terminal)
            {
                var snapshot = JObject.Parse(job.ResponseJson);
                snapshot["resultData"] = record.resultData == null
                    ? JValue.CreateNull()
                    : JToken.FromObject(record.resultData, JsonSerializer.Create(_jsonSettings));
                job.ResponseJson = snapshot.ToString(Formatting.None);
            }
        }
    }
}

// Producer:Betsy
