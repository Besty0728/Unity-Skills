using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 M9 follow-up. Before this fix, animator_add_state/animator_add_transition reached
    /// AssetDatabase/AnimatorStateMachine calls with an unvalidated controllerPath/stateName/fromState/toState
    /// (an omitted one failed as an unclassified internal error, not a clean response), and ui_create_batch's
    /// unknown-type case threw a raw Exception instead of the structured error every sibling validator in the
    /// same file returns. Both now match their siblings: MISSING_PARAM / SEMANTIC_INVALID, never a throw.
    /// </summary>
    [TestFixture]
    public class AnimatorAndUiBatchGuardTests
    {
        private const string ProbeFolder = "Assets/__UnitySkillsAnimatorGuardProbe__";

        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
        }

        // ---------- animator_add_state / animator_add_transition: missing required params ----------

        [Test]
        public void AnimatorAddState_MissingControllerPath_IsMissingParam()
        {
            var json = ToJson(AnimatorSkills.AnimatorAddState(null, "Idle"));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("controllerPath"));
        }

        [Test]
        public void AnimatorAddState_MissingStateName_IsMissingParam()
        {
            // SafePath only checks the string's shape, not that the asset exists, so a plausible path is enough
            // to clear the controllerPath guard and reach the stateName guard under test here.
            var json = ToJson(AnimatorSkills.AnimatorAddState("Assets/NotYetCreated.controller", null));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("stateName"));
        }

        [Test]
        public void AnimatorAddTransition_MissingControllerPath_IsMissingParam()
        {
            var json = ToJson(AnimatorSkills.AnimatorAddTransition(null, "A", "B"));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("controllerPath"));
        }

        [TestCase(null, "B", "fromState")]
        [TestCase("A", null, "toState")]
        public void AnimatorAddTransition_MissingFromOrToState_IsMissingParam(string fromState, string toState, string missingParam)
        {
            var json = ToJson(AnimatorSkills.AnimatorAddTransition("Assets/NotYetCreated.controller", fromState, toState));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain(missingParam));
        }

        // ---------- animator_add_state / animator_add_transition: valid calls still work ----------

        [Test]
        public void AnimatorAddState_ValidCall_StillSucceeds()
        {
            var path = CreateProbeController("StateProbe");
            var json = ToJson(AnimatorSkills.AnimatorAddState(path, "Idle"));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["stateName"]?.ToString(), Is.EqualTo("Idle"));
        }

        [Test]
        public void AnimatorAddTransition_ValidCall_StillSucceeds()
        {
            var path = CreateProbeController("TransitionProbe");
            AnimatorSkills.AnimatorAddState(path, "A");
            AnimatorSkills.AnimatorAddState(path, "B");

            var json = ToJson(AnimatorSkills.AnimatorAddTransition(path, "A", "B"));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["from"]?.ToString(), Is.EqualTo("A"));
            Assert.That(json["to"]?.ToString(), Is.EqualTo("B"));
        }

        // ---------- ui_create_batch: unknown type is a structured per-item error, not a thrown exception ----------

        [Test]
        public void UICreateBatch_UnknownType_ReturnsStructuredErrorNotAThrow()
        {
            var json = ToJson(UISkills.UICreateBatch("[{\"type\":\"bogus\"}]"));

            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            var item = (JObject)json["results"]?[0];
            Assert.That(item["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(item["parameter"]?.ToString(), Is.EqualTo("type"));
            Assert.That(item["target"]?.ToString(), Is.EqualTo("bogus"));
            Assert.That(item["validValues"]?.ToObject<string[]>(), Does.Contain("canvas"));
        }

        [Test]
        public void UICreateBatch_ValidTypes_StillSucceed()
        {
            var json = ToJson(UISkills.UICreateBatch("[{\"type\":\"canvas\"},{\"type\":\"button\"}]"));

            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(2), json.ToString(Formatting.None));
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(0));
        }

        private static string CreateProbeController(string name)
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.CreateFolder("Assets", "__UnitySkillsAnimatorGuardProbe__");
            var path = $"{ProbeFolder}/{name}.controller";
            AnimatorController.CreateAnimatorControllerAtPath(path);
            return path;
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
