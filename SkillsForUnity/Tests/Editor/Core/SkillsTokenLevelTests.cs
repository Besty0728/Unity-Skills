using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>Pure derivation and one-shot default migration coverage for token levels.</summary>
    [TestFixture]
    public class SkillsTokenLevelTests
    {
        private const string TruncateKey = "UnitySkills_SummaryAutoTruncate";
        private const string PageSizeKey = "UnitySkills_SummaryPageSize";
        private bool _hadTruncate;
        private bool _savedTruncate;
        private bool _hadPageSize;
        private int _savedPageSize;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _hadTruncate = EditorPrefs.HasKey(TruncateKey);
            _savedTruncate = EditorPrefs.GetBool(TruncateKey, false);
            _hadPageSize = EditorPrefs.HasKey(PageSizeKey);
            _savedPageSize = EditorPrefs.GetInt(PageSizeKey, SkillsTokenLevel.DefaultSummaryPageSize);
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillRouter.ResetSummaryPreferencesForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadTruncate) EditorPrefs.SetBool(TruncateKey, _savedTruncate);
            else EditorPrefs.DeleteKey(TruncateKey);
            if (_hadPageSize) EditorPrefs.SetInt(PageSizeKey, _savedPageSize);
            else EditorPrefs.DeleteKey(PageSizeKey);
            SkillRouter.ResetSummaryPreferencesForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsSurfaceProfile.Current = _savedProfile;
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
            EditorPrefs.DeleteKey(TruncateKey);
            EditorPrefs.DeleteKey(PageSizeKey);
            SkillRouter.ResetSummaryPreferencesForTests();

            Assert.That(SkillRouter.SummaryAutoTruncate, Is.True);
            Assert.That(SkillRouter.SummaryPageSize, Is.EqualTo(10));
            Assert.That(EditorPrefs.GetBool(TruncateKey, false), Is.True);
            Assert.That(EditorPrefs.GetInt(PageSizeKey, 0), Is.EqualTo(10));
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Standard));
        }

        [Test]
        public void ExistingInstall_MigratesToHistoricTruncationOff()
        {
            EditorPrefs.DeleteKey(TruncateKey);
            SkillRouter.ResetSummaryPreferencesForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = true;

            Assert.That(SkillRouter.SummaryAutoTruncate, Is.False);
            Assert.That(EditorPrefs.GetBool(TruncateKey, true), Is.False);
            Assert.That(SkillsTokenLevel.Current, Is.EqualTo(TokenLevel.Maximum));
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
