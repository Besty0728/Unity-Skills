using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    [InitializeOnLoad]
    internal static class AsyncJobService
    {
        private const int TestStartTimeoutSeconds = 90;
        private const int MaxConcurrentActiveTestJobs = 1;

        private sealed class TestRuntimeContext
        {
            public TestRunnerApi Api;
            public TestCallbacks Callbacks;
        }

        private sealed class InternalTestRunnerState
        {
            public bool IsRunning;
            public int TaskIndex;
        }

        private sealed class SmokeRuntimeContext
        {
            public BatchJobRecord Job;
            public SkillRouter.SkillInfo[] Skills;
            public string[] MetadataIssues;
            public int FailureItemLimit;
        }

        private static readonly Dictionary<string, TestRuntimeContext> TestRuntimeJobs =
            new Dictionary<string, TestRuntimeContext>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SmokeRuntimeContext> SmokeRuntimeJobs =
            new Dictionary<string, SmokeRuntimeContext>(StringComparer.OrdinalIgnoreCase);

        static AsyncJobService()
        {
            BatchPersistence.EnsureLoaded();
            EditorApplication.update += ProcessJobs;
        }

        internal static BatchJobRecord CreateJob(
            string kind,
            string currentStage,
            string resultSummary,
            bool canCancel,
            Dictionary<string, object> metadata = null,
            Dictionary<string, object> resultData = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var job = new BatchJobRecord
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = kind,
                status = "queued",
                progress = 0,
                currentStage = currentStage,
                progressStage = currentStage,
                startedAt = now,
                updatedAt = now,
                resultSummary = resultSummary,
                canCancel = canCancel,
                metadata = metadata ?? new Dictionary<string, object>(),
                resultData = resultData ?? new Dictionary<string, object>()
            };

            job.progressEvents.Add(new BatchJobProgressEvent
            {
                timestamp = now,
                progress = 0,
                stage = currentStage,
                description = resultSummary
            });
            AddLog(job, "info", currentStage, resultSummary, "job_created");
            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static BatchJobRecord StartScriptMutationJob(
            string operation,
            string targetPath,
            bool checkCompile,
            int diagnosticLimit,
            bool supportsDiagnostics = true)
        {
            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation"] = operation ?? "script_mutation",
                ["scriptPath"] = targetPath ?? string.Empty,
                ["checkCompile"] = checkCompile,
                ["diagnosticLimit"] = diagnosticLimit,
                ["supportsDiagnostics"] = supportsDiagnostics
            };

            var job = CreateJob(
                "compile",
                ServerAvailabilityHelper.IsCompilationInProgress() ? "waiting_domain_reload" : "mutation_applied",
                $"Script operation '{operation}' accepted.",
                canCancel: false,
                metadata: metadata,
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = targetPath ?? string.Empty,
                    ["operation"] = operation ?? "script_mutation"
                });

            job.status = ServerAvailabilityHelper.IsCompilationInProgress() ? "waiting_domain_reload" : "running";
            job.progress = ServerAvailabilityHelper.IsCompilationInProgress() ? 35 : 10;
            job.progressStage = job.currentStage;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static BatchJobRecord StartPackageJob(string operation, string packageId, string version = null)
        {
            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation"] = operation ?? string.Empty,
                ["packageId"] = packageId ?? string.Empty,
                ["version"] = version ?? string.Empty,
                ["refreshRequested"] = false
            };

            var summary = operation == "refresh"
                ? "Package refresh accepted."
                : $"Package operation '{operation}' accepted for {packageId}" + (string.IsNullOrEmpty(version) ? "." : $"@{version}.");

            var job = CreateJob(
                "package",
                "waiting_external",
                summary,
                canCancel: false,
                metadata: metadata,
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["operation"] = operation ?? string.Empty,
                    ["packageId"] = packageId ?? string.Empty,
                    ["version"] = version ?? string.Empty
                });

            job.status = "waiting_external";
            job.progress = 5;
            job.progressStage = job.currentStage;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static bool TryStartTestJob(string testMode, string filter, out BatchJobRecord job, out string error)
        {
            job = null;
            error = null;

            if (!TryValidateTestRunPreconditions(testMode, out error))
                return false;

            var activeJobs = BatchPersistence.ListJobs(100)
                .Where(existing => existing != null &&
                                   string.Equals(existing.kind, "test", StringComparison.OrdinalIgnoreCase) &&
                                   !IsTerminal(existing.status))
                .OrderByDescending(existing => existing.updatedAt)
                .ToArray();

            if (activeJobs.Length >= MaxConcurrentActiveTestJobs)
            {
                var activeJob = activeJobs[0];
                error = $"Another test run is already active: {activeJob.jobId} ({activeJob.currentStage ?? activeJob.status}). Wait for it to finish before starting a new run.";
                return false;
            }

            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["testMode"] = testMode ?? "EditMode",
                ["filter"] = filter ?? string.Empty
            };

            job = CreateJob(
                "test",
                "queued",
                "Test run accepted and waiting to start.",
                canCancel: false,
                metadata: metadata,
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["testMode"] = testMode ?? "EditMode",
                    ["filter"] = filter ?? string.Empty,
                    ["totalTests"] = 0,
                    ["passedTests"] = 0,
                    ["failedTests"] = 0,
                    ["skippedTests"] = 0,
                    ["inconclusiveTests"] = 0,
                    ["otherTests"] = 0,
                    ["failedTestNames"] = new List<string>()
                });

            var mode = string.Equals(testMode, "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new TestCallbacks(job.jobId);
            api.RegisterCallbacks(callbacks);

            var filterObj = BuildTestFilter(testMode, filter, mode);

            TestRuntimeJobs[job.jobId] = new TestRuntimeContext
            {
                Api = api,
                Callbacks = callbacks
            };

            job.status = "running";
            job.currentStage = "starting";
            job.progressStage = "starting";
            job.progress = 1;
            job.resultSummary = "Launching Unity Test Runner.";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AddLog(job, "info", "starting", $"Starting {testMode} tests.", "test_start");
            job.progressEvents.Add(new BatchJobProgressEvent
            {
                timestamp = job.updatedAt,
                progress = job.progress,
                stage = "starting",
                description = job.resultSummary
            });
            BatchPersistence.UpsertJob(job);

            var runnerJobId = api.Execute(new ExecutionSettings
            {
                filters = new[] { filterObj }
            });
            if (!string.IsNullOrWhiteSpace(runnerJobId))
            {
                job.metadata["runnerJobId"] = runnerJobId;
                BatchPersistence.UpsertJob(job);
            }
            return true;
        }

        private static bool TryValidateTestRunPreconditions(string testMode, out string error)
        {
            error = null;
            if (string.Equals(testMode, "PlayMode", StringComparison.OrdinalIgnoreCase))
                return true;

            var dirtyScenes = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isDirty)
                    continue;

                var sceneName = !string.IsNullOrWhiteSpace(scene.path)
                    ? scene.path.Replace('\\', '/')
                    : $"<UnsavedScene:{(string.IsNullOrWhiteSpace(scene.name) ? i.ToString() : scene.name)}>";
                dirtyScenes.Add(sceneName);
            }

            if (dirtyScenes.Count == 0)
                return true;

            var sceneList = string.Join(", ", dirtyScenes.Take(3));
            if (dirtyScenes.Count > 3)
                sceneList += ", ...";

            error =
                "Cannot start EditMode tests while there are unsaved scene changes. " +
                $"Dirty scenes: {sceneList}. " +
                "Save or discard scene changes first; otherwise Unity may show a hidden Save Scene dialog and the test run will hang.";
            return false;
        }

        private static Filter BuildTestFilter(string testMode, string filter, TestMode mode)
        {
            var filterObj = new Filter { testMode = mode };
            if (string.IsNullOrEmpty(filter))
                return filterObj;

            var exactNames = TestSkills.ResolveExactTestNames(testMode, filter);
            if (exactNames.Length > 0)
            {
                filterObj.testNames = exactNames;
                return filterObj;
            }

            if (TestSkills.MatchesDiscoveredTestGroup(testMode, filter))
            {
                var groupedNames = TestSkills.ResolveGroupedTestNames(testMode, filter);
                if (groupedNames.Length > 0)
                    filterObj.testNames = groupedNames;
                var assemblyNames = TestSkills.ResolveGroupAssemblyNames(testMode, filter);
                if (assemblyNames.Length > 0)
                    filterObj.assemblyNames = assemblyNames;
                return filterObj;
            }

            filterObj.testNames = new[] { filter };
            return filterObj;
        }

        internal static BatchJobRecord Get(string jobId)
        {
            return BatchPersistence.GetJob(jobId);
        }

        internal static BatchJobRecord[] List(int limit)
        {
            return BatchPersistence.ListJobs(limit);
        }

        internal static BatchJobRecord StartSmokeJob(
            SkillRouter.SkillInfo[] skills,
            string[] metadataIssues,
            bool executeReadOnly,
            int chunkSize,
            int failureItemLimit)
        {
            var skillNames = skills?.Select(skill => skill.Name).ToArray() ?? Array.Empty<string>();
            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["executeReadOnly"] = executeReadOnly,
                ["skillNames"] = skillNames,
                ["failureItemLimit"] = Mathf.Clamp(failureItemLimit, 1, 200)
            };

            var resultData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["totalSkills"] = skillNames.Length,
                ["completedSkills"] = 0,
                ["executedCount"] = 0,
                ["dryRunCount"] = 0,
                ["skippedCount"] = 0,
                ["failureCount"] = 0,
                ["failureItems"] = new List<object>(),
                ["metadataWarnings"] = metadataIssues ?? Array.Empty<string>()
            };

            var job = CreateJob(
                "test_smoke",
                "queued",
                $"Smoke test accepted for {skillNames.Length} skills.",
                canCancel: true,
                metadata: metadata,
                resultData: resultData);

            job.chunkSize = Mathf.Clamp(chunkSize, 1, 100);
            job.totalItems = skillNames.Length;
            job.status = "running";
            job.currentStage = "queued";
            job.progressStage = "queued";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            SmokeRuntimeJobs[job.jobId] = new SmokeRuntimeContext
            {
                Job = job,
                Skills = skills ?? Array.Empty<SkillRouter.SkillInfo>(),
                MetadataIssues = metadataIssues ?? Array.Empty<string>(),
                FailureItemLimit = Mathf.Clamp(failureItemLimit, 1, 200)
            };

            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static BatchJobRecord Cancel(string jobId)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null)
                return null;

            if (IsTerminal(job.status))
                return job;

            if (!job.canCancel)
            {
                job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                job.warnings.Add("This job cannot be cancelled once started.");
                job.resultSummary ??= "Cancellation is not supported for this job.";
                AddLog(job, "warn", job.currentStage ?? "running", "Cancellation is not supported for this job.", "cancel_unsupported");
                BatchPersistence.UpsertJob(job);
                return job;
            }

            job.status = "cancelled";
            job.currentStage = "cancelled";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = "Job was cancelled.";
            AddLog(job, "warn", "cancelled", "Cancellation requested.", "cancel_requested");
            BatchPersistence.UpsertJob(job);
            BatchJobService.NotifyCancelled(jobId);
            return job;
        }

        internal static BatchJobRecord Wait(string jobId, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));
            BatchJobRecord job;
            do
            {
                Pump(jobId);
                BatchJobService.Pump(jobId);
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
            ProcessJobs(jobId);
        }

        internal static void FailJob(string jobId, string error, string stage = "failed", Dictionary<string, object> resultData = null)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            if (resultData != null)
                job.resultData = resultData;

            job.status = "failed";
            job.currentStage = stage;
            job.error = error;
            job.progress = 100;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = error;
            job.progressStage = stage;
            job.progressEvents.Add(new BatchJobProgressEvent
            {
                timestamp = job.updatedAt,
                progress = 100,
                stage = stage,
                description = error
            });
            AddLog(job, "error", stage, error, "job_failed");
            BatchPersistence.UpsertJob(job);
        }

        internal static void CompleteJob(string jobId, string summary, Dictionary<string, object> resultData = null)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            if (resultData != null)
                job.resultData = resultData;

            job.status = "completed";
            job.currentStage = "completed";
            job.progress = 100;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = summary;
            job.progressStage = "completed";
            job.progressEvents.Add(new BatchJobProgressEvent
            {
                timestamp = job.updatedAt,
                progress = 100,
                stage = "completed",
                description = summary
            });
            AddLog(job, "info", "completed", summary, "job_completed");
            BatchPersistence.UpsertJob(job);
        }

        private static void ProcessJobs()
        {
            ProcessJobs(null);
        }

        private static void ProcessJobs(string onlyJobId)
        {
            foreach (var job in BatchPersistence.ListJobs(100))
            {
                if (job == null || job.preview != null || IsTerminal(job.status))
                    continue;

                if (!string.IsNullOrEmpty(onlyJobId) &&
                    !string.Equals(job.jobId, onlyJobId, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (job.kind)
                {
                    case "compile":
                        ProcessCompileJob(job);
                        break;
                    case "package":
                        ProcessPackageJob(job);
                        break;
                    case "test":
                        ProcessTestJob(job);
                        break;
                    case "test_smoke":
                        ProcessSmokeJob(job);
                        break;
                }
            }
        }

        private static void ProcessCompileJob(BatchJobRecord job)
        {
            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                Transition(job, "waiting_domain_reload", "compiling", 40, "Waiting for Unity compilation or asset refresh to finish.", "compile_wait");
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - job.startedAt < 1)
            {
                Transition(job, "running", "stabilizing", 20, "Waiting briefly for post-mutation compilation signals.", "compile_stabilizing");
                return;
            }

            var checkCompile = GetMetadataBool(job, "checkCompile", true);
            var supportsDiagnostics = GetMetadataBool(job, "supportsDiagnostics", true);
            var diagnosticLimit = GetMetadataInt(job, "diagnosticLimit", 20);
            var scriptPath = GetMetadataString(job, "scriptPath");
            var operation = GetMetadataString(job, "operation", "script_mutation");

            Dictionary<string, object> resultData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = scriptPath ?? string.Empty,
                ["operation"] = operation
            };

            if (checkCompile && supportsDiagnostics && !string.IsNullOrEmpty(scriptPath))
            {
                Transition(job, "running", "verifying", 90, "Collecting compilation diagnostics.", "compile_verifying");
                var compilation = ScriptSkills.GetCompilationFeedbackSnapshot(scriptPath, diagnosticLimit);
                resultData["compilation"] = compilation;
                if (TryGetCompilationHasErrors(compilation))
                {
                    FailJob(job.jobId, "Script compilation completed with errors.", "failed_compile", resultData);
                    return;
                }
            }

            CompleteJob(job.jobId, $"Script operation '{operation}' completed.", resultData);
        }

        private static void ProcessPackageJob(BatchJobRecord job)
        {
            var operation = GetMetadataString(job, "operation");
            var packageId = GetMetadataString(job, "packageId");
            var version = GetMetadataString(job, "version");

            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                Transition(job, "waiting_domain_reload", "package_domain_reload", 40, "Waiting for package import and domain reload to finish.", "package_reload");
                return;
            }

            if (PackageManagerHelper.HasPendingOperation || PackageManagerHelper.IsRefreshing)
            {
                var stage = string.IsNullOrEmpty(PackageManagerHelper.CurrentOperation)
                    ? "waiting_external"
                    : PackageManagerHelper.CurrentOperation;
                var packageName = string.IsNullOrEmpty(PackageManagerHelper.CurrentPackageId)
                    ? packageId
                    : PackageManagerHelper.CurrentPackageId;
                Transition(job, "waiting_external", stage, 60, $"Package Manager is processing {packageName}.", "package_wait");
                return;
            }

            if (PackageManagerHelper.InstalledPackages == null)
            {
                if (!GetMetadataBool(job, "refreshRequested", false))
                {
                    PackageManagerHelper.RefreshPackageList(_ => { });
                    job.metadata["refreshRequested"] = true;
                    BatchPersistence.UpsertJob(job);
                }

                Transition(job, "waiting_external", "refreshing_package_list", 70, "Refreshing installed package list before finalizing the job.", "package_refresh");
                return;
            }

            var installed = PackageManagerHelper.IsPackageInstalled(packageId);
            var installedVersion = PackageManagerHelper.GetInstalledVersion(packageId);
            var resultData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation"] = operation ?? string.Empty,
                ["packageId"] = packageId ?? string.Empty,
                ["requestedVersion"] = version ?? string.Empty,
                ["installed"] = installed,
                ["installedVersion"] = installedVersion ?? string.Empty
            };

            switch (operation)
            {
                case "install":
                    if (installed && (string.IsNullOrEmpty(version) || string.Equals(installedVersion, version, StringComparison.OrdinalIgnoreCase)))
                    {
                        CompleteJob(job.jobId, $"Installed {packageId}" + (string.IsNullOrEmpty(installedVersion) ? "." : $"@{installedVersion}."), resultData);
                        return;
                    }

                    break;
                case "remove":
                    if (!installed)
                    {
                        CompleteJob(job.jobId, $"Removed {packageId}.", resultData);
                        return;
                    }

                    break;
                case "refresh":
                    CompleteJob(job.jobId, "Package list refreshed.", resultData);
                    return;
            }

            FailJob(job.jobId, $"Package operation '{operation}' did not reach the expected final state.", "failed_package", resultData);
        }

        private static void ProcessTestJob(BatchJobRecord job)
        {
            if (TestRuntimeJobs.ContainsKey(job.jobId))
            {
                var runnerJobId = GetMetadataString(job, "runnerJobId");
                if (TryGetInternalTestRunnerState(runnerJobId, out var runnerState))
                {
                    var stage = MapInternalTestRunnerStage(runnerState.TaskIndex);
                    var summary = BuildInternalTestRunnerSummary(stage, runnerState.TaskIndex);
                    var progress = MapInternalTestRunnerProgress(runnerState.TaskIndex);

                    if (!string.Equals(job.currentStage, stage, StringComparison.OrdinalIgnoreCase))
                    {
                        Transition(job, "running", stage, progress, summary, "test_runner_internal_progress");
                    }
                    else if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.updatedAt >= 5)
                    {
                        job.progress = Math.Max(job.progress, progress);
                        job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        job.resultSummary = summary;
                        job.progressStage = stage;
                        BatchPersistence.UpsertJob(job);
                    }

                    return;
                }

                if (job.status == "running" &&
                    string.Equals(job.currentStage, "starting", StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.updatedAt > TestStartTimeoutSeconds)
                {
                    FailJob(job.jobId, $"Unity Test Runner did not leave 'starting' within {TestStartTimeoutSeconds} seconds.", "failed_start_timeout");
                    CleanupTestRuntime(job.jobId);
                }
                return;
            }

            if (job.status == "reconnecting")
            {
                var testMode = GetMetadataString(job, "testMode", "EditMode");
                var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.startedAt;

                // PlayMode tests cannot recover after Domain Reload (Unity limitation)
                // Also fail if more than 5 minutes have elapsed
                if (string.Equals(testMode, "PlayMode", StringComparison.OrdinalIgnoreCase) || elapsed > 300)
                {
                    FailJob(job.jobId,
                        $"Test run ({testMode}) cannot recover after domain reload.",
                        "failed_reload_unrecoverable");
                    return;
                }

                // EditMode tests: attempt to restart
                try
                {
                    var filter = GetMetadataString(job, "filter");
                    var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                    var callbacks = new TestCallbacks(job.jobId);
                    api.RegisterCallbacks(callbacks);

                    var filterObj = BuildTestFilter(testMode, filter, TestMode.EditMode);

                    TestRuntimeJobs[job.jobId] = new TestRuntimeContext
                    {
                        Api = api,
                        Callbacks = callbacks
                    };

                    Transition(job, "running", "restarting", 10,
                        "Restarting EditMode tests after domain reload.", "test_recovery");

                    api.Execute(new ExecutionSettings
                    {
                        filters = new[] { filterObj }
                    });
                }
                catch (Exception ex)
                {
                    FailJob(job.jobId, $"Failed to restart tests: {ex.Message}", "failed_reconnect");
                }
            }
        }

        private static void ProcessSmokeJob(BatchJobRecord job)
        {
            if (job == null)
                return;

            if (job.status == "cancelled")
            {
                CleanupSmokeRuntime(job.jobId);
                return;
            }

            if (!SmokeRuntimeJobs.TryGetValue(job.jobId, out var runtime))
            {
                runtime = TryRebuildSmokeRuntime(job);
                if (runtime == null)
                {
                    FailJob(job.jobId, "Smoke runtime context was lost and could not be rebuilt.", "failed_reconnect");
                    return;
                }

                SmokeRuntimeJobs[job.jobId] = runtime;
            }

            var start = Mathf.Clamp(job.processedItems, 0, runtime.Skills.Length);
            if (start >= runtime.Skills.Length)
            {
                CompleteSmokeJob(runtime);
                return;
            }

            var endExclusive = Math.Min(runtime.Skills.Length, start + Math.Max(1, job.chunkSize));
            Transition(job, "running", $"chunk_{(start / Math.Max(1, job.chunkSize)) + 1}", job.progress, $"Smoke probe {start + 1}-{endExclusive}/{runtime.Skills.Length}.", "smoke_chunk");

            for (var i = start; i < endExclusive; i++)
            {
                var outcome = TestSkills.EvaluateSmokeSkill(runtime.Skills[i], runtime.MetadataIssues, GetMetadataBool(job, "executeReadOnly", true));
                RecordSmokeOutcome(job, outcome, runtime.FailureItemLimit);
            }

            job.processedItems = endExclusive;
            job.progress = runtime.Skills.Length == 0
                ? 100
                : (int)Math.Round(job.processedItems * 100.0 / runtime.Skills.Length);
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = BuildSmokeSummary(job);

            if (job.processedItems >= runtime.Skills.Length)
            {
                CompleteSmokeJob(runtime);
                return;
            }

            BatchPersistence.UpsertJob(job);
        }

        private static void Transition(BatchJobRecord job, string status, string stage, int progress, string summary, string code)
        {
            if (job == null || IsTerminal(job.status))
                return;

            var shouldLog = !string.Equals(job.status, status, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(job.currentStage, stage, StringComparison.OrdinalIgnoreCase);

            job.status = status;
            job.currentStage = stage;
            job.progress = Math.Max(job.progress, progress);
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = summary;
            job.progressStage = stage;
            if (shouldLog)
            {
                AddLog(job, "info", stage, summary, code);
                job.progressEvents.Add(new BatchJobProgressEvent
                {
                    timestamp = job.updatedAt,
                    progress = job.progress,
                    stage = stage,
                    description = summary
                });
            }
            BatchPersistence.UpsertJob(job);
        }

        private static void UpdateTestRunStarted(string jobId, int totalTests)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            job.resultData["totalTests"] = totalTests;
            Transition(job, "running", "running", 5, $"Running {totalTests} tests.", "test_running");
        }

        private static void UpdateTestFinished(
            string jobId,
            int passedTests,
            int failedTests,
            int skippedTests,
            int inconclusiveTests,
            int otherTests,
            string failedTestName)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            job.resultData["passedTests"] = passedTests;
            job.resultData["failedTests"] = failedTests;
            job.resultData["skippedTests"] = skippedTests;
            job.resultData["inconclusiveTests"] = inconclusiveTests;
            job.resultData["otherTests"] = otherTests;
            var totalTests = GetResultInt(job, "totalTests", 0);
            if (!job.resultData.TryGetValue("failedTestNames", out var value) || !(value is List<string> failedNames))
            {
                failedNames = new List<string>();
                job.resultData["failedTestNames"] = failedNames;
            }

            if (!string.IsNullOrEmpty(failedTestName) && !failedNames.Contains(failedTestName))
                failedNames.Add(failedTestName);

            var completedTests = passedTests + failedTests + skippedTests + inconclusiveTests + otherTests;
            if (totalTests > 0)
                job.progress = Math.Min(95, (int)Math.Round(completedTests * 100.0 / totalTests));

            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.progressStage = "running";
            job.resultSummary = BuildTestProgressSummary(passedTests, failedTests, skippedTests, inconclusiveTests, otherTests);
            BatchPersistence.UpsertJob(job);
        }

        private static void CompleteTestRun(string jobId)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null)
                return;

            var passedTests = GetResultInt(job, "passedTests", 0);
            var failedTests = GetResultInt(job, "failedTests", 0);
            var skippedTests = GetResultInt(job, "skippedTests", 0);
            var inconclusiveTests = GetResultInt(job, "inconclusiveTests", 0);
            var otherTests = GetResultInt(job, "otherTests", 0);
            var totalTests = GetResultInt(job, "totalTests", 0);
            Transition(job, "running", "collecting_results", 95, "Collecting final Unity Test Runner results.", "test_collecting");
            var summary = BuildCompletedTestSummary(totalTests, passedTests, failedTests, skippedTests, inconclusiveTests, otherTests);
            CompleteJob(jobId, summary, job.resultData);
            CleanupTestRuntime(jobId);
        }

        private static void FinalizeTestRunFromAggregate(ITestResultAdaptor result)
        {
            if (result == null)
                return;
        }

        private static void CleanupTestRuntime(string jobId)
        {
            if (!TestRuntimeJobs.TryGetValue(jobId, out var runtime))
                return;

            runtime.Api?.UnregisterCallbacks(runtime.Callbacks);
            if (runtime.Api != null)
                UnityEngine.Object.DestroyImmediate(runtime.Api);
            TestRuntimeJobs.Remove(jobId);
        }

        private static void CleanupSmokeRuntime(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return;

            SmokeRuntimeJobs.Remove(jobId);
        }

        private static SmokeRuntimeContext TryRebuildSmokeRuntime(BatchJobRecord job)
        {
            if (job?.metadata == null)
                return null;

            var skillNames = GetMetadataStringArray(job, "skillNames");
            if (skillNames.Length == 0)
                return null;

            var skills = new List<SkillRouter.SkillInfo>(skillNames.Length);
            foreach (var skillName in skillNames)
            {
                if (SkillRouter.TryGetSkill(skillName, out var skill))
                    skills.Add(skill);
            }

            return new SmokeRuntimeContext
            {
                Job = job,
                Skills = skills.ToArray(),
                MetadataIssues = (job.resultData != null && job.resultData.TryGetValue("metadataWarnings", out var warningsValue) && warningsValue is IEnumerable<object> warningsObjects)
                    ? warningsObjects.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                    : Array.Empty<string>(),
                FailureItemLimit = Mathf.Clamp(GetMetadataInt(job, "failureItemLimit", 50), 1, 200)
            };
        }

        private static void RecordSmokeOutcome(BatchJobRecord job, TestSkills.SmokeOutcome outcome, int failureItemLimit)
        {
            IncrementResultCounter(job, "completedSkills");
            if (string.Equals(outcome.ProbeMode, "execute", StringComparison.OrdinalIgnoreCase))
                IncrementResultCounter(job, "executedCount");
            else if (string.Equals(outcome.ProbeMode, "dryRun", StringComparison.OrdinalIgnoreCase))
                IncrementResultCounter(job, "dryRunCount");

            if (string.Equals(outcome.Status, "skipped", StringComparison.OrdinalIgnoreCase) ||
                (string.Equals(outcome.Status, "dryRun", StringComparison.OrdinalIgnoreCase) && !outcome.Valid.GetValueOrDefault(true)))
            {
                IncrementResultCounter(job, "skippedCount");
            }

            if (!string.Equals(outcome.Status, "error", StringComparison.OrdinalIgnoreCase))
                return;

            IncrementResultCounter(job, "failureCount");
            if (!job.resultData.TryGetValue("failureItems", out var failureItemsValue) || !(failureItemsValue is List<object> failureItems))
            {
                failureItems = new List<object>();
                job.resultData["failureItems"] = failureItems;
            }

            if (failureItems.Count >= failureItemLimit)
                return;

            failureItems.Add(new
            {
                skill = outcome.Skill,
                category = outcome.Category,
                probeMode = outcome.ProbeMode,
                error = outcome.Error,
                missingParams = outcome.MissingParams ?? Array.Empty<string>(),
                semanticWarnings = outcome.SemanticWarnings ?? Array.Empty<string>(),
                metadataWarnings = outcome.MetadataWarnings ?? Array.Empty<string>()
            });
        }

        private static void CompleteSmokeJob(SmokeRuntimeContext runtime)
        {
            var job = runtime?.Job;
            if (job == null)
                return;

            job.progress = 100;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.currentStage = "completed";
            job.progressStage = "completed";
            job.resultSummary = BuildSmokeSummary(job);
            CompleteJob(job.jobId, job.resultSummary, job.resultData);
            CleanupSmokeRuntime(job.jobId);
        }

        private static string BuildSmokeSummary(BatchJobRecord job)
        {
            var total = GetResultInt(job, "totalSkills", 0);
            var completed = GetResultInt(job, "completedSkills", 0);
            var failures = GetResultInt(job, "failureCount", 0);
            var skipped = GetResultInt(job, "skippedCount", 0);
            var executed = GetResultInt(job, "executedCount", 0);
            var dryRun = GetResultInt(job, "dryRunCount", 0);
            return $"Smoke {completed}/{total} complete. {executed} executed, {dryRun} dry-run, {skipped} skipped, {failures} failed.";
        }

        private static void IncrementResultCounter(BatchJobRecord job, string key)
        {
            var current = GetResultInt(job, key, 0);
            job.resultData[key] = current + 1;
        }

        private static void AddLog(BatchJobRecord job, string level, string stage, string message, string code)
        {
            job.logs.Add(new BatchJobLogEntry
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                level = level,
                stage = stage,
                message = message,
                code = code
            });
        }

        private static bool TryGetCompilationHasErrors(Dictionary<string, object> compilation)
        {
            if (compilation == null)
                return false;

            if (compilation.TryGetValue("hasErrors", out var value))
            {
                if (value is bool boolValue)
                    return boolValue;
                if (bool.TryParse(value?.ToString(), out var parsed))
                    return parsed;
            }

            return false;
        }

        private static bool TryGetInternalTestRunnerState(string runnerJobId, out InternalTestRunnerState state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(runnerJobId))
                return false;

            try
            {
                var assembly = typeof(TestRunnerApi).Assembly;
                var holderType = assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder");
                if (holderType == null)
                    return false;

                var instanceProperty = holderType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var holder = instanceProperty?.GetValue(null);
                if (holder == null)
                    return false;

                var testRunsField = holderType.GetField("TestRuns", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (!(testRunsField?.GetValue(holder) is System.Collections.IEnumerable testRuns))
                    return false;

                foreach (var testRun in testRuns)
                {
                    if (testRun == null)
                        continue;

                    var testRunType = testRun.GetType();
                    var guidField = testRunType.GetField("guid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var guid = guidField?.GetValue(testRun)?.ToString();
                    if (!string.Equals(guid, runnerJobId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var isRunningField = testRunType.GetField("isRunning", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var taskIndexField = testRunType.GetField("taskIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    state = new InternalTestRunnerState
                    {
                        IsRunning = isRunningField != null && Convert.ToBoolean(isRunningField.GetValue(testRun)),
                        TaskIndex = taskIndexField != null ? Convert.ToInt32(taskIndexField.GetValue(testRun)) : 0
                    };
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static string MapInternalTestRunnerStage(int taskIndex)
        {
            switch (taskIndex)
            {
                case 0:
                    return "save_modified_scenes";
                case 1:
                    return "register_cleanup_verification";
                case 2:
                    return "save_undo_index";
                case 3:
                    return "build_test_tree";
                case 4:
                    return "prebuild_setup";
                case 5:
                    return "execute_tests";
                case 6:
                    return "perform_undo";
                case 7:
                    return "cleanup_verification";
                default:
                    return "starting";
            }
        }

        private static int MapInternalTestRunnerProgress(int taskIndex)
        {
            switch (taskIndex)
            {
                case 0:
                    return 2;
                case 1:
                    return 4;
                case 2:
                    return 6;
                case 3:
                    return 10;
                case 4:
                    return 20;
                case 5:
                    return 30;
                case 6:
                    return 92;
                case 7:
                    return 94;
                default:
                    return 1;
            }
        }

        private static string BuildInternalTestRunnerSummary(string stage, int taskIndex)
        {
            switch (taskIndex)
            {
                case 0:
                    return "Unity Test Runner is checking for unsaved scene changes.";
                case 1:
                    return "Unity Test Runner is preparing cleanup verification.";
                case 2:
                    return "Unity Test Runner is capturing the Undo baseline.";
                case 3:
                    return "Unity Test Runner is building the test tree.";
                case 4:
                    return "Unity Test Runner is running prebuild setup.";
                case 5:
                    return "Unity Test Runner has started executing tests.";
                case 6:
                    return "Unity Test Runner is reverting scene changes after the run.";
                case 7:
                    return "Unity Test Runner is finalizing cleanup verification.";
                default:
                    return $"Unity Test Runner is preparing the run ({stage}).";
            }
        }

        private static int GetResultInt(BatchJobRecord job, string key, int defaultValue)
        {
            if (job?.resultData == null || !job.resultData.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is int intValue)
                return intValue;
            if (value is long longValue)
                return (int)longValue;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
        }

        private static string GetMetadataString(BatchJobRecord job, string key, string defaultValue = "")
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            return value.ToString();
        }

        private static int GetMetadataInt(BatchJobRecord job, string key, int defaultValue)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is int intValue)
                return intValue;
            if (value is long longValue)
                return (int)longValue;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
        }

        private static bool GetMetadataBool(BatchJobRecord job, string key, bool defaultValue)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is bool boolValue)
                return boolValue;
            return bool.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
        }

        private static bool IsTerminal(string status)
        {
            return status == "completed" || status == "failed" || status == "cancelled";
        }

        private static string[] GetMetadataStringArray(BatchJobRecord job, string key)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null)
                return Array.Empty<string>();

            if (value is string[] stringArray)
                return stringArray;

            if (value is IEnumerable<object> objectArray)
                return objectArray.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();

            return Array.Empty<string>();
        }

        private static string BuildTestProgressSummary(int passedTests, int failedTests, int skippedTests, int inconclusiveTests, int otherTests)
        {
            var segments = new List<string>
            {
                $"{passedTests} passed"
            };

            if (failedTests > 0)
                segments.Add($"{failedTests} failed");
            if (skippedTests > 0)
                segments.Add($"{skippedTests} skipped");
            if (inconclusiveTests > 0)
                segments.Add($"{inconclusiveTests} inconclusive");
            if (otherTests > 0)
                segments.Add($"{otherTests} other");

            return string.Join(", ", segments) + ".";
        }

        private static string BuildCompletedTestSummary(int totalTests, int passedTests, int failedTests, int skippedTests, int inconclusiveTests, int otherTests)
        {
            var segments = new List<string> { $"{passedTests}/{totalTests} passed" };
            if (failedTests > 0)
                segments.Add($"{failedTests} failed");
            if (skippedTests > 0)
                segments.Add($"{skippedTests} skipped");
            if (inconclusiveTests > 0)
                segments.Add($"{inconclusiveTests} inconclusive");
            if (otherTests > 0)
                segments.Add($"{otherTests} other");
            return "Test run completed: " + string.Join(", ", segments) + ".";
        }

        private sealed class TestCallbacks : ICallbacks
        {
            private readonly string _jobId;
            private int _passedTests;
            private int _failedTests;
            private int _skippedTests;
            private int _inconclusiveTests;
            private int _otherTests;

            public TestCallbacks(string jobId)
            {
                _jobId = jobId;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                UpdateTestRunStarted(_jobId, CountTests(testsToRun));
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                var job = BatchPersistence.GetJob(_jobId);
                if (job != null && !IsTerminal(job.status))
                {
                    CountResultOutcomes(result, out var totalTests, out var passedTests, out var failedTests, out var skippedTests, out var inconclusiveTests, out var otherTests);
                    var failedNames = new List<string>();
                    CollectFailedTestNames(result, failedNames);

                    job.resultData["totalTests"] = totalTests;
                    job.resultData["passedTests"] = passedTests;
                    job.resultData["failedTests"] = failedTests;
                    job.resultData["skippedTests"] = skippedTests;
                    job.resultData["inconclusiveTests"] = inconclusiveTests;
                    job.resultData["otherTests"] = otherTests;
                    job.resultData["failedTestNames"] = failedNames;
                    BatchPersistence.UpsertJob(job);
                }

                CompleteTestRun(_jobId);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.HasChildren)
                    return;

                string failedTestName = null;
                switch (result.TestStatus.ToString())
                {
                    case "Passed":
                        _passedTests++;
                        break;
                    case "Failed":
                        _failedTests++;
                        failedTestName = result.Test.FullName;
                        break;
                    case "Skipped":
                        _skippedTests++;
                        break;
                    case "Inconclusive":
                        _inconclusiveTests++;
                        break;
                    default:
                        _otherTests++;
                        break;
                }

                UpdateTestFinished(
                    _jobId,
                    _passedTests,
                    _failedTests,
                    _skippedTests,
                    _inconclusiveTests,
                    _otherTests,
                    failedTestName);
            }

            private static int CountTests(ITestAdaptor test)
            {
                if (!test.HasChildren)
                    return 1;

                return test.Children.Sum(CountTests);
            }

            private static void CollectFailedTestNames(ITestResultAdaptor result, List<string> failedNames)
            {
                if (result == null || failedNames == null)
                    return;

                if (!result.HasChildren)
                {
                    if (string.Equals(result.TestStatus.ToString(), "Failed", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(result.FullName) &&
                        !failedNames.Contains(result.FullName))
                    {
                        failedNames.Add(result.FullName);
                    }

                    return;
                }

                foreach (var child in result.Children ?? Enumerable.Empty<ITestResultAdaptor>())
                    CollectFailedTestNames(child, failedNames);
            }

            private static void CountResultOutcomes(
                ITestResultAdaptor result,
                out int totalTests,
                out int passedTests,
                out int failedTests,
                out int skippedTests,
                out int inconclusiveTests,
                out int otherTests)
            {
                totalTests = 0;
                passedTests = 0;
                failedTests = 0;
                skippedTests = 0;
                inconclusiveTests = 0;
                otherTests = 0;

                CountResultOutcomesRecursive(result, ref totalTests, ref passedTests, ref failedTests, ref skippedTests, ref inconclusiveTests, ref otherTests);
            }

            private static void CountResultOutcomesRecursive(
                ITestResultAdaptor result,
                ref int totalTests,
                ref int passedTests,
                ref int failedTests,
                ref int skippedTests,
                ref int inconclusiveTests,
                ref int otherTests)
            {
                if (result == null)
                    return;

                if (!result.HasChildren)
                {
                    totalTests++;
                    switch (result.TestStatus.ToString())
                    {
                        case "Passed":
                            passedTests++;
                            break;
                        case "Failed":
                            failedTests++;
                            break;
                        case "Skipped":
                            skippedTests++;
                            break;
                        case "Inconclusive":
                            inconclusiveTests++;
                            break;
                        default:
                            otherTests++;
                            break;
                    }

                    return;
                }

                foreach (var child in result.Children ?? Enumerable.Empty<ITestResultAdaptor>())
                {
                    CountResultOutcomesRecursive(child, ref totalTests, ref passedTests, ref failedTests, ref skippedTests, ref inconclusiveTests, ref otherTests);
                }
            }
        }
    }
}
