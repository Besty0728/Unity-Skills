using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using PkgInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round5 precision fixes for MaterialSkills (bugs.md B1-B4): an explicit propertyName is
    /// resolved (not silently replaced) before any write, an unresolved shaderName's fallback is
    /// reported, a rejected savePath leaves no orphaned in-memory Material, and the emission batch
    /// no longer forces intensity to 1 or diverges from the single setter's semantics.
    /// </summary>
    [TestFixture]
    public class MaterialPrecisionTests
    {
        private const string ProbeFolder = "Assets/__RB_MaterialProbe__";

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
                AssetDatabase.CreateFolder("Assets", "__RB_MaterialProbe__");
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            if (AssetDatabase.IsValidFolder("Assets/Packages"))
                AssetDatabase.DeleteAsset("Assets/Packages");

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        // ---------- B1: material_set_color propertyName resolution ----------

        [Test]
        public void SetColor_ExplicitMissingProperty_IsRejectedAndWritesNothing()
        {
            var (material, path) = CreateUnlitColorAsset("RB_MissingProp");
            var before = material.GetColor("_Color");

            var json = ToJson(MaterialSkills.MaterialSetColor(path: path, propertyName: "_SpecColour", r: 1, g: 0, b: 0));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("propertyName"));
            Assert.That(json["validValues"]?.Values<string>(), Does.Contain("_Color"));
            Assert.That(material.GetColor("_Color"), Is.EqualTo(before));
        }

        [Test]
        public void SetColor_ExplicitNonColourProperty_IsRejected()
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null, "Standard shader not found in this project.");
            var material = new Material(shader);
            Assume.That(material.HasProperty("_Metallic"), Is.True, "Standard is expected to declare _Metallic.");
            var before = material.GetFloat("_Metallic");
            var go = new GameObject("RB_NonColourProp");
            go.AddComponent<MeshRenderer>().sharedMaterial = material;

            var json = ToJson(MaterialSkills.MaterialSetColor(name: go.name, propertyName: "_Metallic", r: 1, g: 0, b: 0));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("not a colour"));
            Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(before));
        }

        [Test]
        public void SetColor_MainColourAlias_FallsBackAndReportsIt()
        {
            var (material, path) = CreateUnlitColorAsset("RB_AliasFallback");

            var json = ToJson(MaterialSkills.MaterialSetColor(path: path, propertyName: "_BaseColor", r: 1, g: 0, b: 0));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["propertyUsed"]?.ToString(), Is.EqualTo("_Color"));
            Assert.That(json["propertyRequested"]?.ToString(), Is.EqualTo("_BaseColor"));
            Assert.That(json["warnings"]?.Values<string>().Count(), Is.EqualTo(1));
            Assert.That(material.GetColor("_Color"), Is.EqualTo(new Color(1, 0, 0, 1)));
        }

        [Test]
        public void SetColor_CaseInsensitiveName_ResolvesWithAWarning()
        {
            var (material, path) = CreateUnlitColorAsset("RB_CaseInsensitive");

            var json = ToJson(MaterialSkills.MaterialSetColor(path: path, propertyName: "_color", r: 0, g: 1, b: 0));

            Assert.That(json["propertyUsed"]?.ToString(), Is.EqualTo("_Color"), json.ToString(Formatting.None));
            Assert.That(json["warnings"], Is.Not.Null);
            Assert.That(material.GetColor("_Color"), Is.EqualTo(new Color(0, 1, 0, 1)));
        }

        [Test]
        public void SetColor_Omitted_ResponseShapeUnchanged()
        {
            var (_, path) = CreateUnlitColorAsset("RB_OmittedShape");

            var json = ToJson(MaterialSkills.MaterialSetColor(path: path, r: 1, g: 1, b: 1));

            var keys = ((JObject)json).Properties().Select(p => p.Name).ToArray();
            Assert.That(keys, Is.EquivalentTo(new[] { "success", "target", "color", "intensity", "propertyUsed", "hdrEnabled", "materialPath" }),
                json.ToString(Formatting.None));
        }

        [Test]
        public void ColorsBatch_ReportsPropertyUsedPerItem()
        {
            var (_, path1) = CreateUnlitColorAsset("RB_BatchA");
            var (_, path2) = CreateUnlitColorAsset("RB_BatchB");

            var json = ToJson(MaterialSkills.MaterialSetColorsBatch(
                $"[{{\"path\":\"{path1}\",\"r\":1}},{{\"path\":\"{path2}\",\"r\":0,\"g\":1}}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var results = (JArray)json["results"];
            Assert.That(results[0]["propertyUsed"]?.ToString(), Is.EqualTo("_Color"));
            Assert.That(results[1]["propertyUsed"]?.ToString(), Is.EqualTo("_Color"));
        }

        [Test]
        public void ColorsBatch_PerItemPropertyName_HasNoUnknownFieldWarning()
        {
            var (_, path1) = CreateUnlitColorAsset("RB_BatchNamedA");

            var json = ToJson(MaterialSkills.MaterialSetColorsBatch(
                $"[{{\"path\":\"{path1}\",\"r\":1,\"propertyName\":\"_Color\"}}]"));

            Assert.That(json["warnings"], Is.Null, json.ToString(Formatting.None));
        }

        [Test]
        public void ColorsBatch_RejectedItem_RollsBackTheOthers()
        {
            var (material1, path1) = CreateUnlitColorAsset("RB_BatchRollbackA");
            var before = material1.GetColor("_Color");

            var json = ToJson(MaterialSkills.MaterialSetColorsBatch(
                $"[{{\"path\":\"{path1}\",\"r\":1,\"g\":1,\"b\":1}},{{\"path\":\"{path1}\",\"propertyName\":\"_Nope\"}}]"));

            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var results = (JArray)json["results"];
            Assert.That(results[0]["reverted"]?.Value<bool>(), Is.True);
            Assert.That(material1.GetColor("_Color"), Is.EqualTo(before));
        }

        // ---------- B2: material_create shader fallback visibility ----------

        [Test]
        public void Create_UnknownShader_FallsBackAndSaysSo()
        {
            var json = ToJson(MaterialSkills.MaterialCreate("RB_ShaderFallback", "No/Such/Shader_7f3", ProbeFolder));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["shader"]?.ToString(), Is.Not.Null.And.Not.EqualTo("No/Such/Shader_7f3"));
            Assert.That(json["shaderRequested"]?.ToString(), Is.EqualTo("No/Such/Shader_7f3"));
            Assert.That(json["warnings"]?[0]?.ToString(), Does.Contain("not found"));
        }

        [Test]
        public void Create_KnownShader_HasNoFallbackKeys()
        {
            var json = ToJson(MaterialSkills.MaterialCreate("RB_KnownShader", savePath: ProbeFolder));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["shaderRequested"], Is.Null);
            Assert.That(json["warnings"], Is.Null);
        }

        // ---------- B3: savePath resolution (Packages/, and ordered before Material creation) ----------

        [Test]
        public void Create_RejectedPackagePath_LeavesNoMaterialAndNoAsset()
        {
            var info = PkgInfo.FindForAssetPath("Packages/com.unity.test-framework/probe.mat");
            Assume.That(info, Is.Not.Null, "com.unity.test-framework is expected to be installed for EditMode tests to run.");
            Assume.That(info.source, Is.Not.EqualTo(PackageSource.Embedded).And.Not.EqualTo(PackageSource.Local),
                "This test assumes the test framework package is a read-only (non-embedded/local) package here.");

            int materialsBefore = Resources.FindObjectsOfTypeAll<Material>().Length;

            var json = ToJson(MaterialSkills.MaterialCreate("RB_RejectedPkg7f3", savePath: "Packages/com.unity.test-framework/RB_RejectedPkg7f3"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("savePath"));
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>("Packages/com.unity.test-framework/RB_RejectedPkg7f3.mat"), Is.Null);
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materialsBefore),
                "A rejected savePath must not leave an orphaned in-memory Material (bugs.md B3).");
        }

        [Test]
        public void Create_UnknownPackagePath_IsRejected()
        {
            var json = ToJson(MaterialSkills.MaterialCreate("RB_UnknownPkg7f3", savePath: "Packages/com.nobody.nothing7f3/M"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("savePath"));
        }

        [Test]
        public void Create_AssetsRoot_WritesAssetsNotAssetsAssets()
        {
            var json = ToJson(MaterialSkills.MaterialCreate("RB_AssetsRoot7f3", savePath: "Assets"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var path = json["path"]?.ToString();
            Assert.That(path, Is.EqualTo("Assets/RB_AssetsRoot7f3.mat"));
            AssetDatabase.DeleteAsset(path);
        }

        [Test]
        public void Duplicate_UsesTheSameResolver()
        {
            var sourcePath = $"{ProbeFolder}/RB_DupSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);

            var json = ToJson(MaterialSkills.MaterialDuplicate(sourcePath, "RB_DupTarget", ProbeFolder.Replace("Assets/", "Assets\\") + "\\Sub"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["path"]?.ToString(), Is.EqualTo($"{ProbeFolder}/Sub/RB_DupTarget.mat"));
        }

        // ---------- B4: material_set_emission_batch intensity passthrough + defaults ----------

        [Test]
        public void EmissionBatch_ZeroIntensity_DisablesEmissionLikeTheSingle()
        {
            var (goBatch, matBatch) = CreateCubeWithMaterial("RB_EmitBatchZero");
            var (goSingle, matSingle) = CreateCubeWithMaterial("RB_EmitSingleZero");

            var batchJson = ToJson(MaterialSkills.MaterialSetEmissionBatch($"[{{\"name\":\"{goBatch.name}\",\"r\":1,\"intensity\":0}}]"));
            MaterialSkills.MaterialSetEmission(name: goSingle.name, r: 1, intensity: 0);

            Assert.That(batchJson["success"]?.Value<bool>(), Is.True, batchJson.ToString(Formatting.None));
            Assert.That(matBatch.IsKeywordEnabled("_EMISSION"), Is.False);
            Assert.That(matBatch.globalIlluminationFlags, Is.EqualTo(matSingle.globalIlluminationFlags));
            var results = (JArray)batchJson["results"];
            Assert.That(results[0]["emissionEnabled"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void EmissionBatch_OmittedIntensity_StillOne()
        {
            var (go, _) = CreateCubeWithMaterial("RB_EmitOmittedIntensity");

            var json = ToJson(MaterialSkills.MaterialSetEmissionBatch($"[{{\"name\":\"{go.name}\",\"r\":1}}]"));

            var results = (JArray)json["results"];
            Assert.That(results[0]["intensity"]?.Value<float>(), Is.EqualTo(1f), json.ToString(Formatting.None));
            Assert.That(results[0]["emissionEnabled"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void EmissionBatch_OmittedChannels_StayZero()
        {
            var (go, _) = CreateCubeWithMaterial("RB_EmitOmittedChannels");

            var json = ToJson(MaterialSkills.MaterialSetEmissionBatch($"[{{\"name\":\"{go.name}\",\"r\":1}}]"));

            var results = (JArray)json["results"];
            Assert.That(results[0]["hdrColor"]?["g"]?.Value<float>(), Is.EqualTo(0f), json.ToString(Formatting.None));
        }

        [Test]
        public void EmissionBatch_AllChannelsOmitted_WarnsBlackEmission()
        {
            var (go, _) = CreateCubeWithMaterial("RB_EmitAllOmittedWarn");
            var json = ToJson(MaterialSkills.MaterialSetEmissionBatch($"[{{\"name\":\"{go.name}\"}}]"));
            var results = (JArray)json["results"];
            Assert.That(results[0]["warnings"]?.Values<string>().Count(), Is.EqualTo(1), json.ToString(Formatting.None));

            var (go2, _) = CreateCubeWithMaterial("RB_EmitAllOmittedNoWarn");
            var json2 = ToJson(MaterialSkills.MaterialSetEmissionBatch($"[{{\"name\":\"{go2.name}\",\"enableEmission\":false}}]"));
            var results2 = (JArray)json2["results"];
            Assert.That(results2[0]["warnings"], Is.Null, json2.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        /// <summary>Unlit/Color: built-in, present under every render pipeline, declares only _Color as a colour property.</summary>
        private (Material material, string path) CreateUnlitColorAsset(string name)
        {
            var shader = Shader.Find("Unlit/Color");
            Assume.That(shader, Is.Not.Null, "Unlit/Color shader not found in this project.");
            var path = $"{ProbeFolder}/{name}.mat";
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            return (material, path);
        }

        private static (GameObject go, Material material) CreateCubeWithMaterial(string name)
        {
            var shader = Shader.Find("Standard");
            Assume.That(shader, Is.Not.Null, "Standard shader not found in this project.");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            var material = new Material(shader);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return (go, material);
        }
    }
}

// Producer:Betsy
