using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class VersionCheckServiceTests
    {
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
            Assert.That(release.ReleaseUrl, Does.EndWith("/v2.4.3"));
        }

    }
}

// Producer:Betsy
