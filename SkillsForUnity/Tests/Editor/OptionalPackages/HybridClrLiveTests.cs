using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// HybridCLR (com.code-philosophy.hybridclr) live skill tests, restricted to settings/query skills:
    /// hybridclr_status, hybridclr_settings_get, hybridclr_settings_set. Never calls install, generate,
    /// compile or copy-DLL skills (slow, and some write outside Assets/). hybridclr_settings_set writes
    /// ProjectSettings/HybridCLRSettings.asset, a real project file, so every test that touches it
    /// restores the original value afterward.
    /// </summary>
    [TestFixture]
    public class HybridClrSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "hybridclr_settings_get";

        private const string SettingsFile = "ProjectSettings/HybridCLRSettings.asset";
        private bool _settingsFileExisted;

        // A settings file a test creates by saving is removed again, so a project that had none keeps having none
        // (and SettingsSet_AfterTheInstanceWasUnloaded keeps running there).
        [SetUp]
        public void RememberSettingsFile() => _settingsFileExisted = File.Exists(SettingsFile);

        [TearDown]
        public void RemoveCreatedSettingsFile()
        {
            if (_settingsFileExisted || !File.Exists(SettingsFile))
                return;
            File.Delete(SettingsFile);
            SettingsType().GetField("s_Instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }

        private static System.Type SettingsType() => PackageType("HybridCLR.Editor.Settings.HybridCLRSettings");

        [Test]
        public void SettingsSet_AfterTheInstanceWasUnloaded_StillWrites()
        {
            if (_settingsFileExisted)
                Assert.Ignore("HybridCLR reloads from an existing settings file; the stale instance only survives without one.");

            // What a scene change does to the file-less instance: the object is unloaded while s_Instance keeps the
            // dead reference, and LoadOrCreate's `s_Instance ?? CreateInstance()` hands it back. Undo then threw
            // (INTERNAL) and Save() silently wrote nothing.
            var dead = ScriptableObject.CreateInstance(SettingsType());
            Object.DestroyImmediate(dead);
            SettingsType().GetField("s_Instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, dead);

            var set = Ok(Run("hybridclr_settings_set", new JObject { ["maxGenericReferenceIteration"] = 9 }));
            Assert.That(set["settings"]?["maxGenericReferenceIteration"]?.Value<int>(), Is.EqualTo(9), set.ToString());
            Assert.That(File.Exists(SettingsFile), Is.True, "Save() must have written the settings file.");
        }

        [Test]
        public void SettingsGet_ReturnsCurrentSettingsShape()
        {
            var result = Ok(Run("hybridclr_settings_get", new JObject()));
            Assert.That(result["settingsAssetPath"]?.ToString(), Is.EqualTo("ProjectSettings/HybridCLRSettings.asset"), result.ToString());
            Assert.That(result["settings"]?["maxGenericReferenceIteration"], Is.Not.Null, result.ToString());
            Assert.That(result["resolved"], Is.Not.Null);
        }

        [Test]
        public void SettingsSet_MaxGenericReferenceIteration_WritesReadsBackAndRestores()
        {
            var before = Ok(Run("hybridclr_settings_get", new JObject()))["settings"]["maxGenericReferenceIteration"].Value<int>();
            var newValue = before == 7 ? 12 : 7;

            try
            {
                var set = Ok(Run("hybridclr_settings_set", new JObject { ["maxGenericReferenceIteration"] = newValue }));
                Assert.That(set["changed"]?.Values<string>(), Does.Contain("maxGenericReferenceIteration"), set.ToString());
                Assert.That(set["settings"]?["maxGenericReferenceIteration"]?.Value<int>(), Is.EqualTo(newValue));
                Assert.That(((JArray)set["unsupportedFields"]).Count, Is.EqualTo(0), set.ToString());

                var readBack = Ok(Run("hybridclr_settings_get", new JObject()));
                Assert.That(readBack["settings"]?["maxGenericReferenceIteration"]?.Value<int>(), Is.EqualTo(newValue));
            }
            finally
            {
                Ok(Run("hybridclr_settings_set", new JObject { ["maxGenericReferenceIteration"] = before }));
            }
        }

        [Test]
        public void Status_ReportsInstalledAndScriptingBackend()
        {
            var result = Ok(Run("hybridclr_status", new JObject()));
            Assert.That(result["installed"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["scriptingBackend"]?.ToString(), Is.Not.Null.And.Not.Empty);
            Assert.That(result["hotUpdateAssemblies"], Is.InstanceOf<JArray>());
        }
    }
}

// Producer:Betsy
