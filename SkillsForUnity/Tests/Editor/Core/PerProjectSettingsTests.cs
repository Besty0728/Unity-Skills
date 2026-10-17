using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Every setting that moved from machine-wide EditorPrefs to the per-project store, through the API that owns it:
    /// a change in project A persists there (reopening A is what a domain reload does), never shows in project B, never
    /// writes the global EditorPrefs, and still raises the owner's change event; a project without its own value reads
    /// the old global one. Both projects live in a temp directory.
    /// </summary>
    [TestFixture]
    public class PerProjectSettingsTests
    {
        private const string AllowlistProbe = "unity_skills_test_per_project_probe";

        private string _tempRoot;
        private ProjectPrefsTestScope _scope;
        private bool? _savedConfirmationOverride;
        private int _savedTimeoutCache;

        private static readonly FieldInfo TimeoutCacheField =
            typeof(SkillsHttpServer).GetField("_cachedTimeoutMs", BindingFlags.NonPublic | BindingFlags.Static);

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "unityskills-per-project-" + Guid.NewGuid().ToString("N"));
            _savedConfirmationOverride = ConfirmationTokenService.RequireConfirmationOverrideForTests;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            Assert.That(TimeoutCacheField, Is.Not.Null);
            _savedTimeoutCache = (int)TimeoutCacheField.GetValue(null);
        }

        [TearDown]
        public void TearDown()
        {
            _scope?.Dispose();
            _scope = null;
            // The timeout setter refreshes the live server's cached timeout; put back the value it had.
            TimeoutCacheField.SetValue(null, _savedTimeoutCache);
            ConfirmationTokenService.RequireConfirmationOverrideForTests = _savedConfirmationOverride;
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }

        /// <summary>Switches to (or reopens) one project; the previous one's scope is closed first.</summary>
        private void Open(string project, IDictionary<string, object> legacy = null, bool legacyFromEditorPrefs = false)
        {
            _scope?.Dispose();
            _scope = null;
            _scope = ProjectPrefsTestScope.AtRoot(Path.Combine(_tempRoot, project), legacy, legacyFromEditorPrefs);
        }

        [Test]
        public void TokenLevel_Maximum_StaysInTheProjectThatChoseIt()
        {
            Open("A");
            int tokenEvents = 0, profileEvents = 0, summaryEvents = 0;
            Action onToken = () => tokenEvents++, onProfile = () => profileEvents++, onSummary = () => summaryEvents++;
            SkillsTokenLevel.OnChanged += onToken;
            SkillsSurfaceProfile.OnChanged += onProfile;
            SkillRouter.SummarySettingsChanged += onSummary;
            try
            {
                SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
                Assert.That(SkillsTokenLevel.TryApplyPreset(TokenLevel.Maximum), Is.True);
            }
            finally
            {
                SkillsTokenLevel.OnChanged -= onToken;
                SkillsSurfaceProfile.OnChanged -= onProfile;
                SkillRouter.SummarySettingsChanged -= onSummary;
            }
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Maximum));
            Assert.That(profileEvents, Is.EqualTo(2), "guide, then full for the preset");
            Assert.That(summaryEvents, Is.GreaterThanOrEqualTo(1));
            Assert.That(tokenEvents, Is.GreaterThanOrEqualTo(3));

            Open("B");
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Full));
            Assert.That(SkillRouter.SummaryAutoTruncate, Is.True, "a fresh project gets the fresh-install default");
            Assert.That(SkillsTokenLevel.Current, Is.Not.EqualTo(TokenLevel.Maximum));
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
            SkillRouter.SummaryPageSize = 5;

            Open("A");
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Maximum));
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Full));
            Assert.That(SkillRouter.SummaryAutoTruncate, Is.False);

            Open("B");
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.NoSceneAuthoring));
            Assert.That(SkillRouter.SummaryPageSize, Is.EqualTo(5));
        }

        [Test]
        public void TokenLevelSources_FallBackToTheOldGlobalValues()
        {
            Open("A", new Dictionary<string, object>
            {
                ["UnitySkills_SurfaceProfile"] = "guide",
                ["UnitySkills_SummaryAutoTruncate"] = true,
                ["UnitySkills_SummaryPageSize"] = 10,
            });
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Standard));
            Assert.That(ProjectPrefs.SettingsFilePath, Does.Not.Exist, "reading the global values writes nothing");

            // The pre-2.7 guide toggle still migrates when no profile was ever chosen.
            Open("B", new Dictionary<string, object> { ["UnitySkills_GuideMode"] = true });
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Guide));
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            Open("B", new Dictionary<string, object> { ["UnitySkills_GuideMode"] = true });
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Full), "the project's own choice wins");
        }

        [Test]
        public void OperatingModeAndPanelApproval_ArePerProject_AndRaiseOnChanged()
        {
            Open("A");
            int modeEvents = 0;
            Action onMode = () => modeEvents++;
            SkillsModeManager.OnChanged += onMode;
            try
            {
                SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
                SkillsModeManager.PanelApprovalRequired = true;
            }
            finally
            {
                SkillsModeManager.OnChanged -= onMode;
            }
            Assert.That(modeEvents, Is.EqualTo(2));

            Open("B");
            Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Auto), "fresh-install default");
            Assert.That(SkillsModeManager.PanelApprovalRequired, Is.False);

            Open("A");
            Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Approval));
            Assert.That(SkillsModeManager.PanelApprovalRequired, Is.True);
        }

        [Test]
        public void OperatingMode_ReadsTheOldGlobalValueUntilChangedHere()
        {
            var legacy = new Dictionary<string, object> { ["UnitySkills_OperatingMode"] = "Approval" };
            Open("A", legacy);
            Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Approval));
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            Open("B", legacy);
            Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Approval));

            Open("A", legacy);
            Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Bypass));
            Assert.That(legacy["UnitySkills_OperatingMode"], Is.EqualTo("Approval"));
        }

        [Test]
        public void Allowlist_IsPerProject_AndTheV19GrantedListMigratesIntoEachProject()
        {
            Open("A");
            Assert.That(SkillsModeManager.AddToAllowlist(AllowlistProbe), Is.True);

            Open("B");
            Assert.That(SkillsModeManager.IsInAllowlist(AllowlistProbe), Is.False);

            Open("A");
            Assert.That(SkillsModeManager.IsInAllowlist(AllowlistProbe), Is.True);

            var legacy = new Dictionary<string, object> { ["UnitySkills_GrantedSkills"] = "[\"alpha\"]" };
            Open("C", legacy);
            Assert.That(SkillsModeManager.AllowlistSkills, Is.EquivalentTo(new[] { "alpha" }));
            Assert.That(ProjectPrefs.GetBool("UnitySkills_AllowlistMigratedFromGranted"), Is.True);
            SkillsModeManager.RemoveFromAllowlist("alpha");

            Open("C", legacy);
            Assert.That(SkillsModeManager.AllowlistSkills, Is.Empty, "the migration ran once for this project");
        }

        [Test]
        public void RequireConfirmation_IsPerProject()
        {
            Open("A");
            ConfirmationTokenService.RequireConfirmation = true;
            Open("B");
            Assert.That(ConfirmationTokenService.RequireConfirmation, Is.False);
            Open("A");
            Assert.That(ConfirmationTokenService.RequireConfirmation, Is.True);
        }

        [Test]
        public void PortAndRequestTimeout_ArePerProject()
        {
            Open("A");
            SkillsHttpServer.PreferredPort = 8097;
            SkillsHttpServer.RequestTimeoutMinutes = 7;
            Open("B");
            Assert.That(SkillsHttpServer.PreferredPort, Is.EqualTo(0));
            Assert.That(SkillsHttpServer.RequestTimeoutMinutes, Is.EqualTo(15));
            Open("A");
            Assert.That(SkillsHttpServer.PreferredPort, Is.EqualTo(8097));
            Assert.That(SkillsHttpServer.RequestTimeoutMinutes, Is.EqualTo(7));
        }

        [Test]
        public void Telemetry_IsPerProject_AndRaisesOnChanged()
        {
            Open("A");
            int events = 0;
            Action onChanged = () => events++;
            SkillTelemetryService.OnChanged += onChanged;
            try
            {
                SkillTelemetryService.Enabled = false;
            }
            finally
            {
                SkillTelemetryService.OnChanged -= onChanged;
            }
            Assert.That(events, Is.EqualTo(1));
            Open("B");
            Assert.That(SkillTelemetryService.Enabled, Is.True);
            Open("A");
            Assert.That(SkillTelemetryService.Enabled, Is.False);
        }

        [Test]
        public void AutoInstallPackagesOnStartup_IsPerProject()
        {
            // Only the stored flag is read back; nothing here refreshes the package list, which is what acts on it.
            Open("A");
            PackageManagerHelper.AutoInstallPackagesOnStartup = true;
            Open("B");
            Assert.That(PackageManagerHelper.AutoInstallPackagesOnStartup, Is.False);
            Open("A");
            Assert.That(PackageManagerHelper.AutoInstallPackagesOnStartup, Is.True);
        }

        [Test]
        public void WorkflowHistoryLimits_ArePerProject()
        {
            Open("A");
            WorkflowAutoCleanConfig.Enabled = false;
            WorkflowAutoCleanConfig.MaxTasks = 11;
            WorkflowAutoCleanConfig.MaxHistoryMB = 12;
            WorkflowAutoCleanConfig.MaxTaskAgeDays = 13;
            WorkflowAutoCleanConfig.MaxStoreMB = 14;
            WorkflowAutoCleanConfig.StoreMaxAgeDays = 15;

            Open("B");
            Assert.That(WorkflowAutoCleanConfig.Enabled, Is.True);
            Assert.That(WorkflowAutoCleanConfig.MaxTasks, Is.EqualTo(200));
            Assert.That(WorkflowAutoCleanConfig.MaxHistoryMB, Is.EqualTo(32));
            Assert.That(WorkflowAutoCleanConfig.MaxTaskAgeDays, Is.EqualTo(30));
            Assert.That(WorkflowAutoCleanConfig.MaxStoreMB, Is.EqualTo(512));
            Assert.That(WorkflowAutoCleanConfig.StoreMaxAgeDays, Is.EqualTo(7));

            Open("A");
            Assert.That(WorkflowAutoCleanConfig.Enabled, Is.False);
            Assert.That(WorkflowAutoCleanConfig.MaxTasks, Is.EqualTo(11));
            Assert.That(WorkflowAutoCleanConfig.MaxHistoryMB, Is.EqualTo(12));
            Assert.That(WorkflowAutoCleanConfig.MaxTaskAgeDays, Is.EqualTo(13));
            Assert.That(WorkflowAutoCleanConfig.MaxStoreMB, Is.EqualTo(14));
            Assert.That(WorkflowAutoCleanConfig.StoreMaxAgeDays, Is.EqualTo(15));
        }

        [Test]
        public void EvaluationPanelConfig_IsPerProject()
        {
            Open("A");
            EvaluationTabController.WritePanelConfig(new EvaluationTabController.PanelConfig
            {
                Track = "dev", Tier = "challenge", RunMode = "strict", Host = "codex",
                Model = "model-a", Effort = "high", UsageReading = false,
            });

            Open("B");
            var fresh = EvaluationTabController.ReadPanelConfig();
            Assert.That(fresh.Track, Is.EqualTo("ops"));
            Assert.That(fresh.Model, Is.Empty);
            Assert.That(fresh.UsageReading, Is.True);

            Open("A");
            var stored = EvaluationTabController.ReadPanelConfig();
            Assert.That(new[] { stored.Track, stored.Tier, stored.RunMode, stored.Host, stored.Model, stored.Effort },
                Is.EqualTo(new[] { "dev", "challenge", "strict", "codex", "model-a", "high" }));
            Assert.That(stored.UsageReading, Is.False);
        }

        [Test]
        public void HealthSnapshot_ReflectsTheProjectInUse()
        {
            Open("A");
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsTokenLevel.TryApplyPreset(TokenLevel.Standard);
            SkillsHttpServer.RequestTimeoutMinutes = 9;
            var a = HealthSnapshot();
            Assert.That(a.Value<string>("currentMode"), Is.EqualTo("approval"));
            Assert.That(a.Value<string>("surfaceProfile"), Is.EqualTo("guide"));
            Assert.That(a.Value<string>("tokenLevel"), Is.EqualTo("standard"));
            Assert.That(a.Value<int>("requestTimeoutMinutes"), Is.EqualTo(9));

            Open("B");
            SkillsTokenLevel.TryApplyPreset(TokenLevel.Maximum);
            var b = HealthSnapshot();
            Assert.That(b.Value<string>("currentMode"), Is.EqualTo("auto"));
            Assert.That(b.Value<string>("surfaceProfile"), Is.EqualTo("full"));
            Assert.That(b.Value<string>("tokenLevel"), Is.EqualTo("maximum"));
            Assert.That(b.Value<bool>("summaryAutoTruncate"), Is.False);
            Assert.That(b.Value<int>("requestTimeoutMinutes"), Is.EqualTo(15));
        }

        [Test]
        public void ChangingEverySetting_NeverWritesTheGlobalEditorPrefs()
        {
            var keys = new[]
            {
                "UnitySkills_SurfaceProfile", "UnitySkills_GuideMode", "UnitySkills_SummaryAutoTruncate",
                "UnitySkills_SummaryPageSize", "UnitySkills_OperatingMode", "UnitySkills_PanelApprovalRequired",
                "UnitySkills_AllowlistSkills", "UnitySkills_AllowlistMigratedFromGranted", "UnitySkills_GrantedSkills",
                "UnitySkills_RequireConfirmation", "UnitySkills_PreferredPort", "UnitySkills_RequestTimeoutMinutes",
                "UnitySkills_TelemetryEnabled", "UnitySkills_AutoInstallPackagesOnStartup", "UnitySkills_EvalPanelConfig",
                EvaluationModeGuard.PrefKey,
                "UnitySkills.Workflow.Enabled", "UnitySkills.Workflow.MaxTasks", "UnitySkills.Workflow.MaxHistoryMB",
                "UnitySkills.Workflow.MaxTaskAgeDays", "UnitySkills.Workflow.MaxStoreMB",
                "UnitySkills.Workflow.StoreMaxAgeDays",
            };
            var before = keys.Select(GlobalPref).ToArray();

            // The editor's own configuration: the old global values are read from the real EditorPrefs (read-only here).
            Open("A", legacyFromEditorPrefs: true);
            SkillsSurfaceProfile.Current = SkillsSurfaceProfile.Current == SurfaceProfileKind.Guide
                ? SurfaceProfileKind.Full
                : SurfaceProfileKind.Guide;
            SkillRouter.SummaryAutoTruncate = !SkillRouter.SummaryAutoTruncate;
            SkillRouter.SummaryPageSize = SkillRouter.SummaryPageSize == 17 ? 18 : 17;
            SkillsModeManager.CurrentMode = SkillsModeManager.CurrentMode == SkillsOperatingMode.Approval
                ? SkillsOperatingMode.Auto
                : SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = !SkillsModeManager.PanelApprovalRequired;
            SkillsModeManager.AddToAllowlist(AllowlistProbe);
            ConfirmationTokenService.RequireConfirmation = !ConfirmationTokenService.RequireConfirmation;
            SkillsHttpServer.PreferredPort = SkillsHttpServer.PreferredPort == 8099 ? 8098 : 8099;
            SkillsHttpServer.RequestTimeoutMinutes = SkillsHttpServer.RequestTimeoutMinutes == 41 ? 42 : 41;
            SkillTelemetryService.Enabled = !SkillTelemetryService.Enabled;
            PackageManagerHelper.AutoInstallPackagesOnStartup = false;
            WorkflowAutoCleanConfig.Enabled = !WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.MaxTasks = WorkflowAutoCleanConfig.MaxTasks == 201 ? 202 : 201;
            WorkflowAutoCleanConfig.MaxHistoryMB = WorkflowAutoCleanConfig.MaxHistoryMB == 33 ? 34 : 33;
            WorkflowAutoCleanConfig.MaxTaskAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays == 31 ? 32 : 31;
            WorkflowAutoCleanConfig.MaxStoreMB = WorkflowAutoCleanConfig.MaxStoreMB == 513 ? 514 : 513;
            WorkflowAutoCleanConfig.StoreMaxAgeDays = WorkflowAutoCleanConfig.StoreMaxAgeDays == 8 ? 9 : 8;
            EvaluationTabController.WritePanelConfig(new EvaluationTabController.PanelConfig { Model = Guid.NewGuid().ToString("N") });
            _scope.Dispose();
            _scope = null;

            Assert.That(keys.Select(GlobalPref).ToArray(), Is.EqualTo(before));
            var values = (JObject)JObject.Parse(File.ReadAllText(ProjectPrefs.SettingsFileFor(Path.Combine(_tempRoot, "A"))))["values"];
            Assert.That(values.Properties().Select(p => p.Name), Is.SupersetOf(new[]
            {
                "UnitySkills_SurfaceProfile", "UnitySkills_SummaryAutoTruncate", "UnitySkills_SummaryPageSize",
                "UnitySkills_OperatingMode", "UnitySkills_PanelApprovalRequired", "UnitySkills_AllowlistSkills",
                "UnitySkills_RequireConfirmation", "UnitySkills_PreferredPort", "UnitySkills_RequestTimeoutMinutes",
                "UnitySkills_TelemetryEnabled", "UnitySkills_AutoInstallPackagesOnStartup", "UnitySkills_EvalPanelConfig",
                "UnitySkills.Workflow.Enabled", "UnitySkills.Workflow.MaxTasks", "UnitySkills.Workflow.MaxHistoryMB",
                "UnitySkills.Workflow.MaxTaskAgeDays", "UnitySkills.Workflow.MaxStoreMB",
                "UnitySkills.Workflow.StoreMaxAgeDays",
            }));
        }

        private static string GlobalPref(string key)
        {
            return EditorPrefs.HasKey(key)
                ? key + "=" + EditorPrefs.GetString(key, "<s>") + "|" + EditorPrefs.GetInt(key, int.MinValue)
                : key + " absent";
        }

        private static JObject HealthSnapshot()
        {
            var server = typeof(SkillsHttpServer);
            try
            {
                server.GetMethod("RefreshHealthSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { true });
                var vitals = server.GetNestedType("HealthVitals", BindingFlags.NonPublic)
                    .GetMethod("FromSnapshot", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                var json = (string)server.GetMethod("BuildHealthJson", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new[] { vitals, (object)false });
                return JObject.Parse(json);
            }
            finally
            {
                // The live server's snapshot now mirrors the test store; the next editor tick re-reads it.
                server.GetField("_healthSnapshotDirty", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            }
        }
    }
}

// Producer:Betsy
