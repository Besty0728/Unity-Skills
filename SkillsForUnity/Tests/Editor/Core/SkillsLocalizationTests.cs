using System;
using System.Collections.Generic;
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
        private SkillsLocalization.Language _savedPinnedPrimary;
        private SkillsLocalization.Language _savedPinnedSecondary;

        [SetUp]
        public void SetUp()
        {
            _savedLanguage = SkillsLocalization.Current;
            _savedPinnedPrimary = SkillsLocalization.PinnedPrimary;
            _savedPinnedSecondary = SkillsLocalization.PinnedSecondary;
        }

        [TearDown]
        public void TearDown()
        {
            // Pins first: changing a pin may move the current language, which is restored last.
            SkillsLocalization.PinnedPrimary = _savedPinnedPrimary;
            SkillsLocalization.PinnedSecondary = _savedPinnedSecondary;
            SkillsLocalization.Current = _savedLanguage;
        }

        private static void PinChineseEnglishWithChineseCurrent()
        {
            SkillsLocalization.PinnedPrimary = SkillsLocalization.Language.Chinese;
            SkillsLocalization.PinnedSecondary = SkillsLocalization.Language.English;
            SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
        }

        [Test]
        public void Pinning_ReplacingCurrentLanguageSlot_SwitchesToNewLanguageBeforeNotifying()
        {
            PinChineseEnglishWithChineseCurrent();
            var seen = new List<SkillsLocalization.Language>();
            Action handler = () => seen.Add(SkillsLocalization.Current);
            SkillsLocalization.LanguageChanged += handler;
            try
            {
                SkillsLocalization.PinnedPrimary = SkillsLocalization.Language.Russian;
            }
            finally
            {
                SkillsLocalization.LanguageChanged -= handler;
            }

            Assert.That(SkillsLocalization.Current, Is.EqualTo(SkillsLocalization.Language.Russian));
            Assert.That(seen, Is.EqualTo(new[] { SkillsLocalization.Language.Russian }));
        }

        [Test]
        public void Pinning_SwappingSlots_KeepsCurrentLanguage()
        {
            PinChineseEnglishWithChineseCurrent();

            SkillsLocalization.PinnedPrimary = SkillsLocalization.Language.English;

            Assert.That(SkillsLocalization.PinnedSecondary, Is.EqualTo(SkillsLocalization.Language.Chinese));
            Assert.That(SkillsLocalization.Current, Is.EqualTo(SkillsLocalization.Language.Chinese));
        }

        [Test]
        public void Pinning_ReplacingOtherSlot_KeepsCurrentLanguage()
        {
            PinChineseEnglishWithChineseCurrent();

            SkillsLocalization.PinnedSecondary = SkillsLocalization.Language.Russian;

            Assert.That(SkillsLocalization.Current, Is.EqualTo(SkillsLocalization.Language.Chinese));
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

        [Test]
        public void Localization_AgentInstructionKeys_ResolveInAllLanguages()
        {
            SkillsLocalization.Reload();

            SkillsLocalization.Current = SkillsLocalization.Language.English;
            Assert.That(SkillsLocalization.Get("agent_instruction_label"),
                Is.EqualTo("Write guide line to agent instruction files"));
            Assert.That(SkillsLocalization.Get("agent_instruction_hint"), Does.Contain("CLAUDE.md"));

            SkillsLocalization.Current = SkillsLocalization.Language.Chinese;
            Assert.That(SkillsLocalization.Get("agent_instruction_label"),
                Is.EqualTo("向 AI 工具的指令文件写入引导语"));
            Assert.That(SkillsLocalization.Get("agent_instruction_hint"), Does.Contain("AGENTS.md"));

            SkillsLocalization.Current = SkillsLocalization.Language.Russian;
            Assert.That(SkillsLocalization.Get("agent_instruction_label"),
                Is.Not.EqualTo("agent_instruction_label"));
            Assert.That(SkillsLocalization.Get("agent_instruction_hint"), Does.Contain("GEMINI.md"));
        }
    }
}

// Producer:Betsy
