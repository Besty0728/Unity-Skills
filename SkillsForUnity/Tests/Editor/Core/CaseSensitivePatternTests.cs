using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// batch_query_* matched name and namePattern case-insensitively while the docs only said "regex" (P10). The
    /// default stays case-insensitive; queryJson.caseSensitive:true (batch_query_assets: caseSensitive=true) matches
    /// case exactly, and .NET's inline (?-i) / (?i) still work inside a pattern.
    /// </summary>
    [TestFixture]
    public class CaseSensitivePatternTests
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
            foreach (var name in new[] { "Cube_001", "cube_002", "CUBE_003", "Sphere_001" })
                new GameObject(name);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void NamePattern_DefaultIgnoresCase()
        {
            Assert.That(Names("{\"namePattern\":\"^cube_\\\\d+$\"}"), Is.EquivalentTo(new[] { "Cube_001", "cube_002", "CUBE_003" }));
        }

        [Test]
        public void NamePattern_CaseSensitiveFalse_IsTheDefault()
        {
            Assert.That(Names("{\"namePattern\":\"^cube_\\\\d+$\",\"caseSensitive\":false}"),
                Is.EquivalentTo(new[] { "Cube_001", "cube_002", "CUBE_003" }));
        }

        [Test]
        public void NamePattern_CaseSensitiveTrue_MatchesCaseExactly()
        {
            Assert.That(Names("{\"namePattern\":\"^Cube_\\\\d+$\",\"caseSensitive\":true}"), Is.EquivalentTo(new[] { "Cube_001" }));
            Assert.That(Names("{\"namePattern\":\"^cube_\\\\d+$\",\"caseSensitive\":true}"), Is.EquivalentTo(new[] { "cube_002" }));
        }

        [Test]
        public void NamePattern_InlineDisableIgnoreCase_WorksWithTheDefault()
        {
            Assert.That(Names("{\"namePattern\":\"(?-i)^Cube_\\\\d+$\"}"), Is.EquivalentTo(new[] { "Cube_001" }));
        }

        [Test]
        public void NamePattern_InlineIgnoreCase_WorksWithCaseSensitiveTrue()
        {
            Assert.That(Names("{\"namePattern\":\"(?i)^cube_\\\\d+$\",\"caseSensitive\":true}"),
                Is.EquivalentTo(new[] { "Cube_001", "cube_002", "CUBE_003" }));
        }

        [Test]
        public void Name_Substring_DefaultIgnoresCaseAndCaseSensitiveTrueMatchesExactly()
        {
            Assert.That(Names("{\"name\":\"cube\"}"), Is.EquivalentTo(new[] { "Cube_001", "cube_002", "CUBE_003" }));
            Assert.That(Names("{\"name\":\"cube\",\"caseSensitive\":true}"), Is.EquivalentTo(new[] { "cube_002" }));
        }

        [Test]
        public void CaseSensitive_KeyMatchesCaseInsensitivelyLikeTheOtherKeys()
        {
            Assert.That(Names("{\"namePattern\":\"^Cube_\",\"CaseSensitive\":true}"), Is.EquivalentTo(new[] { "Cube_001" }));
        }

        [Test]
        public void CaseSensitive_IsEchoedInTheQuery()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"namePattern\":\"^Cube_\",\"caseSensitive\":true}"));

            Assert.That(json["query"]?["caseSensitive"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
        }

        [Test]
        public void CaseSensitive_TypoIsRejectedWithTheClosestKey()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"namePattern\":\"^Cube_\",\"caseSensative\":true}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["validValues"]?.Values<string>(), Does.Contain("caseSensitive"));
            Assert.That(json["suggestedFixes"]?.ToString(Formatting.None), Does.Contain("'caseSensitive'"));
        }

        [Test]
        public void CaseSensitive_NotABoolean_IsSemanticInvalid()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"namePattern\":\"^Cube_\",\"caseSensitive\":\"maybe\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson"));
        }

        [Test]
        public void BatchQueryComponents_HonorsCaseSensitive()
        {
            var json = ToJson(BatchSkills.BatchQueryComponents("{\"namePattern\":\"^cube_\\\\d+$\",\"caseSensitive\":true}"));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["objects"]?[0]?["name"]?.ToString(), Is.EqualTo("cube_002"));
        }

        [Test]
        public void BatchPreviewRename_HonorsCaseSensitive()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"cube\",\"caseSensitive\":true}", mode: "prefix", prefix: "X_"));

            Assert.That(json["targetCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["sampleChanges"]?[0]?["after"]?.ToString(), Is.EqualTo("X_cube_002"));
        }

        [Test]
        public void SchemaNote_NamesTheCaseDefault()
        {
            var schema = JObject.Parse(SkillRouter.GetFilteredSchema("?names=batch_query_gameobjects"));
            var skill = ((JArray)schema["skills"]).Cast<JObject>().Single(s => (string)s["name"] == "batch_query_gameobjects");
            var note = ((JArray)skill["parameters"]).Cast<JObject>().Single(p => (string)p["name"] == "queryJson")["description"]?.ToString();

            Assert.That(note, Does.Contain("caseSensitive").And.Contain("ignore case").And.Contain("(?-i)"));
        }

        // ---------- batch_query_assets: the file-name pattern follows the same rule ----------

        private const string ScriptFolder = "Packages/com.besty.unity-skills/Editor/Skills";

        [Test]
        public void QueryAssets_NamePattern_DefaultIgnoresCase()
        {
            var json = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^batchskills$"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["assets"]?.Select(a => a["name"]?.ToString()), Is.EquivalentTo(new[] { "BatchSkills" }));
        }

        [Test]
        public void QueryAssets_CaseSensitiveTrue_MatchesCaseExactly()
        {
            var lower = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^batchskills$", caseSensitive: true));
            var exact = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^BatchSkills$", caseSensitive: true));

            Assert.That(lower["count"]?.Value<int>(), Is.EqualTo(0), lower.ToString(Formatting.None));
            Assert.That(exact["assets"]?.Select(a => a["name"]?.ToString()), Is.EquivalentTo(new[] { "BatchSkills" }));
        }

        // ---------- helpers ----------

        private static string[] Names(string queryJson)
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects(queryJson));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            return json["objects"].Select(o => o["name"]?.ToString()).ToArray();
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
