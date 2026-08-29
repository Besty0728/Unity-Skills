using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ServerAutoStartTests
    {
        // expected is passed as a string rather than AutoStartReason: that enum is internal, and
        // even with InternalsVisibleTo, putting it in a public method's signature would trigger
        // CS0051 (a parameter type's accessibility is lower than the method's), failing the whole
        // test assembly to compile.
        // The enum is a purely internal state machine, not worth making public just for testing,
        // so this compares names by string inside the method body instead.
        [TestCase(true, false, false, nameof(SkillsHttpServer.AutoStartReason.DomainReload))]
        [TestCase(false, true, false, nameof(SkillsHttpServer.AutoStartReason.EditorLaunch))]
        [TestCase(false, false, true, nameof(SkillsHttpServer.AutoStartReason.CliColdStart))]
        [TestCase(true, true, true, nameof(SkillsHttpServer.AutoStartReason.CliColdStart))]
        [TestCase(false, false, false, nameof(SkillsHttpServer.AutoStartReason.None))]
        public void GetAutoStartReason_ReturnsExpectedSource(
            bool restoreRequested,
            bool editorLaunchRequested,
            bool cliColdStart,
            string expected)
        {
            Assert.That(
                SkillsHttpServer.GetAutoStartReason(restoreRequested, editorLaunchRequested, cliColdStart).ToString(),
                Is.EqualTo(expected));
        }
    }
}

// Producer:Betsy
