using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// physics_create_material asked AssetDatabase for a unique path inside a folder that did not exist yet, got an empty
    /// path back and failed with "Invalid path" (P8). The folder is now created first, so a new savePath works, a savePath inside
    /// a read-only package is refused with a structured error before anything is created, and the created asset is snapshotted
    /// for workflow undo.
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
        public void ATraversingSavePath_IsRefused_AndCreatesNothing()
        {
            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Escape", "Assets/../Outside"));

            Assert.That(json["error"]?.ToString(), Does.Contain("traversal"), json.ToString(Formatting.None));
            Assert.That(AssetDatabase.IsValidFolder("Assets/Outside"), Is.False);
            Assert.That(PhysicsMaterialAssetsNamed("Escape"), Is.Empty);
        }

        [Test]
        public void ANameWithSeparators_IsRefused_AndCreatesNothing()
        {
            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Bad/Name", TempRoot + "/Never"));

            Assert.That(json["error"]?.ToString(), Does.Contain("path separators"), json.ToString(Formatting.None));
            Assert.That(AssetDatabase.IsValidFolder(TempRoot + "/Never"), Is.False);
        }

        // ---------- read-only packages ----------

        [Test]
        public void APathInsideABuiltInPackage_IsRefusedWithAStructuredError_AndCreatesNothing()
        {
            // A built-in module is always installed and never writable.
            var folder = "Packages/com.unity.modules.physics/PhysicsMaterialProbe";

            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Blocked", folder));

            AssertPackageRefusal(json, folder);
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False, "Nothing may be created inside the package.");
        }

        [Test]
        public void ThePackagesRoot_IsRefused_BecauseNoPackageOwnsIt()
        {
            var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Root", "Packages"));

            AssertPackageRefusal(json, "Packages");
        }

        [Test]
        public void ReadOnlyPackage_ByTheOwnershipSeam_NamesThePackageAndItsSource()
        {
            var registry = new MaterialSkills.PackageWriteCheck(found: true, writable: false, packageName: "com.example.pkg", packageSource: "Registry");

            var json = ToJson(PhysicsSkills.CreatePhysicsMaterial("Blocked", "Packages/com.example.pkg/Materials", 0.6f, 0.6f, 0f, _ => registry));

            AssertPackageRefusal(json, "Packages/com.example.pkg/Materials");
            Assert.That(json["error"]?.ToString(), Does.Contain("package 'com.example.pkg' is a Registry package (read-only)"));
            Assert.That(json["packageName"]?.ToString(), Is.EqualTo("com.example.pkg"));
            Assert.That(json["packageSource"]?.ToString(), Is.EqualTo("Registry"));
            Assert.That(AssetDatabase.IsValidFolder("Packages/com.example.pkg"), Is.False);
        }

        [Test]
        public void APathNoPackageOwns_ByTheOwnershipSeam_IsRefused()
        {
            var unowned = new MaterialSkills.PackageWriteCheck(found: false, writable: false, packageName: null, packageSource: null);

            var json = ToJson(PhysicsSkills.CreatePhysicsMaterial("Blocked", "Packages/com.nobody/Materials", 0.6f, 0.6f, 0f, _ => unowned));

            AssertPackageRefusal(json, "Packages/com.nobody/Materials");
            Assert.That(json["error"]?.ToString(), Does.Contain("no installed package owns"));
        }

        [Test]
        public void APackagePath_ThroughTheRouter_AnswersWithTheStructuredError()
        {
            var args = new JObject { ["name"] = "Routed", ["savePath"] = "Packages/com.unity.modules.physics/PhysicsMaterialProbe" };

            var response = JObject.Parse(SkillRouter.Execute("physics_create_material", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"), response.ToString(Formatting.None));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(response["error"]?.ToString(), Does.Contain("savePath"));
        }

        // ---------- workflow ----------

        [Test]
        public void TheCreatedAsset_IsSnapshottedForWorkflowUndo()
        {
            var workflowRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsPhysicsMaterialTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workflowRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(workflowRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(workflowRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();
            try
            {
                WorkflowManager.BeginTask("physics-material", "test");

                var json = ToJson(PhysicsSkills.PhysicsCreateMaterial("Tracked", TempRoot + "/Undo"));

                Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
                var snapshot = WorkflowManager.CurrentTask.snapshots.Single(s => s.assetPath == (string)json["path"]);
                Assert.That(snapshot.type.ToString(), Is.EqualTo("Created"), "Undoing the task deletes the asset it created.");
            }
            finally
            {
                WorkflowManager.AbortTask();
                WorkflowManager.ResetStateForTests();
                WorkflowManager.OverrideHistoryFilePathForTests = null;
                WorkflowFileStore.OverrideStoreRootForTests = null;
                try { if (Directory.Exists(workflowRoot)) Directory.Delete(workflowRoot, true); } catch { }
            }
        }

        [Test]
        public void ARefusedCall_RecordsNoSnapshot()
        {
            var workflowRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsPhysicsMaterialTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workflowRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(workflowRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(workflowRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();
            try
            {
                WorkflowManager.BeginTask("physics-material-refused", "test");

                PhysicsSkills.PhysicsCreateMaterial("Blocked", "Packages/com.unity.modules.physics/PhysicsMaterialProbe");

                Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty);
            }
            finally
            {
                WorkflowManager.AbortTask();
                WorkflowManager.ResetStateForTests();
                WorkflowManager.OverrideHistoryFilePathForTests = null;
                WorkflowFileStore.OverrideStoreRootForTests = null;
                try { if (Directory.Exists(workflowRoot)) Directory.Delete(workflowRoot, true); } catch { }
            }
        }

        private static void AssertPackageRefusal(JObject json, string savePath)
        {
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("savePath"));
            Assert.That(json["error"]?.ToString(), Does.Contain(savePath).And.Contain("Physics materials"));
            Assert.That(json["retryStrategy"]?.ToString(), Is.EqualTo("fix_and_retry"));
            Assert.That(json["suggestedFixes"]?[0]?["args"]?["savePath"]?.ToString(), Is.EqualTo("Assets/PhysicsMaterials"));
        }

        private static string[] PhysicsMaterialAssetsNamed(string name) =>
            AssetDatabase.FindAssets(name)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".physicMaterial", StringComparison.OrdinalIgnoreCase))
                .ToArray();

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
