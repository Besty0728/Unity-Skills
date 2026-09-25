using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// End-to-end coverage calling SkillRouter.Execute directly (not through the HTTP layer, EditMode only):
    /// a genuinely read-only skill executes normally in any operating mode; unknown parameters are rejected before
    /// the mode gate is even reached; Approval mode's MODE_RESTRICTED path must actually block a FullAuto skill's
    /// side effects, rather than returning an error while still mutating the scene anyway.
    ///
    /// Never assumes the current mode is Bypass, nor that any pre-existing scene/asset exists - every test case
    /// explicitly sets SkillsModeManager.CurrentMode and runs on a brand-new empty scene.
    /// </summary>
    [TestFixture]
    public class SkillRouterExecuteEndToEndTests
    {
        private const string PaginationAssetPrefix = "Assets/UnitySkillsPaginationProbe";

        private ModePreferenceSnapshot _modePreferences;
        private SurfaceProfileKind _savedProfile;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _modePreferences = ModePreferenceSnapshot.Capture();

        [OneTimeTearDown]
        public void OneTimeTearDown() => _modePreferences.Restore();

        [SetUp]
        public void SetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsAuditLog.ResetForTests();
            // The mode-gate assertions below drive gameobject_create, which the Guide / NoSceneAuthoring
            // profiles withdraw before the mode gate is ever consulted (SURFACE_EXCLUDED instead of
            // MODE_RESTRICTED). Pin Full so the tests see the gate they're written against; the pref is
            // global to the machine, so restore it in TearDown.
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            for (var i = 0; i < 3; i++)
                AssetDatabase.DeleteAsset($"{PaginationAssetPrefix}{i}.txt");
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void Execute_AssetFind_PagesNestedAssetsAndPreservesMetadata()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllText($"{PaginationAssetPrefix}{i}.txt", i.ToString());
                AssetDatabase.ImportAsset($"{PaginationAssetPrefix}{i}.txt");
            }

            var full = JObject.Parse(SkillRouter.Execute("asset_find",
                "{\"searchFilter\":\"UnitySkillsPaginationProbe\",\"limit\":10,\"verbose\":false}"));
            var page = JObject.Parse(SkillRouter.Execute("asset_find",
                "{\"searchFilter\":\"UnitySkillsPaginationProbe\",\"limit\":10,\"pageOffset\":1,\"pageLimit\":1,\"verbose\":false}"));

            Assert.That(page["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(page["result"]?["count"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(page["result"]?["totalFound"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(page["result"]?["assets"], Has.Count.EqualTo(1));
            Assert.That(page["result"]?["assets"]?[0]?["path"]?.ToString(),
                Is.EqualTo(full["result"]?["assets"]?[1]?["path"]?.ToString()));
            Assert.That(page["result"]?["offset"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(page["result"]?["limit"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(page["result"]?["hint"]?.ToString(), Does.Contain("pageOffset=2"));
        }

        [Test]
        public void Execute_SummaryAutoTruncateOff_LargeNestedArrayPassesThrough()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            bool saved = SkillRouter.SummaryAutoTruncate;
            try
            {
                SkillRouter.SummaryAutoTruncate = false;

                var response = JObject.Parse(SkillRouter.Execute("asset_find",
                    "{\"searchFilter\":\"\",\"limit\":15,\"verbose\":false}"));

                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
                Assert.That(response["result"]?["assets"], Has.Count.EqualTo(15),
                    "With auto-truncation off, the skill's full result list must pass through.");
                Assert.That(response["result"]?["isTruncated"], Is.Null);
            }
            finally
            {
                SkillRouter.SummaryAutoTruncate = saved;
            }
        }

        [Test]
        public void Execute_SummaryAutoTruncateOn_LargeNestedArrayReturnsFirstPage()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            bool saved = SkillRouter.SummaryAutoTruncate;
            int savedPage = SkillRouter.SummaryPageSize;
            try
            {
                SkillRouter.SummaryAutoTruncate = true;
                SkillRouter.SummaryPageSize = 5;

                var response = JObject.Parse(SkillRouter.Execute("asset_find",
                    "{\"searchFilter\":\"\",\"limit\":15,\"verbose\":false}"));

                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
                Assert.That(response["result"]?["isTruncated"]?.Value<bool>(), Is.True);
                Assert.That(response["result"]?["assets"], Has.Count.EqualTo(5));
                Assert.That(response["result"]?["totalCount"]?.Value<int>(), Is.EqualTo(15));
                Assert.That(response["result"]?["showing"]?.Value<int>(), Is.EqualTo(5));
                Assert.That(response["result"]?["hint"]?.ToString(), Does.Contain("pageOffset=5"));
            }
            finally
            {
                SkillRouter.SummaryAutoTruncate = saved;
                SkillRouter.SummaryPageSize = savedPage;
            }
        }

        [Test]
        public void Execute_SummaryAutoTruncateOn_ArrayOverPageSizeIsTruncated()
        {
            // The truncation trigger follows the configurable SummaryPageSize rather than a hardcoded 10:
            // with PageSize=5, a 7-item array must already be over the threshold.
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            bool saved = SkillRouter.SummaryAutoTruncate;
            int savedPage = SkillRouter.SummaryPageSize;
            try
            {
                SkillRouter.SummaryAutoTruncate = true;
                SkillRouter.SummaryPageSize = 5;

                var response = JObject.Parse(SkillRouter.Execute("asset_find",
                    "{\"searchFilter\":\"\",\"limit\":7,\"verbose\":false}"));

                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
                Assert.That(response["result"]?["isTruncated"]?.Value<bool>(), Is.True);
                Assert.That(response["result"]?["assets"], Has.Count.EqualTo(5));
                Assert.That(response["result"]?["totalCount"]?.Value<int>(), Is.EqualTo(7));
            }
            finally
            {
                SkillRouter.SummaryAutoTruncate = saved;
                SkillRouter.SummaryPageSize = savedPage;
            }
        }

        [Test]
        public void Execute_SummaryAutoTruncateOn_ArrayUnderLargerPageSizePassesThrough()
        {
            // With PageSize=20, a 12-item array is below the threshold and must not be truncated at all.
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            bool saved = SkillRouter.SummaryAutoTruncate;
            int savedPage = SkillRouter.SummaryPageSize;
            try
            {
                SkillRouter.SummaryAutoTruncate = true;
                SkillRouter.SummaryPageSize = 20;

                var response = JObject.Parse(SkillRouter.Execute("asset_find",
                    "{\"searchFilter\":\"\",\"limit\":12,\"verbose\":false}"));

                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"));
                Assert.That(response["result"]?["assets"], Has.Count.EqualTo(12));
                Assert.That(response["result"]?["isTruncated"], Is.Null);
            }
            finally
            {
                SkillRouter.SummaryAutoTruncate = saved;
                SkillRouter.SummaryPageSize = savedPage;
            }
        }

        [Test]
        public void Execute_ReadOnlySemiAutoSkill_SucceedsEvenUnderApprovalMode()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            string response = SkillRouter.Execute("scene_get_info", "{}");
            var json = JObject.Parse(response);

            Assert.That(json["status"]?.ToString(), Is.EqualTo("success"));
            Assert.That(json["result"]?["sceneName"], Is.Not.Null);
        }

        [Test]
        public void Execute_UnknownParameter_RejectedBeforeModeGate()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            string response = SkillRouter.Execute("scene_get_info", "{\"bogusParam\":1}");
            var json = JObject.Parse(response);

            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"));
        }

        [Test]
        public void Execute_ApprovalMode_FullAutoSkill_ReturnsModeRestrictedAndDoesNotRun()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;
            const string objectName = "ModeRestrictedProbeCube";
            Assert.That(GameObject.Find(objectName), Is.Null, "Precondition: object must not already exist.");

            string response = SkillRouter.Execute("gameobject_create", "{\"name\":\"" + objectName + "\"}");
            var json = JObject.Parse(response);

            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MODE_RESTRICTED"));
            Assert.That(json["details"]?["grantRequestToken"]?.ToString(), Is.Not.Null.And.Not.Empty);
            Assert.That(GameObject.Find(objectName), Is.Null,
                "A MODE_RESTRICTED response must mean the skill never actually ran.");
        }

        [Test]
        public void Execute_AssetCreateFolderBatchForExistingFolder_ReturnsStructuredRootError()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            var json = JObject.Parse(SkillRouter.Execute("asset_create_folder_batch",
                "{\"items\":[{\"folderPath\":\"Assets\"}]}"));

            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"));
            Assert.That(json["error"]?.ToString(), Is.Not.Empty);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(json["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.RetryFixAndRetry));
            Assert.That(json["suggestedFixes"], Has.Count.GreaterThan(0));
        }
    }
}

// Producer:Betsy
