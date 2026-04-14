using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnitySkills
{
    [InitializeOnLoad]
    internal static class AsyncJobService
    {
        private sealed class TestRuntimeContext
        {
            public TestRunnerApi Api;
            public TestCallbacks Callbacks;
        }

        private static readonly Dictionary<string, TestRuntimeContext> TestRuntimeJobs =
            new Dictionary<string, TestRuntimeContext>(StringComparer.OrdinalIgnoreCase);

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
                startedAt = now,
                updatedAt = now,
                resultSummary = resultSummary,
                canCancel = canCancel,
                metadata = metadata ?? new Dictionary<string, object>(),
                resultData = resultData ?? new Dictionary<string, object>()
            };

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
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BatchPersistence.UpsertJob(job);
            return job;
        }

        internal static BatchJobRecord StartTestJob(string testMode, string filter = null)
        {
            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["testMode"] = testMode ?? "EditMode",
                ["filter"] = filter ?? string.Empty
            };

            var job = CreateJob(
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
                    ["failedTestNames"] = new List<string>()
                });

            var mode = string.Equals(testMode, "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var callbacks = new TestCallbacks(job.jobId);
            api.RegisterCallbacks(callbacks);

            var filterObj = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(filter))
                filterObj.testNames = new[] { filter };

            TestRuntimeJobs[job.jobId] = new TestRuntimeContext
            {
                Api = api,
                Callbacks = callbacks
            };

            job.status = "running";
            job.currentStage = "starting";
            job.resultSummary = "Launching Unity Test Runner.";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AddLog(job, "info", "starting", $"Starting {testMode} tests.", "test_start");
            BatchPersistence.UpsertJob(job);

            api.Execute(new ExecutionSettings(filterObj));
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
                return;

            if (job.status == "reconnecting")
            {
                FailJob(job.jobId, "Test run tracking was interrupted by domain reload and could not be resumed.", "failed_reconnect");
            }
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
            if (shouldLog)
                AddLog(job, "info", stage, summary, code);
            BatchPersistence.UpsertJob(job);
        }

        private static void UpdateTestRunStarted(string jobId, int totalTests)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            job.status = "running";
            job.currentStage = "running";
            job.progress = 5;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = $"Running {totalTests} tests.";
            job.resultData["totalTests"] = totalTests;
            AddLog(job, "info", "running", $"Test run started with {totalTests} tests.", "test_running");
            BatchPersistence.UpsertJob(job);
        }

        private static void UpdateTestFinished(string jobId, int passedTests, int failedTests, string failedTestName)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null || IsTerminal(job.status))
                return;

            job.resultData["passedTests"] = passedTests;
            job.resultData["failedTests"] = failedTests;
            var totalTests = GetResultInt(job, "totalTests", 0);
            if (!job.resultData.TryGetValue("failedTestNames", out var value) || !(value is List<string> failedNames))
            {
                failedNames = new List<string>();
                job.resultData["failedTestNames"] = failedNames;
            }

            if (!string.IsNullOrEmpty(failedTestName) && !failedNames.Contains(failedTestName))
                failedNames.Add(failedTestName);

            if (totalTests > 0)
                job.progress = Math.Min(95, (int)Math.Round((passedTests + failedTests) * 100.0 / totalTests));

            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.resultSummary = failedTests > 0
                ? $"{passedTests} passed, {failedTests} failed."
                : $"{passedTests} tests passed.";
            BatchPersistence.UpsertJob(job);
        }

        private static void CompleteTestRun(string jobId)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null)
                return;

            var passedTests = GetResultInt(job, "passedTests", 0);
            var failedTests = GetResultInt(job, "failedTests", 0);
            var totalTests = GetResultInt(job, "totalTests", 0);
            var summary = failedTests > 0
                ? $"Test run completed: {passedTests}/{totalTests} passed, {failedTests} failed."
                : $"Test run completed: {passedTests}/{totalTests} passed.";
            CompleteJob(jobId, summary, job.resultData);
            CleanupTestRuntime(jobId);
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

        private sealed class TestCallbacks : ICallbacks
        {
            private readonly string _jobId;
            private int _passedTests;
            private int _failedTests;

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
                if (result.TestStatus == TestStatus.Passed)
                {
                    _passedTests++;
                }
                else if (result.TestStatus == TestStatus.Failed)
                {
                    _failedTests++;
                    failedTestName = result.Test.FullName;
                }

                UpdateTestFinished(_jobId, _passedTests, _failedTests, failedTestName);
            }

            private static int CountTests(ITestAdaptor test)
            {
                if (!test.HasChildren)
                    return 1;

                return test.Children.Sum(CountTests);
            }
        }
    }
}
