using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class RuntimeScenarioTests
    {
        private readonly List<string> _jobs = new List<string>();
        private bool _playing, _background;
        private int _frame, _dispatches;
        private JObject _logs;

        [SetUp]
        public void SetUp()
        {
            _playing = false; _background = false; _frame = 0; _dispatches = 0;
            _logs = new JObject { ["complete"] = true, ["Logs"] = new JArray() };
            RuntimeScenarioService.PlayingForTests = () => _playing;
            RuntimeScenarioService.SetPlayingForTests = value => _playing = value;
            RuntimeScenarioService.BackgroundForTests = () => _background;
            RuntimeScenarioService.SetBackgroundForTests = value => _background = value;
            RuntimeScenarioService.FrameForTests = () => _frame;
            RuntimeScenarioService.LogWindowForTests = () => _logs;
            RuntimeScenarioService.LogSeqForTests = () => 10;
            RuntimeScenarioService.ExecuteForTests = (skill, args) => { _dispatches++; return "{\"status\":\"success\",\"result\":{\"value\":12}}"; };
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var id in _jobs) BatchPersistence.RemoveJob(id);
            _jobs.Clear();
            RuntimeScenarioService.PlayingForTests = null;
            RuntimeScenarioService.SetPlayingForTests = null;
            RuntimeScenarioService.BackgroundForTests = null;
            RuntimeScenarioService.SetBackgroundForTests = null;
            RuntimeScenarioService.FrameForTests = null;
            RuntimeScenarioService.LogWindowForTests = null;
            RuntimeScenarioService.LogSeqForTests = null;
            RuntimeScenarioService.ExecuteForTests = null;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = null;
            BatchPersistence.FlushIfDirty();
        }

        private BatchJobRecord Start(string steps)
        {
            var response = JObject.FromObject(RuntimeScenarioService.Start(steps, "edit", 30));
            Assert.That(response["error"], Is.Null, response.ToString());
            string id = response.Value<string>("jobId"); _jobs.Add(id);
            return AsyncJobService.Get(id);
        }
        private const string Assertion = "{\"kind\":\"assert\",\"skill\":\"editor_get_state\",\"select\":\"value\",\"expected\":12}";

        [Test]
        public void PreviewHasNoDispatchJobsOrEditorStateChanges()
        {
            int count = BatchPersistence.ListJobs(100).Length;
            var result = JObject.FromObject(RuntimeScenarioService.Preview("[" + Assertion + "]", "play", 30));
            Assert.That(result.Value<bool>("valid"), Is.True);
            Assert.That(_dispatches, Is.Zero);
            Assert.That(_playing, Is.False);
            Assert.That(_background, Is.False);
            Assert.That(BatchPersistence.ListJobs(100).Length, Is.EqualTo(count));
            Assert.That(((JObject)RuntimeScenarioService.Preview("[" + Assertion + "]", "invalid", 30))["error"], Is.Not.Null);
        }

        [Test]
        public void RealRouterReadEnvelopeIsUnwrappedByScenario()
        {
            RuntimeScenarioService.ExecuteForTests = null;
            var steps = new JArray(new JObject { ["kind"] = "assert", ["skill"] = "editor_get_state",
                ["select"] = "isPlaying", ["expected"] = UnityEditor.EditorApplication.isPlaying });
            var job = Start(steps.ToString());
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"), job.error);
            Assert.That(job.resultData["passed"], Is.True);
        }

        [Test]
        public void RealRouterDryRunDoesNotDispatchOrCreateJobs()
        {
            int before = BatchPersistence.ListJobs(100).Length;
            var args = new JObject { ["steps"] = "[" + Assertion + "]", ["executionMode"] = "edit" };
            var preview = JObject.Parse(SkillRouter.DryRun("editor_run_scenario", args.ToString()));
            Assert.That(preview.Value<string>("status"), Is.EqualTo("dryRun"), preview.ToString());
            Assert.That(preview.Value<bool>("valid"), Is.True, preview.ToString());
            Assert.That(_dispatches, Is.Zero);
            Assert.That(BatchPersistence.ListJobs(100).Length, Is.EqualTo(before));
            Assert.That(_playing, Is.False);
        }

        [Test]
        public void InvocationOnlyCannotClaimValidationPassed()
        {
            Assert.Throws<ArgumentException>(() => RuntimeScenarioService.ValidateSteps(JArray.Parse("[{\"kind\":\"wait_seconds\",\"seconds\":0}]")));
        }

        [Test]
        public void MutableReadAndLifecycleInvocationAreRejectedWithoutExecution()
        {
            Assert.Throws<ArgumentException>(() => RuntimeScenarioService.ValidateSteps(JArray.Parse("[{\"kind\":\"assert\",\"skill\":\"editor_play\",\"expected\":true}]")));
            Assert.Throws<ArgumentException>(() => RuntimeScenarioService.ValidateSteps(JArray.Parse("[{\"kind\":\"invoke\",\"skill\":\"editor_play\"}," + Assertion + "]")));
            Assert.That(_dispatches, Is.Zero);
            Assert.That(_playing, Is.False);
        }

        [Test]
        public void AssertionRecordsActualExpectedAndRestoresBackground()
        {
            var job = Start("[" + Assertion + "]");
            Assert.That(_background, Is.True);
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(job.resultData["passed"], Is.True);
            Assert.That(_background, Is.False);
            var step = ((JArray)job.resultData["steps"])[0];
            Assert.That(step.Value<int>("actual"), Is.EqualTo(12));
            Assert.That(step.Value<int>("expected"), Is.EqualTo(12));
        }

        [Test]
        public void RouterRefusalStopsScenarioWithoutRetrying()
        {
            RuntimeScenarioService.ExecuteForTests = (skill, args) => { _dispatches++; return "{\"status\":\"error\",\"errorCode\":\"MODE_FORBIDDEN\",\"error\":\"denied\"}"; };
            var job = Start("[" + Assertion + "]");
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(_dispatches, Is.EqualTo(1));
            Assert.That(job.resultData["passed"], Is.False);
        }

        [TestCase("interrupted")]
        [TestCase("timeout")]
        public void ChildUncertaintyPropagatesAndPreventsReplay(string stage)
        {
            var child = AsyncJobService.CreateJob("component_invoke", stage, "fixture", true);
            _jobs.Add(child.jobId);
            child.status = "failed";
            child.currentStage = stage;
            RuntimeScenarioService.ExecuteForTests = (skill, args) => "{\"status\":\"success\",\"result\":{\"jobId\":\"" + child.jobId + "\"}}";
            var job = Start("[{\"kind\":\"invoke\",\"skill\":\"component_invoke_method\"}," + Assertion + "]");
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.currentStage, Is.EqualTo(stage));
            Assert.That(job.resultData["executionUncertain"], Is.True);
        }

        [Test]
        public void AsyncDispatchRestoresCapturedAuditIdentity()
        {
            BatchJobRecord job;
            using (SkillsAuditLog.BeginRequestContext(12345, "fixture-agent", true))
                job = Start("[" + Assertion + "]");
            string seen = null;
            RuntimeScenarioService.ExecuteForTests = (skill, args) =>
            {
                seen = SkillsAuditLog.CaptureRequestContext().FallbackAgentId;
                return "{\"status\":\"success\",\"result\":{\"value\":12}}";
            };
            RuntimeScenarioService.Process(job);
            Assert.That(seen, Is.EqualTo("fixture-agent"));
        }

        [Test]
        public void StartedStepAfterReloadIsInterruptedWithoutDispatch()
        {
            var job = Start("[" + Assertion + "]");
            job.metadata["stepExecuting"] = true;
            RuntimeScenarioService.Process(job);
            Assert.That(job.currentStage, Is.EqualTo("interrupted"));
            Assert.That(_dispatches, Is.Zero);
        }

        [Test]
        public void FramesWaitForActualFrameCounter()
        {
            var job = Start("[{\"kind\":\"wait_frames\",\"frames\":2}," + Assertion + "]");
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(Convert.ToInt32(job.metadata["stepIndex"]), Is.Zero);
            _frame = 2;
            RuntimeScenarioService.Process(job);
            Assert.That(Convert.ToInt32(job.metadata["stepIndex"]), Is.EqualTo(1));
        }

        [Test]
        public void ConditionPollingKeepsActualForTimeoutEvidence()
        {
            var job = Start("[{\"kind\":\"wait_condition\",\"skill\":\"editor_get_state\",\"select\":\"value\",\"expected\":99}]");
            RuntimeScenarioService.Process(job);
            Assert.That(_dispatches, Is.EqualTo(1));
            Assert.That(((JToken)job.resultData["lastConditionActual"]).Value<int>(), Is.EqualTo(12));
            job.metadata["deadlineTicks"] = DateTime.UtcNow.AddSeconds(-1).Ticks;
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(_dispatches, Is.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void ErrorsOrCoverageGapsPreventVerifiedPass(bool complete, bool error)
        {
            _logs["complete"] = complete;
            if (error) ((JArray)_logs["Logs"]).Add(new JObject { ["type"] = "Exception", ["message"] = "fixture" });
            var job = Start("[" + Assertion + "]");
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.resultData["passed"], Is.False);
        }

        [Test]
        public void TruncatedCompleteWindowWithoutErrorsStillPasses()
        {
            _logs["hasMore"] = true;
            var job = Start("[" + Assertion + "]");
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(job.resultData["passed"], Is.True);
        }

        [Test]
        public void TruncatedWindowWithExceptionStillFails()
        {
            _logs["hasMore"] = true;
            ((JArray)_logs["Logs"]).Add(new JObject { ["type"] = "Exception", ["message"] = "fixture" });
            var job = Start("[" + Assertion + "]");
            RuntimeScenarioService.Process(job);
            RuntimeScenarioService.Process(job);
            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.resultData["passed"], Is.False);
        }

        [Test]
        public void CancellationRestoresOwnedPlayAndBackgroundThroughSeams()
        {
            var job = Start("[" + Assertion + "]");
            job.metadata["startedPlayMode"] = true;
            _playing = true;
            RuntimeScenarioService.NotifyCancelled(job);
            Assert.That(_playing, Is.False);
            Assert.That(_background, Is.False);
            Assert.That(job.resultData["notReplayed"], Is.True);
        }

        [Test]
        public void CancellationPreservesPreexistingPlayMode()
        {
            var job = Start("[" + Assertion + "]");
            _playing = true;
            RuntimeScenarioService.NotifyCancelled(job);
            Assert.That(_playing, Is.True);
        }

        [Test]
        public void NumericToleranceAppliesRecursivelyAndMissingIsNotNull()
        {
            Assert.That(RuntimeScenarioService.Matches(JToken.Parse("{\"x\":[1.01,2]}"), JToken.Parse("{\"x\":[1,2]}"), .02), Is.True);
            Assert.That(RuntimeScenarioService.Matches(JToken.Parse("{\"x\":[1.1,2]}"), JToken.Parse("{\"x\":[1,2]}"), .02), Is.False);
            Assert.That(RuntimeScenarioService.Matches(null, JValue.CreateNull(), 0), Is.False);
        }

        [Test]
        public void RuntimeOperationsAreExcludedFromTransactionalBatchesByPureSeam()
        {
            foreach (var name in new[] { "component_invoke_method", "component_invoke_method_batch", "editor_run_scenario" })
                Assert.That(SkillsHttpServer.IsRuntimeInvocationSkill(name), Is.True);
            Assert.That(SkillsHttpServer.IsRuntimeInvocationSkill("component_get_methods"), Is.False);
        }
    }
}

// Producer:Betsy
