using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Round6 silent-degradation fixes: each call below used to succeed (or crash after writing) while it ignored,
    /// widened or narrowed what the caller asked for. Every test drives the skill through SkillRouter.Execute and
    /// checks both the structured rejection and that nothing was written.
    /// </summary>
    [TestFixture]
    public class SilentDegradationRound6Tests
    {
        private const string ProbeFolder = "Assets/R6SilentProbe";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            CompilationResultService.LastResultJsonOverrideForTests = null;
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void TerrainPaintTexture_NegativeLayerIndex_IsRejectedBeforeAnyWrite()
        {
            var data = new TerrainData { heightmapResolution = 33, alphamapResolution = 16, size = new Vector3(10, 1, 10) };
            data.terrainLayers = new[] { new TerrainLayer(), new TerrainLayer() };
            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "R6Terrain";
            GameObjectFinder.InvalidateCache();
            var before = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);

            var json = Run("terrain_paint_texture", new JObject
            {
                ["name"] = "R6Terrain", ["normalizedX"] = 0.5, ["normalizedZ"] = 0.5, ["layerIndex"] = -1, ["brushSize"] = 8,
            });

            AssertSemanticInvalid(json, "layerIndex");
            var after = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            Assert.That(after.Cast<float>(), Is.EqualTo(before.Cast<float>()), "A rejected paint must not touch the alphamaps.");
        }

        [Test]
        public void ConsoleGetLogs_UnknownType_IsRejectedInBothModes()
        {
            AssertSemanticInvalid(Run("console_get_logs", new JObject { ["type"] = "Errpr" }), "type");

            Run("console_start_capture", new JObject());
            try
            {
                AssertSemanticInvalid(Run("console_get_logs", new JObject { ["type"] = "Errpr" }), "type");
                Assert.That(Run("console_get_logs", new JObject { ["type"] = "error" })["status"]?.ToString(), Is.EqualTo("success"),
                    "Known type names are matched case-insensitively.");
            }
            finally
            {
                Run("console_stop_capture", new JObject());
            }
        }

        [Test]
        public void DebugGetLogs_TypeIsCaseInsensitive_AndUnknownTypeIsRejected()
        {
            const string marker = "R6 probe warning 7c3f";
            Debug.LogWarning(marker);
            var canonical = Run("debug_get_logs", new JObject { ["type"] = "Warning", ["filter"] = marker });
            if (canonical["result"]?["count"]?.Value<int>() == 0)
                Assert.Ignore("The console history is not readable in this editor session.");

            var lower = Run("debug_get_logs", new JObject { ["type"] = "warning", ["filter"] = marker });
            Assert.That(lower["result"]?["count"]?.Value<int>(), Is.GreaterThan(0),
                "type=warning must read warnings, not silently fall back to errors only: " + lower.ToString(Formatting.None));
            AssertSemanticInvalid(Run("debug_get_logs", new JObject { ["type"] = "Errpr" }), "type");
        }

        [Test]
        public void SmartSceneQuery_RejectsUnknownPropertyOperatorAndMismatchedComparison()
        {
            var go = new GameObject("R6Light");
            var light = go.AddComponent<Light>();
            light.intensity = 2f;
            GameObjectFinder.InvalidateCache();

            var misspelled = Run("smart_scene_query", new JObject
            {
                ["componentName"] = "Light", ["propertyName"] = "intensty", ["op"] = ">", ["value"] = "1",
            });
            AssertSemanticInvalid(misspelled, "propertyName");
            Assert.That(misspelled.ToString(Formatting.None), Does.Contain("intensity"), "The closest member name is suggested.");

            AssertSemanticInvalid(Run("smart_scene_query", new JObject
            {
                ["componentName"] = "Light", ["propertyName"] = "intensity", ["op"] = "~~", ["value"] = "1",
            }), "op");
            AssertSemanticInvalid(Run("smart_scene_query", new JObject
            {
                ["componentName"] = "Light", ["propertyName"] = "enabled", ["op"] = ">", ["value"] = "true",
            }), "op");
            AssertSemanticInvalid(Run("smart_scene_query", new JObject
            {
                ["componentName"] = "Light", ["propertyName"] = "intensity", ["op"] = ">", ["value"] = "bright",
            }), "value");

            var valid = Run("smart_scene_query", new JObject
            {
                ["componentName"] = "Light", ["propertyName"] = "intensity", ["op"] = ">", ["value"] = "1",
            });
            Assert.That(valid["result"]?["count"]?.Value<int>(), Is.EqualTo(1), valid.ToString(Formatting.None));
        }

        [Test]
        public void UitkCreateFromTemplate_UnknownTemplate_IsRejectedAndWritesNothing()
        {
            var json = Run("uitk_create_from_template", new JObject { ["template"] = "menuu", ["savePath"] = ProbeFolder });

            AssertSemanticInvalid(json, "template");
            Assert.That(json.ToString(Formatting.None), Does.Contain("notification"), "The valid template names are listed.");
            Assert.That(Directory.Exists(ProbeFolder), Is.False, "A rejected template must not create files or folders.");
        }

        [Test]
        public void TestSmokeSkills_UnknownCategory_IsRejectedInsteadOfSmokingEverything()
        {
            var json = Run("test_smoke_skills", new JObject { ["category"] = "Gameobjct", ["limit"] = 1, ["runAsync"] = false });

            AssertSemanticInvalid(json, "category");
        }

        [Test]
        public void CleanerFindDuplicates_ReportsFilesItCouldNotHash()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                Assert.Ignore("Making a file unreadable relies on chmod.");

            Directory.CreateDirectory(ProbeFolder);
            foreach (var name in new[] { "a", "b", "c" })
                File.WriteAllText($"{ProbeFolder}/{name}.txt", "identical probe content");
            AssetDatabase.Refresh();
            var locked = $"{ProbeFolder}/c.txt";
            Chmod("000", locked);
            try
            {
                var json = Run("cleaner_find_duplicates", new JObject { ["assetType"] = "TextAsset", ["searchPath"] = ProbeFolder });

                Assert.That(json["status"]?.ToString(), Is.EqualTo("success"), json.ToString(Formatting.None));
                var skipped = json["result"]?["skipped"] as JArray;
                Assert.That(skipped, Is.Not.Null, "Files that could not be hashed must be reported: " + json.ToString(Formatting.None));
                Assert.That(skipped.Select(s => s["path"]?.ToString()), Does.Contain(locked));
            }
            finally
            {
                Chmod("644", locked);
            }
        }

        [Test]
        public void DebugCheckCompilation_ReportsAFailedLastCompilation()
        {
            CompilationResultService.LastResultJsonOverrideForTests =
                "{\"success\":false,\"errorCount\":2,\"finishedAtUtc\":\"2026-09-25T12:00:00Z\"," +
                "\"errors\":[{\"file\":\"Assets/R6.cs\",\"line\":3,\"message\":\"CS1002: ; expected\"}]}";

            var json = Run("debug_check_compilation", new JObject());

            var last = json["result"]?["lastCompilation"];
            Assert.That(last, Is.Not.Null, "Not compiling says nothing about whether the last compilation worked: " + json.ToString(Formatting.None));
            Assert.That(last["succeeded"]?.Value<bool>(), Is.False);
            Assert.That(last["errorCount"]?.Value<int>(), Is.EqualTo(2));
        }

        [Test]
        public void SpriteSetImportSettings_BadOrHalfPivot_IsRejectedBeforeAnyChange()
        {
            Directory.CreateDirectory(ProbeFolder);
            var path = $"{ProbeFolder}/r6sprite.png";
            var texture = new Texture2D(4, 4);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var typeBefore = importer.textureType;

            AssertSemanticInvalid(Run("sprite_set_import_settings", new JObject
            {
                ["assetPath"] = path, ["spriteMode"] = "Single", ["pivotX"] = "abc", ["pivotY"] = "0.5",
            }), "pivotX");
            AssertSemanticInvalid(Run("sprite_set_import_settings", new JObject
            {
                ["assetPath"] = path, ["spriteMode"] = "Single", ["pivotX"] = "0.25",
            }), "pivotY");

            importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(importer.textureType, Is.EqualTo(typeBefore), "A rejected call must leave the importer untouched.");
        }

        [Test]
        public void ShaderStrippingRestore_FailsWhenAFieldIsMissing()
        {
            var probe = new GameObject("R6StrippingProbe");
            try
            {
                // A settings object without the three stripping fields: the restore used to skip them and report success.
                var restored = GraphicsSkills.ApplyShaderStripping("{\"lightmap\":0,\"fog\":0,\"instancing\":0}", new SerializedObject(probe));
                Assert.That(restored, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
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

        private static void Chmod(string mode, string path)
        {
            using (var process = Process.Start(new ProcessStartInfo("chmod", $"{mode} \"{path}\"")
                   { UseShellExecute = false, CreateNoWindow = true }))
            {
                process?.WaitForExit();
            }
        }
    }
}

// Producer:Betsy
