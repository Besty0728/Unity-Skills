using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// QFramework editor skills against the Toolkits source copied into Assets/QFramework (opt2022 only — QFramework
    /// has no UPM package). UIKit settings, ResKit build options and the editor locale all persist outside the scene
    /// and outside ProbeFolder, so every test captures the current value first and every value touched is restored
    /// in TearDown regardless of which test changed it. AudioKit settings, MarkAB, code generation and AssetBundle
    /// building are deliberately never called (see AGENTS.md / task instructions).
    /// </summary>
    [TestFixture]
    public class QFrameworkSkillsLiveTests : OptionalPackageTestBase
    {
        protected override string ProbeSkill => "qframework_get_uikit_settings";

        private JObject _originalUIKitSettings;
        private JObject _originalResKitOptions;
        private bool _originalEditorLocale;

        [SetUp]
        public void CaptureQFrameworkSettings()
        {
            _originalUIKitSettings = Ok(Run("qframework_get_uikit_settings", new JObject()));
            _originalResKitOptions = Ok(Run("qframework_get_reskit_build_options", new JObject()));
            _originalEditorLocale = Ok(Run("qframework_get_status", new JObject()))["editorLocaleIsCN"].Value<bool>();
        }

        [TearDown]
        public void RestoreQFrameworkSettings()
        {
            // Values are re-extracted as plain strings/bools (not the original JToken instances) so restoring
            // never fights Json.NET over a token that already belongs to the captured JObject.
            Run("qframework_set_uikit_settings", new JObject
            {
                ["namespaceName"] = _originalUIKitSettings["namespaceName"]?.ToString(),
                ["uiScriptDir"] = _originalUIKitSettings["uiScriptDir"]?.ToString(),
                ["uiPrefabDir"] = _originalUIKitSettings["uiPrefabDir"]?.ToString(),
                ["assemblyNamesToSearch"] = _originalUIKitSettings["assemblyNamesToSearch"]?.ToString(Formatting.None),
            });
            Run("qframework_set_reskit_build_options", new JObject
            {
                ["simulationMode"] = _originalResKitOptions["simulationMode"]?.Value<bool>(),
                ["appendHash"] = _originalResKitOptions["appendHash"]?.Value<bool>(),
                ["autoGenerateClass"] = _originalResKitOptions["autoGenerateClass"]?.Value<bool>(),
            });
            Run("qframework_set_editor_locale", new JObject { ["isCN"] = _originalEditorLocale });
        }

        [Test]
        public void GetStatus_ReportsToolkitsInstalled()
        {
            var status = Ok(Run("qframework_get_status", new JObject()));
            Assert.That(status["installed"]?.Value<bool>(), Is.True, status.ToString());
            Assert.That(status["installKind"]?.ToString(), Is.EqualTo("toolkits"), status.ToString());
        }

        [Test]
        public void SetUIKitSettings_RoundTripsAndReadsBack()
        {
            var result = Ok(Run("qframework_set_uikit_settings", new JObject
            {
                ["namespaceName"] = "R6OptProbe.Generated",
                ["uiScriptDir"] = "/R6Scripts/UI",
                ["uiPrefabDir"] = "/R6Art/UIPrefab",
                ["assemblyNamesToSearch"] = "[\"Assembly-CSharp\",\"R6Fake\"]",
            }));
            Assert.That(result["changed"]?.Values<string>(), Is.EquivalentTo(new[]
            {
                "namespaceName", "uiScriptDir", "uiPrefabDir", "assemblyNamesToSearch",
            }), result.ToString());
            Assert.That(result["namespaceName"]?.ToString(), Is.EqualTo("R6OptProbe.Generated"));

            var readBack = Ok(Run("qframework_get_uikit_settings", new JObject()));
            Assert.That(readBack["namespaceName"]?.ToString(), Is.EqualTo("R6OptProbe.Generated"), readBack.ToString());
            Assert.That(readBack["uiScriptDir"]?.ToString(), Is.EqualTo("/R6Scripts/UI"));
            Assert.That(readBack["uiPrefabDir"]?.ToString(), Is.EqualTo("/R6Art/UIPrefab"));
            Assert.That(readBack["assemblyNamesToSearch"]?.Values<string>(), Is.EquivalentTo(new[] { "Assembly-CSharp", "R6Fake" }));
        }

        [Test]
        public void SetUIKitSettings_PartialUpdate_OnlyChangesRequestedField()
        {
            var result = Ok(Run("qframework_set_uikit_settings", new JObject { ["namespaceName"] = "R6OnlyNamespace" }));
            Assert.That(result["changed"]?.Values<string>(), Is.EquivalentTo(new[] { "namespaceName" }), result.ToString());

            var after = Ok(Run("qframework_get_uikit_settings", new JObject()));
            Assert.That(after["namespaceName"]?.ToString(), Is.EqualTo("R6OnlyNamespace"));
            Assert.That(after["uiScriptDir"]?.ToString(), Is.EqualTo(_originalUIKitSettings["uiScriptDir"]?.ToString()));
            Assert.That(after["uiPrefabDir"]?.ToString(), Is.EqualTo(_originalUIKitSettings["uiPrefabDir"]?.ToString()));
        }

        [Test]
        public void SetUIKitSettings_MalformedAssemblyNamesJson_IsRejected()
        {
            var result = Run("qframework_set_uikit_settings", new JObject { ["assemblyNamesToSearch"] = "not-json" });
            Assert.That(result["status"]?.ToString(), Is.EqualTo("error"), result.ToString());
            Assert.That(result["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), result.ToString());
            Assert.That(result["error"]?.ToString(), Does.Contain("assemblyNamesToSearch"));

            var after = Ok(Run("qframework_get_uikit_settings", new JObject()));
            Assert.That(after["namespaceName"]?.ToString(), Is.EqualTo(_originalUIKitSettings["namespaceName"]?.ToString()),
                "Malformed JSON must be rejected before any field is written.");
        }

        [Test]
        public void SetResKitBuildOptions_RoundTripsBooleans()
        {
            bool newSim = !_originalResKitOptions["simulationMode"].Value<bool>();
            bool newAppend = !_originalResKitOptions["appendHash"].Value<bool>();
            bool newAuto = !_originalResKitOptions["autoGenerateClass"].Value<bool>();

            var result = Ok(Run("qframework_set_reskit_build_options", new JObject
            {
                ["simulationMode"] = newSim, ["appendHash"] = newAppend, ["autoGenerateClass"] = newAuto,
            }));
            Assert.That(result["changed"]?.Values<string>(), Is.EquivalentTo(new[]
            {
                "simulationMode", "appendHash", "autoGenerateClass",
            }), result.ToString());
            Assert.That(result["simulationMode"]?.Value<bool>(), Is.EqualTo(newSim));
            Assert.That(result["appendHash"]?.Value<bool>(), Is.EqualTo(newAppend));
            Assert.That(result["autoGenerateClass"]?.Value<bool>(), Is.EqualTo(newAuto));

            var after = Ok(Run("qframework_get_reskit_build_options", new JObject()));
            Assert.That(after["simulationMode"]?.Value<bool>(), Is.EqualTo(newSim));
            Assert.That(after["appendHash"]?.Value<bool>(), Is.EqualTo(newAppend));
            Assert.That(after["autoGenerateClass"]?.Value<bool>(), Is.EqualTo(newAuto));
        }

        [Test]
        public void SetEditorLocale_ChangesAndReadsBackViaGetStatus()
        {
            bool flipped = !_originalEditorLocale;

            var result = Ok(Run("qframework_set_editor_locale", new JObject { ["isCN"] = flipped }));
            Assert.That(result["changed"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["isCN"]?.Value<bool>(), Is.EqualTo(flipped), result.ToString());

            var afterStatus = Ok(Run("qframework_get_status", new JObject()));
            Assert.That(afterStatus["editorLocaleIsCN"]?.Value<bool>(), Is.EqualTo(flipped), afterStatus.ToString());

            var noOp = Ok(Run("qframework_set_editor_locale", new JObject { ["isCN"] = flipped }));
            Assert.That(noOp["changed"]?.Value<bool>(), Is.False, "Setting the same value again must be a no-op.");
        }

        [Test]
        public void PreviewArchitectureCode_ForModel_ReturnsValidCode()
        {
            EnsureProbeFolder();

            var result = Ok(Run("qframework_preview_architecture_code", new JObject
            {
                ["codeType"] = "Model", ["inputName"] = "R6TestModel", ["namespaceName"] = "R6OptProbe", ["outputRoot"] = ProbeFolder,
            }));
            Assert.That(result["isValid"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["className"]?.ToString(), Does.Contain("R6TestModel"), result.ToString());
            Assert.That(result["code"]?.ToString(), Does.Contain("R6TestModel"), result.ToString());
        }
    }
}

// Producer:Betsy
