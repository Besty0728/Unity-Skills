using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// gameobject_find, scene_find_objects, batch_query_*, batch_query_assets and the batch previews used to stop at
    /// their limit without saying so, so a capped list read as the complete answer (P3, P11). count keeps its meaning
    /// (what came back, or what was found under the cap); the match total before the cap (totalMatched) and a
    /// truncated flag now sit beside it. The find skills deliberately do not echo limit or call the total totalCount:
    /// the router's paging wrapper writes its own totalCount / limit over a result that has them.
    /// </summary>
    [TestFixture]
    public class QueryTruncationTests
    {
        private const string Probe = "QT_Probe";
        private const int ProbeCount = 7;
        private const string ScriptFolder = "Packages/com.besty.unity-skills/Editor/Skills";

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
            for (var i = 0; i < ProbeCount; i++)
                new GameObject($"{Probe}_{i}");
            new GameObject("QT_Decoy");
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

        // ---------- gameobject_find ----------

        [Test]
        public void GameObjectFind_LimitBelowMatches_ReportsTheTotalAndTruncation()
        {
            var json = ToJson(GameObjectSkills.GameObjectFind(name: Probe, limit: 3));

            AssertFindShape(json, count: 3, totalMatched: ProbeCount, truncated: true);
        }

        [Test]
        public void GameObjectFind_LimitAboveMatches_IsNotTruncated()
        {
            var json = ToJson(GameObjectSkills.GameObjectFind(name: Probe));

            AssertFindShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
        }

        [Test]
        public void GameObjectFind_LimitEqualToMatches_IsNotTruncated()
        {
            var json = ToJson(GameObjectSkills.GameObjectFind(name: Probe, limit: ProbeCount));

            AssertFindShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
        }

        [Test]
        public void GameObjectFind_DefaultLimit_CutsAtFiftyAndSaysSo()
        {
            for (var i = 0; i < 60; i++)
                new GameObject($"QT_Bulk_{i}");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(GameObjectSkills.GameObjectFind(name: "QT_Bulk"));

            AssertFindShape(json, count: 50, totalMatched: 60, truncated: true);
        }

        [Test]
        public void GameObjectFind_NoMatch_IsZeroAndNotTruncated()
        {
            var json = ToJson(GameObjectSkills.GameObjectFind(name: "QT_NoSuchObject_7f3"));

            AssertFindShape(json, count: 0, totalMatched: 0, truncated: false);
        }

        [Test]
        public void GameObjectFind_ThroughTheRouter_KeepsTheKeys()
        {
            var result = ExecuteResult("gameobject_find", new JObject { ["name"] = Probe, ["limit"] = 2 });

            AssertFindShape(result, count: 2, totalMatched: ProbeCount, truncated: true);
        }

        [Test]
        public void GameObjectFind_PagedByTheRouter_KeepsTheSkillsOwnTotalAndFlag()
        {
            var result = ExecuteResult("gameobject_find",
                new JObject { ["name"] = Probe, ["limit"] = 3, ["pageOffset"] = 0, ["pageLimit"] = 2 });

            Assert.That(result["totalMatched"]?.Value<int>(), Is.EqualTo(ProbeCount), "The router's totalCount describes its page; the skill's total has its own key.");
            Assert.That(result["truncated"]?.Value<bool>(), Is.True, "limit cut the skill's own list; paging that list must not hide it.");
            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(result["objects"], Has.Count.EqualTo(2), "pageLimit pages the objects the skill returned.");
        }

        // ---------- scene_find_objects ----------

        [Test]
        public void SceneFindObjects_LimitBelowMatches_ReportsTheTotalAndTruncation()
        {
            var json = ToJson(SceneSkills.SceneFindObjects(namePattern: Probe, limit: 4));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            AssertFindShape(json, count: 4, totalMatched: ProbeCount, truncated: true);
        }

        [Test]
        public void SceneFindObjects_LimitAboveMatches_IsNotTruncated()
        {
            var json = ToJson(SceneSkills.SceneFindObjects(namePattern: Probe));

            AssertFindShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
        }

        [Test]
        public void SceneFindObjects_LimitEqualToMatches_IsNotTruncated()
        {
            var json = ToJson(SceneSkills.SceneFindObjects(namePattern: Probe, limit: ProbeCount));

            AssertFindShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
        }

        [Test]
        public void SceneFindObjects_DefaultLimit_CutsAtFiftyAndSaysSo()
        {
            for (var i = 0; i < 60; i++)
                new GameObject($"QT_Bulk_{i}");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(SceneSkills.SceneFindObjects(namePattern: "QT_Bulk"));

            AssertFindShape(json, count: 50, totalMatched: 60, truncated: true);
        }

        // ---------- batch_query_gameobjects / batch_query_components ----------

        [Test]
        public void BatchQueryGameObjects_LimitBelowMatches_ReportsTotalMatchedAndTruncated()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"" + Probe + "\",\"limit\":3}"));

            AssertBatchShape(json, count: 3, totalMatched: ProbeCount, truncated: true);
            Assert.That(json["summary"]?.ToString(), Does.Contain("3 of 7").And.Contain("limit"), json.ToString(Formatting.None));
        }

        [Test]
        public void BatchQueryGameObjects_NoCut_KeepsTheOriginalSummary()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"" + Probe + "\"}"));

            AssertBatchShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
            Assert.That(json["summary"]?.ToString(), Is.EqualTo("Matched 7 GameObjects."));
        }

        [Test]
        public void BatchQueryGameObjects_LimitEqualToMatches_IsNotTruncated()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"" + Probe + "\",\"limit\":7}"));

            AssertBatchShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
        }

        [Test]
        public void BatchQueryGameObjects_DefaultLimit_CutsAtFiveHundredAndSaysSo()
        {
            for (var i = 0; i < 520; i++)
                new GameObject($"QT_Bulk_{i}");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"QT_Bulk\"}", sampleLimit: 5));

            AssertBatchShape(json, count: 500, totalMatched: 520, truncated: true);
            Assert.That(json["objects"], Has.Count.EqualTo(5));
        }

        [Test]
        public void BatchQueryGameObjects_SampleLimitBelowCount_IsNotATruncation()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"" + Probe + "\"}", sampleLimit: 2));

            AssertBatchShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
            Assert.That(json["objects"], Has.Count.EqualTo(2), "objects is a sample of at most sampleLimit; truncated only reports queryJson.limit.");
        }

        [Test]
        public void BatchQueryGameObjects_NoMatch_IsZeroAndNotTruncated()
        {
            var json = ToJson(BatchSkills.BatchQueryGameObjects("{\"name\":\"QT_NoSuchObject_7f3\"}"));

            AssertBatchShape(json, count: 0, totalMatched: 0, truncated: false);
        }

        [Test]
        public void BatchQueryComponents_LimitBelowMatches_ReportsTotalMatchedAndTruncated()
        {
            var json = ToJson(BatchSkills.BatchQueryComponents("{\"name\":\"" + Probe + "\",\"limit\":2}", componentType: "Transform"));

            AssertBatchShape(json, count: 2, totalMatched: ProbeCount, truncated: true);
            Assert.That(json["summary"]?.ToString(), Does.Contain("2 of 7").And.Contain("limit"), json.ToString(Formatting.None));
        }

        [Test]
        public void BatchQueryComponents_NoCut_KeepsTheOriginalSummary()
        {
            var json = ToJson(BatchSkills.BatchQueryComponents("{\"name\":\"" + Probe + "\"}", componentType: "Transform"));

            AssertBatchShape(json, count: ProbeCount, totalMatched: ProbeCount, truncated: false);
            Assert.That(json["summary"]?.ToString(), Is.EqualTo("Matched 7 objects with component filters."));
        }

        // ---------- batch_query_assets ----------

        [Test]
        public void QueryAssets_WithNamePattern_TotalMatchedCountsTheFilteredAssets()
        {
            var all = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^Batch", maxResults: 1000));
            var capped = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^Batch", maxResults: 2));
            var raw = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, maxResults: 2));

            var total = all["count"].Value<int>();
            Assert.That(total, Is.GreaterThan(2), all.ToString(Formatting.None));
            Assert.That(all["totalMatched"]?.Value<int>(), Is.EqualTo(total));
            Assert.That(all["truncated"]?.Value<bool>(), Is.False);
            Assert.That(all["summary"]?.ToString(), Does.Not.Contain("showing"), "A filtered-out asset is not a cut-off one.");

            Assert.That(capped["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(capped["totalMatched"]?.Value<int>(), Is.EqualTo(total), "Counted after the name filter, not the raw FindAssets hits.");
            Assert.That(capped["truncated"]?.Value<bool>(), Is.True);
            Assert.That(capped["summary"]?.ToString(), Does.Contain($"showing 2 of {total}"));

            Assert.That(raw["totalMatched"]?.Value<int>(), Is.GreaterThan(total), "Without the pattern every script in the folder is a match.");
        }

        [Test]
        public void QueryAssets_WithoutNamePattern_TotalMatchedIsTheHitCount()
        {
            var json = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, maxResults: 3));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(3), json.ToString(Formatting.None));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.GreaterThan(3));
            Assert.That(json["truncated"]?.Value<bool>(), Is.True);
            Assert.That(json["summary"]?.ToString(), Does.Contain("showing 3 of"));
        }

        [Test]
        public void QueryAssets_NoCut_KeepsTheOriginalSummary()
        {
            var json = ToJson(BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: ScriptFolder, namePattern: "^BatchSkills$"));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
            Assert.That(json["summary"]?.ToString(), Is.EqualTo("Found 1 assets."));
        }

        // ---------- batch previews and fixers ----------

        [Test]
        public void BatchPreviewRename_LimitBelowMatches_ReportsTheCut()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"" + Probe + "\",\"limit\":3}", mode: "prefix", prefix: "X_"));

            Assert.That(json["targetCount"]?.Value<int>(), Is.EqualTo(3), json.ToString(Formatting.None));
            Assert.That(json["executableCount"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(ProbeCount));
            Assert.That(json["truncated"]?.Value<bool>(), Is.True);
            Assert.That(json["summary"]?.ToString(), Does.StartWith("Will rename 3 of 3 matched objects.")
                .And.Contain("7 objects matched").And.Contain("4 are not in this preview"));
        }

        [Test]
        public void BatchPreviewRename_NoCut_KeepsTheOriginalSummary()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"" + Probe + "\"}", mode: "prefix", prefix: "X_"));

            Assert.That(json["targetCount"]?.Value<int>(), Is.EqualTo(ProbeCount), json.ToString(Formatting.None));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(ProbeCount));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
            Assert.That(json["summary"]?.ToString(), Is.EqualTo("Will rename 7 of 7 matched objects."));
        }

        [Test]
        public void BatchPreviewRename_LimitEqualToMatches_IsNotTruncated()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"" + Probe + "\",\"limit\":7}", mode: "prefix", prefix: "X_"));

            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(ProbeCount), json.ToString(Formatting.None));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void BatchPreview_StoresTheCutWithTheToken()
        {
            var json = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"" + Probe + "\",\"limit\":3}", mode: "prefix", prefix: "X_"));

            var stored = BatchPersistence.GetPreview(json["confirmToken"]?.ToString());
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored.truncated, Is.True);
            Assert.That(stored.totalMatched, Is.EqualTo(ProbeCount));
        }

        [Test]
        public void BatchPreview_TruncatedPreview_ExecutesOnlyThePreviewedObjects()
        {
            var preview = ToJson(BatchSkills.BatchPreviewRename("{\"name\":\"" + Probe + "\",\"limit\":3}", mode: "prefix", prefix: "X_"));
            var execution = ToJson(BatchSkills.BatchExecute(preview["confirmToken"]?.ToString(), runAsync: false, chunkSize: 10));
            Assert.That(execution["status"]?.ToString(), Is.EqualTo("completed"), execution.ToString(Formatting.None));

            GameObjectFinder.InvalidateCache();
            var names = GameObjectFinder.GetSceneObjects().Select(go => go.name).ToArray();
            Assert.That(names.Count(name => name.StartsWith("X_" + Probe)), Is.EqualTo(3));
            Assert.That(names.Count(name => name.StartsWith(Probe)), Is.EqualTo(ProbeCount - 3), "The objects past the cap keep their names.");
        }

        [Test]
        public void BatchCleanupTempObjects_OnlyExaminesLimitMatches_AndSaysSo()
        {
            // Every object is a temp one, so which objects the cap keeps does not matter, only how many.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (var i = 0; i < 5; i++)
                new GameObject($"Temp_Item_{i}");
            GameObjectFinder.InvalidateCache();

            var capped = ToJson(BatchSkills.BatchCleanupTempObjects("{\"limit\":3}"));
            var whole = ToJson(BatchSkills.BatchCleanupTempObjects());

            Assert.That(capped["executableCount"]?.Value<int>(), Is.EqualTo(3), capped.ToString(Formatting.None));
            Assert.That(capped["totalMatched"]?.Value<int>(), Is.EqualTo(5));
            Assert.That(capped["truncated"]?.Value<bool>(), Is.True);
            Assert.That(capped["summary"]?.ToString(), Does.Contain("5 objects matched").And.Contain("2 are not in this preview"));
            Assert.That(whole["executableCount"]?.Value<int>(), Is.EqualTo(5), whole.ToString(Formatting.None));
            Assert.That(whole["truncated"]?.Value<bool>(), Is.False);
        }

        // ---------- declared Outputs ----------

        [TestCase("gameobject_find", "{\"name\":\"" + Probe + "\"}", new[] { "count", "totalMatched", "truncated", "objects" })]
        [TestCase("scene_find_objects", "{\"namePattern\":\"" + Probe + "\"}", new[] { "count", "totalMatched", "truncated", "objects" })]
        [TestCase("batch_query_gameobjects", "{\"queryJson\":\"{\\\"name\\\":\\\"" + Probe + "\\\"}\"}", new[] { "count", "totalMatched", "truncated", "objects", "summary" })]
        [TestCase("batch_query_components", "{\"queryJson\":\"{\\\"name\\\":\\\"" + Probe + "\\\"}\"}", new[] { "count", "totalMatched", "truncated", "objects", "summary" })]
        [TestCase("batch_query_assets", "{\"typeFilter\":\"t:Script\",\"folder\":\"" + ScriptFolder + "\",\"maxResults\":2}", new[] { "count", "totalMatched", "truncated", "assets", "summary" })]
        [TestCase("batch_preview_rename", "{\"queryJson\":\"{\\\"name\\\":\\\"" + Probe + "\\\"}\",\"mode\":\"prefix\",\"prefix\":\"X_\"}", new[] { "targetCount", "totalMatched", "truncated", "sampleChanges" })]
        [TestCase("batch_cleanup_temp_objects", "{\"queryJson\":\"{\\\"name\\\":\\\"" + Probe + "\\\"}\"}", new[] { "targetCount", "totalMatched", "truncated", "sampleChanges" })]
        public void NewKeys_AreDeclaredAndReturned(string skill, string argsJson, string[] keys)
        {
            Assert.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);
            Assert.That(info.Outputs, Is.SupersetOf(keys), $"{skill} must declare the keys it now returns.");

            var result = ExecuteResult(skill, JObject.Parse(argsJson));

            foreach (var key in keys)
                Assert.That(result[key], Is.Not.Null, $"{skill} declares '{key}' but did not return it: {result.ToString(Formatting.None)}");
        }

        [TestCase("batch_preview_rename")]
        [TestCase("batch_preview_set_property")]
        [TestCase("batch_preview_replace_material")]
        [TestCase("batch_fix_missing_scripts")]
        [TestCase("batch_standardize_naming")]
        [TestCase("batch_set_render_layer")]
        [TestCase("batch_replace_material")]
        [TestCase("batch_cleanup_temp_objects")]
        public void EveryPreviewSkill_DeclaresTheCutAmongItsOutputs(string skill)
        {
            Assert.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);
            Assert.That(info.Outputs, Is.SupersetOf(new[] { "confirmToken", "targetCount", "totalMatched", "truncated" }));
        }

        // ---------- helpers ----------

        // limit and totalCount stay out on purpose: the router's paging wrapper writes both over a result that has them.
        private static void AssertFindShape(JToken json, int count, int totalMatched, bool truncated)
        {
            var text = json.ToString(Formatting.None);
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(count), text);
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(totalMatched), text);
            Assert.That(json["truncated"]?.Value<bool>(), Is.EqualTo(truncated), text);
            Assert.That(json["objects"], Has.Count.EqualTo(count), text);
            Assert.That(json["limit"], Is.Null, text);
            Assert.That(json["totalCount"], Is.Null, text);
        }

        private static void AssertBatchShape(JToken json, int count, int totalMatched, bool truncated)
        {
            var text = json.ToString(Formatting.None);
            Assert.That(json["success"]?.Value<bool>(), Is.True, text);
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(count), text);
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(totalMatched), text);
            Assert.That(json["truncated"]?.Value<bool>(), Is.EqualTo(truncated), text);
        }

        private static JToken ExecuteResult(string skill, JObject args)
        {
            var response = JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            return response["result"];
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
