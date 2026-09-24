using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 write-then-read-back fixes that don't belong to a single module's own precision-test
    /// file: material_set_render_queue / material_set_color's HDR intensity / material_set_texture's
    /// missing-property case, gameobject_set_active's activeInHierarchy, gameobject_set_parent
    /// refusing to reparent a nested Prefab-instance child, and the small physics/UI read-backs
    /// (physics_set_material, ui_add_outline).
    /// </summary>
    [TestFixture]
    public class SceneWriteReadBackTests
    {
        private const string ProbeFolder = "Assets/__RB_SceneReadBackProbe__";

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
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.CreateFolder("Assets", "__RB_SceneReadBackProbe__");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [Test]
        public void SetRenderQueue_NegativeOne_ReadsBackShaderDefault()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null, "Standard shader not found in this project.");
            var material = new Material(shader);
            var path = $"{ProbeFolder}/RB_RenderQueue.mat";
            AssetDatabase.CreateAsset(material, path);

            var json = ToJson(MaterialSkills.MaterialSetRenderQueue(path: path, renderQueue: -1));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var actual = json["renderQueue"]?.Value<int>();
            Assert.That(actual, Is.Not.EqualTo(-1), "material.renderQueue must resolve -1 to the shader's actual queue.");
            Assert.That(json["valueRequested"]?.Value<int>(), Is.EqualTo(-1));
            Assert.That(json["queueCategory"]?.ToString(), Is.Not.Null.And.Not.EqualTo("ShaderDefault"));
        }

        [Test]
        public void SetColor_Intensity2_ReadsBackTheMultipliedStoredValue()
        {
            var shader = Shader.Find("Unlit/Color");
            Assume.That(shader, Is.Not.Null, "Unlit/Color shader not found in this project.");
            var material = new Material(shader);
            var path = $"{ProbeFolder}/RB_Intensity.mat";
            AssetDatabase.CreateAsset(material, path);

            var json = ToJson(MaterialSkills.MaterialSetColor(path: path, r: 1, g: 0, b: 0, a: 1, intensity: 2));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var color = json["color"];
            Assert.That(color?["r"]?.Value<float>(), Is.EqualTo(2f).Within(1e-4f),
                "color must be the stored (post-intensity) value, not the raw request, " + json.ToString(Formatting.None));
            Assert.That(material.GetColor("_Color").r, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void SetTexture_MissingProperty_IsRejectedNotSilent()
        {
            var shader = Shader.Find("Unlit/Color");
            Assume.That(shader, Is.Not.Null, "Unlit/Color shader not found in this project.");
            var material = new Material(shader);
            Assume.That(material.HasProperty("_BaseMap"), Is.False, "Unlit/Color is expected to have no _BaseMap property.");
            var path = $"{ProbeFolder}/RB_MissingTexProp.mat";
            AssetDatabase.CreateAsset(material, path);

            var json = ToJson(MaterialSkills.MaterialSetTexture(path: path, texturePath: "Assets/RB_DoesNotExist7f3.png", propertyName: "_BaseMap"));

            Assert.That(json["success"]?.Value<bool>() ?? false, Is.False, json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("_BaseMap"));
        }

        [Test]
        public void SetActive_UnderInactiveParent_ReportsActiveInHierarchyFalse()
        {
            var parent = new GameObject("RB_InactiveParent");
            parent.SetActive(false);
            var child = new GameObject("RB_ChildUnderInactiveParent");
            child.transform.SetParent(parent.transform);

            var json = ToJson(GameObjectSkills.GameObjectSetActive(name: child.name, active: true));

            Assert.That(json["active"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["activeInHierarchy"]?.Value<bool>(), Is.False,
                "The object itself is active, but a disabled ancestor still keeps it out of the running hierarchy.");
        }

        [Test]
        public void SetParent_PrefabInstanceNestedChild_ReturnsStructuredErrorAndLeavesHierarchyUnchanged()
        {
            var root = new GameObject("RB_PrefabRoot7f3");
            var nested = new GameObject("RB_PrefabNestedChild7f3");
            nested.transform.SetParent(root.transform);
            var prefabPath = $"{ProbeFolder}/RB_Prefab7f3.prefab";
            PrefabUtility.SaveAsPrefabAssetAndConnect(root, prefabPath, InteractionMode.AutomatedAction);

            new GameObject("RB_OtherParent7f3");

            var json = ToJson(GameObjectSkills.GameObjectSetParent(childName: "RB_PrefabNestedChild7f3", parentName: "RB_OtherParent7f3"));

            Assert.That(json["success"]?.Value<bool>() ?? false, Is.False, json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(GameObject.Find("RB_PrefabRoot7f3/RB_PrefabNestedChild7f3"), Is.Not.Null,
                "The nested prefab-instance child must not actually move.");
        }

        [Test]
        public void PhysicsSetMaterial_ReadsBackAssetPathAndColliderType()
        {
            var go = new GameObject("RB_PhysicsBox7f3");
            go.AddComponent<BoxCollider>();
#if UNITY_6000_0_OR_NEWER
            var mat = new PhysicsMaterial("RB_PM7f3");
#else
            var mat = new PhysicMaterial("RB_PM7f3");
#endif
            var matPath = $"{ProbeFolder}/RB_PM7f3.physicMaterial";
            AssetDatabase.CreateAsset(mat, matPath);

            var json = ToJson(PhysicsSkills.PhysicsSetMaterial(matPath, name: go.name));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["material"]?.ToString(), Is.EqualTo(matPath));
            Assert.That(json["collider"]?.ToString(), Is.EqualTo("BoxCollider"));
        }

        [Test]
        public void AddOutline_ReadsBackFromComponent()
        {
            var go = new GameObject("RB_UIOutline7f3", typeof(RectTransform));

            var json = ToJson(UISkills.UIAddOutline(name: go.name, effectType: "Outline", r: 1, g: 0, b: 0, a: 1, distanceX: 2, distanceY: 3));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(go.GetComponent<Outline>(), Is.Not.Null);
            Assert.That(json["effectDistance"]?.ToString(), Is.EqualTo("(2,3)"));
        }

        [Test]
        public void AddOutline_UnknownEffectType_IsRejected()
        {
            var go = new GameObject("RB_UIOutlineBad7f3", typeof(RectTransform));

            var json = ToJson(UISkills.UIAddOutline(name: go.name, effectType: "Glow"));

            Assert.That(json["success"]?.Value<bool>() ?? false, Is.False, json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(go.GetComponent<Outline>(), Is.Null);
            Assert.That(go.GetComponent<Shadow>(), Is.Null);
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
