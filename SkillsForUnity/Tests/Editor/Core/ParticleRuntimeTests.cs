#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// P2 runtime control (particle_play / pause / stop / simulate / emit and batches): Unity's state is read back, the
    /// metadata says what the calls are (a transient Execute, not workflow-tracked, never Play Mode), batches validate
    /// everything and then run without a rollback while reporting what ran, the transactional batch refuses them, the
    /// first version's refusals and budgets hold, and a long simulation runs as a sliced job. Everything runs in Edit Mode
    /// (ps.Play / Simulate in Edit Mode are fine); no test enters Play Mode, installs a package or compiles a script.
    /// </summary>
    [TestFixture]
    public class ParticleRuntimeTests : ParticleTestBase
    {
        internal static readonly string[] PlaybackSkills =
        {
            "particle_play", "particle_play_batch", "particle_pause", "particle_pause_batch", "particle_stop", "particle_stop_batch",
            "particle_simulate", "particle_simulate_batch", "particle_emit", "particle_emit_batch"
        };

        private readonly List<string> _jobs = new List<string>();

        [SetUp]
        public void ResetPlaybackSeams()
        {
            ParticleSkills.ResetSimulateJobsForTests();
            ParticleSkills.BeforePlaybackHookForTests = null;
        }

        [TearDown]
        public void CleanUpPlayback()
        {
            ParticleSkills.ResetSimulateJobsForTests();
            ParticleSkills.BeforePlaybackHookForTests = null;
            foreach (var id in _jobs)
                BatchPersistence.RemoveJob(id);
            _jobs.Clear();
            BatchPersistence.FlushIfDirty();
            Selection.activeGameObject = null;
        }

        /// <summary>A stopped system with a fixed seed and the given emission, made without Undo as test content.</summary>
        private static ParticleSystem Fx(string name, Transform parent = null, float rate = 20f, int maxParticles = 1000, float lifetime = 5f)
        {
            var system = MakeSystem(name, parent);
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            system.useAutoRandomSeed = false;
            system.randomSeed = 7;
            var main = system.main;
            main.playOnAwake = false;
            main.maxParticles = maxParticles;
            main.startLifetime = lifetime;
            var emission = system.emission;
            emission.rateOverTime = rate;
            return system;
        }

        private static string Items(params JObject[] items) => new JArray(items).ToString(Formatting.None);

        private static JObject Target(ParticleSystem system, JObject extra = null)
        {
            var item = new JObject { ["entityId"] = Id(system.gameObject) };
            if (extra != null)
                foreach (var property in extra.Properties())
                    item[property.Name] = property.Value;
            return item;
        }

        private string JobOf(JObject accepted)
        {
            var id = accepted["jobId"]?.ToString();
            Assert.That(id, Is.Not.Null.And.Not.Empty, accepted.ToString(Formatting.None));
            _jobs.Add(id);
            return id;
        }

        private static string[] ProblemTexts(JObject error) =>
            (error["problems"] as JArray)?.Select(problem => problem["error"]?.ToString()).ToArray() ?? new string[0];

        // ===================== Metadata and the framework =====================

        [Test]
        public void Metadata_PlaybackSkillsAreTransientExecuteCalls()
        {
            foreach (var name in PlaybackSkills)
            {
                Assert.That(SkillRouter.TryGetSkill(name, out var skill), Is.True, name);
                Assert.That(skill.Category, Is.EqualTo(SkillCategory.Particle), name);
                Assert.That(skill.Operation, Is.EqualTo(SkillOperation.Execute), name);
                Assert.That(skill.ReadOnly, Is.False, name);
                Assert.That(skill.MutatesScene, Is.True, name + ": playback changes live scene state");
                Assert.That(skill.TracksWorkflow, Is.False, name + ": transient state is not a workflow snapshot");
                Assert.That(skill.SkipAutoPresnapshot, Is.True, name);
                Assert.That(skill.MayEnterPlayMode, Is.False, name);
                Assert.That(skill.MayTriggerReload, Is.False, name);
                Assert.That(skill.MutatesAssets, Is.False, name);
                Assert.That(skill.RiskLevel, Is.EqualTo("low"), name);
                Assert.That(skill.Mode, Is.EqualTo(SkillMode.FullAuto), name);
                Assert.That(ParticleSkills.IsPlaybackSkill(name), Is.True, name);
                Assert.That(ParticleSkills.HasPlanner(name), Is.True, name);
            }
            Assert.That(ParticleSkills.IsPlaybackSkill("particle_configure"), Is.False);
            Assert.That(ParticleSkills.IsPlaybackSkill("particle_get_info"), Is.False);

            var issues = SkillRouter.ValidateMetadata().Where(issue => PlaybackSkills.Any(name => issue.Contains(name + ":"))).ToArray();
            Assert.That(issues, Is.Empty, string.Join("\n", issues));

            var saved = SkillsSurfaceProfile.Current;
            try
            {
                SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
                SkillRouter.TryGetSkill("particle_play", out var play);
                Assert.That(SkillsSurfaceProfile.IsExcluded(play), Is.True);
            }
            finally
            {
                SkillsSurfaceProfile.Current = saved;
            }
        }

        [Test]
        public void MissingModuleSeam_AnswersMissingPackage()
        {
            var system = Fx("SeamPlay");
            ParticleSkills.SimulateMissingModuleForTests = true;
            foreach (var skill in PlaybackSkills)
            {
                var method = SkillRouter.GetAllSkillsSnapshotUnfiltered().First(info => info.Name == skill).Method;
                var defaults = method.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
                Assert.That(Json(method.Invoke(null, defaults))["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"), skill);
            }
            var dry = DryRun("particle_play", new JObject { ["entityId"] = Id(system.gameObject) });
            Assert.That(dry["validation"]["missingPackages"]?.Values<string>(), Does.Contain("com.unity.modules.particlesystem"));
            Assert.That(system.isPlaying, Is.False);
        }

        [Test]
        public void TransactionalBatch_RefusesPlaybackSteps_BeforeRunningAnything()
        {
            var system = Fx("TxPlayback");
            var body = new JObject
            {
                ["steps"] = new JArray
                {
                    new JObject { ["skill"] = "particle_configure", ["args"] = new JObject { ["entityId"] = Id(system.gameObject), ["configJson"] = JObject.Parse(Config(new JObject { ["main"] = new JObject { ["maxParticles"] = 9 } })) } },
                    new JObject { ["skill"] = "particle_play", ["args"] = new JObject { ["entityId"] = Id(system.gameObject) } }
                }
            }.ToString(Formatting.None);

            var (status, json) = DryRunPolicyTestHarness.Send("POST", "/skills/batch", "?mode=transactional", body);
            var response = JObject.Parse(json);
            Assert.That(status, Is.EqualTo(400), json);
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json);
            var violation = response["details"]["violations"][0];
            Assert.That(violation["step"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(violation["skill"]?.ToString(), Is.EqualTo("particle_play"));
            StringAssert.Contains("undo stack does not record", violation["reason"]?.ToString());
            Assert.That(system.main.maxParticles, Is.EqualTo(1000), "Nothing ran: the earlier configure step was refused with the batch.");
            Assert.That(system.isPlaying, Is.False);

            var every = new JObject
            {
                ["steps"] = new JArray(PlaybackSkills.Select(name => (object)new JObject { ["skill"] = name, ["args"] = new JObject() }).ToArray())
            }.ToString(Formatting.None);
            var all = JObject.Parse(DryRunPolicyTestHarness.Send("POST", "/skills/batch", "?mode=transactional", every).Json);
            Assert.That(((JArray)all["details"]["violations"]).Count, Is.EqualTo(PlaybackSkills.Length), all.ToString(Formatting.None));
        }

        [Test]
        public void PlainBatch_RunsPlaybackSteps_WithoutATransaction()
        {
            var system = Fx("PlainBatchFx");
            var body = new JObject
            {
                ["steps"] = new JArray
                {
                    new JObject { ["skill"] = "particle_simulate", ["args"] = new JObject { ["entityId"] = Id(system.gameObject), ["seconds"] = 0.5 } }
                }
            }.ToString(Formatting.None);
            JObject response;
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            try
            {
                response = DryRunPolicyTestHarness.Post("/skills/batch", "", body);
            }
            finally
            {
                DryRunPolicyService.OverrideForTests = null;
            }
            Assert.That(response["executed"]?.Value<int>(), Is.EqualTo(1), response.ToString(Formatting.None));
            Assert.That(system.isPaused, Is.True);
            Assert.That(system.time, Is.EqualTo(0.5f).Within(0.021f));
        }

        [Test]
        public void Capabilities_ReportThePlaybackLimits()
        {
            var limits = Json(ParticleSkills.ParticleGetCapabilities())["limits"]["playback"];
            Assert.That(limits["maxSimulateSteps"]?.Value<int>(), Is.EqualTo(ParticleSkills.MaxSimulateSteps));
            Assert.That(limits["maxSimulateSystems"]?.Value<int>(), Is.EqualTo(ParticleSkills.MaxSimulateSystems));
            Assert.That(limits["maxSimulateParticles"]?.Value<int>(), Is.EqualTo(ParticleSkills.MaxSimulateParticles));
            Assert.That(limits["maxEmitCount"]?.Value<int>(), Is.EqualTo(ParticleSkills.MaxEmitCount));
            Assert.That(limits["fixedDeltaTime"]?.Value<float>(), Is.EqualTo(Time.fixedDeltaTime));
        }

        // ===================== Play, pause, stop =====================

        [Test]
        public void PlayPauseStop_ReturnTheStateUnityReports()
        {
            var system = Fx("Pps");
            var played = Json(ParticleSkills.ParticlePlay(entityId: Id(system.gameObject)));
            AssertSuccess(played);
            Assert.That(played["action"]?.ToString(), Is.EqualTo("play"));
            Assert.That(played["playback"]["isPlaying"]?.Value<bool>(), Is.True);
            Assert.That(played["undoable"]?.Value<bool>(), Is.False);
            Assert.That(played["systems"].Count(), Is.EqualTo(1));
            Assert.That(system.isPlaying, Is.True);

            var paused = Json(ParticleSkills.ParticlePause(entityId: Id(system.gameObject)));
            AssertSuccess(paused);
            Assert.That(paused["playback"]["isPaused"]?.Value<bool>(), Is.True);
            Assert.That(system.isPaused, Is.True);

            var stopped = Json(ParticleSkills.ParticleStop(entityId: Id(system.gameObject)));
            AssertSuccess(stopped);
            Assert.That(stopped["stopBehavior"]?.ToString(), Is.EqualTo("StopEmittingAndClear"));
            Assert.That(stopped["playback"]["isStopped"]?.Value<bool>(), Is.True);
            Assert.That(stopped["playback"]["particleCount"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(system.isStopped, Is.True);

            var emitting = Json(ParticleSkills.ParticleStop(entityId: Id(system.gameObject), stopBehavior: "stopemitting"));
            AssertSuccess(emitting);
            Assert.That(emitting["stopBehavior"]?.ToString(), Is.EqualTo("StopEmitting"));

            var invalid = Json(ParticleSkills.ParticleStop(entityId: Id(system.gameObject), stopBehavior: "Halt"));
            AssertError(invalid, "SEMANTIC_INVALID");
            StringAssert.Contains("StopEmittingAndClear", invalid["error"]?.ToString());
        }

        [Test]
        public void Stop_IsWhatTheConfigurationSkillsPointTo()
        {
            var system = Fx("StopThenConfigure");
            AssertSuccess(Json(ParticleSkills.ParticleSimulate(entityId: Id(system.gameObject), seconds: 0.5f)));
            Assert.That(system.isPaused, Is.True);

            var refused = Json(ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: Config(new JObject { ["main"] = new JObject { ["maxParticles"] = 40 } })));
            AssertError(refused, "SEMANTIC_INVALID");
            StringAssert.Contains("particle_stop", refused["error"]?.ToString());
            Assert.That(refused["relatedSkills"]?.Values<string>(), Does.Contain("particle_stop"));
            Assert.That(refused["suggestedFixes"][0]["skill"]?.ToString(), Is.EqualTo("particle_stop"));

            AssertSuccess(Json(ParticleSkills.ParticleStop(entityId: Id(system.gameObject))));
            AssertSuccess(Json(ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: Config(new JObject { ["main"] = new JObject { ["maxParticles"] = 40 } }))));
            Assert.That(system.main.maxParticles, Is.EqualTo(40));
        }

        [Test]
        public void WithChildren_IsOffByDefault_AndReachesEveryChildSystem()
        {
            var root = Fx("Root");
            var child = Fx("Child", root.transform);
            var mid = new GameObject("Mid");
            mid.transform.SetParent(root.transform, false);
            var deep = Fx("Deep", mid.transform);

            var alone = Json(ParticleSkills.ParticlePlay(entityId: Id(root.gameObject)));
            AssertSuccess(alone);
            Assert.That(alone["systems"].Count(), Is.EqualTo(1));
            Assert.That(root.isPlaying, Is.True);
            Assert.That(child.isPlaying, Is.False, "withChildren defaults to false.");
            root.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var dry = DryRun("particle_play", new JObject { ["entityId"] = Id(root.gameObject), ["withChildren"] = true });
            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            var previewed = ((JArray)dry["changes"]["modify"]).Select(entry => entry["target"]?.ToString()).ToArray();
            Assert.That(previewed, Is.EquivalentTo(new[] { "Root", "Root/Child", "Root/Mid/Deep" }), dry.ToString(Formatting.None));
            Assert.That(root.isPlaying || child.isPlaying || deep.isPlaying, Is.False, "dryRun played nothing.");

            var all = Json(ParticleSkills.ParticlePlay(entityId: Id(root.gameObject), withChildren: true));
            AssertSuccess(all);
            Assert.That(all["systems"].Select(entry => entry["path"]?.ToString()).ToArray(), Is.EqualTo(new[] { "Root", "Root/Child", "Root/Mid/Deep" }));
            Assert.That(root.isPlaying && child.isPlaying && deep.isPlaying, Is.True);
        }

        [Test]
        public void WithChildren_OverTheSystemCap_IsRefused()
        {
            var root = Fx("BigRoot");
            for (int i = 0; i < ParticleSkills.MaxSimulateSystems; i++)
                Fx("Part" + i, root.transform);
            var refused = Json(ParticleSkills.ParticleSimulate(entityId: Id(root.gameObject), seconds: 0.2f, withChildren: true));
            AssertError(refused, "SEMANTIC_INVALID");
            StringAssert.Contains("at most " + ParticleSkills.MaxSimulateSystems, refused["error"]?.ToString());
            Assert.That(root.isPaused, Is.False, "Nothing was simulated.");
            AssertSuccess(Json(ParticleSkills.ParticleSimulate(entityId: Id(root.gameObject), seconds: 0.2f)));
        }

        // ===================== Refusals of the first version =====================

        [Test]
        public void StopActionOtherThanNone_IsRefused_ButPauseIsNot()
        {
            var system = Fx("StopActionFx");
            var main = system.main;
            main.stopAction = ParticleSystemStopAction.Disable;
            var id = Id(system.gameObject);

            foreach (var refused in new[]
            {
                Json(ParticleSkills.ParticlePlay(entityId: id)),
                Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 0.2f)),
                Json(ParticleSkills.ParticleEmit(entityId: id, count: 3)),
                Json(ParticleSkills.ParticleStop(entityId: id))
            })
            {
                AssertError(refused, "SEMANTIC_INVALID");
                StringAssert.Contains("stopAction Disable", refused["error"]?.ToString());
            }
            Assert.That(system.isStopped && system.particleCount == 0, Is.True, "No refused call touched the system.");
            Assert.That(system.gameObject.activeSelf, Is.True);
            AssertSuccess(Json(ParticleSkills.ParticlePause(entityId: id)));
        }

        [Test]
        public void CollisionMessages_TriggerCallbacks_AndParticleJobScripts_AreRefused()
        {
            var collided = Fx("CollisionMessagesFx");
            var collision = collided.collision;
            collision.enabled = true;
            collision.sendCollisionMessages = true;
            var refused = Json(ParticleSkills.ParticlePlay(entityId: Id(collided.gameObject)));
            AssertError(refused, "SEMANTIC_INVALID");
            StringAssert.Contains("OnParticleCollision", refused["error"]?.ToString());
            collision.sendCollisionMessages = false;
            AssertSuccess(Json(ParticleSkills.ParticlePlay(entityId: Id(collided.gameObject))));

            var triggered = Fx("TriggerFx");
            var trigger = triggered.trigger;
            trigger.enabled = true;
            trigger.enter = ParticleSystemOverlapAction.Callback;
            var simulate = Json(ParticleSkills.ParticleSimulate(entityId: Id(triggered.gameObject), seconds: 0.2f));
            AssertError(simulate, "SEMANTIC_INVALID");
            StringAssert.Contains("OnParticleTrigger", simulate["error"]?.ToString());

            var scripted = Fx("JobScriptFx");
            scripted.gameObject.AddComponent<ParticleJobCallbackFixture>();
            var emit = Json(ParticleSkills.ParticleEmit(entityId: Id(scripted.gameObject), count: 2));
            AssertError(emit, "SEMANTIC_INVALID");
            StringAssert.Contains("OnParticleUpdateJobScheduled", emit["error"]?.ToString());
            Assert.That(scripted.particleCount, Is.EqualTo(0));

            var editOnlyIdle = Fx("IdleScriptFx");
            editOnlyIdle.gameObject.AddComponent<ParticleJobCallbackPlayModeOnlyFixture>();
            AssertSuccess(Json(ParticleSkills.ParticleEmit(entityId: Id(editOnlyIdle.gameObject), count: 2)));
        }

        [Test]
        public void SubEmitterRisks_CountForTheParent()
        {
            var parent = Fx("SubParent");
            var sub = Fx("SubChild", parent.transform, rate: 0f);
            var subMain = sub.main;
            subMain.stopAction = ParticleSystemStopAction.Destroy;
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(sub, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);

            var refused = Json(ParticleSkills.ParticleSimulate(entityId: Id(parent.gameObject), seconds: 0.2f));
            AssertError(refused, "SEMANTIC_INVALID");
            StringAssert.Contains("SubParent/SubChild", refused["error"]?.ToString());
            Assert.That(sub != null && sub.gameObject.activeSelf, Is.True);
        }

        // ===================== Simulate =====================

        [Test]
        public void Simulate_ReadsBackTimeParticleCountAndPaused()
        {
            var system = Fx("SimFx");
            var simulated = Json(ParticleSkills.ParticleSimulate(entityId: Id(system.gameObject), seconds: 0.5f));
            AssertSuccess(simulated);
            Assert.That(simulated["status"]?.ToString(), Is.EqualTo("completed"));
            Assert.That(simulated["steps"]?.Value<int>(), Is.EqualTo(Mathf.RoundToInt(0.5f / Time.fixedDeltaTime)));
            Assert.That(simulated["isPaused"]?.Value<bool>(), Is.True);
            Assert.That(simulated["time"]?.Value<float>(), Is.EqualTo(system.time));
            Assert.That(simulated["time"]?.Value<float>(), Is.EqualTo(0.5f).Within(Time.fixedDeltaTime * 0.51f));
            Assert.That(simulated["particleCount"]?.Value<int>(), Is.EqualTo(system.particleCount).And.GreaterThan(0));
            Assert.That(simulated["undoable"]?.Value<bool>(), Is.False);
            Assert.That(system.isPaused, Is.True, "Unity leaves a simulated system paused.");

            var continued = Json(ParticleSkills.ParticleSimulate(entityId: Id(system.gameObject), seconds: 0.5f, restart: false));
            AssertSuccess(continued);
            Assert.That(continued["time"]?.Value<float>(), Is.EqualTo(1f).Within(Time.fixedDeltaTime * 0.51f));
            Assert.That(continued["particleCount"]?.Value<int>(), Is.GreaterThan(simulated["particleCount"].Value<int>()));

            var restarted = Json(ParticleSkills.ParticleSimulate(entityId: Id(system.gameObject), seconds: 0.5f));
            Assert.That(restarted["time"]?.Value<float>(), Is.EqualTo(simulated["time"].Value<float>()));
            Assert.That(restarted["particleCount"]?.Value<int>(), Is.EqualTo(simulated["particleCount"].Value<int>()), "A fixed seed and fixed steps repeat in the same Editor.");
        }

        [Test]
        public void Simulate_BudgetsAreParameterErrors_AndSimulateNothing()
        {
            var system = Fx("BudgetFx");
            var id = Id(system.gameObject);
            float overSteps = (ParticleSkills.MaxSimulateSteps + 10) * Time.fixedDeltaTime;
            var tooLong = Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: overSteps));
            AssertError(tooLong, "SEMANTIC_INVALID");
            StringAssert.Contains("at most " + ParticleSkills.MaxSimulateSteps + " steps", tooLong["error"]?.ToString());

            AssertError(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 0f)), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: float.NaN)), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: Time.fixedDeltaTime * 0.2f)), "SEMANTIC_INVALID");

            var main = system.main;
            main.maxParticles = ParticleSkills.MaxSimulateParticles + 1;
            var tooMany = Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 0.2f));
            AssertError(tooMany, "SEMANTIC_INVALID");
            StringAssert.Contains(ParticleSkills.MaxSimulateParticles.ToString(), tooMany["error"]?.ToString());

            Assert.That(system.isStopped && system.time == 0f, Is.True, "A refused simulation changes nothing.");

            var missing = Execute("particle_simulate", new JObject { ["entityId"] = id });
            Assert.That(missing["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), missing.ToString(Formatting.None));
        }

        [Test]
        public void Simulate_AboveTheInlineLimit_NeedsRunAsync()
        {
            var system = Fx("HeavyFx", maxParticles: ParticleSkills.MaxSimulateParticles);
            float seconds = ParticleSkills.MaxSimulateSteps * Time.fixedDeltaTime;
            Assert.That((long)ParticleSkills.MaxSimulateSteps * ParticleSkills.MaxSimulateParticles, Is.GreaterThan(ParticleSkills.MaxInlineSimulateWork));
            var refused = Json(ParticleSkills.ParticleSimulate(entityId: Id(system.gameObject), seconds: seconds));
            AssertError(refused, "SEMANTIC_INVALID");
            StringAssert.Contains("runAsync=true", refused["error"]?.ToString());
            Assert.That(system.isStopped, Is.True);

            var dry = DryRun("particle_simulate", new JObject { ["entityId"] = Id(system.gameObject), ["seconds"] = seconds, ["runAsync"] = true });
            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            Assert.That(system.isStopped, Is.True, "dryRun simulated nothing and started no job.");
            Assert.That(ParticleSkills.SimulateJobOf(system), Is.Null);
        }

        [Test]
        public void SimulateJob_AdvancesSliceBySlice_AndEndsWhereInlineEnds()
        {
            var inline = Fx("InlineTwin");
            var sliced = Fx("SlicedTwin");
            var expected = Json(ParticleSkills.ParticleSimulate(entityId: Id(inline.gameObject), seconds: 1f));
            AssertSuccess(expected);

            ParticleSkills.SimulateJobRegistry.StepsPerSliceForTests = 7;
            var accepted = Json(ParticleSkills.ParticleSimulate(entityId: Id(sliced.gameObject), seconds: 1f, runAsync: true));
            AssertSuccess(accepted);
            Assert.That(accepted["status"]?.ToString(), Is.EqualTo("accepted"));
            Assert.That(accepted["kind"]?.ToString(), Is.EqualTo(ParticleSkills.SimulateJobKind));
            Assert.That(accepted["steps"]?.Value<int>(), Is.EqualTo(expected["steps"].Value<int>()));
            var jobId = JobOf(accepted);
            Assert.That(sliced.isStopped, Is.True, "Accepting the job simulated nothing yet.");

            var job = AsyncJobService.Get(jobId);
            int ticks = 0;
            float lastTime = -1f;
            while (job.status != "completed" && ticks++ < 100)
            {
                ParticleSkills.ProcessSimulateJob(job);
                job = AsyncJobService.Get(jobId);
                if (job.status == "running")
                {
                    Assert.That(sliced.isPaused, Is.True);
                    Assert.That(sliced.time, Is.GreaterThan(lastTime));
                    lastTime = sliced.time;
                }
            }
            Assert.That(job.status, Is.EqualTo("completed"), job.error);
            Assert.That(ticks, Is.GreaterThan(2), "The job advanced over several ticks.");
            var result = JObject.FromObject(job.resultData);
            Assert.That(result["time"]?.Value<float>(), Is.EqualTo(expected["time"].Value<float>()).Within(1e-4f));
            Assert.That(result["particleCount"]?.Value<int>(), Is.EqualTo(expected["particleCount"].Value<int>()));
            Assert.That(result["isPaused"]?.Value<bool>(), Is.True);
            Assert.That(result["jobId"]?.ToString(), Is.EqualTo(jobId));
            Assert.That(ParticleSkills.SimulateJobOf(sliced), Is.Null);
        }

        [Test]
        public void SimulateJob_ReportsChangesCancellationAndInterruption()
        {
            ParticleSkills.SimulateJobRegistry.StepsPerSliceForTests = 3;
            var system = Fx("JobFx");
            var id = Id(system.gameObject);

            var first = JobOf(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 1f, runAsync: true)));
            var job = AsyncJobService.Get(first);
            ParticleSkills.ProcessSimulateJob(job);
            var busy = Json(ParticleSkills.ParticlePlay(entityId: id));
            AssertError(busy, "SEMANTIC_INVALID");
            StringAssert.Contains("being simulated by job " + first, busy["error"]?.ToString());
            system.Play();
            ParticleSkills.ProcessSimulateJob(job);
            job = AsyncJobService.Get(first);
            Assert.That(job.status, Is.EqualTo("failed"));
            StringAssert.Contains("was changed while the simulation job ran", job.error);
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var second = JobOf(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 1f, runAsync: true)));
            ParticleSkills.ProcessSimulateJob(AsyncJobService.Get(second));
            float stoppedAt = system.time;
            var cancelled = AsyncJobService.Cancel(second);
            Assert.That(cancelled.status, Is.EqualTo("cancelled"));
            StringAssert.Contains("stay paused", cancelled.resultData["note"]?.ToString());
            Assert.That(system.isPaused && system.time == stoppedAt, Is.True, "Cancelling leaves the system where the job stopped it.");
            Assert.That(ParticleSkills.SimulateJobOf(system), Is.Null);

            var third = JobOf(Json(ParticleSkills.ParticleSimulate(entityId: id, seconds: 1f, runAsync: true)));
            ParticleSkills.ResetSimulateJobsForTests();
            var lost = AsyncJobService.Get(third);
            ParticleSkills.ProcessSimulateJob(lost);
            lost = AsyncJobService.Get(third);
            Assert.That(lost.status, Is.EqualTo("failed"));
            StringAssert.Contains("interrupted", lost.error);
        }

        // ===================== Emit =====================

        [Test]
        public void Emit_ReportsWhatUnityKept()
        {
            var system = Fx("EmitFx", maxParticles: 10);
            var emitted = Json(ParticleSkills.ParticleEmit(entityId: Id(system.gameObject), count: 4));
            AssertSuccess(emitted);
            Assert.That(emitted["requested"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(emitted["emitted"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(emitted["particleCount"]?.Value<int>(), Is.EqualTo(4));
            Assert.That(system.particleCount, Is.EqualTo(4));

            var capped = Json(ParticleSkills.ParticleEmit(entityId: Id(system.gameObject), count: 50));
            AssertSuccess(capped);
            Assert.That(capped["emitted"]?.Value<int>(), Is.EqualTo(6));
            Assert.That(capped["warnings"]?.Values<string>().Any(warning => warning.Contains("maxParticles")), Is.True, capped.ToString(Formatting.None));

            AssertError(Json(ParticleSkills.ParticleEmit(entityId: Id(system.gameObject), count: 0)), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleEmit(entityId: Id(system.gameObject), count: ParticleSkills.MaxEmitCount + 1)), "SEMANTIC_INVALID");
            Assert.That(system.particleCount, Is.EqualTo(10));
        }

        // ===================== Batches =====================

        [Test]
        public void PlaybackBatch_RunsNothingWhenAnyItemIsInvalid()
        {
            var a = Fx("EmitA");
            var b = Fx("EmitB");
            var json = Json(ParticleSkills.ParticleEmitBatch(Items(Target(a, new JObject { ["count"] = 5 }), Target(b, new JObject { ["count"] = 0 }))));
            Assert.That(json["success"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            Assert.That(json["executedCount"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(json["atomic"]?.Value<bool>(), Is.False);
            Assert.That(json["results"][0]["executed"]?.Value<bool>(), Is.False);
            Assert.That(json["results"][1]["problems"][0]["path"]?.ToString(), Is.EqualTo("items[1].count"));
            StringAssert.Contains("ran nothing", json["error"]?.ToString());
            Assert.That(a.particleCount, Is.EqualTo(0), "The valid item did not run either.");

            var keys = Json(ParticleSkills.ParticlePlayBatch(Items(Target(a, new JObject { ["withChildern"] = true }))));
            Assert.That(keys["success"]?.Value<bool>(), Is.False);
            StringAssert.Contains("did you mean 'withChildren'", keys["results"][0]["error"]?.ToString());
        }

        [Test]
        public void PlaybackBatch_ARunTimeFailureIsReportedAsPartialSuccess()
        {
            var a = Fx("PartialA");
            var b = Fx("PartialB");
            ParticleSkills.BeforePlaybackHookForTests = item =>
            {
                if (item.Target.System == b)
                    throw new InvalidOperationException("injected failure");
            };
            var json = Json(ParticleSkills.ParticlePlayBatch(Items(Target(a), Target(b))));
            Assert.That(json["success"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["executedCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["rolledBack"], Is.Null, "Nothing claims a rollback.");
            Assert.That(json["undoable"]?.Value<bool>(), Is.False);
            StringAssert.Contains("not rolled back", json["error"]?.ToString());
            StringAssert.Contains("Resend only the failed items", json["suggestedFixes"][0]["reason"]?.ToString());
            Assert.That(json["results"][0]["success"]?.Value<bool>(), Is.True);
            Assert.That(a.isPlaying, Is.True, "The item that ran stays played.");
            Assert.That(b.isPlaying, Is.False);
        }

        [Test]
        public void PlaybackBatch_RefusesTwoItemsReachingOneSystem()
        {
            var root = Fx("OverlapRoot");
            var child = Fx("OverlapChild", root.transform);
            var json = Json(ParticleSkills.ParticleStopBatch(Items(Target(root, new JObject { ["withChildren"] = true }), Target(child))));
            Assert.That(json["success"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            StringAssert.Contains("already drives", json["results"][1]["error"]?.ToString());
            Assert.That(json["executedCount"]?.Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void PlaybackBatch_DryRunPreviewsEveryItem_AndIsNotAtomic()
        {
            var a = Fx("DryA");
            var b = Fx("DryB");
            var dry = DryRun("particle_pause_batch", new JObject { ["items"] = new JArray(Target(a), Target(b, new JObject { ["withChildren"] = true })) });
            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
            var preview = dry["batchPreview"];
            Assert.That(preview, Is.Not.Null, dry.ToString(Formatting.None));
            Assert.That(preview["atomic"]?.Value<bool>(), Is.False);
            Assert.That(preview["runsOnlyWhenAllValid"]?.Value<bool>(), Is.True);
            Assert.That(preview["items"][1]["systems"]?.Values<string>(), Is.EqualTo(new[] { "DryB" }));
            Assert.That(a.isPaused || b.isPaused, Is.False);

            var invalid = DryRun("particle_emit_batch", new JObject { ["items"] = new JArray(Target(a, new JObject { ["count"] = -1 })) });
            Assert.That(invalid["valid"]?.Value<bool>(), Is.False);
            Assert.That(SemanticFieldsOf(invalid), Does.Contain("items[0].count"), invalid.ToString(Formatting.None));
        }

        [Test]
        public void SimulateBatch_RunsInline_OrAsOneJob()
        {
            var a = Fx("SimBatchA");
            var b = Fx("SimBatchB");
            var inline = Json(ParticleSkills.ParticleSimulateBatch(Items(Target(a, new JObject { ["seconds"] = 0.3 }), Target(b, new JObject { ["seconds"] = 0.6, ["fixedTimeStep"] = false }))));
            Assert.That(inline["success"]?.Value<bool>(), Is.True, inline.ToString(Formatting.None));
            Assert.That(inline["status"]?.ToString(), Is.EqualTo("completed"));
            Assert.That(inline["successCount"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(a.isPaused && b.isPaused, Is.True);
            Assert.That(b.time, Is.EqualTo(0.6f).Within(1e-3f), "Variable steps simulate the exact time.");

            var missing = Json(ParticleSkills.ParticleSimulateBatch(Items(Target(a))));
            Assert.That(missing["results"][0]["problems"][0]["path"]?.ToString(), Is.EqualTo("items[0].seconds"), missing.ToString(Formatting.None));

            ParticleSkills.SimulateJobRegistry.StepsPerSliceForTests = 10;
            var accepted = Json(ParticleSkills.ParticleSimulateBatch(Items(Target(a, new JObject { ["seconds"] = 0.4 }), Target(b, new JObject { ["seconds"] = 0.4 })), runAsync: true));
            Assert.That(accepted["status"]?.ToString(), Is.EqualTo("accepted"), accepted.ToString(Formatting.None));
            Assert.That(accepted["totalItems"]?.Value<int>(), Is.EqualTo(2));
            var jobId = JobOf(accepted);
            var job = AsyncJobService.Get(jobId);
            for (int i = 0; i < 20 && job.status != "completed"; i++)
            {
                ParticleSkills.ProcessSimulateJob(job);
                job = AsyncJobService.Get(jobId);
            }
            Assert.That(job.status, Is.EqualTo("completed"), job.error);
            var envelope = JObject.FromObject(job.resultData);
            Assert.That(envelope["successCount"]?.Value<int>(), Is.EqualTo(2), envelope.ToString(Formatting.None));
            Assert.That(envelope["results"][1]["target"]?.ToString(), Is.EqualTo("SimBatchB"));
            Assert.That(envelope["results"][1]["time"]?.Value<float>(), Is.EqualTo(b.time));
        }

        [Test]
        public void Playback_LeavesNoWorkflowTaskAndNoSerializedChange()
        {
            var system = Fx("NoTraceFx");
            var before = EditorJsonUtility.ToJson(system);
            int tasks = WorkflowManager.History.tasks.Count;
            foreach (var (skill, args) in new[]
            {
                ("particle_play", new JObject()),
                ("particle_pause", new JObject()),
                ("particle_simulate", new JObject { ["seconds"] = 0.3 }),
                ("particle_emit", new JObject { ["count"] = 3 }),
                ("particle_stop", new JObject())
            })
            {
                args["entityId"] = Id(system.gameObject);
                var executed = Execute(skill, args);
                Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), skill + ": " + executed.ToString(Formatting.None));
            }
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(tasks), "Playback is not workflow-tracked.");
            Assert.That(EditorJsonUtility.ToJson(system), Is.EqualTo(before), "Playback changed no serialized property.");
        }

        private static string[] SemanticFieldsOf(JObject dryRun) =>
            (dryRun["validation"]?["semanticErrors"] as JArray)?.Select(error => error["field"]?.ToString()).ToArray() ?? new string[0];
    }

    /// <summary>A script Unity would call while a particle system updates in Edit Mode.</summary>
    [ExecuteAlways]
    public class ParticleJobCallbackFixture : MonoBehaviour
    {
        private void OnParticleUpdateJobScheduled()
        {
        }
    }

    /// <summary>The same message on a script that does not run in Edit Mode.</summary>
    public class ParticleJobCallbackPlayModeOnlyFixture : MonoBehaviour
    {
        private void OnParticleUpdateJobScheduled()
        {
        }
    }
}
#endif

// Producer:Betsy
