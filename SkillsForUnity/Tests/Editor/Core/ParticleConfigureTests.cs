#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// particle_configure's patch contract: omitted fields stay, false/0 apply, disabled modules stay disabled, axis and
    /// same-mode rules, the stopped-system and Edit Mode preconditions, and atomicity both at validation and while applying.
    /// </summary>
    [TestFixture]
    public class ParticleConfigureTests : ParticleTestBase
    {
        private ParticleSystem _system;

        [SetUp]
        public void CreateSystem() => _system = MakeSystem("ConfigureFx");

        [TearDown]
        public void StopSystem() => StopAndClear(_system);

        private JObject Configure(JObject modules, JObject system = null) =>
            Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject), configJson: Config(modules, system)));

        private JObject Snapshot() => Json(ParticleSkills.ParticleGetInfo(entityId: Id(_system.gameObject), includeModules: true));

        [Test]
        public void OmittedFields_KeepTheirValues()
        {
            var before = (JObject)Snapshot()["main"];
            var json = Configure(new JObject { ["main"] = new JObject { ["duration"] = 7.5 } });
            AssertSuccess(json);
            Assert.That(json["applied"]?.Values<string>(), Is.EqualTo(new[] { "modules.main.duration" }));

            var after = (JObject)Snapshot()["main"];
            Assert.That(after["duration"]?.Value<float>(), Is.EqualTo(7.5f));
            foreach (var property in before.Properties().Where(property => property.Name != "duration"))
                Assert.That(JToken.DeepEquals(property.Value, after[property.Name]), Is.True, $"main.{property.Name} changed although it was not in the patch.");
        }

        [Test]
        public void ExplicitFalseAndZero_AreApplied()
        {
            var json = Configure(new JObject
            {
                ["main"] = new JObject { ["loop"] = false, ["maxParticles"] = 0, ["startSpeed"] = 0, ["gravityModifier"] = 0 },
                ["emission"] = new JObject { ["enabled"] = false, ["rateOverTime"] = 0 },
                ["shape"] = new JObject { ["enabled"] = false, ["angle"] = 0 }
            });
            AssertSuccess(json);
            Assert.That(_system.main.loop, Is.False);
            Assert.That(_system.main.maxParticles, Is.EqualTo(0));
            Assert.That(_system.main.startSpeed.constant, Is.EqualTo(0f));
            Assert.That(_system.emission.enabled, Is.False);
            Assert.That(_system.emission.rateOverTime.constant, Is.EqualTo(0f));
            Assert.That(_system.shape.enabled, Is.False);
            Assert.That(_system.shape.angle, Is.EqualTo(0f));
        }

        [Test]
        public void DisabledModule_StoresValues_StaysDisabled_AndSaysSo()
        {
            Assert.That(_system.noise.enabled, Is.False, "A new system starts with Noise disabled.");
            var json = Configure(new JObject { ["noise"] = new JObject { ["strength"] = 2.5, ["frequency"] = 0.8 } });
            AssertSuccess(json);
            Assert.That(_system.noise.enabled, Is.False, "A write never enables a module implicitly.");
            Assert.That(_system.noise.strength.constant, Is.EqualTo(2.5f));
            Assert.That(json["state"]["modules"]["noise"]["enabled"]?.Value<bool>(), Is.False);
            Assert.That(json["warnings"]?.Values<string>().Any(warning => warning.Contains("modules.noise is disabled")), Is.True, json.ToString(Formatting.None));
        }

        [Test]
        public void State_IsReadBackFromUnity_AndAdjustmentsReportClamps()
        {
            var json = Configure(new JObject { ["main"] = new JObject { ["startLifetime"] = 0.00001 } });
            AssertSuccess(json);
            var stored = json["state"]["modules"]["main"]["startLifetime"]["constant"]?.Value<float>();
            Assert.That(stored, Is.EqualTo(_system.main.startLifetime.constant));
            Assert.That(stored, Is.GreaterThan(0.00001f), "Unity clamps a start lifetime this small; the state shows what it kept.");
            var adjustment = json["adjustments"]?[0];
            Assert.That(adjustment?["path"]?.ToString(), Is.EqualTo("modules.main.startLifetime"), json.ToString(Formatting.None));
            Assert.That(adjustment?["valueRequested"]?["constant"]?.Value<float>(), Is.EqualTo(0.00001f).Within(1e-9f));
        }

        [Test]
        public void SystemSection_SeedRules()
        {
            var refused = Configure(null, new JObject { ["randomSeed"] = 42 });
            AssertError(refused, "SEMANTIC_INVALID");
            Assert.That(_system.useAutoRandomSeed, Is.True, "randomSeed alone would switch useAutoRandomSeed off as a side effect, so it is refused.");

            var json = Configure(null, new JObject { ["useAutoRandomSeed"] = false, ["randomSeed"] = 42 });
            AssertSuccess(json);
            Assert.That(_system.useAutoRandomSeed, Is.False);
            Assert.That(_system.randomSeed, Is.EqualTo(42u));
            Assert.That(json["state"]["system"]["randomSeed"]?.Value<long>(), Is.EqualTo(42));
        }

        [Test]
        public void AxisFlags_DecideWhichFieldsApply()
        {
            AssertError(Configure(new JObject { ["main"] = new JObject { ["startSizeY"] = 2 } }), "SEMANTIC_INVALID");
            Assert.That(_system.main.startSize3D, Is.False);

            AssertSuccess(Configure(new JObject { ["main"] = new JObject { ["startSize3D"] = true, ["startSizeX"] = 1, ["startSizeY"] = 2, ["startSizeZ"] = 3 } }));
            Assert.That(_system.main.startSizeY.constant, Is.EqualTo(2f));

            var uniform = Configure(new JObject { ["main"] = new JObject { ["startSize"] = 5 } });
            AssertError(uniform, "SEMANTIC_INVALID");
            Assert.That(uniform["problems"][0]["path"]?.ToString(), Is.EqualTo("configJson.modules.main.startSize"));

            var readback = Field(_system, "main", "startSizeY");
            Assert.That(readback, Is.Not.Null, "With startSize3D on, readback shows the per-axis fields.");
            Assert.That(Field(_system, "main", "startSize"), Is.Null, "...and hides the uniform one (same storage as startSizeX).");
        }

        [Test]
        public void SameModeTriplets_MustShareOneMode()
        {
            var curve = JObject.Parse("{\"mode\":\"Curve\",\"multiplier\":1,\"curve\":{\"keys\":[{\"time\":0,\"value\":0},{\"time\":1,\"value\":1}]}}");
            var mixed = Configure(new JObject { ["velocityOverLifetime"] = new JObject { ["x"] = curve } });
            AssertError(mixed, "SEMANTIC_INVALID");
            StringAssert.Contains("must share one curve mode", mixed["error"]?.ToString());

            AssertSuccess(Configure(new JObject { ["velocityOverLifetime"] = new JObject { ["x"] = curve, ["y"] = curve.DeepClone(), ["z"] = curve.DeepClone() } }));
            Assert.That(_system.velocityOverLifetime.y.mode, Is.EqualTo(ParticleSystemCurveMode.Curve));
        }

        [Test]
        public void EmptyPatch_SchemaVersion_UnknownAndReadOnlyFields_AreRejected()
        {
            var target = Id(_system.gameObject);
            AssertError(Json(ParticleSkills.ParticleConfigure(entityId: target, configJson: "{\"schemaVersion\":1}")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleConfigure(entityId: target, configJson: "{\"modules\":{\"main\":{\"loop\":false}}}")), "MISSING_PARAM");
            AssertError(Json(ParticleSkills.ParticleConfigure(entityId: target, configJson: "{\"schemaVersion\":2,\"modules\":{\"main\":{\"loop\":false}}}")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleConfigure(entityId: target, configJson: null)), "MISSING_PARAM");

            var unknownModule = Configure(new JObject { ["mian"] = new JObject { ["loop"] = false } });
            StringAssert.Contains("did you mean 'main'", unknownModule["error"]?.ToString());
            var planned = Configure(new JObject { ["collision"] = new JObject { ["enabled"] = true } });
            StringAssert.Contains("not configurable", planned["error"]?.ToString());
            var readOnly = Configure(new JObject { ["emission"] = new JObject { ["burstCount"] = 2 } });
            StringAssert.Contains("read-only", readOnly["error"]?.ToString());
            Assert.That(_system.main.loop, Is.True, "None of the rejected calls changed anything.");
        }

        [Test]
        public void PlayingOrPausedSystem_IsRefusedAndUntouched()
        {
            _system.Play();
            var playing = Configure(new JObject { ["main"] = new JObject { ["duration"] = 3 } });
            AssertError(playing, "SEMANTIC_INVALID");
            StringAssert.Contains("is playing", playing["error"]?.ToString());
            Assert.That(_system.main.duration, Is.EqualTo(5f));

            _system.Pause();
            AssertError(Configure(new JObject { ["main"] = new JObject { ["duration"] = 3 } }), "SEMANTIC_INVALID");
            Assert.That(_system.isPaused, Is.True, "The refusal never stops the system implicitly.");

            StopAndClear(_system);
            AssertSuccess(Configure(new JObject { ["main"] = new JObject { ["duration"] = 3 } }));
            Assert.That(_system.main.duration, Is.EqualTo(3f));
        }

        [Test]
        public void InvalidField_RejectsTheWholeCall()
        {
            var json = Configure(new JObject
            {
                ["main"] = new JObject { ["duration"] = 4 },
                ["emission"] = new JObject { ["rateOverTime"] = -1 }
            });
            AssertError(json, "SEMANTIC_INVALID");
            Assert.That(_system.main.duration, Is.EqualTo(5f), "The valid field of a rejected call is not written either.");
        }

        [Test]
        public void FailureWhileApplying_RevertsEverythingAndLeavesNoWorkflowResidue()
        {
            WorkflowManager.BeginTask("particle-apply-failure", "test");
            int writes = 0;
            ParticleSkills.AfterWriteHookForTests = write =>
            {
                if (++writes == 2)
                    throw new InvalidOperationException("injected failure");
            };

            var json = Configure(new JObject
            {
                ["main"] = new JObject { ["duration"] = 4, ["loop"] = false },
                ["emission"] = new JObject { ["rateOverTime"] = 3 }
            });

            AssertError(json, "INTERNAL");
            StringAssert.Contains("rolled back", json["error"]?.ToString());
            Assert.That(_system.main.duration, Is.EqualTo(5f), "The write that succeeded before the failure was reverted.");
            Assert.That(_system.main.loop, Is.True);
            Assert.That(_system.emission.rateOverTime.constant, Is.EqualTo(10f));
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty, "The failed call's workflow snapshots were removed.");
        }

        [Test]
        public void PrefabInstance_GetsAnOverride_AndTheAssetIsRefused()
        {
            EnsureAssetRoot();
            var source = MakeSystem("PrefabFx");
            var prefab = PrefabUtility.SaveAsPrefabAsset(source.gameObject, AssetRoot + "/PrefabFx.prefab");
            UnityEngine.Object.DestroyImmediate(source.gameObject);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            var json = Json(ParticleSkills.ParticleConfigure(entityId: Id(instance), configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = 4.5 } })));
            AssertSuccess(json);
            Assert.That(instance.GetComponent<ParticleSystem>().main.duration, Is.EqualTo(4.5f));
            Assert.That(prefab.GetComponent<ParticleSystem>().main.duration, Is.EqualTo(5f), "The prefab asset is never changed.");
            var overrides = PrefabUtility.GetPropertyModifications(instance).Where(modification => modification.target is ParticleSystem).ToArray();
            Assert.That(overrides.Any(modification => modification.propertyPath == "lengthInSec"), Is.True, "The write is recorded as an instance override.");

            var asset = Json(ParticleSkills.ParticleConfigure(entityId: Id(prefab), configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = 3 } })));
            AssertError(asset, "SEMANTIC_INVALID");
            StringAssert.Contains("not in an open scene", asset["error"]?.ToString());
            Assert.That(prefab.GetComponent<ParticleSystem>().main.duration, Is.EqualTo(5f));
        }

        [Test]
        public void ReadOnlyQueriesAnswerWhileTheSystemPlays()
        {
            _system.Play();
            var info = Json(ParticleSkills.ParticleGetInfo(entityId: Id(_system.gameObject)));
            AssertSuccess(info);
            Assert.That(info["playback"]["isPlaying"]?.Value<bool>(), Is.True);
        }
    }
}
#endif

// Producer:Betsy
