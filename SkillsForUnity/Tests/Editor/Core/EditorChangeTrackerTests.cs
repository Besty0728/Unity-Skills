using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class EditorChangeTrackerTests
    {
        private string _tempPath;
        private string _savedOverride;

        [SetUp]
        public void SetUp()
        {
            _savedOverride = EditorChangeTrackerService.OverrideLogPathForTests;
            _tempPath = Path.Combine(Path.GetTempPath(), "UnitySkillsEditorChanges_" + Guid.NewGuid().ToString("N") + ".jsonl");
            EditorChangeTrackerService.OverrideLogPathForTests = _tempPath;
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
        }

        [TearDown]
        public void TearDown()
        {
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
            EditorChangeTrackerService.OverrideLogPathForTests = _savedOverride;
        }

        [Test]
        public void ReadChanges_ReturnsHistoryAndCursor()
        {
            EditorChangeTrackerService.PublishForTests("file_changes", "editor",
                new JObject { ["imported"] = new JArray("Assets/Player.cs") });

            var result = ToJson(EditorSkills.EditorGetChanges());

            Assert.IsTrue(result.Value<bool>("success"));
            Assert.IsTrue(result.Value<bool>("hasChanges"));
            Assert.AreEqual(1, result.Value<long>("cursor"));
            Assert.AreEqual("Assets/Player.cs", result["changes"]?[0]?["payload"]?["imported"]?[0]?.ToString());
        }

        [Test]
        public void ReadChanges_FiltersTypeAndSource()
        {
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.PublishForTests("file_changes", "editor");
            EditorChangeTrackerService.PublishForTests("scene_saved", "editor");

            var result = ToJson(EditorSkills.EditorGetChanges(types: "scene", source: "manual"));
            var changes = (JArray)result["changes"];

            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual("scene_saved", changes[0]?["type"]?.ToString());
            Assert.AreEqual(3, result.Value<long>("cursor"));
        }

        [Test]
        public void ReadChanges_LimitKeepsNewestAndReportsTruncation()
        {
            for (int i = 0; i < 5; i++)
                EditorChangeTrackerService.PublishForTests("file_changes", "editor", new JObject { ["index"] = i });

            var result = ToJson(EditorSkills.EditorGetChanges(limit: 2));
            var changes = (JArray)result["changes"];

            Assert.IsTrue(result.Value<bool>("truncated"));
            Assert.AreEqual(2, changes.Count);
            Assert.AreEqual(4, changes[0]?["seq"]?.Value<int>());
            Assert.AreEqual(5, changes[1]?["seq"]?.Value<int>());
        }

        [Test]
        public void Journal_ReloadsFromDiskAcrossDomainStyleReset()
        {
            EditorChangeTrackerService.PublishForTests("file_changes", "editor",
                new JObject { ["deleted"] = new JArray("Assets/Old.cs") });

            EditorChangeTrackerService.ReloadForTests();
            var result = ToJson(EditorSkills.EditorGetChanges());

            Assert.AreEqual(1, result.Value<long>("cursor"));
            Assert.AreEqual("Assets/Old.cs", result["changes"]?[0]?["payload"]?["deleted"]?[0]?.ToString());
            Assert.IsTrue(File.ReadLines(_tempPath).Any());
        }

        [Test]
        public void ReadChanges_ReportsDroppedWhenRetentionWasExceeded()
        {
            for (int i = 0; i <= EditorChangeTrackerService.BufferCapacity; i++)
                EditorChangeTrackerService.PublishForTests("file_changes", "editor");

            var result = ToJson(EditorSkills.EditorGetChanges(since: 0, limit: 1));

            Assert.IsTrue(result.Value<bool>("dropped"));
            Assert.AreEqual(2, result.Value<long>("oldestSeq"));
            Assert.AreEqual(EditorChangeTrackerService.BufferCapacity + 1, result.Value<long>("cursor"));

            EditorChangeTrackerService.ReloadForTests();
            var reloaded = ToJson(EditorSkills.EditorGetChanges(since: 0, limit: 1));
            Assert.AreEqual(2, reloaded.Value<long>("oldestSeq"));
            Assert.AreEqual(EditorChangeTrackerService.BufferCapacity + 1, reloaded.Value<long>("cursor"));
        }

        [Test]
        public void ReadChanges_RejectsUnknownFilters()
        {
            var typeError = ToJson(EditorSkills.EditorGetChanges(types: "prefab_magic"));
            var sourceError = ToJson(EditorSkills.EditorGetChanges(source: "external_only"));

            StringAssert.Contains("Unknown change type", typeError.Value<string>("error"));
            StringAssert.Contains("Unknown source", sourceError.Value<string>("error"));
        }

        private static JObject ToJson(object value)
        {
            return JObject.Parse(JsonConvert.SerializeObject(value));
        }
    }
}
