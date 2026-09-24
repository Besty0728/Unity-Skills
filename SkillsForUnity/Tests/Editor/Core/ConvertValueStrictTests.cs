using System.IO;
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
    /// Strict text parsing behind component_set_property, the serialized-property writers, prefab_set_property and
    /// scriptableobject_set(_batch): a bool outside true/false/1/0/yes/no/on/off and an unknown AnimationCurve
    /// preset are rejected instead of silently becoming false / linear, and the JSON curve form works on the
    /// reflection path too.
    /// </summary>
    [TestFixture]
    public class ConvertValueStrictTests
    {
        private const string ProbeFolder = "Assets/__UnitySkillsConvertValueProbe__";

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
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        [TestCase("true", true)]
        [TestCase("1", true)]
        [TestCase("yes", true)]
        [TestCase("on", true)]
        [TestCase("TRUE", true)]
        [TestCase(" On ", true)]
        [TestCase("false", false)]
        [TestCase("0", false)]
        [TestCase("no", false)]
        [TestCase("off", false)]
        [TestCase("", false)]
        [TestCase("null", false)]
        public void ConvertValue_Bool_AcceptsTheVocabulary(string text, bool expected)
        {
            Assert.That(ComponentSkills.ConvertValue(text, typeof(bool)), Is.EqualTo(expected));
        }

        [TestCase("treu")]
        [TestCase("2")]
        [TestCase("enabled")]
        [TestCase("y")]
        public void ConvertValue_Bool_RejectsEverythingElse(string text)
        {
            Assert.That(() => ComponentSkills.ConvertValue(text, typeof(bool)),
                Throws.Exception.With.Message.StartsWith("Invalid value"));
        }

        [Test]
        public void ComponentSetProperty_InvalidBool_IsSemanticInvalidAndWritesNothing()
        {
            var go = new GameObject("CV_Trigger");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetProperty(name: "CV_Trigger", componentType: "BoxCollider",
                propertyName: "isTrigger", value: "treu"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("value"));
            Assert.That(box.isTrigger, Is.True, "A rejected bool must not be written as false.");
        }

        [Test]
        public void ComponentSetProperty_InvalidBool_IsRejectedBeforeExecution()
        {
            var go = new GameObject("CV_TriggerPlan");
            go.AddComponent<BoxCollider>();
            GameObjectFinder.InvalidateCache();

            var dry = JObject.Parse(SkillRouter.DryRun("component_set_property",
                "{\"name\":\"CV_TriggerPlan\",\"componentType\":\"BoxCollider\",\"propertyName\":\"isTrigger\",\"value\":\"treu\"}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            Assert.That(dry["validation"]?["semanticErrors"]?.ToString(Formatting.None), Does.Contain("isTrigger"));
        }

        [Test]
        public void SerializedProperty_InvalidBool_IsRejected_AndOffIsFalse()
        {
            var go = new GameObject("CV_SerializedTrigger");
            var box = go.AddComponent<BoxCollider>();
            box.isTrigger = true;
            GameObjectFinder.InvalidateCache();

            var rejected = ToJson(ComponentSkills.ComponentSetSerializedProperty(name: "CV_SerializedTrigger",
                componentType: "BoxCollider", propertyPath: "m_IsTrigger", value: "treu"));
            Assert.That(rejected["error"]?.ToString(), Does.StartWith("Invalid value"), rejected.ToString(Formatting.None));
            Assert.That(box.isTrigger, Is.True);

            var accepted = ToJson(ComponentSkills.ComponentSetSerializedProperty(name: "CV_SerializedTrigger",
                componentType: "BoxCollider", propertyPath: "m_IsTrigger", value: "off"));
            Assert.That(accepted["success"]?.Value<bool>(), Is.True, accepted.ToString(Formatting.None));
            Assert.That(box.isTrigger, Is.False);
        }

        [TestCase("linear", 2)]
        [TestCase("EaseIn", 2)]
        [TestCase("easeOut", 2)]
        [TestCase("EASEINOUT", 2)]
        [TestCase("Constant", 2)]
        public void ConvertValue_AnimationCurve_KnownPresetsUnchanged(string preset, int keyCount)
        {
            var curve = (AnimationCurve)ComponentSkills.ConvertValue(preset, typeof(AnimationCurve));
            Assert.That(curve.length, Is.EqualTo(keyCount));
        }

        [Test]
        public void ConvertValue_AnimationCurve_UnknownPresetIsRejected()
        {
            Assert.That(() => ComponentSkills.ConvertValue("bouncy", typeof(AnimationCurve)),
                Throws.Exception.With.Message.StartsWith("Invalid value"));
        }

        [Test]
        public void ConvertValue_AnimationCurve_AcceptsJsonKeys()
        {
            var curve = (AnimationCurve)ComponentSkills.ConvertValue(
                "{\"keys\":[{\"time\":0,\"value\":0},{\"time\":2,\"value\":1}],\"postWrapMode\":\"Loop\"}", typeof(AnimationCurve));

            Assert.That(curve.length, Is.EqualTo(2));
            Assert.That(curve.keys[1].time, Is.EqualTo(2f));
            Assert.That(curve.keys[1].value, Is.EqualTo(1f));
            Assert.That(curve.postWrapMode, Is.EqualTo(WrapMode.Loop));
        }

        [Test]
        public void ConvertValue_AnimationCurve_MalformedJsonIsRejected()
        {
            Assert.That(() => ComponentSkills.ConvertValue("{\"keys\":[]}", typeof(AnimationCurve)),
                Throws.Exception.With.Message.StartsWith("Invalid value"));
        }

        [Test]
        public void ScriptableObjectSetBatch_InvalidBool_ReportsAFailedKey()
        {
            var assetPath = CreateBoolProbeAsset();

            var json = ToJson(ScriptableObjectSkills.ScriptableObjectSetBatch(assetPath, "{\"flag\":\"treu\",\"count\":\"4\"}"));

            Assert.That(json["failed"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["fieldsSet"]?.Value<int>(), Is.EqualTo(1), "The other key is still set.");
            Assert.That(json["results"]?[0]?["field"]?.ToString(), Is.EqualTo("flag"));
            Assert.That(json["results"]?[0]?["error"]?.ToString(), Does.StartWith("Invalid value"));
            var asset = AssetDatabase.LoadAssetAtPath<BoolProbeAsset>(assetPath);
            Assert.That(asset.flag, Is.True, "The rejected bool must not be written as false.");
            Assert.That(asset.count, Is.EqualTo(4));
        }

        [Test]
        public void ScriptableObjectSetBatch_MalformedFields_IsSemanticInvalid()
        {
            var assetPath = CreateBoolProbeAsset();

            var json = ToJson(ScriptableObjectSkills.ScriptableObjectSetBatch(assetPath, "{\"flag\":"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("fields"));
        }

        [Test]
        public void ScriptableObjectSet_InvalidBool_IsAnInvalidValueError()
        {
            var assetPath = CreateBoolProbeAsset();

            var json = ToJson(ScriptableObjectSkills.ScriptableObjectSet(assetPath, "flag", "enabled"));

            Assert.That(json["error"]?.ToString(), Does.StartWith("Invalid value"), json.ToString(Formatting.None));
            Assert.That(AssetDatabase.LoadAssetAtPath<BoolProbeAsset>(assetPath).flag, Is.True);
        }

        private static string CreateBoolProbeAsset()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(ProbeFolder));
            var assetPath = ProbeFolder + "/CV_BoolProbe.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<BoolProbeAsset>(), assetPath);
            AssetDatabase.SaveAssets();
            return assetPath;
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
