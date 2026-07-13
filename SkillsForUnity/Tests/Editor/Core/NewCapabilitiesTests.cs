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
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            GameObjectFinder.InvalidateCache();
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
            const string path = "Assets/Scenes/SampleScene.unity";
            Assert.That(File.Exists(Path.GetFullPath(path)), Is.True, "The validation projects must contain Assets/Scenes/SampleScene.unity.");
            Assert.That(BuildPlayerService.TryResolveScenes(new[] { path }, out var scenes, out var error), Is.True, error);
            Assert.That(scenes, Is.EqualTo(new[] { path }));
            Assert.That(BuildPlayerService.TryResolveScenes(new[] { "Assets/Missing.unity" }, out _, out _), Is.False);
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
