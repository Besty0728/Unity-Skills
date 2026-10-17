using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Serialized-property descriptions of vectors, rectangles, quaternions and colors interpolated their floats with the
    /// current culture, so on a comma-decimal machine "1.5,2" read "1,5,2" (P14). They are now culture-invariant, the
    /// way a float and what the writers parse already were.
    /// </summary>
    [TestFixture]
    public class InvariantFormattingTests
    {
        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private CultureInfo _savedCulture;
        private FormattingProbe _probe;

        // Exactly representable values, so G9 prints them without float noise.
        private static readonly Dictionary<string, string> Expected = new Dictionary<string, string>
        {
            ["scalar"] = "1.5",
            ["vector2"] = "0.5,-1.25",
            ["vector3"] = "1.5,-2.25,3",
            ["vector4"] = "0.5,0.25,-0.125,8",
            ["color"] = "0.5,0.25,0.125,1",
            ["quaternion"] = "0.5,0.5,-0.5,0.5",
            ["rect"] = "0.5,1.5,2.25,3.75",
            ["bounds"] = "0.5,1.5,-2.5,1,2,3.5",
            ["vector2Int"] = "-1,2",
            ["vector3Int"] = "-1,2,-3",
            ["rectInt"] = "-1,2,30,-40",
            ["boundsInt"] = "-1,2,-3,4,5,6"
        };

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedCulture = CultureInfo.CurrentCulture;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            _probe = new GameObject("Formatted").AddComponent<FormattingProbe>();
            _probe.scalar = 1.5f;
            _probe.vector2 = new Vector2(0.5f, -1.25f);
            _probe.vector3 = new Vector3(1.5f, -2.25f, 3f);
            _probe.vector4 = new Vector4(0.5f, 0.25f, -0.125f, 8f);
            _probe.color = new Color(0.5f, 0.25f, 0.125f, 1f);
            _probe.quaternion = new Quaternion(0.5f, 0.5f, -0.5f, 0.5f);
            _probe.rect = new Rect(0.5f, 1.5f, 2.25f, 3.75f);
            _probe.bounds = new Bounds(new Vector3(0.5f, 1.5f, -2.5f), new Vector3(1f, 2f, 3.5f));
            _probe.vector2Int = new Vector2Int(-1, 2);
            _probe.vector3Int = new Vector3Int(-1, 2, -3);
            _probe.rectInt = new RectInt(-1, 2, 30, -40);
            _probe.boundsInt = new BoundsInt(-1, 2, -3, 4, 5, 6);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            CultureInfo.CurrentCulture = _savedCulture;
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void TheCommaDecimalCulture_ReallyChangesPlainFormatting()
        {
            CultureInfo.CurrentCulture = GermanCulture();

            Assert.That(1.5f.ToString(), Is.EqualTo("1,5"), "Without this the tests below prove nothing.");
            Assert.That($"{1.5f:G9}", Is.EqualTo("1,5"));
        }

        [Test]
        public void Descriptions_UnderTheInvariantCulture_AreTheDocumentedFormat()
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            Assert.That(Values(), Is.EquivalentTo(Expected));
        }

        [TestCase("de-DE")]
        [TestCase("fr-FR")]
        [TestCase("ru-RU")]
        public void Descriptions_UnderACommaDecimalCulture_AreUnchanged(string culture)
        {
            CultureInfo.CurrentCulture = CultureFor(culture);
            Assume.That(1.5f.ToString(), Is.EqualTo("1,5"), $"{culture} is not a comma-decimal culture on this runtime.");

            Assert.That(Values(), Is.EquivalentTo(Expected));
        }

        [Test]
        public void TheSetterEcho_UnderAGermanCulture_StaysParseable()
        {
            CultureInfo.CurrentCulture = GermanCulture();

            var json = JObject.Parse(JsonConvert.SerializeObject(ComponentSkills.ComponentSetSerializedProperty(
                name: "Formatted", componentType: nameof(FormattingProbe), propertyPath: "color", value: "0.25,0.5,0.75,1")));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That((string)json["valueSet"], Is.EqualTo("0.25,0.5,0.75,1"));
        }

        [Test]
        public void ADescription_FedBackToTheWriter_RoundTripsUnderAGermanCulture()
        {
            CultureInfo.CurrentCulture = GermanCulture();
            var read = Values();
            _probe.vector3 = Vector3.zero;
            _probe.color = Color.black;

            foreach (var field in new[] { "vector3", "color" })
            {
                var json = JObject.Parse(JsonConvert.SerializeObject(ComponentSkills.ComponentSetSerializedProperty(
                    name: "Formatted", componentType: nameof(FormattingProbe), propertyPath: field, value: read[field])));
                Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            }

            Assert.That(_probe.vector3, Is.EqualTo(new Vector3(1.5f, -2.25f, 3f)));
            Assert.That(_probe.color, Is.EqualTo(new Color(0.5f, 0.25f, 0.125f, 1f)));
        }

        // ---------- helpers ----------

        private static Dictionary<string, string> Values()
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(
                ComponentSkills.ComponentGetSerializedProperties(name: "Formatted", componentType: nameof(FormattingProbe))));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            return ((JArray)json["properties"]).Cast<JObject>()
                .Where(e => Expected.ContainsKey((string)e["propertyPath"]))
                .ToDictionary(e => (string)e["propertyPath"], e => (string)e["value"]);
        }

        private static CultureInfo GermanCulture() => CultureFor("de-DE");

        private static CultureInfo CultureFor(string name)
        {
            try { return new CultureInfo(name); }
            catch (CultureNotFoundException) { Assert.Ignore($"Culture {name} is not available on this runtime."); return null; }
        }
    }
}

// Producer:Betsy
