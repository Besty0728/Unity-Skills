using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// physics_create_material asked AssetDatabase for a unique path inside a folder that did not exist yet, got an empty
    /// path back and failed with "Invalid path" (P8). The folder is now created first, so a new savePath works.
    /// </summary>
    [TestFixture]
    public class PhysicsMaterialFolderTests
    {
        private const string TempRoot = "Assets/Temp/PhysicsMaterialFolderTests";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            DeleteTempRoot();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            DeleteTempRoot();
        }

        [Test]
        public void ANewNestedFolder_IsCreatedAndTheMaterialSaved()
        {
            var folder = TempRoot + "/Fresh/Deeper";
            Assume.That(AssetDatabase.IsValidFolder(folder), Is.False);

            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Slippery", folder, 0.1f, 0.2f, 0.9f));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That((string)json["path"], Is.EqualTo(folder + "/Slippery.physicMaterial"));
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.True);
            AssertSaved((string)json["path"], dynamicFriction: 0.1f, staticFriction: 0.2f, bounciness: 0.9f);
        }

        [Test]
        public void AnExistingName_GetsAUniqueSibling_InsideTheNewFolder()
        {
            var folder = TempRoot + "/Twice";

            var first = ToJson(PhysicsSkills.PhysicsCreateMaterial("Same", folder));
            var second = ToJson(PhysicsSkills.PhysicsCreateMaterial("Same", folder));

            Assert.That((string)first["path"], Is.EqualTo(folder + "/Same.physicMaterial"), first.ToString(Formatting.None));
            Assert.That((string)second["path"], Is.EqualTo(folder + "/Same 1.physicMaterial"), second.ToString(Formatting.None));
            AssertSaved((string)first["path"]);
            AssertSaved((string)second["path"]);
        }

        [Test]
        public void AFolderThatExists_StillWorks()
        {
            EnsureFolder(TempRoot);

            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Plain", TempRoot));

            Assert.That((string)json["path"], Is.EqualTo(TempRoot + "/Plain.physicMaterial"), json.ToString(Formatting.None));
            AssertSaved((string)json["path"]);
        }

        [Test]
        public void ThroughTheRouter_ANewFolderSucceeds()
        {
            var args = new JObject { ["name"] = "Routed", ["savePath"] = TempRoot + "/Routed/Sub" };

            var response = JObject.Parse(SkillRouter.Execute("physics_create_material", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            AssertSaved((string)response["result"]["path"]);
        }

        [Test]
        public void ARejectedSavePath_CreatesNothing()
        {
            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Escape", "Assets/../Outside"));

            Assert.That(json["error"], Is.Not.Null, json.ToString(Formatting.None));
            Assert.That(AssetDatabase.IsValidFolder(TempRoot), Is.False);
        }

        private static void AssertSaved(string path, float? dynamicFriction = null, float? staticFriction = null, float? bounciness = null)
        {
#if UNITY_6000_0_OR_NEWER
            var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
#else
            var material = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(path);
#endif
            Assert.That(material, Is.Not.Null, $"Nothing loads from {path}.");
            if (dynamicFriction.HasValue) Assert.That(material.dynamicFriction, Is.EqualTo(dynamicFriction.Value).Within(1e-4f));
            if (staticFriction.HasValue) Assert.That(material.staticFriction, Is.EqualTo(staticFriction.Value).Within(1e-4f));
            if (bounciness.HasValue) Assert.That(material.bounciness, Is.EqualTo(bounciness.Value).Within(1e-4f));
        }

        private static void EnsureFolder(string folder)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/Temp", "PhysicsMaterialFolderTests");
        }

        private static void DeleteTempRoot()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
