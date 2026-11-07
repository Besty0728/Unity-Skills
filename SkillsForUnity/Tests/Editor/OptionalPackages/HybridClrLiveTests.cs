using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// HybridCLR (com.code-philosophy.hybridclr) live skill tests, restricted to settings/query skills:
    /// hybridclr_status, hybridclr_install_status, hybridclr_settings_get, hybridclr_settings_set. Never runs
    /// install, generate, compile or copy-DLL: the only hybridclr_generate_step calls are rejections that
    /// return before any command resolves. hybridclr_settings_set writes ProjectSettings/HybridCLRSettings.asset,
    /// a real project file, so every test that touches it restores the original value afterward.
    /// The installed package is either API line: 8.x (HybridCLR.Editor.*) or 9.x (HybridCLR.*); tests that only
    /// hold on one line Assert.Ignore on the other.
    /// </summary>
    [TestFixture]
    public class HybridClrSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "hybridclr_settings_get";

        private const string SettingsFile = "ProjectSettings/HybridCLRSettings.asset";
        private static readonly string[] Api9RemovedFields =
            { "patchAOTAssemblies", "hotUpdateDllCompileOutputRootDir", "strippedAOTDllOutputRootDir" };
        private bool _settingsFileExisted;

        private static bool IsApi9 => SkillsCommon.FindTypeByName("HybridCLR.SettingsUtil") != null;

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
            // 8.x only: the 9.x ScriptableSingleton keeps no s_Instance on the settings type, so this finds nothing there.
            SettingsType().GetField("s_Instance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        }

        private static System.Type SettingsType() =>
            SkillsCommon.FindTypeByName("HybridCLR.Settings.HybridCLRSettings")
            ?? PackageType("HybridCLR.Editor.Settings.HybridCLRSettings");

        [Test]
        public void SettingsSet_AfterTheInstanceWasUnloaded_StillWrites()
        {
            if (IsApi9)
                Assert.Ignore("hybridclr_unity 9.x settings are a ScriptableSingleton; the 8.x s_Instance reload path does not exist.");
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
        public void SettingsGet_ListsExactlyTheFieldsTheInstalledLineNoLongerDeclares()
        {
            var result = Ok(Run("hybridclr_settings_get", new JObject()));
            var unsupported = ((JArray)result["unsupportedFields"]).Values<string>().ToArray();

            if (IsApi9)
            {
                Assert.That(unsupported, Is.EquivalentTo(Api9RemovedFields), result.ToString());
                foreach (var field in Api9RemovedFields)
                    Assert.That(result["settings"][field]?.Type, Is.EqualTo(JTokenType.Null), $"{field} must read as an explicit null on 9.x.");
                Assert.That(result["settings"]["linkPreserveLevel"]?.ToString(), Is.Not.Null.And.Not.Empty, "9.x settings carry linkPreserveLevel.");
            }
            else
            {
                Assert.That(unsupported, Is.Empty, result.ToString());
                Assert.That(result["settings"]["patchAOTAssemblies"], Is.InstanceOf<JArray>());
            }
        }

        [Test]
        public void SettingsSet_FieldsRemovedOnApi9_AreRejectedAndNothingIsWritten()
        {
            if (!IsApi9)
                Assert.Ignore("patchAOTAssemblies and the two output-root dirs still exist on the 8.x line.");

            var json = Run("hybridclr_settings_set", new JObject { ["patchAOTAssemblies"] = new JArray("mscorlib") });
            AssertSemanticInvalid(json, "patchAOTAssemblies");
            Assert.That(File.Exists(SettingsFile), Is.EqualTo(_settingsFileExisted), "A rejected call must not save the settings file.");
        }

        [Test]
        public void GenerateStep_AStepOfTheOtherLine_IsRejectedAsInvalidWithTheValidSteps()
        {
            // Called directly rather than through the router: the high-risk confirmation gate is a user preference and could
            // answer first. Both rejections return before any command type is resolved, so nothing can run.
            var wrongLine = IsApi9 ? "clean_il2cpp_cache" : "method_bridge_empty";
            var rejected = JObject.FromObject(HybridCLRSkills.GenerateStep(wrongLine));
            Assert.That(rejected["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), rejected.ToString());
            Assert.That(rejected["parameter"]?.ToString(), Is.EqualTo("step"));

            var valid = rejected["validValues"].Values<string>().ToArray();
            Assert.That(valid, Does.Contain("il2cpp_def").And.Contain("aot_dlls"));
            Assert.That(valid, Does.Not.Contain(wrongLine));
            Assert.That(valid, Does.Contain(IsApi9 ? "method_bridge_empty" : "clean_il2cpp_cache"));

            var unknown = JObject.FromObject(HybridCLRSkills.GenerateStep("bogus"));
            Assert.That(unknown["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), unknown.ToString());
            Assert.That(unknown["validValues"].Values<string>(), Is.EquivalentTo(valid));
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

        [Test]
        public void Status_ReportsTheApiLineAndTheAotListThatLineHas()
        {
            var result = Ok(Run("hybridclr_status", new JObject()));
            Assert.That(result["apiLine"]?.ToString(), Is.EqualTo(IsApi9 ? "9.x" : "8.x"), result.ToString());

            if (IsApi9)
                Assert.That(result["patchAOTAssemblies"]?.Type, Is.EqualTo(JTokenType.Null), "9.x has no patchAOTAssemblies setting.");
            else
                Assert.That(result["patchAOTAssemblies"], Is.InstanceOf<JArray>());
        }

        [Test]
        public void InstallStatus_ReportsTheApiLineAndTheUnityVersionsTheReleaseSupports()
        {
            var result = Ok(Run("hybridclr_install_status", new JObject()));
            Assert.That(result["apiLine"]?.ToString(), Is.EqualTo(IsApi9 ? "9.x" : "8.x"), result.ToString());
            Assert.That(result["unityVersionSupported"]?.Type, Is.EqualTo(JTokenType.Boolean), result.ToString());
            Assert.That((result["supportedUnityVersions"] as JArray)?.Count, Is.GreaterThan(0), result.ToString());
        }
    }
}

// Producer:Betsy
