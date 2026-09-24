using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 precision fix for CameraSkills (bugs.md B12): camera_set_transform's "instant" parameter
    /// used to be ignored (the 3-arg SceneView.LookAt it called is always animated), so a caller
    /// reading camera_get_info right after could see a mid-animation value. The 5-arg overload makes
    /// instant=true actually synchronous, and the response now reads pivot/rotation/size/orthographic
    /// back from the SceneView instead of only reporting a message.
    /// </summary>
    [TestFixture]
    public class CameraTransformReadBackTests
    {
        private const float Tolerance = 1e-2f;
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
            Assume.That(SceneView.lastActiveSceneView, Is.Not.Null, "No active Scene View in this test run.");
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void SetTransform_Instant_ReadsBackImmediately()
        {
            var json = ToJson(CameraSkills.CameraSetTransform(1, 2, 3, 30, 45, 0, size: 7, instant: true));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            AssertVector(json["pivot"], new Vector3(1, 2, 3), "pivot");
            Assert.That(json["size"]?.Value<float>(), Is.EqualTo(7f).Within(Tolerance));
            Assert.That(json["instant"]?.Value<bool>(), Is.True);

            var sv = SceneView.lastActiveSceneView;
            Assert.That(Vector3.Distance(sv.pivot, new Vector3(1, 2, 3)), Is.LessThan(Tolerance),
                "instant=true must apply the same frame the call returns, not animate toward it.");
        }

        [Test]
        public void SetTransform_NotInstant_ReportsAnimatingTargets()
        {
            var json = ToJson(CameraSkills.CameraSetTransform(4, 5, 6, 0, 0, 0, size: 3, instant: false));

            Assert.That(json["animating"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["instant"]?.Value<bool>(), Is.False);
            AssertVector(json["pivot"], new Vector3(4, 5, 6), "pivot");
            Assert.That(json["cameraPosition"], Is.Null);
        }

        [Test]
        public void SetTransform_KeepsOrthographicMode()
        {
            var sv = SceneView.lastActiveSceneView;
            var wasOrthographic = sv.orthographic;
            sv.orthographic = true;
            try
            {
                CameraSkills.CameraSetTransform(0, 0, 0, 0, 0, 0);
                Assert.That(sv.orthographic, Is.True);
            }
            finally
            {
                sv.orthographic = wasOrthographic;
            }
        }

        [Test]
        public void SetTransform_ThroughRouter_ReturnsDeclaredOutputs()
        {
            var response = JObject.Parse(SkillRouter.Execute("camera_set_transform",
                "{\"posX\":0,\"posY\":0,\"posZ\":0,\"rotX\":0,\"rotY\":0,\"rotZ\":0}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            var result = response["result"];
            foreach (var key in new[] { "message", "pivot", "rotation", "size", "orthographic" })
                Assert.That(result?[key], Is.Not.Null, $"missing declared output '{key}'");
        }

        [Test]
        public void SetTransform_CameraPosition_IsBehindThePivot()
        {
            var json = ToJson(CameraSkills.CameraSetTransform(0, 0, 0, 0, 0, 0, size: 5, instant: true));

            var pivot = ParseVector(json["pivot"]);
            var cameraPosition = ParseVector(json["cameraPosition"]);
            var sv = SceneView.lastActiveSceneView;

            Assert.That(Vector3.Distance(pivot, cameraPosition), Is.EqualTo(sv.cameraDistance).Within(Tolerance),
                json.ToString(Formatting.None));
        }

        [Test]
        public void LookAt_ReadsBackPivotRotationSize()
        {
            var json = ToJson(CameraSkills.CameraLookAt(2, 3, 4));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            AssertVector(json["pivot"], new Vector3(2, 3, 4), "pivot");
            Assert.That(json["rotation"], Is.Not.Null);
            Assert.That(json["size"], Is.Not.Null);
        }

        [Test]
        public void SetCullingMask_ReadsBackMaskAndLayers()
        {
            var go = new GameObject("RB_CullingCam");
            var cam = go.AddComponent<Camera>();

            var json = ToJson(CameraSkills.CameraSetCullingMask("Default, Water", name: go.name));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["cullingMask"]?.Value<int>(), Is.EqualTo(cam.cullingMask));
            Assert.That(json["layers"]?.Values<string>(), Does.Contain("Default").And.Contain("Water"));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        private static void AssertVector(JToken token, Vector3 expected, string label)
        {
            Assert.That(token, Is.Not.Null, $"{label} is missing.");
            var actual = ParseVector(token);
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"{label}: expected {expected}, got {actual}.");
        }

        private static Vector3 ParseVector(JToken token) =>
            new Vector3(token["x"].Value<float>(), token["y"].Value<float>(), token["z"].Value<float>());
    }
}

// Producer:Betsy
