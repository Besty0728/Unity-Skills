using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 precision fix for LightSkills (bugs.md B5): light_set_enabled_batch no longer defaults
    /// an omitted "enabled" to false (it rejects the item instead, atomically), and
    /// light_set_properties_batch gains spotAngle plus the same applied/skipped bookkeeping as the
    /// single-object setter (H9's shared ApplyLightProperties).
    /// </summary>
    [TestFixture]
    public class LightBatchPrecisionTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void LightEnabledBatch_OmittedEnabled_FailsTheBatchAndTogglesNothing()
        {
            var lightA = CreateLight("RB_LightA", LightType.Point);
            var lightB = CreateLight("RB_LightB", LightType.Point);
            lightA.enabled = true;
            lightB.enabled = true;

            var json = ToJson(LightSkills.LightSetEnabledBatch(
                $"[{{\"name\":\"{lightA.gameObject.name}\",\"enabled\":false}},{{\"name\":\"{lightB.gameObject.name}\"}}]"));

            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            var results = (JArray)json["results"];
            Assert.That(results[1]["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
            Assert.That(results[1]["parameter"]?.ToString(), Is.EqualTo("enabled"));
            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True);
            Assert.That(lightA.enabled, Is.True, "The batch is atomic: item 0's toggle must be rolled back too.");
            Assert.That(lightB.enabled, Is.True);
        }

        [Test]
        public void LightEnabledBatch_ExplicitValues_Unchanged()
        {
            var lightA = CreateLight("RB_LightC", LightType.Point);
            var lightB = CreateLight("RB_LightD", LightType.Point);
            lightA.enabled = true;
            lightB.enabled = false;

            var json = ToJson(LightSkills.LightSetEnabledBatch(
                $"[{{\"name\":\"{lightA.gameObject.name}\",\"enabled\":false}},{{\"name\":\"{lightB.gameObject.name}\",\"enabled\":true}}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(lightA.enabled, Is.False);
            Assert.That(lightB.enabled, Is.True);
        }

        [Test]
        public void LightPropsBatch_SpotAngle_IsAppliedAndListed()
        {
            var light = CreateLight("RB_Spot", LightType.Spot);

            var json = ToJson(LightSkills.LightSetPropertiesBatch($"[{{\"name\":\"{light.gameObject.name}\",\"spotAngle\":45}}]"));

            Assert.That(json["warnings"], Is.Null, json.ToString(Formatting.None));
            Assert.That(light.spotAngle, Is.EqualTo(45f).Within(1e-3f));
            var results = (JArray)json["results"];
            Assert.That(results[0]["applied"]?.Values<string>(), Does.Contain("spotAngle"));
        }

        [Test]
        public void LightPropsBatch_RangeOnDirectional_IsReportedSkipped()
        {
            var light = CreateLight("RB_Directional", LightType.Directional);
            var originalRange = light.range;

            var json = ToJson(LightSkills.LightSetPropertiesBatch(
                $"[{{\"name\":\"{light.gameObject.name}\",\"range\":5,\"intensity\":2}}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var results = (JArray)json["results"];
            Assert.That(results[0]["applied"]?.Values<string>(), Is.EquivalentTo(new[] { "intensity" }));
            Assert.That(results[0]["skipped"]?.Values<string>().First(), Does.StartWith("range"));
            Assert.That(light.range, Is.EqualTo(originalRange));
        }

        [Test]
        public void LightPropsBatch_MatchesSingleSemantics()
        {
            var batchLight = CreateLight("RB_MatchBatch", LightType.Spot);
            var singleLight = CreateLight("RB_MatchSingle", LightType.Spot);

            var batchJson = ToJson(LightSkills.LightSetPropertiesBatch(
                $"[{{\"name\":\"{batchLight.gameObject.name}\",\"range\":8,\"spotAngle\":50,\"intensity\":3}}]"));
            var singleJson = ToJson(LightSkills.LightSetProperties(name: singleLight.gameObject.name, range: 8, spotAngle: 50, intensity: 3));

            var batchResult = (JArray)batchJson["results"];
            Assert.That(batchResult[0]["applied"]?.Values<string>().OrderBy(s => s),
                Is.EquivalentTo(singleJson["applied"]?.Values<string>().OrderBy(s => s)),
                batchJson.ToString(Formatting.None) + " vs " + singleJson.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        private static Light CreateLight(string name, LightType type)
        {
            var go = new GameObject(name);
            var light = go.AddComponent<Light>();
            light.type = type;
            return light;
        }
    }
}

// Producer:Betsy
