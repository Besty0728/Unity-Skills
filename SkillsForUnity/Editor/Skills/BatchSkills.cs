using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        [UnitySkill("batch_query_gameobjects", "Query GameObjects with unified batch filters. queryJson supports name/path/entityId/instanceId/tag/layer/active/componentType/sceneName/parentPath/prefabSource/includeInactive/limit.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "query", "gameobject", "filter" },
            Outputs = new[] { "count", "objects", "summary" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchQueryComponents(string queryJson = null, string componentType = null, int sampleLimit = 20)
        {
            var query = ParseQuery(queryJson);
            if (!string.IsNullOrWhiteSpace(componentType))
                query.componentType = componentType;

            var targets = QueryTargets(query);
            var objects = targets.Take(Math.Max(1, sampleLimit)).Select(go => new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetCachedPath(go),
                components = go.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().Name).ToArray()
            }).ToArray();

            return new { success = true, count = targets.Count, summary = $"Matched {targets.Count} objects with component filters.", query, objects };
        }

        [UnitySkill("batch_query_assets", "Query project assets by type, path pattern, and labels.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "batch", "query", "asset", "filter", "project" },
            Outputs = new[] { "count", "assets", "summary" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            try
            {
                var query = ParseQuery(queryJson);
                var preview = BuildRenamePreview(query, mode, prefix, suffix, search, replacement, regexPattern, regexReplacement);
                return SavePreview(preview, sampleLimit);
            }
            catch (ArgumentException ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("batch_preview_set_property", "Preview setting a component property or field across queried targets.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "preview", "property", "component" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            RequiresInput = new[] { "componentType", "propertyName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            try
            {
                var query = ParseQuery(queryJson);
                var preview = BuildSetPropertyPreview(query, componentType, propertyName, value, referencePath, referenceName, assetPath);
                return SavePreview(preview, sampleLimit);
            }
            catch (ArgumentException ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("batch_preview_replace_material", "Preview replacing Renderer materials across queried targets.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "preview", "material", "renderer" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            RequiresInput = new[] { "materialPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchPreviewReplaceMaterial(string queryJson = null, string materialPath = null, int sampleLimit = DefaultSampleLimit)
        {
            try
            {
                var query = ParseQuery(queryJson);
                var preview = BuildReplaceMaterialPreview(query, materialPath);
                return SavePreview(preview, sampleLimit);
            }
            catch (ArgumentException ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("batch_execute", "Execute a previously previewed batch operation by confirmToken. Large operations return a jobId.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "batch", "execute", "job", "report" },
            Outputs = new[] { "jobId", "reportId", "workflowId", "status" },
            RequiresInput = new[] { "confirmToken" },
            // 真正执行什么由 confirmToken 决定，所以这一个入口覆盖了 ExecutePreviewItem
            // 分发到的全部执行器——包括 WorkflowManager.DeleteSceneObject 和资源删除。
            // 少声明不只是标错，而是留洞：铸造 token 的 preview 技能都是 ReadOnly，
            // 于是撤下场景/资源写权限的 profile 仍会把 token 交给 agent，再在这里执行掉。
            // 这条声明关掉的是隐藏一切 MutatesScene 写操作的 profile（noSceneAuthoring）；
            // 它关不掉按类别撤下的 profile——Workflow 不在任何一个隐藏集合里——所以 token
            // 自身的操作还要在 SurfaceRejectionForKind 里按当前 profile 再分类一次。
            MutatesScene = true,
            MutatesAssets = true,
            SupportsDryRun = false)]
        public static object BatchExecute(string confirmToken, bool runAsync = true, int chunkSize = 100, int progressGranularity = 10)
        {
            chunkSize = Mathf.Clamp(chunkSize, 1, 200);

            if (Validate.Required(confirmToken, "confirmToken") is object err)
                return err;

            var preview = BatchPersistence.GetPreview(confirmToken);
            if (preview == null)
                return new { success = false, error = "Invalid or expired confirmToken. Call preview again to get a new token." };

            // 要问 surface profile 的是 token 而不是本技能的元数据：被撤下的从来不是本技能，
            // 而铸造 token 的 preview 是 ReadOnly、任何 profile 下都提供。
            // 这一步放在 RemovePreview 之前，拒绝时 token 保持有效——用户把 profile 切回来
            // 即可继续执行，不必再走一轮 preview。
            var withdrawn = SurfaceRejectionForKind(preview.kind, "batch_execute",
                "the operation this confirmToken was minted for");
            if (withdrawn != null)
                return withdrawn;

            if (preview.executableCount <= 0)
            {
                BatchPersistence.RemovePreview(confirmToken);
                return new { success = false, error = "Preview contains no executable changes." };
            }

            BatchPersistence.RemovePreview(confirmToken);
            var job = BatchJobService.Start(preview, chunkSize, progressGranularity);
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

            // 每项 50ms 预算、下限 5s，再夹到共享的主线程等待上限，
            // 免得超大的内联预览让 Wait 把编辑器冻住超过允许的时长。
            var inlineTimeoutMs = Math.Min(BatchJobService.MaxWaitTimeoutMs,
                Math.Max(5000, preview.executableCount * 50));
            var completed = BatchJobService.Wait(job.jobId, inlineTimeoutMs);
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("job_status", "Get status for an asynchronous UnitySkills job. Built for repeated polling, so the job's full result payload is NOT inlined by default: resultAvailable says whether one exists and resultHint names the skill that returns it. Pass includeDetails=true to inline it as `details` (the pre-2.7 behaviour).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "status", "async" },
            Outputs = new[] { "jobId", "status", "progress", "currentStage", "resultAvailable", "resultHint" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object JobStatus(string jobId, bool includeDetails = false)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Get(jobId);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            bool resultAvailable = job.resultData != null && job.resultData.Count > 0;

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
                resultAvailable,
                resultHint = includeDetails ? null : BuildJobResultHint(job, "job_status", resultAvailable),
                details = includeDetails ? job.resultData : null
            };
        }

        [UnitySkill("job_progress", "Get fine-grained progress events for a UnitySkills job. Pass previous totalCount as offset for incremental polling.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "async", "progress" },
            Outputs = new[] { "jobId", "status", "totalCount", "offset", "events", "terminal" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object JobProgress(string jobId, int offset = 0)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Get(jobId);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            return AsyncJobService.BuildProgressSnapshot(job, offset);
        }

        [UnitySkill("job_logs", "Get structured logs for a UnitySkills job.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Query,
            Tags = new[] { "job", "logs", "async" },
            Outputs = new[] { "jobId", "logs" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("job_wait", "Wait for a UnitySkills job to finish or until timeoutMs elapses (clamped to [0, 2000]ms — this blocks the Unity main thread, so longer waits are not allowed). For job kinds that advance via Unity's own engine loop (compile/package/test/playmode/play_capture/build_player), blocking cannot help them progress, so this returns the current snapshot immediately with waitNotSupported=true instead of spinning — poll GET /jobs/{id} or subscribe to GET /events instead, both of which run off the main thread. The job's full result payload is NOT inlined by default: resultAvailable says whether one exists and resultHint names the skill that returns it. Pass includeDetails=true to inline it as `details` (the pre-2.7 behaviour).",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "job", "wait", "async" },
            Outputs = new[] { "jobId", "status", "reportId", "waitNotSupported", "terminal", "resultAvailable", "resultHint" },
            RequiresInput = new[] { "jobId" })]
        // 默认值必须等于夹取上限而不能高于它：写 10000 会让 manifest（进而让 agent）以为
        // 省略该参数能等 10s，而 AsyncJobService.Wait 一直是砍到 2000 的。
        public static object JobWait(string jobId, int timeoutMs = 2000, bool includeDetails = false)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Wait(jobId, timeoutMs, out var waitNotSupported);
            if (job == null)
                return new { success = false, error = $"Job not found: {jobId}" };

            bool resultAvailable = job.resultData != null && job.resultData.Count > 0;

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
                resultAvailable,
                resultHint = includeDetails ? null : BuildJobResultHint(job, "job_wait", resultAvailable),
                details = includeDetails ? job.resultData : null,
                terminal = IsTerminalStatus(job.status),
                waitNotSupported,
                hint = waitNotSupported
                    ? $"Job kind '{job.kind}' advances via Unity's own engine loop (compilation/package manager/test runner/play mode/build pipeline) and cannot be progressed by blocking the main thread. Poll GET /jobs/{job.jobId} or subscribe to GET /events instead."
                    : null
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchFixMissingScripts(string queryJson = null, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildMissingScriptsPreview(ParseQuery(queryJson));
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_standardize_naming", "Preview standardizing names by trimming whitespace and normalizing separators. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "naming", "standardize", "rename" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchStandardizeNaming(string queryJson = null, string separator = "_", int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildStandardizeNamingPreview(ParseQuery(queryJson), separator);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_set_render_layer", "Preview setting GameObject layers in batch. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "layer", "rendering", "gameobject" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            // layer 为空时 BuildSetLayerPreview 会抛 "layer is required"，不声明就变成先执行后失败。
            // queryJson 仍是可选的（缺省 = 全场景对象）。
            RequiresInput = new[] { "layer" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchSetRenderLayer(string queryJson = null, string layer = null, bool recursive = false, int sampleLimit = DefaultSampleLimit)
        {
            try
            {
                var preview = BuildSetLayerPreview(ParseQuery(queryJson), layer, recursive);
                return SavePreview(preview, sampleLimit);
            }
            catch (ArgumentException ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("batch_replace_material", "Preview replacing materials in batch. Execute with batch_execute(confirmToken).",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "material", "replace", "renderer" },
            Outputs = new[] { "confirmToken", "targetCount", "sampleChanges", "riskLevel" },
            // 与 batch_preview_replace_material 走同一个 builder，两者的声明必须保持一致：
            // 缺了这行，空 materialPath 会一路走到 BuildReplaceMaterialPreview 才失败，
            // 而不是在入口直接拒绝。
            RequiresInput = new[] { "materialPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchReplaceMaterial(string queryJson = null, string materialPath = null, int sampleLimit = DefaultSampleLimit)
        {
            try
            {
                var preview = BuildReplaceMaterialPreview(ParseQuery(queryJson), materialPath);
                return SavePreview(preview, sampleLimit);
            }
            catch (ArgumentException ex)
            {
                return new { error = ex.Message };
            }
        }

        [UnitySkill("batch_validate_scene_objects", "Analyze scene objects for missing scripts, missing references, duplicate names, and empty objects.",
            Category = SkillCategory.Validation, Operation = SkillOperation.Analyze,
            Tags = new[] { "batch", "validate", "scene", "report" },
            Outputs = new[] { "summary", "issues" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BatchCleanupTempObjects(string queryJson = null, string patternsCsv = null, int sampleLimit = DefaultSampleLimit)
        {
            var preview = BuildCleanupTempObjectsPreview(ParseQuery(queryJson), patternsCsv);
            return SavePreview(preview, sampleLimit);
        }

        [UnitySkill("batch_retry_failed", "Re-run only the failed items from a previous batch execution report.",
            Category = SkillCategory.Workflow, Operation = SkillOperation.Execute,
            Tags = new[] { "batch", "retry", "failed", "recovery" },
            Outputs = new[] { "jobId", "retryCount", "originalReportId" },
            // 把记录下来的条目交给与 batch_execute 相同的执行器重跑，
            // 因此必须带上与它相同的声明（原因同上）。
            MutatesScene = true,
            MutatesAssets = true)]
        public static object BatchRetryFailed(string reportId, bool runAsync = true, int chunkSize = 100)
        {
            chunkSize = Mathf.Clamp(chunkSize, 1, 200);
            if (Validate.Required(reportId, "reportId") is object err)
                return err;

            var report = BatchPersistence.GetReport(reportId);
            if (report == null)
                return new { error = $"Report not found: {reportId}" };

            // 执行器与 batch_execute 相同，只是入口从 token 换成 reportId，
            // 所以同样要拿记录下来的 kind 去问 surface profile。
            var withdrawn = SurfaceRejectionForKind(report.kind, "batch_retry_failed",
                "re-running this report's failed items");
            if (withdrawn != null)
                return withdrawn;

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
                    entityId = fi.entityId,
                    instanceId = fi.instanceId,
                    willChange = true,
                    valid = true
                });
            }

            BatchPersistence.UpsertPreview(preview);

            var job = BatchJobService.Start(preview, chunkSize);
            if (!runAsync && job != null)
            {
                var result = BatchJobService.Wait(job.jobId, BatchJobService.MaxWaitTimeoutMs);
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
                        entityId = item.entityId,
                        instanceId = item.instanceId,
                        action = item.action,
                        status = "failed",
                        reason = $"Unsupported batch preview kind: {preview.kind}",
                        chunkIndex = chunkIndex
                    };
            }
        }

        /// <summary>
        /// 某个 preview kind 属于哪个写类别，供执行路径上的 surface profile 检查使用。
        /// 与 <see cref="ExecutePreviewItem"/> 的分发保持一致；类别的选取既为判定，
        /// 也为随拒绝一起交给 agent 的手册文档——Guide 撤下的那三类都各自配有手册，
        /// 拒绝时才能说"读这篇、照着教"。
        ///
        /// default 分支故意 fail closed：未映射的 kind 本来就执行不了
        /// （ExecutePreviewItem 自己的 default 会让它的每一项都失败），当成场景写没有代价，
        /// 还能防止新加的 kind 在映射未补齐前成为绕过 profile 的新缺口。
        /// </summary>
        private static SkillCategory SurfaceCategoryForKind(string kind)
        {
            switch (kind)
            {
                case "rename":
                case "standardize_naming":
                case "set_render_layer":
                case "cleanup_temp_objects":
                    return SkillCategory.GameObject;
                // 移除脚本丢失的 MonoBehaviour 属于对象上的组件手术，对应 manual-component。
                case "set_property":
                case "fix_missing_scripts":
                    return SkillCategory.Component;
                // 写的是 Renderer.sharedMaterial：材质资源本身不动，变的是对象用哪个材质，
                // 对应 manual-material 教的拖拽操作。
                case "replace_material":
                    return SkillCategory.Material;
                default:
                    return SkillCategory.GameObject;
            }
        }

        /// <summary>
        /// 当前 profile 撤下该 kind 的写权限时返回 SURFACE_EXCLUDED 载荷，允许时返回 null；
        /// full profile 下恒为 null。
        /// </summary>
        private static object SurfaceRejectionForKind(string kind, string skillName, string subject)
        {
            var category = SurfaceCategoryForKind(kind);
            return SkillsSurfaceProfile.WithdrawsWriteIn(category)
                ? SkillsSurfaceProfile.CarriedWriteRejection(skillName, category, subject, kind)
                : null;
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
            if (!string.IsNullOrWhiteSpace(query.entityId))
                results = results.Where(go => UnityObjectIdUtility.MatchesEntityId(go, query.entityId));
            if (query.instanceId != 0)
                results = results.Where(go => UnityObjectIdUtility.MatchesObjectId(go, query.instanceId));
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                    entityId = UnityObjectIdUtility.GetEntityId(target),
                    instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                item.entityId,
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

            var payload = new
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

            // 预览照旧成功、照旧铸 token、照旧返回 diff——在教学 profile 下这份 diff 本身就是目的。
            // 只是提前告知 batch_execute 会拒绝该 token，让 agent 把这道墙读成用户的设置而非 bug。
            // 这里是"新增字段"而不是置空，所以在允许该操作的 profile 下载荷与以前逐字节一致。
            var category = SurfaceCategoryForKind(preview.kind);
            if (!SkillsSurfaceProfile.WithdrawsWriteIn(category))
                return payload;

            var annotated = JObject.FromObject(payload);
            annotated["surfaceExclusion"] =
                JObject.FromObject(SkillsSurfaceProfile.CarriedWriteNotice("batch_execute", category));
            return annotated;
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
                entityId = UnityObjectIdUtility.GetEntityId(target),
                instanceId = UnityObjectIdUtility.GetObjectId(target),
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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
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

        // 报告里的 currentValue/nextValue 会被 agent 读回并原样回填，因此每个数字都必须能
        // 无损往返。SkillParamUtil 的格式化器是统一答案：文化不变（本地化编辑器否则会输出
        // "0,5"，这里没有解析器认它），并通过重新解析校验 "R"，在 "R" 仍是旧有损实现的
        // 运行时上退回到按位数安全的格式。只有 SkillParamUtil 无从知晓的情形留在本地处理：
        // Unity 的结构体（自带 ToString 会四舍五入到一位小数）与资源引用（回显为路径）。
        private static string FormatValue(object value)
        {
            if (value is Vector2 v2) return SkillParamUtil.FormatVector2(v2);
            if (value is Vector3 v3) return SkillParamUtil.FormatVector3(v3);
            if (value is Vector4 v4) return SkillParamUtil.FormatVector4(v4);
            if (value is Color color) return SkillParamUtil.FormatColor(color);
            if (value is UnityEngine.Object unityObject)
            {
                var assetPath = AssetDatabase.GetAssetPath(unityObject);
                return string.IsNullOrWhiteSpace(assetPath) ? unityObject.name : assetPath;
            }
            return SkillParamUtil.FormatScalarR(value);
        }

        private static GameObject FindTarget(BatchPreviewItem item)
        {
            return GameObjectFinder.Find(item.targetName, item.instanceId, item.targetPath, entityId: item.entityId);
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

            if (!WorkflowManager.DeleteSceneObject(target))
                return CreateSkippedReport(item, chunkIndex, "workflow_snapshot_failed");
            return new BatchReportItemRecord
            {
                targetName = item.targetName,
                targetPath = item.targetPath,
                entityId = item.entityId,
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
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, entityId = item.entityId, instanceId = item.instanceId, action = item.action, status = "success", before = before, after = after, chunkIndex = chunkIndex };
        }

        private static BatchReportItemRecord CreateSkippedReport(BatchPreviewItem item, int chunkIndex, string reason)
        {
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, entityId = item.entityId, instanceId = item.instanceId, action = item.action, status = "skipped", before = ChooseBeforeValue(item), after = ChooseAfterValue(item), reason = reason, chunkIndex = chunkIndex };
        }

        private static BatchReportItemRecord CreateFailedReport(BatchPreviewItem item, int chunkIndex, string reason)
        {
            return new BatchReportItemRecord { targetName = item.targetName, targetPath = item.targetPath, entityId = item.entityId, instanceId = item.instanceId, action = item.action, status = "failed", before = ChooseBeforeValue(item), after = ChooseAfterValue(item), reason = reason, chunkIndex = chunkIndex };
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

        /// <summary>
        /// 告诉调用方去哪里取作业的完整载荷：job_status/job_wait 现在只报 resultAvailable
        /// 而不再内联 resultData（一个完成的 test/compile 作业可达几十 KB，轮询会每次重发）。
        /// 有专用结果技能的 kind 指向该技能；产出了批处理报告的作业指向 batch_report_get；
        /// 其余告知带 includeDetails=true 重问——那是取到载荷的唯一途径。
        /// 没有载荷可取时返回 null。
        /// </summary>
        private static string BuildJobResultHint(BatchJobRecord job, string selfSkill, bool resultAvailable)
        {
            if (!resultAvailable)
                return null;

            if (string.Equals(job.kind, "test", StringComparison.OrdinalIgnoreCase))
                return "Call test_get_result(jobId) for the parsed totals, failed test names and failure details.";
            if (string.Equals(job.kind, "test_discovery", StringComparison.OrdinalIgnoreCase))
                return "Call test_discover_get_result(jobId) for the discovered test list.";

            if (!string.IsNullOrEmpty(job.reportId))
                return $"Call batch_report_get(reportId=\"{job.reportId}\") for the per-item execution report.";

            return $"Job kind '{job.kind}' has no dedicated result skill — re-call {selfSkill} with includeDetails=true to inline the full result payload.";
        }
    }
}

// Producer:Betsy
