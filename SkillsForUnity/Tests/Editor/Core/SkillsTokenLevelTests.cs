using System.Collections.Generic;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Pure derivation and one-shot default migration coverage for token levels. Each test gets a fresh in-memory
    /// per-project store whose stand-in for the pre-3.0 global EditorPrefs starts empty.
    /// </summary>
    [TestFixture]
    public class SkillsTokenLevelTests
    {
        private const string TruncateKey = "UnitySkills_SummaryAutoTruncate";
        private const string PageSizeKey = "UnitySkills_SummaryPageSize";
        private Dictionary<string, object> _legacy;
        private ProjectPrefsTestScope _settings;

        [SetUp]
        public void SetUp()
        {
            _legacy = new Dictionary<string, object>();
            _settings = ProjectPrefsTestScope.InMemory(_legacy);
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillRouter.ResetSummaryPreferencesForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            _settings.Dispose();
        }

        [Test]
        public void Resolve_MapsCanonicalTuplesAndCustomValues()
        {
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.NoSceneAuthoring, true, 5), Is.EqualTo(TokenLevel.Minimal));
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.Guide, true, 10), Is.EqualTo(TokenLevel.Standard));
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.Full, true, 20), Is.EqualTo(TokenLevel.Full));
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.Full, false, 99), Is.EqualTo(TokenLevel.Maximum));
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.Guide, false, 10), Is.EqualTo(TokenLevel.Custom));
            Assert.That(SkillsTokenLevel.Resolve(SurfaceProfileKind.Full, true, 6), Is.EqualTo(TokenLevel.Custom));
        }

        [Test]
        public void FreshInstall_MigratesToTruncationOnAndDefaultPage()
        {
            ProjectPrefs.DeleteKey(TruncateKey);
            ProjectPrefs.DeleteKey(PageSizeKey);
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            SkillRouter.ResetSummaryPreferencesForTests();

            Assert.That(SkillRouter.SummaryAutoTruncate, Is.True);
            Assert.That(SkillRouter.SummaryPageSize, Is.EqualTo(10));
            Assert.That(ProjectPrefs.GetBool(TruncateKey, false), Is.True);
            Assert.That(ProjectPrefs.GetInt(PageSizeKey, 0), Is.EqualTo(10));
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Standard));
        }

        [Test]
        public void ExistingInstall_MigratesToHistoricTruncationOff()
        {
            ProjectPrefs.DeleteKey(TruncateKey);
            SkillRouter.ResetSummaryPreferencesForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = true;

            Assert.That(SkillRouter.SummaryAutoTruncate, Is.False);
            Assert.That(ProjectPrefs.GetBool(TruncateKey, true), Is.False);
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Maximum));
        }

        [Test]
        public void LegacyGlobalSettings_AreReadAsTheyAre_WithoutAOneShotDefault()
        {
            // An upgraded machine: the pre-3.0 global values decide the level until this project changes them.
            _legacy[TruncateKey] = false;
            _legacy[PageSizeKey] = 20;
            _legacy["UnitySkills_SurfaceProfile"] = "full";
            SkillsSurfaceProfile.ReloadForTests();
            SkillRouter.ResetSummaryPreferencesForTests();

            Assert.That(SkillRouter.SummaryAutoTruncate, Is.False);
            Assert.That(SkillRouter.SummaryPageSize, Is.EqualTo(20));
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Maximum));

            SkillsTokenLevel.TryApplyPreset(TokenLevel.Full);
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Full));
            Assert.That(_legacy[TruncateKey], Is.EqualTo(false), "the global value is never written");
        }

        [Test]
        public void TryApplyPreset_UsesOnlySourceSettings()
        {
            Assert.That(SkillsTokenLevel.TryApplyPreset(TokenLevel.Minimal), Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SurfaceProfile, Is.EqualTo(SurfaceProfileKind.NoSceneAuthoring));
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryAutoTruncate, Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryPageSize, Is.EqualTo(5));

            Assert.That(SkillsTokenLevel.TryApplyPreset(TokenLevel.Standard), Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SurfaceProfile, Is.EqualTo(SurfaceProfileKind.Guide));
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryAutoTruncate, Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryPageSize, Is.EqualTo(10));

            Assert.That(SkillsTokenLevel.TryApplyPreset(TokenLevel.Full), Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SurfaceProfile, Is.EqualTo(SurfaceProfileKind.Full));
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryAutoTruncate, Is.True);
            Assert.That(SkillsTokenLevel.CurrentSettings.SummaryPageSize, Is.EqualTo(20));

            Assert.That(SkillsTokenLevel.TryApplyPreset(TokenLevel.Custom), Is.False);
        }
    }
}

// Producer:Betsy
