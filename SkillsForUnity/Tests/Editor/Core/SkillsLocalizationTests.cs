using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Verifies that decoupled JSON-backed localization loads properly across EN, CN, and RU.
    /// </summary>
    [TestFixture]
    public class SkillsLocalizationTests
    {
        private SkillsLocalization.Language _savedLanguage;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = SkillsLocalization.Current;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsLocalization.Current = _savedLanguage;
        }

        [Test]
        public void Localization_LoadsDecoupledJsonLocales_WithKeyParity()
        {
            SkillsLocalization.Reload();

            SkillsLocalization.Current = SkillsLocalization.Language.English;
            string enVal = SkillsLocalization.Get("window_title");
            Assert.That(enVal, Is.EqualTo("UnitySkills"));

            SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
            string cnVal = SkillsLocalization.Get("window_title");
            Assert.That(cnVal, Is.EqualTo("UnitySkills"));
            string cnServer = SkillsLocalization.Get("start_server");
            Assert.That(cnServer, Is.EqualTo("启动服务器"));

            SkillsLocalization.Current = SkillsLocalization.Language.Russian;
            string ruServer = SkillsLocalization.Get("start_server");
            Assert.That(ruServer, Is.EqualTo("Запустить сервер"));
        }

        [Test]
        public void Localization_Format_ReplacesPlaceholders()
        {
            SkillsLocalization.Current = SkillsLocalization.Language.English;
            string formatted = SkillsLocalization.Get("total_skills", 42, 5);
            Assert.That(formatted, Does.Contain("42").And.Contain("5"));
        }

        [Test]
        public void Localization_MissingKey_ReturnsKeyGracefully()
        {
            string fallback = SkillsLocalization.Get("some_non_existent_key_xyz");
            Assert.That(fallback, Is.EqualTo("some_non_existent_key_xyz"));
        }
    }
}

// Producer:Betsy
