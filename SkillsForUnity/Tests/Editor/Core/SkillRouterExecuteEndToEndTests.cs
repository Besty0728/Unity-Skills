using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// End-to-end coverage for SkillRouter.Execute called directly (no HTTP layer, EditMode only):
    /// a genuinely read-only skill executes normally regardless of operating mode; an unknown
    /// parameter is rejected before the mode gate even runs; and the Approval-mode MODE_RESTRICTED
    /// path actually blocks a FullAuto skill's side effect rather than just returning an error
    /// while still mutating the scene.
    ///
    /// Never assumes Bypass mode or any pre-existing scene/asset — every test sets
    /// SkillsModeManager.CurrentMode explicitly and works against a fresh empty scene.
    /// </summary>
    [TestFixture]
    public class SkillRouterExecuteEndToEndTests
    {
        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";

        private bool _hadMode;
        private string _savedMode;
        private bool _hadPanelApproval;
        private bool _savedPanelApproval;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _hadMode = EditorPrefs.HasKey(PrefKeyMode);
            _savedMode = EditorPrefs.GetString(PrefKeyMode, string.Empty);
            _hadPanelApproval = EditorPrefs.HasKey(PrefKeyPanelApproval);
            _savedPanelApproval = EditorPrefs.GetBool(PrefKeyPanelApproval, false);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_hadMode) EditorPrefs.SetString(PrefKeyMode, _savedMode);
            else EditorPrefs.DeleteKey(PrefKeyMode);
            if (_hadPanelApproval) EditorPrefs.SetBool(PrefKeyPanelApproval, _savedPanelApproval);
            else EditorPrefs.DeleteKey(PrefKeyPanelApproval);
        }

        [SetUp]
        public void SetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsAuditLog.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
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
    }
}

// Producer:Betsy
