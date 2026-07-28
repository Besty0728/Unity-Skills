using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ServerAutoStartTests
    {
        [TestCase(true, false, false, SkillsHttpServer.AutoStartReason.DomainReload)]
        [TestCase(false, true, false, SkillsHttpServer.AutoStartReason.EditorLaunch)]
        [TestCase(false, false, true, SkillsHttpServer.AutoStartReason.CliColdStart)]
        [TestCase(true, true, true, SkillsHttpServer.AutoStartReason.CliColdStart)]
        [TestCase(false, false, false, SkillsHttpServer.AutoStartReason.None)]
        public void GetAutoStartReason_ReturnsExpectedSource(
            bool restoreRequested,
            bool editorLaunchRequested,
            bool cliColdStart,
            SkillsHttpServer.AutoStartReason expected)
        {
            Assert.That(
                SkillsHttpServer.GetAutoStartReason(restoreRequested, editorLaunchRequested, cliColdStart),
                Is.EqualTo(expected));
        }
    }
}
