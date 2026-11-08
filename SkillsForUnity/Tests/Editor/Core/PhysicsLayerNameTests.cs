using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// physics_get_layer_collision and physics_set_layer_collision accept layer names (layer1Name / layer2Name) as well as indices.
    /// The built-in layers "Default" (0) and "Ignore Raycast" (2) always exist, so the tests use that pair; the set test restores it.
    /// </summary>
    [TestFixture]
    public class PhysicsLayerNameTests
    {
        private const int DefaultLayer = 0;
        private const int IgnoreRaycastLayer = 2;
        private const string DefaultName = "Default";
        private const string IgnoreRaycastName = "Ignore Raycast";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private bool _originalIgnore;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            _originalIgnore = Physics.GetIgnoreLayerCollision(DefaultLayer, IgnoreRaycastLayer);
        }

        [TearDown]
        public void TearDown()
        {
            Physics.IgnoreLayerCollision(DefaultLayer, IgnoreRaycastLayer, _originalIgnore);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void Get_ByName_MatchesGet_ByIndex()
        {
            var byIndex = ToJson(PhysicsSkills.PhysicsGetLayerCollision(DefaultLayer, IgnoreRaycastLayer));
            var byName = ToJson(PhysicsSkills.PhysicsGetLayerCollision(layer1Name: DefaultName, layer2Name: IgnoreRaycastName));

            Assert.That((int)byName["layer1"], Is.EqualTo(DefaultLayer), byName.ToString(Formatting.None));
            Assert.That((int)byName["layer2"], Is.EqualTo(IgnoreRaycastLayer), byName.ToString(Formatting.None));
            Assert.That((bool)byName["collisionEnabled"], Is.EqualTo((bool)byIndex["collisionEnabled"]));
        }

        [Test]
        public void Set_ByName_IsReadBackByIndex()
        {
            var set = ToJson(PhysicsSkills.PhysicsSetLayerCollision(layer1Name: DefaultName, layer2Name: IgnoreRaycastName, enableCollision: false));
            Assert.That(set["success"]?.Value<bool>(), Is.True, set.ToString(Formatting.None));

            var readBack = ToJson(PhysicsSkills.PhysicsGetLayerCollision(DefaultLayer, IgnoreRaycastLayer));
            Assert.That((bool)readBack["collisionEnabled"], Is.False, readBack.ToString(Formatting.None));
            Assert.That(Physics.GetIgnoreLayerCollision(DefaultLayer, IgnoreRaycastLayer), Is.True);
        }

        [Test]
        public void Get_UnknownLayerName_ReturnsSemanticInvalid()
        {
            var json = ToJson(PhysicsSkills.PhysicsGetLayerCollision(layer1Name: "NoSuchLayer__PhysicsLayerNameTests", layer2: IgnoreRaycastLayer));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
        }

        [Test]
        public void Set_UnknownLayerName_ReturnsSemanticInvalid()
        {
            var json = ToJson(PhysicsSkills.PhysicsSetLayerCollision(layer1Name: "NoSuchLayer__PhysicsLayerNameTests", layer2: IgnoreRaycastLayer, enableCollision: false));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
        }

        [Test]
        public void Get_WithNeitherIndexNorName_ReturnsMissingParam()
        {
            var json = ToJson(PhysicsSkills.PhysicsGetLayerCollision(layer2: IgnoreRaycastLayer));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
