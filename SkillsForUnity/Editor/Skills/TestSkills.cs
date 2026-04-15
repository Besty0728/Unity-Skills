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
        [UnitySkill("test_run", "Run Unity tests asynchronously. Returns a platform jobId immediately. Poll with job_status/job_wait or test_get_result(jobId).",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "run", "async", "editmode", "playmode", "job" },
            Outputs = new[] { "jobId", "testMode", "message" },
            SupportsDryRun = false)]
        public static object TestRun(string testMode = "EditMode", string filter = null)
        {
            var job = AsyncJobService.StartTestJob(testMode, filter);
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
            Outputs = new[] { "jobId", "status", "totalTests", "passedTests", "failedTests", "failedTestNames" },
            RequiresInput = new[] { "jobId" },
            ReadOnly = true)]
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
                failedTestNames = GetResultStringList(job, "failedTestNames").ToArray(),
                elapsedSeconds = System.Math.Max(0, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.startedAt),
                resultSummary = job.resultSummary,
                error = job.error
            };
        }

        [UnitySkill("test_list", "List available tests",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "list", "discover", "enumerate" },
            Outputs = new[] { "testMode", "count", "tests" },
            ReadOnly = true)]
        public static object TestList(string testMode = "EditMode", int limit = 100)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var tests = new List<object>();

            api.RetrieveTestList(mode, testRoot => { CollectTests(testRoot, tests, limit); });
            UnityEngine.Object.DestroyImmediate(api);
            return new { testMode, count = tests.Count, tests };
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

        private static void CollectTests(ITestAdaptor test, List<object> tests, int limit)
        {
            if (tests.Count >= limit)
                return;

            if (!test.HasChildren)
            {
                tests.Add(new
                {
                    name = test.Name,
                    fullName = test.FullName,
                    runState = test.RunState.ToString()
                });
                return;
            }

            foreach (var child in test.Children)
                CollectTests(child, tests, limit);
        }

        [UnitySkill("test_run_by_name", "Run specific tests by class or method name. Returns a unified jobId.",
            Category = SkillCategory.Test, Operation = SkillOperation.Execute,
            Tags = new[] { "test", "run", "name", "specific", "job" },
            Outputs = new[] { "jobId", "testName", "testMode" },
            SupportsDryRun = false)]
        public static object TestRunByName(string testName, string testMode = "EditMode")
        {
            if (Validate.Required(testName, "testName") is object err)
                return err;

            var job = AsyncJobService.StartTestJob(testMode, testName);
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
            Outputs = new[] { "jobId", "status", "total", "passed", "failed", "failedNames" },
            ReadOnly = true)]
        public static object TestGetLastResult()
        {
            var last = AsyncJobService.List(100)
                .Where(job => job.kind == "test")
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
                failedNames = GetResultStringList(last, "failedTestNames").ToArray()
            };
        }

        [UnitySkill("test_list_categories", "List test categories",
            Category = SkillCategory.Test, Operation = SkillOperation.Query,
            Tags = new[] { "test", "categories", "list", "nunit" },
            Outputs = new[] { "count", "categories" },
            ReadOnly = true)]
        public static object TestListCategories(string testMode = "EditMode")
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var categories = new HashSet<string>();
            api.RetrieveTestList(mode, testRoot => CollectCategories(testRoot, categories));
            UnityEngine.Object.DestroyImmediate(api);
            return new { success = true, count = categories.Count, categories = categories.OrderBy(c => c).ToArray() };
        }

        [UnitySkill("test_smoke_skills", "Run a reusable smoke test across registered skills. Executes safe read-only skills and dry-runs the rest for broad regression coverage.",
            Category = SkillCategory.Test, Operation = SkillOperation.Analyze,
            Tags = new[] { "test", "smoke", "skills", "regression", "coverage" },
            Outputs = new[] { "totalSkills", "executedCount", "dryRunCount", "failureCount", "results" },
            ReadOnly = true)]
        public static object TestSmokeSkills(
            string category = null,
            string nameContains = null,
            string excludeNamesCsv = null,
            bool executeReadOnly = true,
            bool includeMutating = true,
            int limit = 0)
        {
            SkillRouter.Initialize();

            var excludedNames = ParseCsv(excludeNamesCsv);
            var metadataIssues = SkillRouter.ValidateMetadata();
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
            {
                skills = skills.Take(limit);
            }

            var selectedSkills = skills.ToArray();
            var results = new List<object>(selectedSkills.Length);
            int executedCount = 0;
            int dryRunCount = 0;
            int skippedCount = 0;
            int failureCount = 0;

            foreach (var skill in selectedSkills)
            {
                var validation = SkillRouter.ValidateParameters(skill, "{}");
                var canExecuteReadOnly = executeReadOnly &&
                                         skill.ReadOnly &&
                                         validation.MissingParams.Count == 0 &&
                                         validation.TypeErrors.Count == 0 &&
                                         !skill.MayTriggerReload;

                if (skill.MayTriggerReload)
                {
                    skippedCount++;
                    results.Add(new
                    {
                        skill = skill.Name,
                        category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        readOnly = skill.ReadOnly,
                        riskLevel = skill.RiskLevel,
                        probeMode = "skipped",
                        status = "skipped",
                        reason = "MayTriggerReload — executing would cause Domain Reload and break subsequent skills"
                    });
                    continue;
                }

                string probeMode;
                string probeJson;
                if (canExecuteReadOnly)
                {
                    probeMode = "execute";
                    probeJson = "{\"verbose\":false}";
                    executedCount++;
                }
                else
                {
                    probeMode = "dryRun";
                    probeJson = "{}";
                    dryRunCount++;
                }

                var rawResponse = probeMode == "execute"
                    ? SkillRouter.Execute(skill.Name, probeJson)
                    : SkillRouter.DryRun(skill.Name, probeJson);

                JObject response;
                try
                {
                    response = JObject.Parse(rawResponse);
                }
                catch (Exception ex)
                {
                    failureCount++;
                    results.Add(new
                    {
                        skill = skill.Name,
                        category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                        probeMode,
                        status = "error",
                        error = $"Smoke test produced non-JSON response: {ex.Message}"
                    });
                    continue;
                }

                var status = response["status"]?.ToString() ?? "unknown";
                var skillIssueTag = $"] {skill.Name}: ";
                var metadataWarnings = metadataIssues
                    .Where(issue => issue.IndexOf(skillIssueTag, StringComparison.Ordinal) >= 0)
                    .ToArray();

                if (status == "error")
                {
                    failureCount++;
                }

                if (status == "dryRun" && !(response["valid"]?.Value<bool?>() ?? false))
                {
                    skippedCount++;
                }

                results.Add(new
                {
                    skill = skill.Name,
                    category = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                    readOnly = skill.ReadOnly,
                    riskLevel = skill.RiskLevel,
                    probeMode,
                    status,
                    valid = response["valid"]?.Value<bool?>(),
                    missingParams = response["validation"]?["missingParams"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                    semanticWarnings = response["validation"]?["warnings"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                    metadataWarnings,
                    error = response["error"]?.ToString()
                });
            }

            return new
            {
                success = failureCount == 0,
                totalSkills = selectedSkills.Length,
                executedCount,
                dryRunCount,
                skippedCount,
                failureCount,
                filters = new
                {
                    category,
                    nameContains,
                    excludeNames = excludedNames.OrderBy(name => name).ToArray(),
                    executeReadOnly,
                    includeMutating,
                    limit
                },
                note = "Read-only skills with no required inputs are executed directly; all other skills are smoke-tested via dryRun with empty arguments.",
                results
            };
        }

        private static void CollectCategories(ITestAdaptor test, HashSet<string> categories)
        {
            if (test.Categories != null)
                foreach (var cat in test.Categories)
                    categories.Add(cat);
            if (test.HasChildren)
                foreach (var child in test.Children)
                    CollectCategories(child, categories);
        }

        [UnitySkill("test_create_editmode", "Create an EditMode test script template and return a compile-monitor job.",
            Category = SkillCategory.Test, Operation = SkillOperation.Create,
            Tags = new[] { "test", "create", "editmode", "template", "job" },
            Outputs = new[] { "path", "testName", "jobId" })]
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
            System.IO.File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
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
            Outputs = new[] { "path", "testName", "jobId" })]
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
            System.IO.File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
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
            Outputs = new[] { "totalRuns", "completedRuns", "totalPassed", "totalFailed", "allFailedTests" },
            ReadOnly = true)]
        public static object TestGetSummary()
        {
            var runs = AsyncJobService.List(200).Where(job => job.kind == "test").ToList();
            return new
            {
                success = true,
                totalRuns = runs.Count,
                completedRuns = runs.Count(r => r.status == "completed"),
                totalPassed = runs.Sum(r => GetResultInt(r, "passedTests")),
                totalFailed = runs.Sum(r => GetResultInt(r, "failedTests")),
                allFailedTests = runs
                    .SelectMany(r => GetResultStringList(r, "failedTestNames"))
                    .Distinct()
                    .ToArray()
            };
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
    }
}
