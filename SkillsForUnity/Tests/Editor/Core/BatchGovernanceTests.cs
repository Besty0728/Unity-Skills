using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class BatchGovernanceTests
    {
        private static JObject ToJObject(object result)
        {
            return JObject.Parse(JsonConvert.SerializeObject(result));
        }

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void BatchQueryGameObjects_FiltersByName()
        {
            new GameObject("PlayerRoot");
            new GameObject("EnemyRoot");
            GameObjectFinder.InvalidateCache();

            var result = BatchSkills.BatchQueryGameObjects("{\"name\":\"Player\",\"includeInactive\":true}");
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.AreEqual(1, json["count"]?.Value<int>());
            Assert.AreEqual("PlayerRoot", json["objects"]?[0]?["name"]?.ToString());
        }

        [Test]
        public void BatchPreviewRename_ThenExecuteSync_RenamesObjectsAndCreatesReport()
        {
            new GameObject("CubeA");
            new GameObject("CubeB");
            GameObjectFinder.InvalidateCache();

            var preview = ToJObject(BatchSkills.BatchPreviewRename("{\"name\":\"Cube\",\"includeInactive\":true}", mode: "prefix", prefix: "Renamed_"));
            var token = preview["confirmToken"]?.ToString();
            Assert.IsNotNull(token);
            Assert.AreEqual(2, preview["executableCount"]?.Value<int>());

            var execution = ToJObject(BatchSkills.BatchExecute(token, runAsync: false, chunkSize: 10));
            Assert.AreEqual("completed", execution["status"]?.ToString());
            Assert.IsNotNull(execution["reportId"]?.ToString());
            Assert.IsNotNull(GameObject.Find("Renamed_CubeA"));
            Assert.IsNotNull(GameObject.Find("Renamed_CubeB"));
        }

        [Test]
        public void BatchPreviewSetProperty_WhenAlreadyAtTargetValue_ReturnsSkip()
        {
            var go = new GameObject("Main Light");
            var light = go.AddComponent<Light>();
            light.intensity = 1f;
            GameObjectFinder.InvalidateCache();

            var preview = ToJObject(BatchSkills.BatchPreviewSetProperty(
                "{\"name\":\"Main Light\",\"includeInactive\":true}",
                componentType: "Light",
                propertyName: "intensity",
                value: "1"));

            Assert.AreEqual(0, preview["executableCount"]?.Value<int>());
            Assert.AreEqual(1, preview["skipCount"]?.Value<int>());
            Assert.AreEqual("already_target_value", preview["skipReasons"]?[0]?["reason"]?.ToString());
        }

        [Test]
        public void BatchCleanupTempObjects_AsyncJobCompletes()
        {
            new GameObject("Temp_Helper_1");
            new GameObject("Temp_Helper_2");
            GameObjectFinder.InvalidateCache();

            var preview = ToJObject(BatchSkills.BatchCleanupTempObjects("{\"includeInactive\":true}"));
            var token = preview["confirmToken"]?.ToString();
            Assert.IsNotNull(token);
            Assert.AreEqual(2, preview["executableCount"]?.Value<int>());

            var accepted = ToJObject(BatchSkills.BatchExecute(token, runAsync: true, chunkSize: 1));
            var jobId = accepted["jobId"]?.ToString();
            Assert.AreEqual("accepted", accepted["status"]?.ToString());
            Assert.IsNotNull(jobId);

            var waited = ToJObject(BatchSkills.JobWait(jobId, 5000));
            Assert.AreEqual("completed", waited["status"]?.ToString());
            Assert.IsNotNull(waited["reportId"]?.ToString());
            Assert.IsNull(GameObject.Find("Temp_Helper_1"));
            Assert.IsNull(GameObject.Find("Temp_Helper_2"));
        }

        [Test]
        public void BatchQueryGameObjects_FiltersByNamePattern_Regex()
        {
            new GameObject("Cube_001");
            new GameObject("Cube_002");
            new GameObject("Sphere_001");
            GameObjectFinder.InvalidateCache();

            var result = BatchSkills.BatchQueryGameObjects("{\"namePattern\":\"^Cube_\\\\d+$\",\"includeInactive\":true}");
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.AreEqual(2, json["count"]?.Value<int>());
        }

        [Test]
        public void BatchQueryGameObjects_FiltersByIsStatic()
        {
            var staticGo = new GameObject("StaticObj");
            staticGo.isStatic = true;
            var dynamicGo = new GameObject("DynamicObj");
            dynamicGo.isStatic = false;
            GameObjectFinder.InvalidateCache();

            var result = BatchSkills.BatchQueryGameObjects("{\"isStatic\":true,\"includeInactive\":true}");
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.AreEqual(1, json["count"]?.Value<int>());
            Assert.AreEqual("StaticObj", json["objects"]?[0]?["name"]?.ToString());
        }

        [Test]
        public void BatchQueryAssets_FiltersByTypeAndFolder()
        {
            var result = BatchSkills.BatchQueryAssets(typeFilter: "t:Script", folder: "Assets", maxResults: 5);
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.IsTrue(json["count"]?.Value<int>() >= 0);
        }

        [Test]
        public void BatchRetryFailed_WithNoFailedItems_ReturnsZeroCount()
        {
            // Create a mock report with no failed items by running a successful batch
            new GameObject("RetryTestObj");
            GameObjectFinder.InvalidateCache();

            var preview = ToJObject(BatchSkills.BatchPreviewRename(
                "{\"name\":\"RetryTestObj\",\"includeInactive\":true}", mode: "prefix", prefix: "X_"));
            var token = preview["confirmToken"]?.ToString();
            var exec = ToJObject(BatchSkills.BatchExecute(token, runAsync: false, chunkSize: 100));
            var reportId = exec["reportId"]?.ToString();
            Assert.IsNotNull(reportId);

            // Retry should find 0 failed items
            var retry = ToJObject(BatchSkills.BatchRetryFailed(reportId));
            Assert.AreEqual(0, retry["retryCount"]?.Value<int>());
        }

        [Test]
        public void BatchRetryFailed_ReplaysStoredOperationContext()
        {
            var go = new GameObject("RetryLight");
            var light = go.AddComponent<Light>();
            light.intensity = 1f;
            GameObjectFinder.InvalidateCache();

            var reportId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var report = new BatchReportRecord
            {
                reportId = reportId,
                kind = "set_property",
                status = "completed",
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                query = new BatchTargetQuery
                {
                    name = "RetryLight",
                    includeInactive = true
                },
                operation = new Dictionary<string, object>
                {
                    ["componentType"] = "Light",
                    ["propertyName"] = "intensity",
                    ["value"] = "2"
                }
            };

            report.items.Add(new BatchReportItemRecord
            {
                targetName = go.name,
                targetPath = GameObjectFinder.GetCachedPath(go),
                instanceId = go.GetEntityId().GetHashCode(),
                action = "set_property",
                status = "failed",
                before = "1",
                after = "2",
                reason = "mock_failure"
            });

            BatchPersistence.UpsertReport(report);

            var retry = ToJObject(BatchSkills.BatchRetryFailed(reportId, runAsync: false, chunkSize: 10));

            Assert.AreEqual("completed", retry["status"]?.ToString());
            Assert.AreEqual(1, retry["retryCount"]?.Value<int>());
            Assert.AreEqual(2f, go.GetComponent<Light>().intensity);
        }

        [Test]
        public void TestSmokeSkills_ForTestCategory_ReturnsAggregateSummary()
        {
            var result = TestSkills.TestSmokeSkills(
                category: "Test",
                executeReadOnly: false,
                includeMutating: false,
                limit: 20,
                runAsync: false);
            var json = ToJObject(result);

            Assert.IsTrue(json["success"]?.Value<bool>() ?? false);
            Assert.Greater(json["totalSkills"]?.Value<int>() ?? 0, 0);
            Assert.AreEqual(0, json["failureCount"]?.Value<int>());
            Assert.IsNotNull(json["results"]);
        }
    }
}
