using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Test runner skills.
    /// </summary>
    public static class TestSkills
    {
        private const string TestDiscoveryMode = "unity_test_runner_async_cache";
        private const string TestDiscoveryJobKind = "test_discovery";

        internal sealed class SmokeOutcome
        {
            public string Skill;
            public string Category;
            public string ProbeMode;
            public string Status;
            public bool? Valid;
            public string Error;
            public string[] MissingParams;
            public string[] SemanticWarnings;
            public string[] MetadataWarnings;
        }

        [UnitySkill("test_run", "Run Unity tests asynchronously. Returns a platform jobId immediately. Poll with job_status/job_wait or test_get_result(jobId).",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "run", "async", "editmode", "playmode", "job" },
            Outputs = new[] { "jobId", "testMode", "message" },
            SupportsDryRun = false, MayEnterPlayMode = true)]
        public static object TestRun(string testMode = "EditMode", string filter = null)
        {
            if (!AsyncJobService.TryStartTestJob(testMode, filter, out var job, out var error))
                return new { success = false, error };

            return new
            {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                kind = job.kind,
                testMode,
                filter,
                message = "Tests started. Use job_status/job_wait or test_get_result(jobId) to monitor progress."
            };
        }

        [UnitySkill("test_get_result", "Get the result of a test run. Compatible wrapper over the unified job model.",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "result", "status", "poll", "job" },
            Outputs = new[] { "jobId", "status", "totalTests", "passedTests", "failedTests", "skippedTests", "inconclusiveTests", "otherTests", "failedTestNames", "failedTestDetails" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestGetResult(string jobId)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Get(jobId);
            if (job == null || job.kind != "test")
                return new { error = $"Test job not found: {jobId}" };

            return new
            {
                success = true,
                jobId,
                status = job.status,
                totalTests = GetResultInt(job, "totalTests"),
                passedTests = GetResultInt(job, "passedTests"),
                failedTests = GetResultInt(job, "failedTests"),
                skippedTests = GetResultInt(job, "skippedTests"),
                inconclusiveTests = GetResultInt(job, "inconclusiveTests"),
                otherTests = GetResultInt(job, "otherTests"),
                failedTestNames = GetResultStringList(job, "failedTestNames").ToArray(),
                failedTestDetails = GetResultValue(job, "failedTestDetails") ?? Array.Empty<object>(),
                elapsedSeconds = System.Math.Max(0, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.startedAt),
                resultSummary = job.resultSummary,
                error = job.error
            };
        }

        [UnitySkill("test_list", "List available tests via Unity Test Runner async discovery. Returns pendingDiscovery=true + discoveryJobId on first call (cache miss) — poll test_discover_get_result(jobId) then retry test_list.",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "list", "discover", "enumerate" },
            Outputs = new[] { "testMode", "count", "tests", "pendingDiscovery", "discoveryJobId", "discoveryStatus" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestList(string testMode = "EditMode", int limit = 100)
        {
            var discovery = GetLatestCompletedDiscovery(testMode);
            if (discovery == null)
            {
                var started = StartTestDiscovery(testMode);
                return new
                {
                    success = true,
                    pendingDiscovery = true,
                    testMode,
                    discoveryMode = TestDiscoveryMode,
                    discoveryJobId = started.jobId,
                    discoveryStatus = started.status,
                    message = "No cached Unity Test Runner discovery result is available yet. Discovery has been started asynchronously; poll test_discover_get_result(jobId) and retry test_list after it completes."
                };
            }

            var tests = GetDiscoveredTests(discovery)
                .Take(Mathf.Max(1, limit))
                .Select(test => new
                {
                    name = test.Name,
                    fullName = test.FullName,
                    runState = test.RunState
                })
                .ToArray();

            return new
            {
                success = true,
                testMode,
                count = tests.Length,
                discoveryMode = TestDiscoveryMode,
                tests
            };
        }

        [UnitySkill("test_discover_start", "Start asynchronous Unity Test Runner discovery and return a discovery jobId.",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "discover", "list", "async", "job" },
            Outputs = new[] { "jobId", "testMode", "message" },
            SupportsDryRun = false)]
        public static object TestDiscoverStart(string testMode = "EditMode")
        {
            var job = StartTestDiscovery(testMode);
            return new
            {
                success = true,
                status = job.status,
                jobId = job.jobId,
                kind = job.kind,
                testMode,
                message = "Unity Test Runner discovery started. Poll with test_discover_get_result(jobId)."
            };
        }

        [UnitySkill("test_discover_get_result", "Get the result of an asynchronous Unity Test Runner discovery job.",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "discover", "result", "poll", "job" },
            Outputs = new[] { "jobId", "status", "count", "tests" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestDiscoverGetResult(string jobId, int limit = 100)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = BatchPersistence.GetJob(jobId);
            if (job == null || !string.Equals(job.kind, TestDiscoveryJobKind, StringComparison.OrdinalIgnoreCase))
                return new { error = $"Test discovery job not found: {jobId}" };

            var discoveredTests = GetDiscoveredTests(job);
            var tests = discoveredTests
                .Take(Mathf.Max(1, limit))
                .Select(test => new
                {
                    name = test.Name,
                    fullName = test.FullName,
                    runState = test.RunState,
                    categories = test.Categories ?? Array.Empty<string>()
                })
                .ToArray();

            return new
            {
                success = true,
                jobId,
                status = job.status,
                testMode = GetMetadataString(job, "testMode"),
                discoveryMode = TestDiscoveryMode,
                count = discoveredTests.Count,
                tests,
                error = job.error
            };
        }

        [UnitySkill("test_cancel", "Cancel a running test job if supported. Unity TestRunner itself does not provide a hard cancel.",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "cancel", "abort", "stop", "job" },
            Outputs = new[] { "cancelled" },
            RequiresInput = new[] { "jobId" })]
        public static object TestCancel(string jobId = null)
        {
            if (Validate.Required(jobId, "jobId") is object err)
                return err;

            var job = AsyncJobService.Cancel(jobId);
            if (job == null || job.kind != "test")
                return new { error = $"Test job not found: {jobId}" };

            return new
            {
                success = true,
                jobId = job.jobId,
                status = job.status,
                cancelled = job.status == "cancelled",
                note = "Unity TestRunnerApi does not support direct cancellation. The unified job layer only reports supported cancellation states.",
                warnings = job.warnings
            };
        }

        [UnitySkill("test_run_by_name", "Run specific tests by class or method name. Returns a unified jobId.",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "run", "name", "specific", "job" },
            Outputs = new[] { "jobId", "testName", "testMode" },
            SupportsDryRun = false, MayEnterPlayMode = true)]
        public static object TestRunByName(string testName, string testMode = "EditMode")
        {
            if (Validate.Required(testName, "testName") is object err)
                return err;

            if (!AsyncJobService.TryStartTestJob(testMode, testName, out var job, out var error))
                return new { success = false, error };

            return new
            {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                testName,
                testMode
            };
        }

        [UnitySkill("test_get_last_result", "Get the most recent test run result",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "result", "last", "recent" },
            Outputs = new[] { "jobId", "status", "total", "passed", "failed", "skipped", "inconclusive", "other", "failedNames" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestGetLastResult()
        {
            var last = EnumerateRealTestRuns(100)
                .OrderByDescending(job => job.startedAt)
                .FirstOrDefault();
            if (last == null)
                return new { error = "No test runs found" };

            return new
            {
                success = true,
                jobId = last.jobId,
                status = last.status,
                total = GetResultInt(last, "totalTests"),
                passed = GetResultInt(last, "passedTests"),
                failed = GetResultInt(last, "failedTests"),
                skipped = GetResultInt(last, "skippedTests"),
                inconclusive = GetResultInt(last, "inconclusiveTests"),
                other = GetResultInt(last, "otherTests"),
                failedNames = GetResultStringList(last, "failedTestNames").ToArray()
            };
        }

        [UnitySkill("test_list_categories", "List test categories via Unity Test Runner async discovery. Returns pendingDiscovery=true + discoveryJobId on first call (cache miss) — poll test_discover_get_result(jobId) then retry.",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "categories", "list", "nunit" },
            Outputs = new[] { "count", "categories", "pendingDiscovery", "discoveryJobId", "discoveryStatus" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestListCategories(string testMode = "EditMode")
        {
            var discovery = GetLatestCompletedDiscovery(testMode);
            if (discovery == null)
            {
                var started = StartTestDiscovery(testMode);
                return new
                {
                    success = true,
                    pendingDiscovery = true,
                    testMode,
                    discoveryMode = TestDiscoveryMode,
                    discoveryJobId = started.jobId,
                    discoveryStatus = started.status,
                    message = "No cached Unity Test Runner discovery result is available yet. Discovery has been started asynchronously; poll test_discover_get_result(jobId) and retry test_list_categories after it completes."
                };
            }

            var categories = GetDiscoveredTests(discovery)
                .SelectMany(test => test.Categories ?? Array.Empty<string>())
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                success = true,
                count = categories.Length,
                categories,
                discoveryMode = TestDiscoveryMode,
                note = categories.Length == 0
                    ? "No [Category] attributes were found in discovered tests."
                    : null
            };
        }

        [UnitySkill("test_smoke_skills", "Run a reusable smoke test across registered skills. Executes safe read-only skills and dry-runs the rest for broad regression coverage.",
            Category = SkillCategory.Test, Operation = SkillOperation.Analyze,
            Tags = new[] { "test", "smoke", "skills", "regression", "coverage" },
            Outputs = new[] { "totalSkills", "executedCount", "dryRunCount", "failureCount", "results" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestSmokeSkills(
            string category = null,
            string nameContains = null,
            string excludeNamesCsv = null,
            bool executeReadOnly = true,
            bool includeMutating = true,
            int limit = 0,
            bool runAsync = true,
            int chunkSize = 25,
            int failureItemLimit = 50)
        {
            var request = BuildSmokeRequest(category, nameContains, excludeNamesCsv, executeReadOnly, includeMutating, limit);

            if (runAsync)
            {
                var job = AsyncJobService.StartSmokeJob(request.SelectedSkills, request.MetadataIssues, executeReadOnly, chunkSize, failureItemLimit);
                return new
                {
                    success = true,
                    status = "accepted",
                    jobId = job.jobId,
                    kind = job.kind,
                    totalSkills = request.SelectedSkills.Length,
                    filters = new
                    {
                        category,
                        nameContains,
                        excludeNames = request.ExcludedNames.OrderBy(name => name).ToArray(),
                        executeReadOnly,
                        includeMutating,
                        limit,
                        chunkSize,
                        failureItemLimit
                    },
                    message = "Smoke test job created. Use job_status/job_wait to monitor progress."
                };
            }

            var results = new List<object>(request.SelectedSkills.Length);
            int executedCount = 0;
            int dryRunCount = 0;
            int skippedCount = 0;
            int failureCount = 0;

            foreach (var skill in request.SelectedSkills)
            {
                var outcome = EvaluateSmokeSkill(skill, request.MetadataIssues, executeReadOnly);
                if (string.Equals(outcome.ProbeMode, "execute", StringComparison.OrdinalIgnoreCase))
                    executedCount++;
                else if (string.Equals(outcome.ProbeMode, "dryRun", StringComparison.OrdinalIgnoreCase))
                    dryRunCount++;

                if (string.Equals(outcome.Status, "error", StringComparison.OrdinalIgnoreCase))
                    failureCount++;

                if (string.Equals(outcome.Status, "skipped", StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(outcome.Status, "dryRun", StringComparison.OrdinalIgnoreCase) && !outcome.Valid.GetValueOrDefault(true)))
                {
                    skippedCount++;
                }

                results.Add(new
                {
                    skill = outcome.Skill,
                    category = outcome.Category,
                    readOnly = skill.ReadOnly,
                    riskLevel = skill.RiskLevel,
                    probeMode = outcome.ProbeMode,
                    status = outcome.Status,
                    valid = outcome.Valid,
                    missingParams = outcome.MissingParams ?? Array.Empty<string>(),
                    semanticWarnings = outcome.SemanticWarnings ?? Array.Empty<string>(),
                    metadataWarnings = outcome.MetadataWarnings ?? Array.Empty<string>(),
                    error = outcome.Error
                });
            }

            return new
            {
                success = failureCount == 0,
                totalSkills = request.SelectedSkills.Length,
                executedCount,
                dryRunCount,
                skippedCount,
                failureCount,
                filters = new
                {
                    category,
                    nameContains,
                    excludeNames = request.ExcludedNames.OrderBy(name => name).ToArray(),
                    executeReadOnly,
                    includeMutating,
                    limit
                },
                note = "Read-only skills with no required inputs are executed directly; all other skills are smoke-tested via dryRun with empty arguments.",
                results
            };
        }

        internal static SmokeOutcome EvaluateSmokeSkill(SkillRouter.SkillInfo skill, string[] metadataIssues, bool executeReadOnly)
        {
            var validation = SkillRouter.ValidateParameters(skill, "{}");
            var canExecuteReadOnly = executeReadOnly &&
                                     skill.ReadOnly &&
                                     (skill.RequiresInput == null || skill.RequiresInput.Length == 0) &&
                                     validation.MissingParams.Count == 0 &&
                                     validation.TypeErrors.Count == 0 &&
                                     !skill.MayTriggerReload;

            if (skill.MayTriggerReload)
            {
                return new SmokeOutcome
                {
                    Skill = skill.Name,
                    Category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                    ProbeMode = "skipped",
                    Status = "skipped",
                    Valid = false,
                    Error = "MayTriggerReload — executing would cause Domain Reload and break subsequent skills",
                    MetadataWarnings = FindMetadataWarnings(metadataIssues, skill.Name)
                };
            }

            if (PackageManagerHelper.InstalledPackages != null &&
                skill.RequiresPackages != null && skill.RequiresPackages.Length > 0)
            {
                var missingPackages = skill.RequiresPackages
                    .Where(packageId => !PackageManagerHelper.IsPackageInstalled(packageId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (missingPackages.Length > 0)
                {
                    return new SmokeOutcome
                    {
                        Skill = skill.Name,
                        Category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        ProbeMode = "skipped",
                        Status = "skipped",
                        Valid = false,
                        Error = $"Missing required package(s): {string.Join(", ", missingPackages)}",
                        MetadataWarnings = FindMetadataWarnings(metadataIssues, skill.Name)
                    };
                }
            }

            var probeMode = canExecuteReadOnly ? "execute" : "dryRun";
            JObject response;
            try
            {
                response = probeMode == "execute"
                    ? ExecuteSmokeProbe(skill, validation)
                    : JObject.Parse(SkillRouter.DryRun(skill.Name, "{}"));
            }
            catch (Exception ex)
            {
                return new SmokeOutcome
                {
                    Skill = skill.Name,
                    Category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                    ProbeMode = probeMode,
                    Status = "error",
                    Valid = false,
                    Error = $"Smoke test produced non-JSON response: {ex.Message}",
                    MetadataWarnings = FindMetadataWarnings(metadataIssues, skill.Name)
                };
            }

            return new SmokeOutcome
            {
                Skill = skill.Name,
                Category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                ProbeMode = probeMode,
                Status = response["status"]?.ToString() ?? "unknown",
                Valid = response["valid"]?.Value<bool?>(),
                Error = response["error"]?.ToString(),
                MissingParams = response["validation"]?["missingParams"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                SemanticWarnings = response["validation"]?["warnings"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                MetadataWarnings = FindMetadataWarnings(metadataIssues, skill.Name)
            };
        }

        [UnitySkill("test_create_editmode", "Create an EditMode test script template and return a compile-monitor job.",
            Category = SkillCategory.Test, Operation = SkillOperation.Create,
            Tags = new[] { "test", "create", "editmode", "template", "job" },
            Outputs = new[] { "path", "testName", "jobId", "serverAvailability" },
            MutatesAssets = true, MayTriggerReload = true)]
        public static object TestCreateEditMode(string testName, string folder = "Assets/Tests/Editor")
        {
            if (Validate.Required(testName, "testName") is object nameErr) return nameErr;
            if (testName.Contains("/") || testName.Contains("\\") || testName.Contains(".."))
                return new { error = "testName must not contain path separators" };
            if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
            if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, testName + ".cs");
            if (System.IO.File.Exists(path)) return new { error = $"File already exists: {path}" };
            var content = $@"using NUnit.Framework;
using UnityEngine;

[TestFixture]
public class {testName}
{{
    [Test]
    public void SampleTest()
    {{
        Assert.Pass();
    }}
}}
";
            System.IO.File.WriteAllText(path, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(path);
            var job = AsyncJobService.StartScriptMutationJob("test_create_editmode", path.Replace("\\", "/"), true, 20);
            return new
            {
                success = true,
                status = "accepted",
                path,
                testName,
                jobId = job.jobId,
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"已创建测试脚本: {path}。Unity 可能短暂重载脚本域。",
                    alwaysInclude: true)
            };
        }

        [UnitySkill("test_create_playmode", "Create a PlayMode test script template and return a compile-monitor job.",
            Category = SkillCategory.Test, Operation = SkillOperation.Create,
            Tags = new[] { "test", "create", "playmode", "template", "job" },
            Outputs = new[] { "path", "testName", "jobId", "serverAvailability" },
            MutatesAssets = true, MayTriggerReload = true)]
        public static object TestCreatePlayMode(string testName, string folder = "Assets/Tests/Runtime")
        {
            if (Validate.Required(testName, "testName") is object nameErr) return nameErr;
            if (testName.Contains("/") || testName.Contains("\\") || testName.Contains(".."))
                return new { error = "testName must not contain path separators" };
            if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
            if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, testName + ".cs");
            if (System.IO.File.Exists(path)) return new { error = $"File already exists: {path}" };
            var content = $@"using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class {testName}
{{
    [UnityTest]
    public IEnumerator SamplePlayModeTest()
    {{
        yield return null;
        Assert.Pass();
    }}
}}
";
            System.IO.File.WriteAllText(path, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(path);
            var job = AsyncJobService.StartScriptMutationJob("test_create_playmode", path.Replace("\\", "/"), true, 20);
            return new
            {
                success = true,
                status = "accepted",
                path,
                testName,
                jobId = job.jobId,
                serverAvailability = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    $"已创建测试脚本: {path}。Unity 可能短暂重载脚本域。",
                    alwaysInclude: true)
            };
        }

        [UnitySkill("test_get_summary", "Get aggregated test summary across all runs",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "summary", "aggregate", "report" },
            Outputs = new[] { "totalRuns", "completedRuns", "totalPassed", "totalFailed", "totalSkipped", "totalInconclusive", "totalOther", "allFailedTests" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TestGetSummary()
        {
            var runs = EnumerateRealTestRuns(200).ToList();
            return new
            {
                success = true,
                totalRuns = runs.Count,
                completedRuns = runs.Count(r => r.status == "completed"),
                totalPassed = runs.Sum(r => GetResultInt(r, "passedTests")),
                totalFailed = runs.Sum(r => GetResultInt(r, "failedTests")),
                totalSkipped = runs.Sum(r => GetResultInt(r, "skippedTests")),
                totalInconclusive = runs.Sum(r => GetResultInt(r, "inconclusiveTests")),
                totalOther = runs.Sum(r => GetResultInt(r, "otherTests")),
                allFailedTests = runs
                    .SelectMany(r => GetResultStringList(r, "failedTestNames"))
                    .Distinct()
                    .ToArray()
            };
        }

        private static JObject ExecuteSmokeProbe(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            using (BatchPersistence.BeginTransientScope())
            {
                if (validation.UnknownParams.Count > 0)
                {
                    return JObject.FromObject(new
                    {
                        status = "error",
                        error = $"Unknown parameters: {validation.UnknownParams.Count}"
                    });
                }

                if (validation.MissingParams.Count > 0)
                {
                    return JObject.FromObject(new
                    {
                        status = "dryRun",
                        valid = false,
                        validation = new
                        {
                            missingParams = validation.MissingParams.ToArray(),
                            semanticErrors = validation.SemanticErrors.ToArray(),
                            warnings = validation.Warnings.ToArray()
                        }
                    });
                }

                if (validation.TypeErrors.Count > 0 || validation.SemanticErrors.Count > 0)
                {
                    return JObject.FromObject(new
                    {
                        status = "dryRun",
                        valid = false,
                        validation = new
                        {
                            missingParams = validation.MissingParams.ToArray(),
                            typeErrors = validation.TypeErrors.ToArray(),
                            semanticErrors = validation.SemanticErrors.ToArray(),
                            warnings = validation.Warnings.ToArray()
                        }
                    });
                }

                try
                {
                    var result = skill.Method.Invoke(null, validation.InvokeArgs);
                    if (SkillResultHelper.TryGetError(result, out var errorText))
                    {
                        if (IsExpectedMissingSceneFixture(skill.Name, errorText))
                        {
                            return JObject.FromObject(new
                            {
                                status = "skipped",
                                error = $"Scene fixture unavailable: {errorText}"
                            });
                        }

                        return JObject.FromObject(new
                        {
                            status = "error",
                            error = errorText
                        });
                    }

                    return JObject.FromObject(new
                    {
                        status = "success"
                    });
                }
                catch (Exception ex)
                {
                    var actual = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;

                    return JObject.FromObject(new
                    {
                        status = "error",
                        error = actual.Message
                    });
                }
            }
        }

        private static bool IsExpectedMissingSceneFixture(string skillName, string error)
        {
            if (string.IsNullOrEmpty(error)) return false;

            if (string.Equals(skillName, "cinemachine_get_brain_info", StringComparison.OrdinalIgnoreCase))
            {
                return error.IndexOf("No Main Camera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("No CinemachineBrain", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (string.Equals(skillName, "cinemachine_inspect_vcam", StringComparison.OrdinalIgnoreCase))
            {
                return error.IndexOf("GameObject not found", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private static SmokeRequest BuildSmokeRequest(
            string category,
            string nameContains,
            string excludeNamesCsv,
            bool executeReadOnly,
            bool includeMutating,
            int limit)
        {
            SkillRouter.Initialize();

            var excludedNames = ParseCsv(excludeNamesCsv);
            var metadataIssues = SkillRouter.ValidateMetadata().ToArray();
            IEnumerable<SkillRouter.SkillInfo> skills = SkillRouter.GetAllSkillsSnapshot();

            if (!string.IsNullOrWhiteSpace(category) &&
                Enum.TryParse(category, true, out SkillCategory parsedCategory))
            {
                skills = skills.Where(skill => skill.Category == parsedCategory);
            }

            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                skills = skills.Where(skill =>
                    skill.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (excludedNames.Count > 0)
            {
                skills = skills.Where(skill => !excludedNames.Contains(skill.Name));
            }

            if (!includeMutating)
            {
                skills = skills.Where(skill => skill.ReadOnly);
            }

            if (limit > 0)
                skills = skills.Take(limit);

            return new SmokeRequest
            {
                SelectedSkills = skills.ToArray(),
                MetadataIssues = metadataIssues,
                ExcludedNames = excludedNames
            };
        }

        private static IEnumerable<BatchJobRecord> EnumerateRealTestRuns(int limit)
        {
            return AsyncJobService.List(limit)
                .Where(IsRealTestRun)
                .ToArray();
        }

        private static bool IsRealTestRun(BatchJobRecord job)
        {
            if (job == null || !string.Equals(job.kind, "test", StringComparison.OrdinalIgnoreCase))
                return false;

            if (job.metadata != null &&
                job.metadata.TryGetValue("synthetic", out var syntheticValue) &&
                syntheticValue is bool synthetic &&
                synthetic)
            {
                return false;
            }

            return true;
        }

        private static string[] FindMetadataWarnings(IEnumerable<string> metadataIssues, string skillName)
        {
            var issueTag = $"] {skillName}: ";
            return metadataIssues?
                .Where(issue => issue.IndexOf(issueTag, StringComparison.Ordinal) >= 0)
                .ToArray() ?? Array.Empty<string>();
        }

        private static BatchJobRecord StartTestDiscovery(string testMode)
        {
            var mode = ParseTestMode(testMode);
            var normalizedMode = NormalizeTestMode(testMode);

            var inflight = FindInflightDiscovery(normalizedMode);
            if (inflight != null)
                return inflight;

            PruneOldDiscoveries(normalizedMode);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var job = new BatchJobRecord
            {
                jobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                kind = TestDiscoveryJobKind,
                status = "running",
                progress = 5,
                currentStage = "discovering",
                startedAt = now,
                updatedAt = now,
                canCancel = false,
                resultSummary = "Unity Test Runner discovery started.",
                metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["testMode"] = normalizedMode
                },
                resultData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tests"] = new List<object>(),
                    ["count"] = 0
                }
            };
            BatchPersistence.UpsertJob(job);
            BatchPersistence.FlushIfDirty();

            try
            {
                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RetrieveTestList(mode, root =>
                {
                    try
                    {
                        var storedJob = BatchPersistence.GetJob(job.jobId);
                        if (storedJob == null)
                            return;

                        var discovered = new List<DiscoveredTestCase>();
                        CollectDiscoveredTests(root, discovered);

                        storedJob.status = "completed";
                        storedJob.progress = 100;
                        storedJob.currentStage = "completed";
                        storedJob.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        storedJob.resultSummary = $"Discovered {discovered.Count} tests via Unity Test Runner.";
                        storedJob.resultData["tests"] = discovered
                            .Select(test => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["name"] = test.Name,
                                ["fullName"] = test.FullName,
                                ["runState"] = test.RunState,
                                ["categories"] = test.Categories ?? Array.Empty<string>()
                            })
                            .Cast<object>()
                            .ToList();
                        storedJob.resultData["count"] = discovered.Count;
                        BatchPersistence.UpsertJob(storedJob);
                        BatchPersistence.FlushIfDirty();
                    }
                    catch (Exception ex)
                    {
                        MarkDiscoveryFailed(job.jobId, ex.Message);
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(api);
                    }
                });
            }
            catch (Exception ex)
            {
                MarkDiscoveryFailed(job.jobId, ex.Message);
            }

            return job;
        }

        private static BatchJobRecord FindInflightDiscovery(string normalizedMode)
        {
            return BatchPersistence.ListJobs(100)
                .FirstOrDefault(job =>
                    job != null &&
                    string.Equals(job.kind, TestDiscoveryJobKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(job.status, "running", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetMetadataString(job, "testMode"), normalizedMode, StringComparison.OrdinalIgnoreCase));
        }

        private static void PruneOldDiscoveries(string normalizedMode)
        {
            var stale = BatchPersistence.ListJobs(100)
                .Where(job =>
                    job != null &&
                    string.Equals(job.kind, TestDiscoveryJobKind, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(job.status, "running", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetMetadataString(job, "testMode"), normalizedMode, StringComparison.OrdinalIgnoreCase))
                .Select(job => job.jobId)
                .ToArray();

            if (stale.Length == 0)
                return;

            foreach (var id in stale)
                BatchPersistence.RemoveJob(id);
            BatchPersistence.FlushIfDirty();
        }

        internal static string[] ResolveExactTestNames(string testMode, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return Array.Empty<string>();

            return GetCachedDiscoveredTests(testMode)
                .Where(test =>
                    string.Equals(test.FullName, filter, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(test.Name, filter, StringComparison.OrdinalIgnoreCase))
                .Select(test => test.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static bool MatchesDiscoveredTestGroup(string testMode, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return false;

            return GetCachedDiscoveredTests(testMode).Any(test =>
                test.FullName.StartsWith(filter + ".", StringComparison.OrdinalIgnoreCase) ||
                test.FullName.IndexOf("." + filter + ".", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static string[] ResolveGroupedTestNames(string testMode, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return Array.Empty<string>();

            return GetCachedDiscoveredTests(testMode)
                .Where(test =>
                    test.FullName.StartsWith(filter + ".", StringComparison.OrdinalIgnoreCase) ||
                    test.FullName.IndexOf("." + filter + ".", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(test => test.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyList<DiscoveredTestCase> GetCachedDiscoveredTests(string testMode)
        {
            return GetDiscoveredTests(GetLatestCompletedDiscovery(testMode));
        }

        private static BatchJobRecord GetLatestCompletedDiscovery(string testMode)
        {
            var normalizedMode = NormalizeTestMode(testMode);
            return BatchPersistence.ListJobs(100)
                .Where(job =>
                    job != null &&
                    string.Equals(job.kind, TestDiscoveryJobKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(job.status, "completed", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetMetadataString(job, "testMode"), normalizedMode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(job => job.updatedAt)
                .FirstOrDefault();
        }

        private static IReadOnlyList<DiscoveredTestCase> GetDiscoveredTests(BatchJobRecord job)
        {
            if (job?.resultData == null || !job.resultData.TryGetValue("tests", out var rawTests) || rawTests == null)
                return Array.Empty<DiscoveredTestCase>();

            if (rawTests is IEnumerable<object> objects)
            {
                return objects
                    .Select(ConvertDiscoveredTestCase)
                    .Where(test => test != null && !string.IsNullOrWhiteSpace(test.FullName))
                    .OrderBy(test => test.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return Array.Empty<DiscoveredTestCase>();
        }

        private static DiscoveredTestCase ConvertDiscoveredTestCase(object raw)
        {
            if (raw == null)
                return null;

            if (raw is JObject json)
            {
                return new DiscoveredTestCase
                {
                    Name = json["name"]?.ToString(),
                    FullName = json["fullName"]?.ToString(),
                    RunState = json["runState"]?.ToString(),
                    Categories = json["categories"]?.ToObject<string[]>() ?? Array.Empty<string>()
                };
            }

            if (raw is Dictionary<string, object> dict)
            {
                return new DiscoveredTestCase
                {
                    Name = dict.TryGetValue("name", out var name) ? name?.ToString() : null,
                    FullName = dict.TryGetValue("fullName", out var fullName) ? fullName?.ToString() : null,
                    RunState = dict.TryGetValue("runState", out var runState) ? runState?.ToString() : null,
                    Categories = ConvertToStringArray(dict.TryGetValue("categories", out var categories) ? categories : null)
                };
            }

            return null;
        }

        private static void CollectDiscoveredTests(ITestAdaptor test, List<DiscoveredTestCase> tests)
        {
            if (test == null || tests == null)
                return;

            if (!test.IsSuite)
            {
                tests.Add(new DiscoveredTestCase
                {
                    Name = test.Name,
                    FullName = test.FullName,
                    RunState = test.RunState.ToString(),
                    Categories = test.Categories?.Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>()
                });
                return;
            }

            foreach (var child in test.Children ?? Enumerable.Empty<ITestAdaptor>())
                CollectDiscoveredTests(child, tests);
        }

        private static void MarkDiscoveryFailed(string jobId, string error)
        {
            var job = BatchPersistence.GetJob(jobId);
            if (job == null)
                return;

            job.status = "failed";
            job.progress = 100;
            job.currentStage = "failed";
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.error = error;
            job.resultSummary = $"Unity Test Runner discovery failed: {error}";
            BatchPersistence.UpsertJob(job);
            BatchPersistence.FlushIfDirty();
        }

        private static TestMode ParseTestMode(string testMode)
        {
            return string.Equals(testMode, "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;
        }

        private static string NormalizeTestMode(string testMode)
        {
            return ParseTestMode(testMode) == TestMode.PlayMode ? "PlayMode" : "EditMode";
        }

        private static string GetMetadataString(BatchJobRecord job, string key)
        {
            if (job?.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null)
                return null;

            return value.ToString();
        }

        private static string[] ConvertToStringArray(object value)
        {
            if (value == null)
                return Array.Empty<string>();

            if (value is JArray jsonArray)
                return jsonArray.ToObject<string[]>() ?? Array.Empty<string>();

            if (value is IEnumerable<string> strings)
                return strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();

            if (value is IEnumerable<object> objects)
            {
                return objects
                    .Select(item => item?.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
            }

            return Array.Empty<string>();
        }

        private static int GetResultInt(BatchJobRecord job, string key)
        {
            if (job?.resultData == null || !job.resultData.TryGetValue(key, out var value) || value == null)
                return 0;

            if (value is int intValue)
                return intValue;
            if (value is long longValue)
                return (int)longValue;
            return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        }

        private static IEnumerable<string> GetResultStringList(BatchJobRecord job, string key)
        {
            if (job?.resultData == null || !job.resultData.TryGetValue(key, out var value) || value == null)
                return Enumerable.Empty<string>();

            if (value is IEnumerable<string> stringList)
                return stringList;

            if (value is IEnumerable<object> objectList)
                return objectList.Select(item => item?.ToString()).Where(item => !string.IsNullOrEmpty(item));

            return Enumerable.Empty<string>();
        }

        private static object GetResultValue(BatchJobRecord job, string key)
        {
            return job?.resultData != null && job.resultData.TryGetValue(key, out var value)
                ? value
                : null;
        }

        private static HashSet<string> ParseCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class SmokeRequest
        {
            public SkillRouter.SkillInfo[] SelectedSkills;
            public string[] MetadataIssues;
            public HashSet<string> ExcludedNames;
        }

        private sealed class DiscoveredTestCase
        {
            public string Name;
            public string FullName;
            public string RunState;
            public string[] Categories;
        }
    }
}

// Producer:Betsy
