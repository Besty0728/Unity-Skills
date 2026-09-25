using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Base for fixtures that drive an optional package's skills for real. Each fixture names a read-only probe skill;
    /// when the router answers it with MISSING_PACKAGE the whole fixture is ignored, so projects without the package
    /// stay green, while the opt6000 / opt2022 gates hold a per-module floor of tests that actually ran. Tests call
    /// skills by name through SkillRouter and read state back by reflection: no package type is referenced, so this
    /// assembly compiles whether or not the packages are installed.
    /// </summary>
    public abstract class OptionalPackageTestBase
    {
        protected const string ProbeFolder = "Assets/R6OptProbe";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        /// <summary>A read-only skill of the module; MISSING_PACKAGE from it ignores the fixture.</summary>
        protected abstract string ProbeSkill { get; }

        protected virtual JObject ProbeArgs => new JObject();

        [OneTimeSetUp]
        public void DetectPackage()
        {
            // Saved before anything can Assert.Ignore: NUnit still runs OneTimeTearDown for an ignored fixture.
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            // Declared packages settle it without a call: a read-only probe still runs when its package is missing (it
            // reports availability itself), and one with a required parameter would answer MISSING_PARAM first.
            if (SkillRouter.TryGetSkill(ProbeSkill, out var skill) && skill.RequiresPackages != null)
            {
                var missing = skill.RequiresPackages.Where(id => !PackageManagerHelper.IsPackageInstalled(id)).ToArray();
                if (missing.Length > 0)
                    Assert.Ignore($"{string.Join(", ", missing)} not installed");
            }

            // Modules without a UPM package (DOTween, QFramework) report their absence from the probe itself.
            var probe = Run(ProbeSkill, ProbeArgs);
            if (probe["errorCode"]?.ToString() == "MISSING_PACKAGE")
                Assert.Ignore($"{ProbeSkill}: package not installed ({probe["error"]})");
        }

        [OneTimeTearDown]
        public void RestoreMode()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        [SetUp]
        public void OpenEmptyScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void CleanUp()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            if (Directory.Exists(ProbeFolder))
            {
                Directory.Delete(ProbeFolder, true);
                File.Delete(ProbeFolder + ".meta");
                AssetDatabase.Refresh();
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        /// <summary>One call = one request: the HTTP layer drops the finder's scene cache after every request, and a
        /// cache built before a skill created an object would otherwise hide that object from the next call.</summary>
        protected static JObject Run(string skill, JObject args)
        {
            try { return JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None))); }
            finally { GameObjectFinder.InvalidateCache(); }
        }

        /// <summary>Asserts success and returns the skill's own result object.</summary>
        protected static JObject Ok(JObject json)
        {
            Assert.That(json["status"]?.ToString(), Is.EqualTo("success"), json.ToString(Formatting.None));
            return json["result"] as JObject ?? new JObject();
        }

        protected static void AssertSemanticInvalid(JObject json, string parameter)
        {
            var text = json.ToString(Formatting.None);
            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"), text);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), text);
            Assert.That(text, Does.Contain("\"" + parameter + "\""), $"The rejection must name '{parameter}': {text}");
        }

        protected static void AssertError(JObject json)
        {
            Assert.That(json["status"]?.ToString(), Is.EqualTo("error"), json.ToString(Formatting.None));
        }

        // AssetDatabase.CreateFolder would pick "R6OptProbe 1" if a stray, unimported folder of that name exists.
        protected static void EnsureProbeFolder()
        {
            Directory.CreateDirectory(ProbeFolder);
            AssetDatabase.ImportAsset(ProbeFolder);
        }

        protected static Type PackageType(string fullName)
        {
            var type = SkillsCommon.FindTypeByName(fullName);
            Assert.That(type, Is.Not.Null, $"{fullName} is not loaded although the probe found the package.");
            return type;
        }

        protected static Component ComponentNamed(GameObject go, string typeName) =>
            go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == typeName);

        /// <summary>Reads a public or non-public instance field or property by reflection.</summary>
        protected static object Member(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target);
                var field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field.GetValue(target);
            }
            Assert.Fail($"{target.GetType().Name} has no member '{name}'.");
            return null;
        }

        protected static GameObject Find(string name)
        {
            GameObjectFinder.InvalidateCache();
            var go = GameObjectFinder.Find(name);
            Assert.That(go, Is.Not.Null, $"GameObject '{name}' was not found.");
            return go;
        }
    }
}

// Producer:Betsy
