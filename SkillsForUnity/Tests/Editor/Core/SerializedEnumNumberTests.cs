using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Serialized enum writes by number: 0..n-1 stays the member index (the number reads report, so read-then-write
    /// round-trips), with a warning when that member's value differs; larger numbers are member values, -1 or
    /// declared bits; bits no member declares are rejected with the classifier's "Enum value ... not found" wording.
    /// </summary>
    [TestFixture]
    public class SerializedEnumNumberTests
    {
        private const string Target = "EN_Probe";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private EnumProbe _probe;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _probe = new GameObject(Target).AddComponent<EnumProbe>();
            Assert.That(_probe, Is.Not.Null, "Unity did not attach EnumProbe; fixtures must live in a non-editor assembly.");
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void Enum_SequentialNumber_IsIndex_NoWarning()
        {
            var json = Set("seq", "2");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_probe.seq, Is.EqualTo(EnumProbe.ProbeSeq.Two));
            Assert.That(json["warnings"], Is.Null, "Index and value agree for a sequential enum.");
        }

        [Test]
        public void Enum_FlagsInRangeNumber_StaysIndex_AndWarns()
        {
            var json = Set("flags", "3");

            Assert.That(_probe.flags, Is.EqualTo(EnumProbe.ProbeFlags.C), "3 is the member index of C (None, A, B, C, D).");
            var warning = json["warnings"]?[0]?.ToString();
            Assert.That(warning, Does.Contain("index 3"), json.ToString(Formatting.None));
            Assert.That(warning, Does.Contain("'A,B'"), "The warning must name the combination that has the value 3.");
        }

        [Test]
        public void Enum_FlagsNames_StillCombine()
        {
            var json = Set("flags", "A,B");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That((int)_probe.flags, Is.EqualTo(3));
        }

        [Test]
        public void Enum_FlagsOutOfRangeCombination_IsBitmask()
        {
            var json = Set("flags", "11");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(_probe.flags, Is.EqualTo(EnumProbe.ProbeFlags.A | EnumProbe.ProbeFlags.B | EnumProbe.ProbeFlags.D));
            Assert.That(json["warnings"]?[0]?.ToString(), Does.Contain("raw bitmask"));
        }

        [Test]
        public void Enum_Minus1_IsEverything()
        {
            var json = Set("flags", "-1");

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That((int)_probe.flags, Is.EqualTo(-1));
        }

        [TestCase("flags", "16")]
        [TestCase("sparse", "99")]
        public void Enum_UndeclaredBits_AreRejected(string field, string value)
        {
            var before = JsonUtility.ToJson(_probe);

            var json = Set(field, value);

            Assert.That(json["error"]?.ToString(), Does.StartWith("Enum value"), json.ToString(Formatting.None));
            Assert.That(JsonUtility.ToJson(_probe), Is.EqualTo(before), "A rejected number must change nothing.");
        }

        [Test]
        public void Enum_SparseMemberValue_OutOfRange_IsThatMember()
        {
            var json = Set("sparse", "20");

            Assert.That(_probe.sparse, Is.EqualTo(EnumProbe.ProbeSparse.Mid), json.ToString(Formatting.None));
            Assert.That(json["warnings"], Is.Null);
        }

        [Test]
        public void Enum_SparseInRangeNumber_IsIndex_AndWarns()
        {
            var json = Set("sparse", "1");

            Assert.That(_probe.sparse, Is.EqualTo(EnumProbe.ProbeSparse.Mid), "1 is the member index of Mid.");
            Assert.That(json["warnings"]?[0]?.ToString(), Does.Contain("the value 1"), json.ToString(Formatting.None));
        }

        [Test]
        public void Enum_ReadBackRoundTrip_IsStable()
        {
            foreach (var field in new[] { "flags", "sparse", "seq" })
            {
                var memberCount = new SerializedObject(_probe).FindProperty(field).enumNames.Length;
                for (int index = 0; index < memberCount; index++)
                {
                    var first = Set(field, index.ToString());
                    var valueSet = first["valueSet"]?.ToString();
                    var before = JsonUtility.ToJson(_probe);

                    var again = Set(field, valueSet);

                    Assert.That(again["valueSet"]?.ToString(), Is.EqualTo(valueSet), $"{field}[{index}]: {again.ToString(Formatting.None)}");
                    Assert.That(JsonUtility.ToJson(_probe), Is.EqualTo(before), $"Writing back {field}'s read value '{valueSet}' changed it.");
                }
            }
        }

        private JObject Set(string propertyPath, string value)
        {
            var result = ComponentSkills.ComponentSetSerializedProperty(name: Target, componentType: nameof(EnumProbe),
                propertyPath: propertyPath, value: value);
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }
    }
}

// Producer:Betsy
