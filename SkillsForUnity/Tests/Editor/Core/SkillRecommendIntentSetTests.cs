using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Regression set for /skills/recommend ranking, distilled from 520 headless-agent transcripts (doc-bench rounds 1–3):
    /// each intent is one an agent actually typed, the targets are the skills it went on to call. Every case must rank one
    /// acceptable target in the top three. Like <see cref="SkillRecommendGoldenTests"/> this pins "this intent finds this
    /// skill", never the full ordering.
    /// </summary>
    [TestFixture]
    public class SkillRecommendIntentSetTests
    {
        private SurfaceProfileKind _savedProfile;
        private bool _savedTelemetry;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedTelemetry = SkillTelemetryService.Enabled;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillTelemetryService.Enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillTelemetryService.Enabled = _savedTelemetry;
        }

        private static readonly (string intent, string[] targets)[] Cases =
        {
            ("set box collider center and size", new[] { "component_set_property", "component_set_property_batch" }),
            ("set box collider center size on gameobject", new[] { "component_set_property", "component_set_property_batch" }),
            ("box collider center size", new[] { "component_set_property", "component_get_properties" }),
            ("set boxcollider properties center size", new[] { "component_set_property" }),
            ("get box collider properties", new[] { "component_get_properties" }),
            ("read component properties of a gameobject", new[] { "component_get_properties", "component_list" }),
            ("set rigidbody mass and use gravity", new[] { "component_set_property", "component_set_property_batch" }),
            ("add rigidbody component to gameobject", new[] { "component_add" }),
            ("add component to gameobject and set field value", new[] { "component_add", "component_set_property" }),
            ("set public field value on a component", new[] { "component_set_property", "component_set_serialized_property" }),
            ("component set property", new[] { "component_set_property" }),
            ("set light color and intensity", new[] { "light_set_properties", "component_set_property" }),
            ("make point light warm orange intensity 3", new[] { "light_set_properties" }),
            ("get light color intensity", new[] { "light_get_info" }),
            ("assign material to renderer", new[] { "material_assign", "material_assign_batch" }),
            ("assign existing material asset to gameobject", new[] { "material_assign", "material_assign_batch" }),
            ("material color", new[] { "material_set_color" }),
            ("create monobehaviour script", new[] { "script_create", "script_create_batch" }),
            ("create a new MonoBehaviour script file", new[] { "script_create", "script_create_batch" }),
            ("add public field to script", new[] { "script_replace", "script_append" }),
            ("create empty gameobject with parent and position", new[] { "gameobject_create", "gameobject_create_batch" }),
            ("create cube under parent at position", new[] { "gameobject_create", "gameobject_create_batch" }),
            ("set gameobject world position and scale", new[] { "gameobject_set_transform", "gameobject_set_transform_batch" }),
            ("set transform world position of gameobject", new[] { "gameobject_set_transform", "gameobject_set_transform_batch" }),
            ("rename gameobject", new[] { "gameobject_rename", "gameobject_rename_batch" }),
            ("set gameobject inactive", new[] { "gameobject_set_active", "gameobject_set_active_batch" }),
            ("duplicate gameobject under parent", new[] { "gameobject_duplicate", "gameobject_duplicate_batch" }),
            ("find gameobject by name", new[] { "gameobject_find", "find_objects_by_name", "scene_find_objects" }),
            ("find child objects by name under parent", new[] { "gameobject_find", "find_objects_by_name", "gameobject_get_info", "scene_find_objects" }),
            ("get gameobject info transform and children", new[] { "gameobject_get_info" }),
            ("get scene hierarchy", new[] { "scene_get_hierarchy", "scene_get_info" }),
            ("create prefab", new[] { "prefab_create" }),
            ("run test", new[] { "test_run" }),
            ("list installed packages", new[] { "package_list" }),
            ("check if probuilder package is installed", new[] { "package_check", "package_list" }),
            ("set camera field of view", new[] { "camera_set_properties" }),
            ("read current camera properties inspect fov clear flags values", new[] { "camera_get_properties" }),
            ("设置物体位置", new[] { "gameobject_set_transform", "gameobject_set_transform_batch" }),
            ("给物体添加组件", new[] { "component_add" }),
            ("修改材质颜色", new[] { "material_set_color" }),
        };

        [Test]
        public void Recommend_TranscriptIntents_RankAnAcceptableSkillInTopThree()
        {
            var failures = new List<string>();
            foreach (var (intent, targets) in Cases)
            {
                var registered = targets.Where(SkillRouter.HasSkill).ToArray();
                if (registered.Length == 0)
                    continue; // optional-package skill absent on this machine; nothing to check
                var results = Recommend("?intent=" + System.Uri.EscapeDataString(intent) + "&topN=10");
                if (!results.Take(3).Intersect(registered).Any())
                    failures.Add($"'{intent}' -> top-10 [{string.Join(", ", results)}], wanted one of [{string.Join(", ", registered)}]");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void Recommend_UnknownFilterWordsDoNotInheritOperationsFromSubstrings()
        {
            // "asset" used to match the operation keyword "set" as a substring and turn every asset query into a Modify intent.
            var response = JObject.Parse(SkillRouter.GetRecommendations("?intent=find+asset+by+path&topN=5"));
            var markers = ((JArray)response["results"]).SelectMany(r => (JArray)r["matchedOn"]).Select(m => m.ToString()).ToArray();
            Assert.That(markers, Does.Not.Contain("operation:Modify"));
        }

        private static string[] Recommend(string query)
        {
            var response = JObject.Parse(SkillRouter.GetRecommendations(query));
            Assert.That(response["errorCode"], Is.Null,
                $"recommend returned an error: {response.ToString(Newtonsoft.Json.Formatting.None)}");
            return ((JArray)response["results"]).Select(r => r["name"].ToString()).ToArray();
        }
    }
}

// Producer:Betsy
