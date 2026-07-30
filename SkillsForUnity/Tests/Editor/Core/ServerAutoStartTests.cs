using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ServerAutoStartTests
    {
        // expected 传字符串而不是 AutoStartReason：该枚举是 internal，即便有
        // InternalsVisibleTo，把它放进 public 方法的签名也会触发 CS0051
        // （参数类型可访问性低于方法），让整个测试程序集编译失败。
        // 枚举是纯内部状态机，不值得为测试改成 public，故在方法体内比对名字。
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
