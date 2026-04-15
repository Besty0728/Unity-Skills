using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class PerceptionSkillsTests
    {
        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void SceneAnalyze_ReturnsSuccessWithExpectedStructure()
        {
            var result = PerceptionSkills.SceneAnalyze();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(json["summary"]);
            Assert.IsNotNull(json["stats"]);
            Assert.IsNotNull(json["findings"]);
            Assert.IsNotNull(json["recommendations"]);
            Assert.IsNotNull(json["suggestedNextSkills"]);
        }

        [Test]
        public void SceneSummarize_CountsObjectsCorrectly()
        {
            new GameObject("TestObj1");
            new GameObject("TestObj2");
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneSummarize();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            // Default scene has Camera + Light, we added 2 more
            Assert.IsTrue(json["stats"]?["totalObjects"]?.Value<int>() >= 4);
        }

        [Test]
        public void SceneHealthCheck_DetectsMissingInfrastructure()
        {
            // Start with empty scene (no default objects)
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneHealthCheck();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(json["findings"]);
            // Empty scene should report missing camera at minimum
            var findings = json["findings"] as JArray;
            Assert.IsTrue(findings?.Count > 0);
        }

        [Test]
        public void SceneComponentStats_CountsComponentTypes()
        {
            var result = PerceptionSkills.SceneComponentStats();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(json["stats"]);
            Assert.IsNotNull(json["topComponents"]);
        }

        [Test]
        public void SceneFindHotspots_DetectsDeepHierarchy()
        {
            // Create a deep hierarchy
            var root = new GameObject("DeepRoot");
            var current = root;
            for (int i = 0; i < 10; i++)
            {
                var child = new GameObject($"Level{i}");
                child.transform.SetParent(current.transform);
                current = child;
            }
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneFindHotspots(deepHierarchyThreshold: 5);
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(json["hotspots"]);
            var hotspots = json["hotspots"] as JArray;
            Assert.IsTrue(hotspots?.Count > 0);
        }

        [Test]
        public void HierarchyDescribe_ReturnsNonEmptyTree()
        {
            var result = PerceptionSkills.HierarchyDescribe();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsNotNull(json["tree"] ?? json["hierarchy"]);
        }

        [Test]
        public void SceneTagLayerStats_ReportsUsedTags()
        {
            var result = PerceptionSkills.SceneTagLayerStats();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
        }

        [Test]
        public void ScenePerformanceHints_ReturnsActionableHints()
        {
            var result = PerceptionSkills.ScenePerformanceHints();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
        }

        [Test]
        public void SceneContractValidate_DefaultContract_ReportsMissingRoots()
        {
            // Empty scene should be missing default contract roots
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneContractValidate();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            var findings = json["findings"] as JArray;
            Assert.IsTrue(findings?.Count > 0, "Empty scene should have missing root findings");
        }

        [Test]
        public void SceneContractValidate_CustomRoots_OverridesDefault()
        {
            new GameObject("MyCustomRoot");
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneContractValidate(
                requiredRootsJson: "[\"MyCustomRoot\"]");
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
        }

        [Test]
        public void BuildSuggestedNextSkills_FiltersInvalidSkillReferences()
        {
            // scene_analyze calls BuildSuggestedNextSkills internally
            // All returned skills should be valid registered skills
            var result = PerceptionSkills.SceneAnalyze();
            var json = ToJObject(result);

            var suggestions = json["suggestedNextSkills"] as JArray;
            if (suggestions != null)
            {
                foreach (var s in suggestions)
                {
                    var skillName = s["skill"]?.ToString();
                    Assert.IsTrue(SkillRouter.HasSkill(skillName),
                        $"suggestedNextSkills contains invalid skill: {skillName}");
                }
            }
        }

        [Test]
        public void SceneDiff_WithoutSnapshot_CapturesCurrentState()
        {
            var result = PerceptionSkills.SceneDiff();
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.AreEqual("snapshot", json["mode"]?.ToString());
            Assert.IsNotNull(json["snapshot"]);
        }

        [Test]
        public void SceneDiff_DetectsAddedAndRemovedObjects()
        {
            var kept = new GameObject("KeepMe");
            var removed = new GameObject("RemoveMe");
            GameObjectFinder.InvalidateCache();

            var snapshotResult = ToJObject(PerceptionSkills.SceneDiff());
            var snapshotJson = snapshotResult["snapshot"]?.ToString(Formatting.None);

            UnityEngine.Object.DestroyImmediate(removed);
            new GameObject("AddedLater");
            GameObjectFinder.InvalidateCache();

            var diffResult = ToJObject(PerceptionSkills.SceneDiff(snapshotJson));

            Assert.IsTrue(diffResult["success"]?.Value<bool>() ?? false);
            Assert.AreEqual("diff", diffResult["mode"]?.ToString());
            Assert.IsTrue((diffResult["added"] as JArray)?.Any(item => item["name"]?.ToString() == "AddedLater") ?? false);
            Assert.IsTrue((diffResult["removed"] as JArray)?.Any(item => item["name"]?.ToString() == "RemoveMe") ?? false);
            Assert.IsNotNull(kept);
        }

        [Test]
        public void SceneDiff_DetectsModifiedTransform()
        {
            var go = new GameObject("Mover");
            go.transform.localPosition = new Vector3(1f, 2f, 3f);
            GameObjectFinder.InvalidateCache();

            var snapshotResult = ToJObject(PerceptionSkills.SceneDiff());
            var snapshotJson = snapshotResult["snapshot"]?.ToString(Formatting.None);

            go.transform.localPosition = new Vector3(4f, 5f, 6f);
            GameObjectFinder.InvalidateCache();

            var diffResult = ToJObject(PerceptionSkills.SceneDiff(snapshotJson));
            var modified = diffResult["modified"] as JArray;

            Assert.IsTrue(modified?.Any(item =>
                item["name"]?.ToString() == "Mover" &&
                (item["changes"] as JArray)?.Any(change => change?.ToString() == "position") == true) ?? false);
        }

        [Test]
        public void SceneDiff_SnapshotOnlyIncludesActiveSceneObjects()
        {
            var activeScene = SceneManager.GetActiveScene();
            var activeObject = new GameObject("ActiveSceneObject");

            var additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var additiveObject = new GameObject("AdditiveSceneObject");
            SceneManager.MoveGameObjectToScene(additiveObject, additiveScene);
            SceneManager.SetActiveScene(activeScene);
            GameObjectFinder.InvalidateCache();

            var result = PerceptionSkills.SceneDiff();
            var json = ToJObject(result);
            var snapshot = json["snapshot"] as JArray;

            Assert.IsTrue(snapshot?.Any(item => item["name"]?.ToString() == "ActiveSceneObject") ?? false);
            Assert.IsFalse(snapshot?.Any(item => item["name"]?.ToString() == "AdditiveSceneObject") ?? true);
            Assert.AreEqual(activeScene.name, json["sceneName"]?.ToString());
        }
    }
}
