using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// scene_save during Play Mode used to throw inside EditorSceneManager.SaveScene, which the router reported as INTERNAL.
    /// It now returns SEMANTIC_INVALID before anything is written, so the target file must not appear.
    /// </summary>
    [TestFixture]
    public class SceneSavePlayModeTests
    {
        private const string TempRoot = "Assets/Temp/SceneSavePlayModeTests";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            DeleteTempRoot();
        }

        [TearDown]
        public void TearDown()
        {
            SceneSkills.PlayingOverrideForTests = null;
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            DeleteTempRoot();
        }

        [Test]
        public void SceneSave_DuringPlayMode_ReturnsSemanticInvalid_AndWritesNothing()
        {
            SceneSkills.PlayingOverrideForTests = true;
            var scenePath = TempRoot + "/PlayModeSave.unity";

            var json = ToJson(SceneSkills.SceneSave(scenePath));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["suggestedFixes"]?[0]?["skill"]?.ToString(), Is.EqualTo("editor_stop"), json.ToString(Formatting.None));
            Assert.That(File.Exists(scenePath), Is.False);
            Assert.That(AssetDatabase.IsValidFolder(TempRoot), Is.False);
        }

        [Test]
        public void SceneUnload_DuringPlayMode_ReturnsSemanticInvalid()
        {
            SceneSkills.PlayingOverrideForTests = true;

            var json = ToJson(SceneSkills.SceneUnload("AnyScene"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["suggestedFixes"]?[0]?["skill"]?.ToString(), Is.EqualTo("editor_stop"), json.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private void DeleteTempRoot()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
