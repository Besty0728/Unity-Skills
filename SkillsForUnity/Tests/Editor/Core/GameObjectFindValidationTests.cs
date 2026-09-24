using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 precision fix for gameobject_find (bugs.md B6): an unknown layer or component used to
    /// be silently ignored (returning every object as if it matched), and an invalid regex escaped as
    /// an unhandled exception (INTERNAL + wait_and_retry). All three are now rejected with a
    /// structured SEMANTIC_INVALID before the scene is even scanned.
    /// </summary>
    [TestFixture]
    public class GameObjectFindValidationTests
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
        public void Find_UnknownLayer_IsRejectedWithDefinedLayers()
        {
            new GameObject("RB_FindA");

            var json = ToJson(GameObjectSkills.GameObjectFind(layer: "NoSuchLayer_7f3"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("layer"));
            Assert.That(json["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
            Assert.That(json["validValues"]?.Values<string>(), Does.Contain("Default"));
        }

        [Test]
        public void Find_UnknownComponent_IsRejected()
        {
            new GameObject("RB_FindB");

            var json = ToJson(GameObjectSkills.GameObjectFind(component: "NoSuchComponent_7f3"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("component"));
        }

        [Test]
        public void Find_InvalidRegex_IsSemanticInvalid_NotInternal()
        {
            var response = JObject.Parse(SkillRouter.Execute("gameobject_find", "{\"name\":\"([\",\"useRegex\":true}"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
        }

        [Test]
        public void Find_ValidLayerAndComponent_StillFilter()
        {
            var waterLayer = LayerMask.NameToLayer("Water");
            Assume.That(waterLayer, Is.GreaterThanOrEqualTo(0), "Built-in 'Water' layer not present in this project.");

            var a = new GameObject("RB_FindWaterA");
            a.layer = waterLayer;
            a.AddComponent<BoxCollider>();
            new GameObject("RB_FindWaterB");

            var layerJson = ToJson(GameObjectSkills.GameObjectFind(layer: "Water"));
            var names = ((JArray)layerJson["objects"]).Select(o => o["name"]?.ToString()).ToArray();
            Assert.That(names, Is.EquivalentTo(new[] { "RB_FindWaterA" }), layerJson.ToString(Formatting.None));

            var componentJson = ToJson(GameObjectSkills.GameObjectFind(component: "BoxCollider"));
            var componentNames = ((JArray)componentJson["objects"]).Select(o => o["name"]?.ToString())
                .Where(n => n != null && n.StartsWith("RB_Find")).ToArray();
            Assert.That(componentNames, Is.EquivalentTo(new[] { "RB_FindWaterA" }), componentJson.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
