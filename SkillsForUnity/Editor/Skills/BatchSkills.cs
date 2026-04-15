using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    public static class BatchSkills
    {
        private const int PreviewTtlSeconds = 3600;
        private const int DefaultSampleLimit = 10;
        private static readonly Regex MultiWhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex MultiUnderscoreRegex = new Regex(@"_+", RegexOptions.Compiled);

        [UnitySkill("batch_query_gameobjects", "Query GameObjects with unified batch filters. queryJson supports name/path/instanceId/tag/layer/active/componentType/sceneName/parentPath/prefabSource/includeInactive/limit.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "query", "gameobject", "filter" },
            Outputs = new[] { "count", "objects", "summary" },
            ReadOnly = true)]
        public static object BatchQueryGameObjects(string queryJson = null, int sampleLimit = 20)
        {
            var query = ParseQuery(queryJson);
            var targets = QueryTargets(query);
            var objects = targets.Take(Math.Max(1, sampleLimit)).Select(BuildTargetInfo).ToArray();
            return new { success = true, count = targets.Count, summary = $"Matched {targets.Count} GameObjects.", query, objects };
        }

        [UnitySkill("batch_query_components", "Query components with unified batch filters. Optional componentType narrows the result.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "query", "component", "filter" },
            Outputs = new[] { "count", "objects", "summary" },
            ReadOnly = true)]
        public static object BatchQueryComponents(string queryJson = null, string componentType = null, int sampleLimit = 20)
        {
            var query = ParseQuery(queryJson);
            if (!string.IsNullOrWhiteSpace(componentType))
                query.componentType = componentType;

            var targets = QueryTargets(query);
            var objects = targets.Take(Math.Max(1, sampleLimit)).Select(go => new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetCachedPath(go),
                components = go.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().Name).ToArray()
            }).ToArray();

            return new { success = true, count = targets.Count, summary = $"Matched {targets.Count} objects with component filters.", query, objects };
        }

        [UnitySkill("batch_query_assets", "Query project assets by type, path pattern, and labels.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "query", "asset", "filter", "project" },
            Outputs = new[] { "count", "assets", "summary" },
            ReadOnly = true)]
        public static object BatchQueryAssets(
            string searchFilter = null,
            string folder = "Assets",
            string typeFilter = null,
            string namePattern = null,
            string labelFilter = null,
            int maxResults = 200)
        {
            var filterParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchFilter))
                filterParts.Add(searchFilter);
            if (!string.IsNullOrWhiteSpace(typeFilter))
                filterParts.Add(typeFilter.StartsWith("t:") ? typeFilter : $"t:{typeFilter}");
            if (!string.IsNullOrWhiteSpace(labelFilter))
                filterParts.Add(labelFilter.StartsWith("l:") ? labelFilter : $"l:{labelFilter}");

            var filter = string.Join(" ", filterParts);
            var searchFolders = string.IsNullOrWhiteSpace(folder) ? new string[0] : new[] { folder };

            string[] guids;
            try { guids = AssetDatabase.FindAssets(filter, searchFolders); }
            catch (Exception ex) { return new { error = $"FindAssets failed: {ex.Message}" }; }

            System.Text.RegularExpressions.Regex regex = null;
            if (!string.IsNullOrWhiteSpace(namePattern))
            {
                try { regex = new System.Text.RegularExpressions.Regex(namePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                catch (Exception ex) { return new { error = $"Invalid namePattern regex: {ex.Message}" }; }
            }

            var assets = new List<object>();
            foreach (var guid in guids)
            {
                if (assets.Count >= maxResults)
                    break;

                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

                if (regex != null && !regex.IsMatch(fileName))
                    continue;

                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                assets.Add(new
                {
                    path = assetPath,
                    name = fileName,
                    type = assetType?.Name ?? "Unknown",
                    guid
                });
            }

            return new
            {
                success = true,
                count = assets.Count,
                totalMatched = guids.Length,
                summary = $"Found {assets.Count} assets" + (guids.Length > assets.Count ? $" (showing {assets.Count} of {guids.Length})" : "") + ".",
                filter,
                folder,
                assets = assets.ToArray()
            };
        }

        [UnitySkill("batch_preview_rename", "Preview batch renaming. mode supports prefix/suffix/replace/regex_replace.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "preview", "rename" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchPreviewRename(
            string queryJson = null,
            string mode = "prefix",
            string prefix = null,
            string suffix = null,
            string search = null,
            string replacement = null,
            string regexPattern = null,
            string regexReplacement = null,
            int sampleLimit = DefaultSampleLimit)
        {
            var query = ParseQuery(queryJson);
            var preview = BuildRenamePreview(query, mode, prefix, suffix, search, replacement, regexPattern, regexReplacement);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_preview_set_property", "Preview setting a component property or field across queried targets.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "preview", "property", "component" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchPreviewSetProperty(
            string queryJson = null,
            string componentType = null,
            string propertyName = null,
            string value = null,
            string referencePath = null,
            string referenceName = null,
            string assetPath = null,
            int sampleLimit = DefaultSampleLimit)
        {
            var query = ParseQuery(queryJson);
            var preview = BuildSetPropertyPreview(query, componentType, propertyName, value, referencePath, referenceName, assetPath);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_preview_replace_material", "Preview replacing Renderer materials across queried targets.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "preview", "material", "renderer" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchPreviewReplaceMaterial(string queryJson = null, string materialPath = null, int sampleLimit = DefaultSampleLimit)
        {
            var query = ParseQuery(queryJson);
            var preview = BuildReplaceMaterialPreview(query, materialPath);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_execute", "Execute a previously previewed batch operation by confirmToken. Large operations return a jobId.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "batch", "execute", "job", "report" },
            Outputs = new[] { "jobId", "reportId", "workflowId", "status" },
            RequiresInput = new[] { "confirmToken" },
            SupportsDryRun = false)]
        public static object BatchExecute(string confirmToken, bool runAsync = true, int chunkSize = 100)
        {
            if (Validate.Required(confirmToken, "confirmToken") is object err)
                return err;

            var preview = BatchPersistence.GetPreview(confirmToken);
            if (preview == null)
                return new { success = false, error = "Invalid or expired confirmToken. Call preview again to get a new token." };
            if (preview.executableCount <= 0)
            {
                BatchPersistence.RemovePreview(confirmToken);
                return new { success = false, error = "Preview contains no executable changes." };
            }

            BatchPersistence.RemovePreview(confirmToken);
            var job = BatchJobService.Start(preview, chunkSize);
            if (runAsync || preview.executableCount > chunkSize)
            {
                return new
                {
                    success = true,
                    status = "accepted",
                    jobId = job.jobId,
                    workflowId = job.relatedWorkflowId,
                    totalItems = job.totalItems,
                    message = "Batch job created. Use job_status/job_wait or batch_report_get after completion."
                };
            }

            var completed = BatchJobService.Wait(job.jobId, Math.Max(5000, preview.executableCount * 50));
            if (completed == null)
                return new { success = false, error = "Job disappeared during execution." };

            return new
            {
                success = completed.status == "completed",
                status = completed.status,
                jobId = completed.jobId,
                reportId = completed.reportId,
                workflowId = completed.relatedWorkflowId,
                resultSummary = completed.resultSummary,
                error = completed.error
            };
        }

        [UnitySkill("batch_report_get", "Get a batch execution report by reportId.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "report", "details" },
            Outputs = new[] { "reportId", "totals", "items", "failureGroups" },
            RequiresInput = new[] { "reportId" },
            ReadOnly = true)]
        public static object BatchReportGet(string reportId)
        {
            if (Validate.Required(reportId, "reportId") is object err)
                return err;
            var report = BatchPersistence.GetReport(reportId);
            if (report == null)
                return new { success = false, error = $"Report not found: {reportId}" };

            return new
            {
                success = true,
                reportId = report.reportId,
                kind = report.kind,
                status = report.status,
                summary = report.summary,
                createdAt = report.createdAt,
                workflowId = report.workflowId,
                jobId = report.jobId,
                rollbackAvailable = report.rollbackAvailable,
                query = report.query,
                operation = report.operation,
                totals = report.totals,
                failureGroups = report.failureGroups,
                items = report.items
            };
        }

        [UnitySkill("batch_report_list", "List recent batch reports.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "report", "list" },
            Outputs = new[] { "count", "reports" },
            ReadOnly = true)]
        public static object BatchReportList(int limit = 20)
        {
            var reports = BatchPersistence.ListReports(limit);
            return new
            {
                success = true,
                count = reports.Length,
                reports = reports.Select(report => new
                {
                    report.reportId,
                    report.kind,
                    report.status,
                    report.summary,
                    report.createdAt,
                    report.workflowId,
                    report.jobId,
                    report.totals
                }).ToArray()
            };
        }

        [UnitySkill("job_status", "Get status for an asynchronous UnitySkills job.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "status", "async" },
            Outputs = new[] { "jobId", "status", "progress", "currentStage" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true)]
        public static object JobStatus(string jobId)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Get(jobId);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            return new
            {
                success = true,
                jobId = job.jobId,
                kind = job.kind,
                status = job.status,
                progress = job.progress,
                currentStage = job.currentStage,
                progressStage = job.progressStage,
                recentProgress = job.progressEvents?.Skip(System.Math.Max(0, job.progressEvents.Count - 5)).ToArray(),
                startedAt = job.startedAt,
                updatedAt = job.updatedAt,
                warnings = job.warnings,
                resultSummary = job.resultSummary,
                workflowId = job.relatedWorkflowId,
                reportId = job.reportId,
                canCancel = job.canCancel && !IsTerminalStatus(job.status),
                error = job.error,
                details = job.resultData
            };
        }

        [UnitySkill("job_logs", "Get structured logs for a UnitySkills job.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "logs", "async" },
            Outputs = new[] { "jobId", "logs" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true)]
        public static object JobLogs(string jobId, int limit = 100)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Get(jobId);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            return new
            {
                success = true,
                jobId = job.jobId,
                status = job.status,
                logs = job.logs.OrderBy(entry => entry.timestamp).Take(Math.Max(1, limit)).ToArray()
            };
        }

        [UnitySkill("job_list", "List recent UnitySkills jobs.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "list", "async" },
            Outputs = new[] { "count", "jobs" },
            ReadOnly = true)]
        public static object JobList(int limit = 20)
        {
            var jobs = AsyncJobService.List(limit);
            return new
            {
                success = true,
                count = jobs.Length,
                jobs = jobs.Select(job => new
                {
                    job.jobId,
                    job.kind,
                    job.status,
                    job.progress,
                    job.currentStage,
                    job.startedAt,
                    job.updatedAt,
                    job.resultSummary,
                    workflowId = job.relatedWorkflowId,
                    job.reportId,
                    canCancel = job.canCancel && !IsTerminalStatus(job.status)
                }).ToArray()
            };
        }

        [UnitySkill("job_wait", "Wait for a UnitySkills job to finish or until timeoutMs elapses.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "job", "wait", "async" },
            Outputs = new[] { "jobId", "status", "reportId" },
            RequiresInput = new[] { "jobId" })]
        public static object JobWait(string jobId, int timeoutMs = 10000)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Wait(jobId, timeoutMs);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            return new
            {
                success = true,
                jobId = job.jobId,
                status = job.status,
                progress = job.progress,
                currentStage = job.currentStage,
                reportId = job.reportId,
                workflowId = job.relatedWorkflowId,
                resultSummary = job.resultSummary,
                error = job.error,
                details = job.resultData
            };
        }

        [UnitySkill("job_cancel", "Cancel a UnitySkills job if the job supports cancellation.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "job", "cancel", "async" },
            Outputs = new[] { "jobId", "status" },
            RequiresInput = new[] { "jobId" })]
        public static object JobCancel(string jobId)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Cancel(jobId);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            return new
            {
                success = true,
                jobId = job.jobId,
                status = job.status,
                resultSummary = job.resultSummary,
                warnings = job.warnings
            };
        }

        [UnitySkill("batch_fix_missing_scripts", "Preview batch removal of missing scripts. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "missing", "scripts", "cleanup" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchFixMissingScripts(string queryJson = null, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildMissingScriptsPreview(ParseQuery(queryJson));
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_standardize_naming", "Preview standardizing names by trimming whitespace and normalizing separators. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "naming", "standardize", "rename" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchStandardizeNaming(string queryJson = null, string separator = "_", int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildStandardizeNamingPreview(ParseQuery(queryJson), separator);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_set_render_layer", "Preview setting GameObject layers in batch. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "layer", "rendering", "gameobject" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchSetRenderLayer(string queryJson = null, string layer = null, bool recursive = false, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildSetLayerPreview(ParseQuery(queryJson), layer, recursive);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_replace_material", "Preview replacing materials in batch. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "material", "replace", "renderer" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchReplaceMaterial(string queryJson = null, string materialPath = null, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildReplaceMaterialPreview(ParseQuery(queryJson), materialPath);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_validate_scene_objects", "Analyze scene objects for missing scripts, missing references, duplicate names, and empty objects.",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "validate", "scene", "report" },
            Outputs = new[] { "summary", "issues" },
            ReadOnly = true)]
        public static object BatchValidateSceneObjects(int issueLimit = 100)
        {
            var scene = ValidationSkills.ValidateScene(checkEmptyGameObjects: true);
            var missingReferences = ValidationSkills.ValidateMissingReferences(issueLimit);
            return new
            {
                success = true,
                summary = "Combined scene validation report generated.",
                scene = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(scene)),
                missingReferences = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(missingReferences))
            };
        }

        [UnitySkill("batch_cleanup_temp_objects", "Preview deleting temporary helper objects by common temp-name patterns. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "cleanup", "temp", "delete" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true)]
        public static object BatchCleanupTempObjects(string queryJson = null, string patternsCsv = null, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildCleanupTempObjectsPreview(ParseQuery(queryJson), patternsCsv);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_retry_failed", "Re-run only the failed items from a previous batch execution report.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "batch", "retry", "failed", "recovery" },
            Outputs = new[] { "jobId", "retryCount", "originalReportId" })]
        public static object BatchRetryFailed(string reportId, bool runAsync = true, int chunkSize = 100)
        {
            if (Validate.Required(reportId, "reportId") is object err)
                return err;

            var report = BatchPersistence.GetReport(reportId);
            if (report == null)
                return new { error = $"Report not found: {reportId}" };

            var failedItems = report.items?.Where(i =>
                string.Equals(i.status, "failed", StringComparison.OrdinalIgnoreCase)).ToList();

            if (failedItems == null || failedItems.Count == 0)
                return new { success = true, retryCount = 0, message = "No failed items to retry.", originalReportId = reportId };

            if (!CanRetryFromReport(report))
            {
                return new
                {
                    success = false,
                    error = $"Report '{reportId}' does not contain enough operation context to retry kind '{report.kind}'. Re-run the original preview first."
                };
            }

            // Reconstruct a preview envelope from failed items
            var preview = new BatchPreviewEnvelope
            {
                confirmToken = Guid.NewGuid().ToString("N").Substring(0, 12),
                kind = report.kind ?? "retry",
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
                riskLevel = "medium",
                summary = $"Retry {failedItems.Count} failed items from report {reportId}.",
                rollbackAvailable = report.rollbackAvailable,
                mayCreateJob = true,
                query = CloneQuery(report.query),
                targetCount = failedItems.Count,
                executableCount = failedItems.Count,
                skipCount = 0,
                operation = CloneOperation(report.operation)
            };

            preview.operation["retrySource"] = reportId;

            foreach (var fi in failedItems)
            {
                preview.items.Add(new BatchPreviewItem
                {
                    action = fi.action ?? report.kind,
                    targetName = fi.targetName,
                    targetPath = fi.targetPath,
                    instanceId = fi.instanceId,
                    willChange = true,
                    valid = true
                });
            }

            BatchPersistence.UpsertPreview(preview);

            // Immediately execute
            var job = BatchJobService.Start(preview, chunkSize);
            if (!runAsync && job != null)
            {
                var result = BatchJobService.Wait(job.jobId, 30000);
                if (result != null && result.status == "completed")
                {
                    return new
                    {
                        success = true,
                        status = "completed",
                        retryCount = failedItems.Count,
                        originalReportId = reportId,
                        reportId = result.reportId,
                        resultSummary = result.resultSummary
                    };
                }
            }

            return new
            {
                success = true,
                status = "accepted",
                jobId = job?.jobId,
                retryCount = failedItems.Count,
                originalReportId = reportId
            };
        }

        internal static BatchReportItemRecord ExecutePreviewItem(BatchPreviewEnvelope preview, BatchPreviewItem item, int chunkIndex)
        {
            switch (preview.kind)
            {
                case "rename":
                case "standardize_naming":
                    return ExecuteRenameItem(item, chunkIndex);
                case "set_property":
                    return ExecuteSetPropertyItem(preview, item, chunkIndex);
                case "replace_material":
                    return ExecuteReplaceMaterialItem(preview, item, chunkIndex);
                case "fix_missing_scripts":
                    return ExecuteMissingScriptsItem(item, chunkIndex);
                case "set_render_layer":
                    return ExecuteSetLayerItem(preview, item, chunkIndex);
                case "cleanup_temp_objects":
                    return ExecuteDeleteGameObjectItem(item, chunkIndex);
                default:
                    return new BatchReportItemRecord
                    {
                        targetName = item.targetName,
                        targetPath = item.targetPath,
                        instanceId = item.instanceId,
                        action = item.action,
                        status = "failed",
                        reason = $"Unsupported batch preview kind: {preview.kind}",
                        chunkIndex = chunkIndex
                    };
            }
        }

        internal static BatchReportRecord CreateReportFromJob(BatchJobRecord job)
        {
            var report = new BatchReportRecord
            {
                reportId = Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = job.kind,
                status = job.status,
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                workflowId = job.relatedWorkflowId,
                jobId = job.jobId,
                rollbackAvailable = !string.IsNullOrEmpty(job.relatedWorkflowId),
                query = CloneQuery(job.preview?.query),
                operation = CloneOperation(job.preview?.operation),
                items = job.items.ToList()
            };

            report.totals.total = report.items.Count;
            report.totals.success = report.items.Count(item => item.status == "success");
            report.totals.failed = report.items.Count(item => item.status == "failed");
            report.totals.skipped = report.items.Count(item => item.status == "skipped");
            report.failureGroups = report.items
                .Where(item => item.status != "success" && !string.IsNullOrWhiteSpace(item.reason))
                .GroupBy(item => item.reason)
                .Select(group => new BatchFailureGroup { reason = group.Key, count = group.Count() })
                .OrderByDescending(group => group.count)
                .ToList();
            report.summary = $"{report.totals.success} succeeded, {report.totals.failed} failed, {report.totals.skipped} skipped.";
            return report;
        }

        private static bool CanRetryFromReport(BatchReportRecord report)
        {
            if (report == null)
                return false;

            if (report.operation != null && report.operation.Count > 0)
                return true;

            switch (report.kind)
            {
                case "rename":
                case "cleanup_temp_objects":
                case "fix_missing_scripts":
                    return true;
                default:
                    return false;
            }
        }

        private static BatchTargetQuery CloneQuery(BatchTargetQuery query)
        {
            if (query == null)
                return null;

            return JsonConvert.DeserializeObject<BatchTargetQuery>(
                JsonConvert.SerializeObject(query));
        }

        private static Dictionary<string, object> CloneOperation(Dictionary<string, object> operation)
        {
            if (operation == null)
                return new Dictionary<string, object>();

            return JsonConvert.DeserializeObject<Dictionary<string, object>>(
                       JsonConvert.SerializeObject(operation))
                   ?? new Dictionary<string, object>();
        }

        private static BatchTargetQuery ParseQuery(string queryJson)
        {
            BatchTargetQuery query = null;
            if (!string.IsNullOrWhiteSpace(queryJson))
            {
                try
                {
                    query = JsonConvert.DeserializeObject<BatchTargetQuery>(queryJson);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException($"Invalid queryJson: {ex.Message}");
                }
            }

            query = query ?? new BatchTargetQuery();
            if (query.limit <= 0)
                query.limit = 500;
            return query;
        }

        private static List<GameObject> QueryTargets(BatchTargetQuery query)
        {
            IEnumerable<GameObject> results = GameObjectFinder.GetSceneObjects();

            if (!query.includeInactive)
                results = results.Where(go => go.activeInHierarchy);
            if (query.instanceId != 0)
                results = results.Where(go => go.GetInstanceID() == query.instanceId);
            if (!string.IsNullOrWhiteSpace(query.path))
                results = results.Where(go => string.Equals(GameObjectFinder.GetCachedPath(go), query.path.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.name))
                results = results.Where(go => go.name.IndexOf(query.name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(query.namePattern))
            {
                System.Text.RegularExpressions.Regex regex;
                try { regex = new System.Text.RegularExpressions.Regex(query.namePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                catch { return new List<GameObject>(); }
                results = results.Where(go => regex.IsMatch(go.name));
            }
            if (query.isStatic.HasValue)
                results = results.Where(go => go.isStatic == query.isStatic.Value);
            if (!string.IsNullOrWhiteSpace(query.tag))
            {
                results = results.Where(go =>
                {
                    try { return go.CompareTag(query.tag); }
                    catch { return false; }
                });
            }
            if (!string.IsNullOrWhiteSpace(query.layer))
            {
                var layerId = LayerMask.NameToLayer(query.layer);
                results = layerId >= 0 ? results.Where(go => go.layer == layerId) : Enumerable.Empty<GameObject>();
            }
            if (query.active.HasValue)
                results = results.Where(go => go.activeSelf == query.active.Value);
            if (!string.IsNullOrWhiteSpace(query.componentType))
            {
                var componentType = ComponentSkills.FindComponentType(query.componentType);
                results = componentType != null ? results.Where(go => go.GetComponent(componentType) != null) : Enumerable.Empty<GameObject>();
            }
            if (!string.IsNullOrWhiteSpace(query.sceneName))
                results = results.Where(go => string.Equals(go.scene.name, query.sceneName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(query.parentPath))
            {
                results = results.Where(go =>
                    go.transform.parent != null &&
                    string.Equals(GameObjectFinder.GetCachedPath(go.transform.parent.gameObject), query.parentPath, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(query.prefabSource))
            {
                results = results.Where(go =>
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source == null)
                        return false;
                    if (string.Equals(query.prefabSource, "Any", StringComparison.OrdinalIgnoreCase))
                        return true;
                    var prefabPath = AssetDatabase.GetAssetPath(source);
                    return source.name.IndexOf(query.prefabSource, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           prefabPath.IndexOf(query.prefabSource, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            return results.Take(query.limit).ToList();
        }

        private static BatchPreviewEnvelope BuildRenamePreview(
            BatchTargetQuery query,
            string mode,
            string prefix,
            string suffix,
            string search,
            string replacement,
            string regexPattern,
            string regexReplacement)
        {
            var preview = CreatePreviewEnvelope("rename", query, "medium");
            preview.operation["mode"] = mode ?? "prefix";
            preview.operation["prefix"] = prefix;
            preview.operation["suffix"] = suffix;
            preview.operation["search"] = search;
            preview.operation["replacement"] = replacement;
            preview.operation["regexPattern"] = regexPattern;
            preview.operation["regexReplacement"] = regexReplacement;

            foreach (var target in QueryTargets(query))
            {
                var nextName = ComputeRenamedValue(target.name, mode, prefix, suffix, search, replacement, regexPattern, regexReplacement);
                if (string.IsNullOrWhiteSpace(nextName))
                {
                    preview.items.Add(CreateSkippedItem(target, "rename", "rename_rule_produced_empty_name"));
                    continue;
                }
                if (string.Equals(target.name, nextName, StringComparison.Ordinal))
                {
                    preview.items.Add(CreateSkippedItem(target, "rename", "already_target_value", target.name, nextName));
                    continue;
                }

                preview.items.Add(new BatchPreviewItem
                {
                    action = "rename",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    currentValue = target.name,
                    nextValue = nextName,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will rename {preview.executableCount} of {preview.targetCount} matched objects.";
            if (preview.targetCount > 200)
                preview.riskLevel = "high";
            return preview;
        }

        private static BatchPreviewEnvelope BuildSetPropertyPreview(
            BatchTargetQuery query,
            string componentType,
            string propertyName,
            string value,
            string referencePath,
            string referenceName,
            string assetPath)
        {
            if (string.IsNullOrWhiteSpace(componentType) || string.IsNullOrWhiteSpace(propertyName))
                throw new ArgumentException("componentType and propertyName are required");

            var preview = CreatePreviewEnvelope("set_property", query, "medium");
            preview.operation["componentType"] = componentType;
            preview.operation["propertyName"] = propertyName;
            preview.operation["value"] = value;
            preview.operation["referencePath"] = referencePath;
            preview.operation["referenceName"] = referenceName;
            preview.operation["assetPath"] = assetPath;

            var targetComponentType = ComponentSkills.FindComponentType(componentType);
            if (targetComponentType == null)
                throw new ArgumentException($"Component type not found: {componentType}");

            foreach (var target in QueryTargets(query))
            {
                var component = target.GetComponent(targetComponentType);
                if (component == null)
                {
                    preview.items.Add(CreateSkippedItem(target, "set_property", "component_missing"));
                    continue;
                }

                if (!TryFindMember(targetComponentType, propertyName, out var property, out var field))
                {
                    preview.items.Add(CreateSkippedItem(target, "set_property", "property_missing"));
                    continue;
                }

                var memberType = property != null ? property.PropertyType : field.FieldType;
                object converted;
                try { converted = ResolveValue(memberType, value, referencePath, referenceName, assetPath); }
                catch (Exception ex)
                {
                    preview.items.Add(CreateSkippedItem(target, "set_property", $"invalid_value:{ex.Message}"));
                    continue;
                }

                var currentValue = property != null ? property.GetValue(component) : field.GetValue(component);
                var currentString = FormatValue(currentValue);
                var nextString = FormatValue(converted);
                if (AreSameValue(currentValue, converted))
                {
                    preview.items.Add(CreateSkippedItem(target, "set_property", "already_target_value", currentString, nextString));
                    continue;
                }

                preview.items.Add(new BatchPreviewItem
                {
                    action = "set_property",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    componentType = componentType,
                    propertyName = propertyName,
                    currentValue = currentString,
                    nextValue = nextString,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will set {componentType}.{propertyName} on {preview.executableCount} objects.";
            return preview;
        }

        private static BatchPreviewEnvelope BuildReplaceMaterialPreview(BatchTargetQuery query, string materialPath)
        {
            if (Validate.Required(materialPath, "materialPath") is object err)
                throw new ArgumentException(SkillResultHelper.TryGetError(err, out var errorText) ? errorText : "materialPath is required");

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                throw new ArgumentException($"Material not found: {materialPath}");

            var preview = CreatePreviewEnvelope("replace_material", query, "medium");
            preview.operation["materialPath"] = materialPath;

            foreach (var target in QueryTargets(query))
            {
                var renderer = target.GetComponent<Renderer>();
                if (renderer == null)
                {
                    preview.items.Add(CreateSkippedItem(target, "replace_material", "renderer_missing"));
                    continue;
                }

                var currentPath = renderer.sharedMaterial != null ? AssetDatabase.GetAssetPath(renderer.sharedMaterial) : null;
                if (string.Equals(currentPath, materialPath, StringComparison.OrdinalIgnoreCase))
                {
                    preview.items.Add(CreateSkippedItem(target, "replace_material", "already_target_value", currentPath, materialPath));
                    continue;
                }

                preview.items.Add(new BatchPreviewItem
                {
                    action = "replace_material",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    currentMaterialPath = currentPath,
                    nextMaterialPath = materialPath,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will replace materials on {preview.executableCount} renderers.";
            return preview;
        }

        private static BatchPreviewEnvelope BuildMissingScriptsPreview(BatchTargetQuery query)
        {
            var preview = CreatePreviewEnvelope("fix_missing_scripts", query, "high");
            foreach (var target in QueryTargets(query))
            {
                var missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(target);
                if (missingCount <= 0)
                    continue;

                preview.items.Add(new BatchPreviewItem
                {
                    action = "remove_missing_scripts",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    currentValue = missingCount.ToString(),
                    nextValue = "0",
                    missingCount = missingCount,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will remove missing scripts from {preview.executableCount} objects.";
            return preview;
        }

        private static BatchPreviewEnvelope BuildStandardizeNamingPreview(BatchTargetQuery query, string separator)
        {
            var preview = CreatePreviewEnvelope("standardize_naming", query, "medium");
            preview.operation["separator"] = string.IsNullOrWhiteSpace(separator) ? "_" : separator;

            foreach (var target in QueryTargets(query))
            {
                var standardized = StandardizeName(target.name, GetOperationString(preview.operation, "separator", "_"));
                if (string.Equals(target.name, standardized, StringComparison.Ordinal))
                {
                    preview.items.Add(CreateSkippedItem(target, "rename", "already_target_value", target.name, standardized));
                    continue;
                }

                preview.items.Add(new BatchPreviewItem
                {
                    action = "rename",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    currentValue = target.name,
                    nextValue = standardized,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will standardize names for {preview.executableCount} objects.";
            return preview;
        }

        private static BatchPreviewEnvelope BuildSetLayerPreview(BatchTargetQuery query, string layer, bool recursive)
        {
            if (string.IsNullOrWhiteSpace(layer))
                throw new ArgumentException("layer is required");

            var layerId = LayerMask.NameToLayer(layer);
            if (layerId < 0)
                throw new ArgumentException($"Layer not found: {layer}");

            var preview = CreatePreviewEnvelope("set_render_layer", query, "medium");
            preview.operation["layer"] = layer;
            preview.operation["recursive"] = recursive;

            foreach (var target in QueryTargets(query))
            {
                var currentLayer = LayerMask.LayerToName(target.layer);
                if (string.Equals(currentLayer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    preview.items.Add(CreateSkippedItem(target, "set_layer", "already_target_value", currentLayer, layer));
                    continue;
                }

                preview.items.Add(new BatchPreviewItem
                {
                    action = "set_layer",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    currentLayer = currentLayer,
                    nextLayer = layer,
                    recursive = recursive,
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will set layer '{layer}' on {preview.executableCount} objects.";
            return preview;
        }

        private static BatchPreviewEnvelope BuildCleanupTempObjectsPreview(BatchTargetQuery query, string patternsCsv)
        {
            var preview = CreatePreviewEnvelope("cleanup_temp_objects", query, "high");
            var patterns = ParseTempPatterns(patternsCsv);
            var matchedTargets = QueryTargets(query)
                .Where(target => patterns.Any(pattern => target.name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            foreach (var target in matchedTargets)
            {
                var matchedPattern = patterns.First(pattern => target.name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
                preview.items.Add(new BatchPreviewItem
                {
                    action = "delete_gameobject",
                    targetName = target.name,
                    targetPath = GameObjectFinder.GetCachedPath(target),
                    instanceId = target.GetInstanceID(),
                    sceneName = target.scene.name,
                    reason = $"matched_pattern:{matchedPattern}",
                    willChange = true
                });
            }

            CompletePreviewEnvelope(preview);
            preview.summary = $"Will delete {preview.executableCount} temporary objects.";
            return preview;
        }

        private static object SavePreview(BatchPreviewEnvelope preview, int sampleLimit)
        {
            BatchPersistence.UpsertPreview(preview);

            var samples = preview.items.Where(item => item.willChange).Take(Math.Max(1, sampleLimit)).Select(item => new
            {
                item.targetName,
                item.targetPath,
                item.action,
                before = ChooseBeforeValue(item),
                after = ChooseAfterValue(item)
            }).ToArray();

            var skipReasons = preview.items
                .Where(item => !item.willChange && !string.IsNullOrWhiteSpace(item.skipReason))
                .GroupBy(item => item.skipReason)
                .Select(group => new { reason = group.Key, count = group.Count() })
                .OrderByDescending(group => group.count)
                .ToArray();

            return new
            {
                success = true,
                status = "preview",
                confirmToken = preview.confirmToken,
                kind = preview.kind,
                summary = preview.summary,
                riskLevel = preview.riskLevel,
                rollbackAvailable = preview.rollbackAvailable,
                mayCreateJob = preview.mayCreateJob,
                targetCount = preview.targetCount,
                executableCount = preview.executableCount,
                skipCount = preview.skipCount,
                sampleChanges = samples,
                skipReasons
            };
        }

        private static BatchPreviewEnvelope CreatePreviewEnvelope(string kind, BatchTargetQuery query, string riskLevel)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new BatchPreviewEnvelope
            {
                confirmToken = Guid.NewGuid().ToString("N").Substring(0, 12),
                kind = kind,
                createdAt = now,
                expiresAt = now + PreviewTtlSeconds,
                riskLevel = riskLevel,
                rollbackAvailable = true,
                mayCreateJob = true,
                query = query
            };
        }

        private static void CompletePreviewEnvelope(BatchPreviewEnvelope preview)
        {
            preview.targetCount = preview.items.Count;
            preview.executableCount = preview.items.Count(item => item.valid && item.willChange);
            preview.skipCount = preview.items.Count(item => !item.willChange);
            preview.mayCreateJob = preview.executableCount > 50;
        }

        private static BatchPreviewItem CreateSkippedItem(GameObject target, string action, string skipReason, string currentValue = null, string nextValue = null)
        {
            return new BatchPreviewItem
            {
                action = action,
                targetName = target.name,
                targetPath = GameObjectFinder.GetCachedPath(target),
                instanceId = target.GetInstanceID(),
                sceneName = target.scene.name,
                currentValue = currentValue,
                nextValue = nextValue,
                willChange = false,
                valid = true,
                skipReason = skipReason
            };
        }

        private static string ComputeRenamedValue(string currentName, string mode, string prefix, string suffix, string search, string replacement, string regexPattern, string regexReplacement)
        {
            switch ((mode ?? "prefix").ToLowerInvariant())
            {
                case "prefix":
                    return (prefix ?? string.Empty) + currentName;
                case "suffix":
                    return currentName + (suffix ?? string.Empty);
                case "replace":
                    return currentName.Replace(search ?? string.Empty, replacement ?? string.Empty);
                case "regex_replace":
                    if (string.IsNullOrWhiteSpace(regexPattern))
                        return currentName;
                    return Regex.Replace(currentName, regexPattern, regexReplacement ?? string.Empty);
                default:
                    return currentName;
            }
        }

        private static string StandardizeName(string currentName, string separator)
        {
            var result = currentName?.Trim() ?? string.Empty;
            result = MultiWhitespaceRegex.Replace(result, " ");
            result = result.Replace(" ", separator).Replace("-", separator).Replace("/", separator);
            result = MultiUnderscoreRegex.Replace(result, separator);
            return result.Trim(separator.ToCharArray());
        }

        private static string[] ParseTempPatterns(string patternsCsv)
        {
            if (string.IsNullOrWhiteSpace(patternsCsv))
                return new[] { "temp", "tmp", "preview", "_copy", "(clone)" };

            return patternsCsv
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static object BuildTargetInfo(GameObject go)
        {
            return new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetCachedPath(go),
                scene = go.scene.name,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                componentCount = go.GetComponents<Component>().Count(component => component != null)
            };
        }

        private static bool TryFindMember(Type type, string memberName, out PropertyInfo property, out FieldInfo field)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            property = type.GetProperty(memberName, flags) ?? type.GetProperties(flags).FirstOrDefault(info => info.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            field = type.GetField(memberName, flags) ?? type.GetFields(flags).FirstOrDefault(info => info.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            return property != null || field != null;
        }

        private static object ResolveValue(Type targetType, string value, string referencePath, string referenceName, string assetPath)
        {
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                if (asset == null)
                    throw new ArgumentException($"Asset not found or type mismatch: {assetPath}");
                return asset;
            }
            if (!string.IsNullOrWhiteSpace(referencePath) || !string.IsNullOrWhiteSpace(referenceName))
            {
                var gameObject = GameObjectFinder.Find(referenceName, path: referencePath);
                if (gameObject == null)
                    throw new ArgumentException("Reference target not found");
                if (targetType == typeof(GameObject))
                    return gameObject;
                if (targetType == typeof(Transform))
                    return gameObject.transform;
                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    var component = gameObject.GetComponent(targetType);
                    if (component == null)
                        throw new ArgumentException($"Reference target does not contain component: {targetType.Name}");
                    return component;
                }
                throw new ArgumentException($"Unsupported reference target type: {targetType.Name}");
            }
            return ComponentSkills.ConvertValue(value, targetType);
        }

        private static bool AreSameValue(object currentValue, object nextValue)
        {
            if (ReferenceEquals(currentValue, nextValue))
                return true;
            if (currentValue == null || nextValue == null)
                return false;

            if (currentValue is UnityEngine.Object currentObject && nextValue is UnityEngine.Object nextObject)
                return currentObject == nextObject;

            return Equals(currentValue, nextValue);
        }

        private static string FormatValue(object value)
        {
            if (value == null) return "null";
            if (value is Vector2 v2) return $"({v2.x}, {v2.y})";
            if (value is Vector3 v3) return $"({v3.x}, {v3.y}, {v3.z})";
            if (value is Vector4 v4) return $"({v4.x}, {v4.y}, {v4.z}, {v4.w})";
            if (value is Color color) return $"({color.r}, {color.g}, {color.b}, {color.a})";
            if (value is UnityEngine.Object unityObject)
            {
                var assetPath = AssetDatabase.GetAssetPath(unityObject);
                return string.IsNullOrWhiteSpace(assetPath) ? unityObject.name : assetPath;
            }
            return value.ToString();
        }

        private static GameObject FindTarget(BatchPreviewItem item)
        {
            return GameObjectFinder.Find(item.targetName, item.instanceId, item.targetPath);
        }

        private static BatchReportItemRecord ExecuteRenameItem(BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");
            WorkflowManager.SnapshotObject(target);
            Undo.RecordObject(target, "Batch Rename");
            target.name = item.nextValue;
            return CreateSuccessReport(item, chunkIndex, item.currentValue, item.nextValue);
        }

        private static BatchReportItemRecord ExecuteSetPropertyItem(BatchPreviewEnvelope preview, BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");

            var componentType = ComponentSkills.FindComponentType(GetOperationString(preview.operation, "componentType"));
            if (componentType == null) return CreateFailedReport(item, chunkIndex, "component_type_missing_at_execution");

            var component = target.GetComponent(componentType);
            if (component == null) return CreateSkippedReport(item, chunkIndex, "component_missing_at_execution");
            if (!TryFindMember(componentType, GetOperationString(preview.operation, "propertyName"), out var property, out var field))
                return CreateSkippedReport(item, chunkIndex, "property_missing_at_execution");

            var memberType = property != null ? property.PropertyType : field.FieldType;
            object converted;
            try
            {
                converted = ResolveValue(memberType, GetOperationString(preview.operation, "value"), GetOperationString(preview.operation, "referencePath"), GetOperationString(preview.operation, "referenceName"), GetOperationString(preview.operation, "assetPath"));
            }
            catch (Exception ex)
            {
                return CreateFailedReport(item, chunkIndex, ex.Message);
            }

            WorkflowManager.SnapshotObject(component);
            Undo.RecordObject(component, "Batch Set Property");
            if (property != null && property.CanWrite) property.SetValue(component, converted);
            else if (field != null) field.SetValue(component, converted);
            else return CreateFailedReport(item, chunkIndex, "property_is_read_only");
            EditorUtility.SetDirty(component);
            return CreateSuccessReport(item, chunkIndex, item.currentValue, FormatValue(converted));
        }

        private static BatchReportItemRecord ExecuteReplaceMaterialItem(BatchPreviewEnvelope preview, BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null) return CreateSkippedReport(item, chunkIndex, "renderer_missing_at_execution");

            var materialPath = GetOperationString(preview.operation, "materialPath");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return CreateFailedReport(item, chunkIndex, $"material_missing:{materialPath}");

            WorkflowManager.SnapshotObject(renderer);
            Undo.RecordObject(renderer, "Batch Replace Material");
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
            return CreateSuccessReport(item, chunkIndex, item.currentMaterialPath, materialPath);
        }

        private static BatchReportItemRecord ExecuteMissingScriptsItem(BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");
            var before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(target);
            if (before <= 0) return CreateSkippedReport(item, chunkIndex, "already_target_value");

            WorkflowManager.SnapshotObject(target);
            Undo.RegisterCompleteObjectUndo(target, "Batch Remove Missing Scripts");
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(target);
            return CreateSuccessReport(item, chunkIndex, before.ToString(), "0");
        }

        private static BatchReportItemRecord ExecuteSetLayerItem(BatchPreviewEnvelope preview, BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");

            var layerName = GetOperationString(preview.operation, "layer");
            var layerId = LayerMask.NameToLayer(layerName);
            if (layerId < 0) return CreateFailedReport(item, chunkIndex, $"layer_missing:{layerName}");

            ApplyLayer(target, layerId, GetOperationBool(preview.operation, "recursive"));
            return CreateSuccessReport(item, chunkIndex, item.currentLayer, layerName);
        }

        private static BatchReportItemRecord ExecuteDeleteGameObjectItem(BatchPreviewItem item, int chunkIndex)
        {
            var target = FindTarget(item);
            if (target == null) return CreateSkippedReport(item, chunkIndex, "target_missing_at_execution");

            WorkflowManager.SnapshotObject(target);
            Undo.DestroyObjectImmediate(target);
            return new BatchReportItemRecord
            {
                targetName = item.targetName,
                targetPath = item.targetPath,
                instanceId = item.instanceId,
                action = item.action,
                status = "success",
                before = item.targetName,
                after = "(deleted)",
                chunkIndex = chunkIndex
            };
        }

        private static void ApplyLayer(GameObject target, int layerId, bool recursive)
        {
            WorkflowManager.SnapshotObject(target);
            Undo.RecordObject(target, "Batch Set Layer");
            target.layer = layerId;
            if (!recursive) return;

            foreach (Transform child in target.GetComponentsInChildren<Transform>(true))
            {
                WorkflowManager.SnapshotObject(child.gameObject);
                Undo.RecordObject(child.gameObject, "Batch Set Layer Recursive");
                child.gameObject.layer = layerId;
            }
        }

        private static BatchReportItemRecord CreateSuccessReport(BatchPreviewItem item, int chunkIndex, string before, string after)
        {
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, instanceId = item.instanceId, action = item.action, status = "success", before = before, after = after, chunkIndex = chunkIndex };
        }

        private static BatchReportItemRecord CreateSkippedReport(BatchPreviewItem item, int chunkIndex, string reason)
        {
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, instanceId = item.instanceId, action = item.action, status = "skipped", before = ChooseBeforeValue(item), after = ChooseAfterValue(item), reason = reason, chunkIndex = chunkIndex };
        }

        private static BatchReportItemRecord CreateFailedReport(BatchPreviewItem item, int chunkIndex, string reason)
        {
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, instanceId = item.instanceId, action = item.action, status = "failed", before = ChooseBeforeValue(item), after = ChooseAfterValue(item), reason = reason, chunkIndex = chunkIndex };
        }

        private static string ChooseBeforeValue(BatchPreviewItem item)
        {
            return !string.IsNullOrWhiteSpace(item.currentValue) ? item.currentValue : !string.IsNullOrWhiteSpace(item.currentLayer) ? item.currentLayer : item.currentMaterialPath;
        }

        private static string ChooseAfterValue(BatchPreviewItem item)
        {
            return !string.IsNullOrWhiteSpace(item.nextValue) ? item.nextValue : !string.IsNullOrWhiteSpace(item.nextLayer) ? item.nextLayer : item.nextMaterialPath;
        }

        private static string GetOperationString(IDictionary<string, object> operation, string key, string defaultValue = null)
        {
            if (operation == null || !operation.TryGetValue(key, out var value) || value == null) return defaultValue;
            return value.ToString();
        }

        private static bool GetOperationBool(IDictionary<string, object> operation, string key)
        {
            if (operation == null || !operation.TryGetValue(key, out var value) || value == null) return false;
            if (value is bool boolValue) return boolValue;
            return bool.TryParse(value.ToString(), out var parsed) && parsed;
        }

        private static bool IsTerminalStatus(string status)
        {
            return status == "completed" || status == "failed" || status == "cancelled";
        }
    }
}
