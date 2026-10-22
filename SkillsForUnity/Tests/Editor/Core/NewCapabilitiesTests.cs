using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class NewCapabilitiesTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            // The batch steps here call write-class skills (including gameobject_delete, which never gets through under semi). Force Bypass so the mode gate doesn't silently turn
            // a step into a MODE_FORBIDDEN no-op -- otherwise the diff assertion would only fail
            // when the persisted EditorPref mode happens to be auto/semi (e.g. on a fresh CI environment).
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            // Same reasoning for the surface profile: the GameObject steps are withdrawn under Guide /
            // NoSceneAuthoring, which would turn every diff into an empty set. The pref is global, so restore it.
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            GameObjectFinder.InvalidateCache();
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.CurrentMode = _savedMode;
        }

        [TestCase("macOS", BuildTarget.StandaloneOSX)]
        [TestCase("windows64", BuildTarget.StandaloneWindows64)]
        [TestCase("linux64", BuildTarget.StandaloneLinux64)]
        [TestCase("android", BuildTarget.Android)]
        [TestCase("webgl", BuildTarget.WebGL)]
        [TestCase("iOS", BuildTarget.iOS)]
        public void BuildPlayer_TargetAliasesResolve(string value, BuildTarget expected)
        {
            Assert.That(BuildPlayerService.TryResolveTarget(value, out var actual, out var error), Is.True, error);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void BuildPlayer_OutputPathRejectsOutsideAndProtectedProjectFolders()
        {
            Assert.That(BuildPlayerService.TryResolveOutputPath("../outside/player", BuildTarget.StandaloneOSX, false, out _, out _), Is.False);
            Assert.That(BuildPlayerService.TryResolveOutputPath("Assets/player", BuildTarget.StandaloneOSX, false, out _, out _), Is.False);
            Assert.That(BuildPlayerService.TryResolveOutputPath("Builds/TestPlayer.app", BuildTarget.StandaloneOSX, false, out var resolved, out var error), Is.True, error);
            StringAssert.Contains(Path.Combine("Builds", "TestPlayer.app"), resolved);
        }

        [Test]
        public void BuildPlayer_ExistingOutputRequiresOverwrite()
        {
            var relative = $"Builds/OverwriteValidation-{Guid.NewGuid():N}.app";
            var absolute = Path.GetFullPath(relative);
            Directory.CreateDirectory(absolute);
            try
            {
                Assert.That(BuildPlayerService.TryResolveOutputPath(relative, BuildTarget.StandaloneOSX, false, out _, out _), Is.False);
                Assert.That(BuildPlayerService.TryResolveOutputPath(relative, BuildTarget.StandaloneOSX, true, out var resolved, out var error), Is.True, error);
                Assert.That(resolved, Is.EqualTo(absolute));
            }
            finally
            {
                Directory.Delete(absolute);
            }
        }

        [Test]
        public void BuildPlayer_ExplicitScenesValidate()
        {
            // Build a disposable scene on disk here, so validation doesn't depend on the host
            // project shipping Assets/Scenes/SampleScene.unity (a fresh CI project has no such file).
            const string dir = "Assets/Temp";
            var path = $"{dir}/BuildPlayerValidation_{Guid.NewGuid():N}.unity";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder("Assets", "Temp");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True, "Failed to write the temporary validation scene.");
            try
            {
                Assert.That(File.Exists(Path.GetFullPath(path)), Is.True, "The temporary validation scene was not created on disk.");
                Assert.That(BuildPlayerService.TryResolveScenes(new[] { path }, out var scenes, out var error), Is.True, error);
                Assert.That(scenes, Is.EqualTo(new[] { path }));
                Assert.That(BuildPlayerService.TryResolveScenes(new[] { "Assets/Missing.unity" }, out _, out _), Is.False);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                if (AssetDatabase.IsValidFolder(dir) && AssetDatabase.FindAssets(string.Empty, new[] { dir }).Length == 0)
                    AssetDatabase.DeleteAsset(dir);
            }
        }

        [TestCase(4, 4, 100, 0)]
        [TestCase(5, 1, 100, 0)]
        [TestCase(5, 2, 100, 1)]
        [TestCase(5, 3, 100, 2)]
        [TestCase(8, 6, 100, 3)]
        public void RecommendationHealth_UsesSampleGateAndBoundedPenalty(int calls, int errors, long totalMs, int expectedPenalty)
        {
            var health = SkillTelemetryService.CalculateRecommendationHealth(calls, errors, totalMs);
            Assert.That(health.Penalty, Is.EqualTo(expectedPenalty));
        }

        [Test]
        public void RecommendationHealth_SlowSkillWarnsWithoutPenalty()
        {
            var health = SkillTelemetryService.CalculateRecommendationHealth(3, 0, 9000);
            Assert.That(health.Penalty, Is.Zero);
            Assert.That(health.Warnings, Has.Some.Contains("average execution time"));
        }

        [Test]
        public void Telemetry_DeleteWindow_RemovesRecordsInsideSelectedWindow()
        {
            bool prevEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                SkillTelemetryService.Record("telemetry_delete_probe", "tests", "execute", true, null, 1);
                SkillTelemetryService.FlushSync();

                var before = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all"));
                Assert.That(before["summary"]?["totalCalls"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(1));

                var result = JObject.FromObject(SkillTelemetryService.DeleteWindow("1h"));
                Assert.That(result["success"]?.Value<bool>(), Is.True);
                Assert.That(result["removed"]?.Value<int>() ?? 0, Is.GreaterThanOrEqualTo(1));
                Assert.That(result["window"]?.ToString(), Is.EqualTo("1h"));

                var after = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all"));
                Assert.That(after["summary"]?["totalCalls"]?.Value<int>() ?? -1, Is.EqualTo(0));
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = prevEnabled;
            }
        }

        [Test]
        public void Telemetry_DeleteWindow_All_WipesRetainedLogs()
        {
            bool prevEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                SkillTelemetryService.Record("telemetry_delete_all_probe", "tests", "execute", false, "SKILL_ERROR", 2);
                SkillTelemetryService.FlushSync();

                var result = JObject.FromObject(SkillTelemetryService.DeleteWindow("all"));
                Assert.That(result["success"]?.Value<bool>(), Is.True);
                Assert.That(result["remaining"]?.Value<int>() ?? -1, Is.EqualTo(0));

                var after = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all"));
                Assert.That(after["summary"]?["totalCalls"]?.Value<int>() ?? -1, Is.EqualTo(0));
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = prevEnabled;
            }
        }

        private static readonly DateTime AnalyticsNowUtc = new DateTime(2026, 10, 21, 10, 30, 0, DateTimeKind.Utc);

        private static SkillTelemetryService.TelemetryRecord TelemetryRow(string skill, string agent, bool ok, string err = null, string target = null) =>
            new SkillTelemetryService.TelemetryRecord
            {
                Ts = "2026-10-21T10:00:00.000Z",
                Skill = skill,
                Agent = agent,
                Mode = "execute",
                Ok = ok,
                ErrorCode = ok ? null : "SKILL_ERROR",
                Err = err,
                Target = target,
                Ms = 5,
            };

        private static JObject ReadLastTelemetryLine(string skill)
        {
            JObject found = null;
            foreach (var line in File.ReadAllLines(SkillTelemetryService.GetLogPath()))
            {
                if (line.Length == 0) continue;
                var entry = JObject.Parse(line);
                if (string.Equals(entry["skill"]?.ToString(), skill, StringComparison.Ordinal))
                    found = entry;
            }
            return found;
        }

        [TestCase("claude-code", "ClaudeCode")]
        [TestCase("  cursor  ", "Cursor")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void Analytics_NormalizeAgentFilter_CanonicalizesOrDropsFilter(string input, string expected)
        {
            Assert.That(SkillTelemetryService.NormalizeAgentFilter(input), Is.EqualTo(expected));
        }

        [Test]
        public void Analytics_AgentFilter_CountsOnlyMatchingRecords()
        {
            var rows = new[]
            {
                TelemetryRow("a", "ClaudeCode", ok: true),
                TelemetryRow("b", "ClaudeCode", ok: false),
                TelemetryRow("c", "Cursor", ok: true),
                TelemetryRow("d", null, ok: true),
            };

            var json = JObject.Parse(SkillTelemetryService.AggregateAnalytics(rows, "all", "claude-code", AnalyticsNowUtc));

            Assert.That(json["agent"]?.ToString(), Is.EqualTo("ClaudeCode"));
            Assert.That(json["summary"]?["totalCalls"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["summary"]?["errorCalls"]?.Value<int>(), Is.EqualTo(1));
            var byAgent = (JArray)json["byAgent"];
            Assert.That(byAgent.Count, Is.EqualTo(1));
            Assert.That(byAgent[0]["agent"]?.ToString(), Is.EqualTo("ClaudeCode"));
            Assert.That(((JArray)json["recentErrors"]).Count, Is.EqualTo(1));
        }

        [Test]
        public void Analytics_UnknownLabel_MatchesRecordsWithoutAgent()
        {
            var rows = new[]
            {
                TelemetryRow("a", null, ok: true),
                TelemetryRow("b", "Cursor", ok: true),
            };

            var json = JObject.Parse(SkillTelemetryService.AggregateAnalytics(rows, "all", "(unknown)", AnalyticsNowUtc));

            Assert.That(json["summary"]?["totalCalls"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(((JArray)json["byAgent"])[0]["agent"]?.ToString(), Is.EqualTo("(unknown)"));
        }

        [Test]
        public void Analytics_UnknownAgent_YieldsEmptyStatsWithEcho()
        {
            var rows = new[] { TelemetryRow("a", "ClaudeCode", ok: true) };

            var json = JObject.Parse(SkillTelemetryService.AggregateAnalytics(rows, "all", "Nobody", AnalyticsNowUtc));

            Assert.That(json["agent"]?.ToString(), Is.EqualTo("Nobody"));
            Assert.That(json["summary"]?["totalCalls"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(((JArray)json["topSkills"]).Count, Is.EqualTo(0));
        }

        [Test]
        public void Analytics_Unfiltered_HasNoAgentFieldAndCountsEverything()
        {
            var rows = new[]
            {
                TelemetryRow("a", "ClaudeCode", ok: true),
                TelemetryRow("b", null, ok: true),
            };

            var json = JObject.Parse(SkillTelemetryService.AggregateAnalytics(rows, "all", null, AnalyticsNowUtc));

            Assert.That(json.Property("agent"), Is.Null);
            Assert.That(json["summary"]?["totalCalls"]?.Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void Analytics_RecentErrors_CarryErrAndTargetOnlyWhenPresent()
        {
            var rows = new[]
            {
                TelemetryRow("a", "x", ok: false, err: "boom", target: "path=~/a"),
                TelemetryRow("b", "x", ok: false),
            };

            var json = JObject.Parse(SkillTelemetryService.AggregateAnalytics(rows, "all", null, AnalyticsNowUtc));

            var recent = (JArray)json["recentErrors"];
            Assert.That(recent[0]["err"], Is.Null);
            Assert.That(((JObject)recent[0]).Property("target"), Is.Null);
            Assert.That(recent[1]["err"]?.ToString(), Is.EqualTo("boom"));
            Assert.That(recent[1]["target"]?.ToString(), Is.EqualTo("path=~/a"));
        }

        [Test]
        public void Analytics_BuildAnalyticsJson_CacheKeepsFilteredAndUnfilteredApart()
        {
            bool prevEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                SkillTelemetryService.Record("telemetry_cache_probe", "tests", "execute", true, null, 1);
                SkillTelemetryService.FlushSync();

                var unfiltered = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all"));
                var filtered = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all", "claude-code"));
                var unfilteredAgain = JObject.Parse(SkillTelemetryService.BuildAnalyticsJson("all"));

                Assert.That(unfiltered.Property("agent"), Is.Null);
                Assert.That(unfiltered["summary"]?["totalCalls"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));
                Assert.That(filtered["agent"]?.ToString(), Is.EqualTo("ClaudeCode"));
                Assert.That(filtered["summary"]?["totalCalls"]?.Value<int>(), Is.EqualTo(0));
                Assert.That(unfilteredAgain.Property("agent"), Is.Null);
                Assert.That(unfilteredAgain["summary"]?["totalCalls"]?.Value<int>(),
                    Is.EqualTo(unfiltered["summary"]?["totalCalls"]?.Value<int>()));
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = prevEnabled;
            }
        }

        [Test]
        public void Telemetry_FailedLine_CarriesRedactedErrAndTarget_SuccessLineDoesNot()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) Assert.Ignore("No user profile folder on this machine.");

            bool prevEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                var err = SkillTelemetryService.SanitizeError("could not open " + home + "/Art/hero.png");
                var target = SkillTelemetryService.SanitizeTarget(new JObject { ["path"] = home + "/Art/hero.png" }.ToString());

                SkillTelemetryService.Record("telemetry_detail_probe", "tests", "execute", false, "SKILL_ERROR", 2, error: err, target: target);
                SkillTelemetryService.Record("telemetry_detail_ok_probe", "tests", "execute", true, null, 2, error: err, target: target);
                SkillTelemetryService.FlushSync();

                var failed = ReadLastTelemetryLine("telemetry_detail_probe");
                var succeeded = ReadLastTelemetryLine("telemetry_detail_ok_probe");

                Assert.That(failed["errorCode"]?.ToString(), Is.EqualTo("SKILL_ERROR"));
                Assert.That(failed["err"]?.ToString(), Does.StartWith("could not open ~/Art/hero.png"));
                Assert.That(failed["err"]?.ToString(), Does.Not.Contain(home));
                Assert.That(failed["target"]?.ToString(), Does.Not.Contain(home));
                Assert.That(succeeded["err"], Is.Null);
                Assert.That(succeeded["target"], Is.Null);
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = prevEnabled;
            }
        }

        [Test]
        public void Telemetry_DeleteWindow_KeepsErrAndTargetOnSurvivingFailedLine()
        {
            bool prevEnabled = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = true;
            SkillTelemetryService.ResetForTests();
            try
            {
                // An old failure, written straight into the log so it lies outside the 1h window.
                const string oldLine = "{\"ts\":\"2020-01-01T00:00:00.000Z\",\"skill\":\"telemetry_old_failure_probe\",\"agent\":\"tests\",\"mode\":\"execute\",\"ok\":false,\"errorCode\":\"SKILL_ERROR\",\"err\":\"old failure\",\"target\":\"x=1\",\"ms\":1}";
                File.AppendAllText(SkillTelemetryService.GetLogPath(), oldLine + "\n");
                SkillTelemetryService.Record("telemetry_recent_failure_probe", "tests", "execute", false, "SKILL_ERROR", 2, error: "recent failure", target: "y=2");
                SkillTelemetryService.FlushSync();

                var result = JObject.FromObject(SkillTelemetryService.DeleteWindow("1h"));
                Assert.That(result["removed"]?.Value<int>(), Is.EqualTo(1));

                var kept = ReadLastTelemetryLine("telemetry_old_failure_probe");
                Assert.That(kept, Is.Not.Null);
                Assert.That(kept["err"]?.ToString(), Is.EqualTo("old failure"));
                Assert.That(kept["target"]?.ToString(), Is.EqualTo("x=1"));
                Assert.That(ReadLastTelemetryLine("telemetry_recent_failure_probe"), Is.Null);
            }
            finally
            {
                SkillTelemetryService.ResetForTests();
                SkillTelemetryService.Enabled = prevEnabled;
            }
        }

        [Test]
        public void Telemetry_ReadFailureEnvelope_UsesTopLevelErrorCodeNotNestedOne()
        {
            const string json = "{\"status\":\"error\",\"errorCode\":\"SKILL_ERROR\",\"error\":\"boom\",\"details\":{\"errorCode\":\"NESTED_CODE\"}}";

            SkillTelemetryService.ReadFailureEnvelope(json, out var code, out var message);

            Assert.That(code, Is.EqualTo("SKILL_ERROR"));
            Assert.That(message, Is.EqualTo("boom"));
        }

        [Test]
        public void Telemetry_ReadFailureEnvelope_NestedOnlyErrorCodeIsIgnoredAndBadJsonYieldsNulls()
        {
            const string nestedOnly = "{\"status\":\"error\",\"error\":\"boom\",\"details\":{\"errorCode\":\"NESTED_CODE\"}}";

            SkillTelemetryService.ReadFailureEnvelope(nestedOnly, out var code, out var message);
            Assert.That(code, Is.Null);
            Assert.That(message, Is.EqualTo("boom"));

            SkillTelemetryService.ReadFailureEnvelope("not json", out var badCode, out var badMessage);
            Assert.That(badCode, Is.Null);
            Assert.That(badMessage, Is.Null);
        }

        [Test]
        public void Telemetry_RedactPaths_ReplacesProjectThenHomeAndRespectsNameBoundaries()
        {
            Assert.That(SkillTelemetryService.RedactPaths("at /Users/me/Game/Assets/a.cs", "/Users/me/Game", "/Users/me"),
                Is.EqualTo("at <project>/Assets/a.cs"));
            Assert.That(SkillTelemetryService.RedactPaths(@"C:\Users\me\Docs\x.txt", null, @"C:\Users\me"),
                Is.EqualTo(@"~\Docs\x.txt"));
            Assert.That(SkillTelemetryService.RedactPaths("/Users/meghan/x", null, "/Users/me"),
                Is.EqualTo("/Users/meghan/x"));
        }

        [Test]
        public void Telemetry_SanitizeTarget_RedactsLongHomeBeforeTruncating()
        {
            const string home = "/Users/a_very_long_user_name_for_redaction_tests";
            var args = new JObject { ["path"] = home + "/Projects/Foo/Assets/X.prefab" }.ToString();

            var target = SkillTelemetryService.SanitizeTarget(args, null, home);

            Assert.That(target, Does.Contain("~/Projects/Foo/Assets/X.prefab"));
            Assert.That(target, Does.Not.Contain("Users"));
            Assert.That(target, Does.Not.Contain("a_very"));
            Assert.That(target, Does.Not.Contain("user_name"));
            Assert.That(target, Does.Not.Contain("redaction"));
        }

        [Test]
        public void Telemetry_SanitizeTarget_ProjectValueBecomesProjectPlaceholder()
        {
            var args = new JObject { ["path"] = "/Users/dev/Game/Assets/X.prefab" }.ToString();

            var target = SkillTelemetryService.SanitizeTarget(args, "/Users/dev/Game", "/Users/dev");

            Assert.That(target, Is.EqualTo("path=<project>/Assets/X.prefab"));
        }

        [Test]
        public void Telemetry_SanitizeError_RedactsLongHomeWithInjectedRoot()
        {
            const string home = "/Users/a_very_long_user_name_for_redaction_tests";

            var error = SkillTelemetryService.SanitizeError("could not open " + home + "/Art/hero.png", null, home);

            Assert.That(error, Is.EqualTo("could not open ~/Art/hero.png"));
        }

        [Test]
        public void BatchDiff_CreateThenModify_ReportsOnlyFinalAddedObject()
        {
            var steps = JArray.Parse(@"[
                { 'skill':'gameobject_create', 'args':{ 'name':'BatchDiffCreated' } },
                { 'skill':'gameobject_set_transform', 'args':{ 'name':'BatchDiffCreated', 'posX':4 } }
            ]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);
            var diff = (JObject)response["sceneDiff"];

            Assert.That(diff["added"], Has.Count.EqualTo(1));
            Assert.That(diff["added"]?[0]?["name"]?.ToString(), Is.EqualTo("BatchDiffCreated"));
            Assert.That(diff["changed"], Has.Count.Zero);
            Assert.That(GameObject.Find("BatchDiffCreated").transform.position.x, Is.EqualTo(4f));
        }

        [Test]
        public void BatchDiff_ExistingObjectMultipleSteps_ReportsFinalNetChangeOnce()
        {
            var target = new GameObject("BatchDiffExisting");
            target.transform.position = Vector3.zero;
            GameObjectFinder.InvalidateCache();
            var steps = JArray.Parse(@"[
                { 'skill':'gameobject_set_transform', 'args':{ 'name':'BatchDiffExisting', 'posX':2 } },
                { 'skill':'gameobject_set_transform', 'args':{ 'name':'BatchDiffExisting', 'posX':7 } }
            ]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);
            var changed = (JArray)response["sceneDiff"]?["changed"];

            Assert.That(changed, Has.Count.EqualTo(1));
            Assert.That(changed[0]?["target"]?["name"]?.ToString(), Is.EqualTo("BatchDiffExisting"));
            Assert.That(target.transform.position.x, Is.EqualTo(7f));
        }

        [Test]
        public void BatchDiff_ReadOnlyBatch_ReturnsNote()
        {
            var steps = JArray.Parse("[{ 'skill':'editor_get_state', 'args':{} }]");
            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);
            StringAssert.Contains("read-only", response["sceneDiff"]?["note"]?.ToString());
        }

        [Test]
        public void BatchDiff_DeleteExistingObject_ReportsRemoved()
        {
            new GameObject("BatchDiffDeleted");
            GameObjectFinder.InvalidateCache();
            var steps = JArray.Parse("[{ 'skill':'gameobject_delete', 'args':{ 'name':'BatchDiffDeleted' } }]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);
            var diff = (JObject)response["sceneDiff"];

            Assert.That(diff["removed"], Has.Count.EqualTo(1));
            Assert.That(diff["removed"]?[0]?["name"]?.ToString(), Is.EqualTo("BatchDiffDeleted"));
            Assert.That(GameObject.Find("BatchDiffDeleted"), Is.Null);
        }

        [Test]
        public void BatchDiff_PartialFailure_ReturnsSuccessfulNetChanges()
        {
            var target = new GameObject("BatchDiffPartial");
            GameObjectFinder.InvalidateCache();
            var steps = JArray.Parse(@"[
                { 'skill':'gameobject_set_transform', 'args':{ 'name':'BatchDiffPartial', 'posX':3 } },
                { 'skill':'skill_that_does_not_exist', 'args':{} }
            ]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);

            Assert.That(response["status"]?.ToString(), Is.EqualTo("partial"));
            Assert.That(response["sceneDiff"]?["changed"], Has.Count.EqualTo(1));
            Assert.That(target.transform.position.x, Is.EqualTo(3f));
        }

        [Test]
        public void BatchDiff_TransactionalRollback_ReportsRolledBackNetState()
        {
            var target = new GameObject("BatchDiffRollback");
            GameObjectFinder.InvalidateCache();
            var steps = JArray.Parse(@"[
                { 'skill':'gameobject_set_transform', 'args':{ 'name':'BatchDiffRollback', 'posX':9 } },
                { 'skill':'skill_that_does_not_exist', 'args':{} }
            ]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, true, "tests", true);

            Assert.That(response["status"]?.ToString(), Is.EqualTo("rolled_back"));
            Assert.That(response["rolledBack"]?.ToObject<bool>(), Is.True);
            Assert.That(response["sceneDiff"]?["changed"], Has.Count.Zero);
            Assert.That(target.transform.position.x, Is.Zero);
        }

        [Test]
        public void BatchDiff_ResolvedRefTarget_IsAggregated()
        {
            var steps = JArray.Parse(@"[
                { 'skill':'gameobject_create', 'args':{ 'name':'BatchDiffRef' } },
                { 'skill':'gameobject_set_transform', 'args':{
                    'entityId':{ '$ref':'$0.entityId' }, 'posX':6
                } }
            ]");

            var response = SkillsHttpServer.ExecuteBatchCore(steps, null, false, false, false, "tests", true);

            Assert.That(response["failed"]?.ToObject<int>(), Is.Zero);
            Assert.That(response["sceneDiff"]?["added"], Has.Count.EqualTo(1));
            Assert.That(response["sceneDiff"]?["changed"], Has.Count.Zero);
            Assert.That(GameObject.Find("BatchDiffRef").transform.position.x, Is.EqualTo(6f));
        }

        [Test]
        public void PlayCapture_ErrorAggregation_DeduplicatesAndTruncates()
        {
            var job = new BatchJobRecord
            {
                jobId = Guid.NewGuid().ToString("N"),
                kind = "play_capture",
                status = "running",
                currentStage = "observing",
                metadata = new System.Collections.Generic.Dictionary<string, object> { ["maxErrors"] = 1 },
                resultData = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["errorCount"] = 0,
                    ["errors"] = new object[0],
                },
            };

            PlayCaptureService.RecordError(job, "same", "stack", LogType.Error);
            PlayCaptureService.RecordError(job, "same", "stack", LogType.Error);
            PlayCaptureService.RecordError(job, "different", "stack", LogType.Exception);

            var errors = JToken.FromObject(job.resultData["errors"]);
            Assert.That(job.resultData["errorCount"], Is.EqualTo(3));
            Assert.That(job.resultData["uniqueErrorCount"], Is.EqualTo(1));
            Assert.That(errors[0]?["count"]?.ToObject<int>(), Is.EqualTo(2));
            Assert.That(job.resultData["errorsTruncated"], Is.EqualTo(true));
            Assert.That(job.resultData["healthy"], Is.EqualTo(false));
            BatchPersistence.RemoveJob(job.jobId);
        }

        [TestCase(0)]
        [TestCase(301)]
        public void PlayCapture_DurationOutsideRange_IsRejected(int durationSeconds)
        {
            var result = JObject.FromObject(PlayCaptureService.Start(durationSeconds, false, null, 50));
            StringAssert.Contains("between 1 and 300", result["error"]?.ToString());
        }

        [Test]
        public void PlayCapture_DomainReloadRecovery_ResumesPersistedStage()
        {
            var job = new BatchJobRecord
            {
                currentStage = "domain_reload_recovery",
                metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["playCaptureStage"] = "observing",
                },
            };

            Assert.That(PlayCaptureService.ResolvePersistedStage(job), Is.EqualTo("observing"));
        }

        [Test]
        public void PlayCapture_DomainReloadRecovery_AcceptsRuntimeErrors()
        {
            var job = new BatchJobRecord
            {
                jobId = Guid.NewGuid().ToString("N"),
                kind = "play_capture",
                status = "reconnecting",
                currentStage = "domain_reload_recovery",
                metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["playCaptureStage"] = "observing",
                    ["maxErrors"] = 10,
                },
                resultData = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["errorCount"] = 0,
                    ["errors"] = new object[0],
                },
            };

            Assert.That(PlayCaptureService.ResolvePersistedStage(job), Is.EqualTo("observing"));
            PlayCaptureService.RecordError(job, "runtime error", "stack", LogType.Error);

            Assert.That(job.resultData["errorCount"], Is.EqualTo(1));
            Assert.That(job.resultData["healthy"], Is.EqualTo(false));
            BatchPersistence.RemoveJob(job.jobId);
        }

        [Test]
        public void NewSkills_HaveExpectedRiskAndAsyncMetadata()
        {
            Assert.That(SkillRouter.TryGetSkill("build_player", out var build), Is.True);
            Assert.That(build.RiskLevel, Is.EqualTo("high"));
            Assert.That(build.SupportsDryRun, Is.False);
            Assert.That(SkillRouter.TryGetSkill("editor_play_capture", out var capture), Is.True);
            Assert.That(capture.MayEnterPlayMode, Is.True);
            Assert.That(capture.SupportsDryRun, Is.False);
        }
    }
}

// Producer:Betsy
