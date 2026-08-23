using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class VersionCheckServiceTests
    {
        private const string PrefDismissedVersion = "UnitySkills_UpdateDismissedVersion";
        private const string PrefNotificationsEnabled = "UnitySkills_UpdateNotificationsEnabled";

        [TestCase("v2.4.3", "2.4.2", 1)]
        [TestCase("2.5.0", "v2.5.0", 0)]
        [TestCase("3.0.0", "2.99.99", 1)]
        public void TryCompareVersions_UsesSemanticOrdering(string left, string right, int expected)
        {
            Assert.That(VersionCheckService.TryCompareVersions(left, right, out var comparison), Is.True);
            Assert.That(System.Math.Sign(comparison), Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("vNext")]
        [TestCase("3.0")]
        [TestCase("2.4.3-beta.1")]
        [TestCase("2.4.3.1")]
        public void TryCompareVersions_RejectsInvalidVersions(string value)
        {
            Assert.That(VersionCheckService.TryCompareVersions(value, "2.4.3", out _), Is.False);
        }

        [Test]
        public void ShouldShowUpdate_RespectsDismissedRelease()
        {
            Assert.That(VersionCheckService.ShouldShowUpdate("2.4.2", "v2.4.3", ""), Is.True);
            Assert.That(VersionCheckService.ShouldShowUpdate("2.4.2", "v2.4.3", "2.4.3"), Is.False);
            Assert.That(VersionCheckService.ShouldShowUpdate("2.4.2", "v2.4.4", "2.4.3"), Is.True);
            Assert.That(VersionCheckService.ShouldShowUpdate("2.5.0", "v2.4.3", ""), Is.False);
        }

        [Test]
        public void TryCreateReleaseInfo_ReadsPublishedStableRelease()
        {
            const string json = @"{
                'tag_name': 'v2.4.3',
                'html_url': 'https://github.com/Besty0728/Unity-Skills/releases/tag/v2.4.3',
                'draft': false,
                'prerelease': false
            }";

            Assert.That(VersionCheckService.TryCreateReleaseInfo(json, out var release), Is.True);
            Assert.That(release.Version, Is.EqualTo("2.4.3"));
            Assert.That(release.TagName, Is.EqualTo("v2.4.3"));
            Assert.That(release.ReleaseUrl, Is.EqualTo(
                "https://github.com/Besty0728/Unity-Skills/releases/tag/v2.4.3"));
        }

        [Test]
        public void TryCreateReleaseInfo_BindsUrlToTagName()
        {
            const string json = @"{
                'tag_name': 'v2.4.4',
                'html_url': 'https://github.com/Besty0728/Unity-Skills/releases/tag/v9.9.9',
                'draft': false,
                'prerelease': false
            }";

            Assert.That(VersionCheckService.TryCreateReleaseInfo(json, out var release), Is.True);
            Assert.That(release.ReleaseUrl, Does.EndWith("/v2.4.4"));
        }

        [TestCase("draft", true)]
        [TestCase("prerelease", true)]
        public void TryCreateReleaseInfo_RejectsNonStableRelease(string flag, bool value)
        {
            var json = $@"{{
                'tag_name': 'v2.4.4',
                'html_url': 'https://github.com/Besty0728/Unity-Skills/releases/tag/v2.4.4',
                '{flag}': {value.ToString().ToLowerInvariant()}
            }}";

            Assert.That(VersionCheckService.TryCreateReleaseInfo(json, out _), Is.False);
        }

        [Test]
        public void NotificationsEnabled_DefaultsToTrue()
        {
            var hadValue = EditorPrefs.HasKey(PrefNotificationsEnabled);
            var previousValue = EditorPrefs.GetBool(PrefNotificationsEnabled, true);
            try
            {
                EditorPrefs.DeleteKey(PrefNotificationsEnabled);
                Assert.That(VersionCheckService.NotificationsEnabled, Is.True);
            }
            finally
            {
                if (hadValue) EditorPrefs.SetBool(PrefNotificationsEnabled, previousValue);
                else EditorPrefs.DeleteKey(PrefNotificationsEnabled);
            }
        }

        [Test]
        public void Dismiss_PersistsDisplayedVersion()
        {
            var hadValue = EditorPrefs.HasKey(PrefDismissedVersion);
            var previousValue = EditorPrefs.GetString(PrefDismissedVersion, string.Empty);
            try
            {
                var release = new VersionCheckService.ReleaseInfo(
                    "2.6.0",
                    "v2.6.0",
                    "https://github.com/Besty0728/Unity-Skills/releases/tag/v2.6.0");

                VersionCheckService.Dismiss(release);

                Assert.That(EditorPrefs.GetString(PrefDismissedVersion), Is.EqualTo("2.6.0"));
            }
            finally
            {
                if (hadValue) EditorPrefs.SetString(PrefDismissedVersion, previousValue);
                else EditorPrefs.DeleteKey(PrefDismissedVersion);
            }
        }

    }
}

// Producer:Betsy
