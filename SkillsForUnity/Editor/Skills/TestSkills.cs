using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Test runner skills.
    /// </summary>
    public static class TestSkills
    {
        private static readonly Dictionary<string, TestRunInfo> _runningTests = new Dictionary<string, TestRunInfo>();
        private static TestRunnerApi _api;

        private class TestRunInfo
        {
            public string JobId;
            public string Status = "running";
            public int TotalTests;
            public int PassedTests;
            public int FailedTests;
            public List<string> FailedTestNames = new List<string>();
            public System.DateTime StartTime;
        }

        [UnitySkill("test_run", "Run Unity tests asynchronously. Returns a jobId immediately — poll with test_get_result(jobId) to check status.")]
        public static object TestRun(string testMode = "EditMode", string filter = null)
        {
            if (_api == null)
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();

            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var jobId = System.Guid.NewGuid().ToString("N").Substring(0, 8);

            var runInfo = new TestRunInfo
            {
                JobId = jobId,
                StartTime = System.DateTime.Now
            };
            _runningTests[jobId] = runInfo;

            var callbacks = new TestCallbacks(runInfo);
            _api.RegisterCallbacks(callbacks);

            var filterObj = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(filter))
                filterObj.testNames = new[] { filter };

            _api.Execute(new ExecutionSettings(filterObj));

            return new
            {
                success = true,
                jobId,
                testMode,
                message = "Tests started. Use test_get_result to poll for results."
            };
        }

        [UnitySkill("test_get_result", "Get the result of a test run. Requires the jobId returned by test_run or test_run_by_name.")]
        public static object TestGetResult(string jobId)
        {
            if (!_runningTests.TryGetValue(jobId, out var runInfo))
                return new { error = $"Test job not found: {jobId}" };

            return new
            {
                jobId,
                status = runInfo.Status,
                totalTests = runInfo.TotalTests,
                passedTests = runInfo.PassedTests,
                failedTests = runInfo.FailedTests,
                failedTestNames = runInfo.FailedTestNames.ToArray(),
                elapsedSeconds = (System.DateTime.Now - runInfo.StartTime).TotalSeconds
            };
        }

        [UnitySkill("test_list", "List available tests")]
        public static object TestList(string testMode = "EditMode", int limit = 100)
        {
            if (_api == null)
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();

            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var tests = new List<object>();

            _api.RetrieveTestList(mode, (testRoot) =>
            {
                CollectTests(testRoot, tests, limit);
            });

            return new { testMode, count = tests.Count, tests };
        }

        [UnitySkill("test_cancel", "Cancel a running test")]
        public static object TestCancel(string jobId = null)
        {
            if (_api == null)
                return new { error = "No test runner available" };

            // Note: Unity's TestRunnerApi doesn't have a direct cancel method
            // This is a placeholder that clears the job status
            if (!string.IsNullOrEmpty(jobId) && _runningTests.ContainsKey(jobId))
            {
                _runningTests[jobId].Status = "cancelled";
                return new { success = true, cancelled = jobId };
            }

            return new { error = "Cannot cancel tests directly. Wait for completion." };
        }

        private static void CollectTests(ITestAdaptor test, List<object> tests, int limit)
        {
            if (tests.Count >= limit) return;

            if (!test.HasChildren)
            {
                tests.Add(new
                {
                    name = test.Name,
                    fullName = test.FullName,
                    runState = test.RunState.ToString()
                });
            }
            else
            {
                foreach (var child in test.Children)
                {
                    CollectTests(child, tests, limit);
                }
            }
        }

        private class TestCallbacks : ICallbacks
        {
            private readonly TestRunInfo _runInfo;

            public TestCallbacks(TestRunInfo runInfo)
            {
                _runInfo = runInfo;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _runInfo.TotalTests = CountTests(testsToRun);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                _runInfo.Status = "completed";
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (!result.Test.HasChildren)
                {
                    if (result.TestStatus == TestStatus.Passed)
                        _runInfo.PassedTests++;
                    else if (result.TestStatus == TestStatus.Failed)
                    {
                        _runInfo.FailedTests++;
                        _runInfo.FailedTestNames.Add(result.Test.FullName);
                    }
                }
            }

            private int CountTests(ITestAdaptor test)
            {
                if (!test.HasChildren) return 1;
                return test.Children.Sum(c => CountTests(c));
            }
        }

        [UnitySkill("test_run_by_name", "Run specific tests by class or method name")]
        public static object TestRunByName(string testName, string testMode = "EditMode")
        {
            if (_api == null) _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var jobId = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            var runInfo = new TestRunInfo { JobId = jobId, StartTime = System.DateTime.Now };
            _runningTests[jobId] = runInfo;
            _api.RegisterCallbacks(new TestCallbacks(runInfo));
            _api.Execute(new ExecutionSettings(new Filter { testMode = mode, testNames = new[] { testName } }));
            return new { success = true, jobId, testName, testMode };
        }

        [UnitySkill("test_get_last_result", "Get the most recent test run result")]
        public static object TestGetLastResult()
        {
            if (_runningTests.Count == 0) return new { error = "No test runs found" };
            var last = _runningTests.Values.OrderByDescending(r => r.StartTime).First();
            return new { jobId = last.JobId, status = last.Status, total = last.TotalTests, passed = last.PassedTests, failed = last.FailedTests, failedNames = last.FailedTestNames.ToArray() };
        }

        [UnitySkill("test_list_categories", "List test categories")]
        public static object TestListCategories(string testMode = "EditMode")
        {
            if (_api == null) _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var mode = testMode.ToLower() == "playmode" ? TestMode.PlayMode : TestMode.EditMode;
            var categories = new HashSet<string>();
            _api.RetrieveTestList(mode, (testRoot) => CollectCategories(testRoot, categories));
            return new { success = true, count = categories.Count, categories = categories.OrderBy(c => c).ToArray() };
        }

        private static void CollectCategories(ITestAdaptor test, HashSet<string> categories)
        {
            if (test.Categories != null)
                foreach (var cat in test.Categories) categories.Add(cat);
            if (test.HasChildren)
                foreach (var child in test.Children) CollectCategories(child, categories);
        }

        [UnitySkill("test_create_editmode", "Create an EditMode test script template")]
        public static object TestCreateEditMode(string testName, string folder = "Assets/Tests/Editor")
        {
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
            System.IO.File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
            return new { success = true, path, testName };
        }

        [UnitySkill("test_create_playmode", "Create a PlayMode test script template")]
        public static object TestCreatePlayMode(string testName, string folder = "Assets/Tests/Runtime")
        {
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
            System.IO.File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
            return new { success = true, path, testName };
        }

        [UnitySkill("test_get_summary", "Get aggregated test summary across all runs")]
        public static object TestGetSummary()
        {
            var runs = _runningTests.Values.ToList();
            return new
            {
                success = true, totalRuns = runs.Count,
                completedRuns = runs.Count(r => r.Status == "completed"),
                totalPassed = runs.Sum(r => r.PassedTests),
                totalFailed = runs.Sum(r => r.FailedTests),
                allFailedTests = runs.SelectMany(r => r.FailedTestNames).Distinct().ToArray()
            };
        }
    }
}
