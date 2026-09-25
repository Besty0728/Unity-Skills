using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round6 second-audit fixes in modules that need no optional package: each call below used to report success
    /// (or escape as an unhandled exception) while it dropped, widened or failed to perform what the caller asked for.
    /// </summary>
    [TestFixture]
    public class SilentDegradationAuditTests
    {
        private const string ProbeFolder = "Assets/R6AuditProbe";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            _tempRoot = Path.GetFullPath("Temp/R6AuditProbe").Replace('\\', '/');
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            if (Directory.Exists(_tempRoot))
            {
                Chmod("-R 755", _tempRoot);
                Directory.Delete(_tempRoot, true);
            }
            var backups = $"{HybridCLRSkills.BackupRoot()}/r6probe";
            if (Directory.Exists(backups))
                Directory.Delete(backups, true);
            if (Directory.Exists(ProbeFolder))
                Chmod("-R 755", ProbeFolder);
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            if (Directory.Exists(ProbeFolder))
            {
                Directory.Delete(ProbeFolder, true);
                File.Delete(ProbeFolder + ".meta");
                AssetDatabase.Refresh();
            }
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void SceneSpatialQuery_UnknownComponentFilter_IsRejected()
        {
            GameObject.CreatePrimitive(PrimitiveType.Cube);
            GameObjectFinder.InvalidateCache();

            // Used to fall through as "no filter" and return every object in range.
            AssertSemanticInvalid(Run("scene_spatial_query", new JObject { ["componentFilter"] = "MeshRendrer", ["radius"] = 100 }), "componentFilter");
            var filtered = Run("scene_spatial_query", new JObject { ["componentFilter"] = "MeshRenderer", ["radius"] = 100 });
            Assert.That(filtered["status"]?.ToString(), Is.EqualTo("success"), filtered.ToString());
        }

        [Test]
        public void SceneFindObjects_UndefinedTagOrType_IsRejectedInsteadOfThrowing()
        {
            new GameObject("R6TagProbe");
            GameObjectFinder.InvalidateCache();

            // An undefined tag made CompareTag log an error and match nothing: success with count 0, as if no object had it.
            AssertSemanticInvalid(Run("scene_find_objects", new JObject { ["tag"] = "R6NoSuchTag" }), "tag");
            AssertSemanticInvalid(Run("scene_find_objects", new JObject { ["componentType"] = "MeshRendrer" }), "componentType");
            var untagged = Run("scene_find_objects", new JObject { ["tag"] = "Untagged" });
            Assert.That(untagged["status"]?.ToString(), Is.EqualTo("success"), untagged.ToString());
            Assert.That(untagged["result"]?["count"]?.Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void SceneCreateSaveUnload_UnwritableFolder_ReportFailure()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                Assert.Ignore("Making a folder unwritable relies on chmod.");
            if (!Application.isBatchMode)
                Assert.Ignore("A failed scene save can raise a modal dialog in an interactive editor; runs in the batchmode gates.");

            var locked = $"{ProbeFolder}/Locked";
            Directory.CreateDirectory(locked);
            // An additive scene can only be created once the untitled main scene has a file.
            Assert.That(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), $"{ProbeFolder}/R6Main.unity"), Is.True);
            var extraPath = $"{locked}/R6Extra.unity";
            var extra = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(extra, extraPath), Is.True);
            var marker = new GameObject("R6DirtyMarker");
            SceneManager.MoveGameObjectToScene(marker, extra);
            EditorSceneManager.MarkSceneDirty(extra);
            Chmod("444", extraPath);
            Chmod("555", locked);
            LogAssert.ignoreFailingMessages = true;

            // All three used to discard SaveScene's result (and CloseScene's) and answer success.
            var save = Run("scene_save", new JObject { ["scenePath"] = $"{locked}/R6Saved.unity" });
            Assert.That(save["status"]?.ToString(), Is.EqualTo("error"), save.ToString());
            Assert.That(File.Exists($"{locked}/R6Saved.unity"), Is.False);

            var unload = Run("scene_unload", new JObject { ["sceneName"] = "R6Extra" });
            Assert.That(unload["status"]?.ToString(), Is.EqualTo("error"), unload.ToString());
            Assert.That(extra.isLoaded, Is.True, "A scene whose unsaved edits could not be written must stay loaded.");

            var create = Run("scene_create", new JObject { ["scenePath"] = $"{locked}/R6Created.unity" });
            Assert.That(create["status"]?.ToString(), Is.EqualTo("error"), create.ToString());
            Assert.That(File.Exists($"{locked}/R6Created.unity"), Is.False);
        }

        [Test]
        public void ScriptableObjectSet_ReadOnlyProperty_IsRejected()
        {
            EnsureProbeFolder();
            var path = $"{ProbeFolder}/R6Skin.guiskin";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<GUISkin>(), path);

            // GUISkin.settings is get-only: the write was skipped and the call still answered success.
            AssertSemanticInvalid(Run("scriptableobject_set", new JObject { ["assetPath"] = path, ["fieldName"] = "settings", ["value"] = "{}" }), "fieldName");
        }

        [Test]
        public void ScriptableObjectJson_MissingSourceFileAndTargetFolder()
        {
            EnsureProbeFolder();
            var path = $"{ProbeFolder}/R6Skin.guiskin";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<GUISkin>(), path);

            // A missing JSON file escaped as FileNotFoundException (INTERNAL).
            var import = Run("scriptableobject_import_json", new JObject { ["assetPath"] = path, ["jsonFilePath"] = $"{ProbeFolder}/Nope.json" });
            Assert.That(import["status"]?.ToString(), Is.EqualTo("error"), import.ToString());
            Assert.That(import["errorCode"]?.ToString(), Is.Not.EqualTo("INTERNAL"), import.ToString());

            // A missing parent folder escaped as DirectoryNotFoundException; it is created like every other writer does.
            var target = $"{ProbeFolder}/Export/Nested/r6.json";
            var export = Run("scriptableobject_export_json", new JObject { ["assetPath"] = path, ["savePath"] = target });
            Assert.That(export["status"]?.ToString(), Is.EqualTo("success"), export.ToString());
            Assert.That(File.Exists(target), Is.True);
        }

        [Test]
        public void UICreateImageAndRawImage_UnresolvablePaths_AreRejectedBeforeCreatingAnything()
        {
            // Both used to create a Canvas and an empty Image / RawImage and report success.
            var image = Run("ui_create_image", new JObject { ["name"] = "R6Icon", ["spritePath"] = "Assets/R6Nope.png" });
            Assert.That(image["status"]?.ToString(), Is.EqualTo("error"), image.ToString());
            var raw = Run("ui_create_rawimage", new JObject { ["name"] = "R6Raw", ["texturePath"] = "Assets/R6Nope.png" });
            Assert.That(raw["status"]?.ToString(), Is.EqualTo("error"), raw.ToString());
            Assert.That(SceneManager.GetActiveScene().rootCount, Is.Zero, "A rejected call must not create a Canvas or element.");
        }

        [Test]
        public void AnimatorAddState_UnresolvableClip_IsRejectedBeforeAddingTheState()
        {
            EnsureProbeFolder();
            var path = $"{ProbeFolder}/R6.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            // Used to add a state without motion and report success.
            var json = Run("animator_add_state", new JObject { ["controllerPath"] = path, ["stateName"] = "Walk", ["clipPath"] = "Assets/R6Nope.anim" });
            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"), json.ToString());
            Assert.That(controller.layers[0].stateMachine.states, Is.Empty);
        }

        [Test]
        public void HybridClrFileSetRestore_FailsWithoutItsBackup()
        {
            var target = $"{_tempRoot}/Target";
            Directory.CreateDirectory(target);
            File.WriteAllText($"{target}/a.dll", "after");
            var json = JsonConvert.SerializeObject(new HybridCLRSkills.FileSetBackup
            {
                label = "r6probe", targetDir = target, targetDirExisted = true,
                backupDir = $"{_tempRoot}/NeverWritten", files = new[] { "a.dll" }, refreshAssetDatabase = false,
            });

            // The restore skipped the missing backup entirely and reported success.
            Assert.That(HybridCLRSkills.ApplyFileSetBackup(json), Is.False);
            Assert.That(File.ReadAllText($"{target}/a.dll"), Is.EqualTo("after"));
        }

        [Test]
        public void HybridClrFileSetRestore_KeepsAndReportsAFileWhoseBackupFailed()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                Assert.Ignore("Making a file unreadable relies on chmod.");

            var target = $"{_tempRoot}/Target";
            Directory.CreateDirectory(target);
            File.WriteAllText($"{target}/a.dll", "before");
            File.WriteAllText($"{target}/b.dll", "unreadable");
            Chmod("000", $"{target}/b.dll");
            HybridCLRSkills.FileSetBackup snap;
            try { snap = HybridCLRSkills.CaptureFileSet("r6probe", target, false); }
            finally { Chmod("644", $"{target}/b.dll"); }
            Assert.That(snap.files, Is.EquivalentTo(new[] { "a.dll", "b.dll" }), "A file whose backup copy failed must still be tracked.");

            File.WriteAllText($"{target}/a.dll", "after");
            File.WriteAllText($"{target}/c.dll", "added");

            // b.dll used to be untracked, so the restore deleted it as "added by the operation" and reported success.
            Assert.That(HybridCLRSkills.ApplyFileSetBackup(JsonConvert.SerializeObject(snap)), Is.False);
            Assert.That(File.ReadAllText($"{target}/a.dll"), Is.EqualTo("before"));
            Assert.That(File.Exists($"{target}/b.dll"), Is.True);
            Assert.That(File.Exists($"{target}/c.dll"), Is.False);
        }

        [Test]
        public void HybridClrGeneratedSourcesRestore_FailsWhenAFileCannotBeWritten()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                Assert.Ignore("Making a folder unwritable relies on chmod.");

            var locked = $"{_tempRoot}/Locked";
            Directory.CreateDirectory(locked);
            Chmod("555", locked);
            var json = JsonConvert.SerializeObject(new HybridCLRSkills.GeneratedSourceSnapshot
            {
                files = new[] { new HybridCLRSkills.GeneratedSourceEntry { path = $"{locked}/link.xml", existed = true, text = "<linker/>" } },
            });

            // A failed write was logged and the restore still reported success.
            Assert.That(HybridCLRSkills.ApplyGeneratedSources(json), Is.False);
        }

        // AssetDatabase.CreateFolder would pick "R6AuditProbe 1" if a stray, unimported folder of that name exists.
        private static void EnsureProbeFolder()
        {
            Directory.CreateDirectory(ProbeFolder);
            AssetDatabase.ImportAsset(ProbeFolder);
        }

        private static JObject Run(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));

        private static void AssertSemanticInvalid(JObject json, string parameter)
        {
            var text = json.ToString(Formatting.None);
            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"), text);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), text);
            Assert.That(text, Does.Contain("\"" + parameter + "\""), $"The rejection must name '{parameter}': {text}");
        }

        private static void Chmod(string args, string path)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor) return;
            using (var process = Process.Start(new ProcessStartInfo("chmod", $"{args} \"{path}\"")
                   { UseShellExecute = false, CreateNoWindow = true }))
            {
                process?.WaitForExit();
            }
        }
    }
}

// Producer:Betsy
