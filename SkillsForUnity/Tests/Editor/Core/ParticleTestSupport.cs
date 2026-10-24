#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.IO;
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
    /// Shared setup of the particle fixtures: Bypass mode and the Full surface (the router paths are under test, not the
    /// gates), a fresh empty scene, a private workflow history and file store, and every particle test seam reset.
    /// Tests create their own systems and assets under <see cref="AssetRoot"/> and never touch existing project content.
    /// </summary>
    public abstract class ParticleTestBase
    {
        protected const string AssetRoot = "Assets/Temp/ParticleSkillsTests";
        private const string TempFolder = "Assets/Temp";

        /// <summary>Set when <see cref="EnsureAssetRoot"/> had to create Assets/Temp, so TearDown removes it again.</summary>
        private static bool _createdTempFolder;

        private ModePreferenceSnapshot _modePreferences;
        private SurfaceProfileKind _savedProfile;
        private string _workflowRoot;

        [OneTimeSetUp]
        public void ParticleOneTimeSetUp() => _modePreferences = ModePreferenceSnapshot.Capture();

        [OneTimeTearDown]
        public void ParticleOneTimeTearDown() => _modePreferences.Restore();

        [SetUp]
        public void ParticleSetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            _workflowRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsParticleTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workflowRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_workflowRoot, "workflow"));

            ParticleSkills.SimulateMissingModuleForTests = false;
            ParticleSkills.AfterWriteHookForTests = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void ParticleTearDown()
        {
            ParticleSkills.SimulateMissingModuleForTests = false;
            ParticleSkills.AfterWriteHookForTests = null;
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            try { if (Directory.Exists(_workflowRoot)) Directory.Delete(_workflowRoot, true); } catch { }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot))
                AssetDatabase.DeleteAsset(AssetRoot);
            // Assets/Temp goes only when this fixture made it and nothing else was put there.
            if (_createdTempFolder && AssetDatabase.IsValidFolder(TempFolder) && !Directory.EnumerateFileSystemEntries(TempFolder).Any())
                AssetDatabase.DeleteAsset(TempFolder);
            _createdTempFolder = false;
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            GameObjectFinder.InvalidateCache();
        }

        /// <summary>A skill's return value as JSON, the way the router serializes it.</summary>
        protected static JObject Json(object result) =>
            result is string text ? JObject.Parse(text) : JObject.Parse(JsonConvert.SerializeObject(result, SkillsCommon.JsonSettings));

        /// <summary>A plain system made without Undo, as test content (not under test).</summary>
        protected static ParticleSystem MakeSystem(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null)
                go.transform.SetParent(parent, false);
            return go.AddComponent<ParticleSystem>();
        }

        protected static string Id(UnityEngine.Object obj) => UnityObjectIdUtility.GetEntityId(obj);

        /// <summary>A configJson with schemaVersion 1 and the given modules object.</summary>
        protected static string Config(JObject modules, JObject system = null)
        {
            var root = new JObject { ["schemaVersion"] = 1 };
            if (system != null)
                root["system"] = system;
            if (modules != null)
                root["modules"] = modules;
            return root.ToString(Formatting.None);
        }

        protected static string RendererConfig(JObject renderer) =>
            new JObject { ["schemaVersion"] = 1, ["renderer"] = renderer }.ToString(Formatting.None);

        protected static JObject Execute(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));

        protected static JObject ExecuteWithDiff(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None), captureDiff: true));

        protected static JObject DryRun(string skill, JObject args) =>
            JObject.Parse(SkillRouter.DryRun(skill, args.ToString(Formatting.None)));

        protected static void AssertSuccess(JObject json)
        {
            Assert.That(json["error"], Is.Null, json.ToString(Formatting.None));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
        }

        protected static void AssertError(JObject json, string errorCode)
        {
            Assert.That(json["error"], Is.Not.Null, json.ToString(Formatting.None));
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo(errorCode), json.ToString(Formatting.None));
        }

        /// <summary>The canonical JSON a field holds now, read through the module readback the skills use.</summary>
        protected static JToken Field(ParticleSystem system, string module, string field)
        {
            var json = Json(ParticleSkills.ParticleGetModule(entityId: Id(system.gameObject), module: module));
            AssertSuccess(json);
            return json["fields"]?[field];
        }

        /// <summary>
        /// Creates <see cref="AssetRoot"/> for scenes, prefabs and materials a test saves. TearDown removes it, and also
        /// Assets/Temp when this call created that folder.
        /// </summary>
        protected static void EnsureAssetRoot()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Temp");
                _createdTempFolder = true;
            }
            if (!AssetDatabase.IsValidFolder(AssetRoot))
                AssetDatabase.CreateFolder(TempFolder, "ParticleSkillsTests");
        }

        protected static Material CreateMaterialAsset(string name)
        {
            EnsureAssetRoot();
            var shader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/InternalErrorShader");
            var material = new Material(shader);
            var path = $"{AssetRoot}/{name}.mat";
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        protected static void StopAndClear(ParticleSystem system)
        {
            if (system != null)
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }
}
#endif

// Producer:Betsy
