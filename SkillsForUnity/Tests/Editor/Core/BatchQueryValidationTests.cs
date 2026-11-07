using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// queryJson is validated before anything is queried or a token minted: malformed JSON, unknown keys, invalid
    /// regexes and unresolvable tag/layer/componentType are SEMANTIC_INVALID (never INTERNAL, never an empty or
    /// widened match), active:false finds inactive objects, and batch_preview_rename rejects an unknown mode or a
    /// mode without its input.
    /// </summary>
    [TestFixture]
    public class BatchQueryValidationTests
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
            new GameObject("BQ_Cube");
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
        public void Query_InvalidNamePattern_IsSemanticInvalid()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"namePattern\":\"([\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson.namePattern"));
            Assert.That(json["count"], Is.Null, "An invalid pattern must not report an empty match.");
        }

        [Test]
        public void Query_UnknownLayer_IsRejectedWithDefinedLayers()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"layer\":\"NoSuchLayer_7f3\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson.layer"));
            Assert.That(json["validValues"]?.Values<string>(), Does.Contain("Default"));
        }

        [Test]
        public void Query_UnknownComponentType_IsRejected()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"componentType\":\"NoSuchComponent_7f3\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson.componentType"));
            Assert.That(json["similarTypes"], Is.Not.Null);
        }

        [Test]
        public void QueryComponents_ExplicitUnknownComponentType_NamesThatParameter()
        {
            var json = ToJson(BatchSkills.BatchQueryComponents(componentType: "NoSuchComponent_7f3"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("componentType"));
        }

        [Test]
        public void Query_UnregisteredTag_IsRejected()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"tag\":\"NoSuchTag_7f3\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson.tag"));
            Assert.That(json["validValues"]?.Values<string>(), Does.Contain("Untagged"));
        }

        [Test]
        public void Query_UnknownKey_IsRejectedWithClosestKey()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"tagg\":\"Player\"}"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("queryJson"));
            Assert.That(json["suggestedFixes"]?.ToString(Formatting.None), Does.Contain("'tag'"));
        }

        [Test]
        public void Query_KeysMatchCaseInsensitively_LikeTheBinder()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"Name\":\"BQ_Cube\"}"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void Query_InvalidJson_IsSemanticInvalid_NotInternal()
        {
            var response = JObject.Parse(SkillRouter.Execute("batch_query_gameobjects",
                new JObject { ["queryJson"] = "{bad" }.ToString(Formatting.None)));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
        }

        [Test]
        public void Query_ActiveFalse_FindsInactiveObjects()
        {
            var hidden = new GameObject("BQ_Hidden");
            hidden.SetActive(false);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"active\":false}"));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["objects"]?[0]?["name"]?.ToString(), Is.EqualTo("BQ_Hidden"));
            Assert.That(json["warnings"]?[0]?.ToString(), Does.Contain("includeInactive"));
        }

        [Test]
        public void Query_ValidQuery_ResponseShapeUnchanged()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"BQ_Cube\"}"));

            // totalMatched and truncated are additive (P11); every earlier key keeps its place.
            var keys = json.Properties().Select(p => p.Name).Where(name => name != "totalMatched" && name != "truncated");
            Assert.That(keys, Is.EqualTo(new[] { "success", "count", "summary", "query", "objects" }));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void PreviewSkills_RejectAnInvalidQuery_WithoutMintingAToken()
        {
            int before = BatchPersistence.State.previews.Count;

            var cleanup = ToJson(BatchSkills.BatchCleanupTempObjects("{\"tagg\":\"x\"}"));
            var fixer = ToJson(BatchSkills.BatchFixMissingScripts("{\"layer\":\"NoSuchLayer_7f3\"}"));

            Assert.That(cleanup["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), cleanup.ToString(Formatting.None));
            Assert.That(fixer["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), fixer.ToString(Formatting.None));
            Assert.That(cleanup["confirmToken"], Is.Null);
            Assert.That(BatchPersistence.State.previews.Count, Is.EqualTo(before));
        }

        [Test]
        public void PreviewRename_UnknownMode_IsRejectedAndMintsNoToken()
        {
            int before = BatchPersistence.State.previews.Count;

            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"BQ_Cube\"}", mode: "prefx", prefix: "X_"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["validValues"]?.Values<string>(), Is.EquivalentTo(new[] { "prefix", "suffix", "replace", "regex_replace" }));
            Assert.That(json["confirmToken"], Is.Null);
            Assert.That(BatchPersistence.State.previews.Count, Is.EqualTo(before));
        }

        [TestCase("prefix", "prefix")]
        [TestCase("suffix", "suffix")]
        [TestCase("replace", "search")]
        [TestCase("regex_replace", "regexPattern")]
        public void PreviewRename_ModeWithoutItsInput_IsMissingParam(string mode, string missing)
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"BQ_Cube\"}", mode: mode));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo(missing));
        }

        [Test]
        public void PreviewRename_InvalidRegex_IsSemanticInvalid()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"BQ_Cube\"}", mode: "regex_replace", regexPattern: "(["));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("regexPattern"));
        }

        [TestCase("prefix", "New_BQ_Cube")]
        [TestCase("suffix", "BQ_Cube_Old")]
        [TestCase("replace", "BQ_Box")]
        [TestCase("regex_replace", "Cube_BQ")]
        public void PreviewRename_ValidModes_StillPreview(string mode, string expected)
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"BQ_Cube\"}", mode: mode,
                prefix: "New_", suffix: "_Old", search: "Cube", replacement: "Box",
                regexPattern: "^(\\w+?)_(\\w+)$", regexReplacement: "$2_$1"));

            Assert.That(json["confirmToken"]?.ToString(), Is.Not.Empty, json.ToString(Formatting.None));
            Assert.That(json["sampleChanges"]?[0]?["after"]?.ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void PreviewRename_ReplaceWithEmptyReplacement_RemovesTheText()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"BQ_Cube\"}", mode: "replace", search: "_Cube"));

            Assert.That(json["sampleChanges"]?[0]?["after"]?.ToString(), Is.EqualTo("BQ"), json.ToString(Formatting.None));
        }

        // ---------- underPath ----------

        [Test]
        public void Query_UnderPath_MatchesDescendantsAtAnyDepth_NotTheNodeOrAPrefixSibling()
        {
            BuildSlotTree();

            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"namePattern\":\"^Slot_\"}"));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(2), json.ToString(Formatting.None));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["objects"].Select(o => o["path"].ToString()), Is.EquivalentTo(new[] { "Root/A/Slot_1", "Root/B/C/Slot_2" }));

            var subtree = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"/Root/B/\"}"));
            Assert.That(subtree["objects"].Select(o => o["path"].ToString()), Is.EquivalentTo(new[] { "Root/B/C", "Root/B/C/Slot_2" }),
                "Only what is below Root/B, at any depth; surrounding slashes are tolerated.");
        }

        [Test]
        public void Query_UnderPath_HonorsIncludeInactive()
        {
            BuildSlotTree();
            GameObject.Find("Root/B").SetActive(false);
            GameObjectFinder.InvalidateCache();

            var active = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"namePattern\":\"^Slot_\"}"));
            var all = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"namePattern\":\"^Slot_\",\"includeInactive\":true}"));

            Assert.That(active["count"]?.Value<int>(), Is.EqualTo(1), active.ToString(Formatting.None));
            Assert.That(all["count"]?.Value<int>(), Is.EqualTo(2), all.ToString(Formatting.None));
        }

        [Test]
        public void Query_UnderPathWithParentPath_IsTheIntersection()
        {
            BuildSlotTree();

            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"parentPath\":\"Root/B/C\"}"));
            var disjoint = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"parentPath\":\"Other\"}"));

            Assert.That(json["objects"].Select(o => o["path"].ToString()), Is.EqualTo(new[] { "Root/B/C/Slot_2" }), json.ToString(Formatting.None));
            Assert.That(disjoint["count"]?.Value<int>(), Is.EqualTo(0), disjoint.ToString(Formatting.None));
        }

        [Test]
        public void Query_UnderPath_IsAppliedBeforeTheLimit_AndInComponentQueries()
        {
            BuildSlotTree();

            var limited = ToJson(BatchSkills.BatchQueryGameObjects("{\"underPath\":\"Root\",\"namePattern\":\"^Slot_\",\"limit\":1}"));
            Assert.That(limited["count"]?.Value<int>(), Is.EqualTo(1), limited.ToString(Formatting.None));
            Assert.That(limited["totalMatched"]?.Value<int>(), Is.EqualTo(2), "totalMatched counts the filtered set, not the scene.");
            Assert.That(limited["truncated"]?.Value<bool>(), Is.True);

            var components = ToJson(BatchSkills.BatchQueryComponents("{\"underPath\":\"Root\",\"namePattern\":\"^Slot_\"}", componentType: "Transform"));
            Assert.That(components["count"]?.Value<int>(), Is.EqualTo(2), components.ToString(Formatting.None));
        }

        /// <summary>Root/A/Slot_1, Root/B/C/Slot_2, Other/Slot_3 and RootX/Slot_4 (a sibling whose name starts with "Root").</summary>
        private static void BuildSlotTree()
        {
            var root = new GameObject("Root");
            Child("Slot_1", Child("A", root));
            Child("Slot_2", Child("C", Child("B", root)));
            Child("Slot_3", new GameObject("Other"));
            Child("Slot_4", new GameObject("RootX"));
            GameObjectFinder.InvalidateCache();
        }

        private static GameObject Child(string name, GameObject parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
