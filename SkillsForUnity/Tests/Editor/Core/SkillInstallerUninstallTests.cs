using System;
using System.IO;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Regression tests for template-scoped uninstall: removing an installed skill copy must delete
    /// only the paths the installed template owns (plus the installer-written agent_config.json) and
    /// never touch files the user added into the destination directory.
    /// </summary>
    [TestFixture]
    public class SkillInstallerUninstallTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsUninstall_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, true);
            }
            catch (IOException)
            {
                // A failure to clean up the temp directory shouldn't turn the test red.
            }
        }

        [Test]
        public void Uninstall_PreservesUserAddedFile()
        {
            var target = Path.Combine(_tempRoot, "with-user-file");
            var seed = SkillInstaller.InstallCustom(target, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            // A user module doc the installer never wrote (install seed: the real template ships no my-custom module).
            var userFile = Path.Combine(target, "skills", "my-custom", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile));
            File.WriteAllText(userFile, "user module");

            var result = SkillInstaller.UninstallSkill(target, "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(userFile), Is.True, "Uninstall must never delete user-added content.");
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.False, "Template files must be removed.");
            Assert.That(File.Exists(Path.Combine(target, "scripts", "unity_skills.py")), Is.False, "Template files must be removed.");
            Assert.That(File.Exists(Path.Combine(target, "scripts", "agent_config.json")), Is.False,
                "The installer-written config must be removed even though it is not part of the template.");
            Assert.That(Directory.Exists(target), Is.True, "A directory kept alive by user files must survive.");
            Assert.That(Directory.Exists(Path.Combine(target, "references")), Is.False,
                "Template directories left empty must be removed.");
        }

        [Test]
        public void Uninstall_RemovesEmptyTemplateDirs()
        {
            var target = Path.Combine(_tempRoot, "clean");
            var seed = SkillInstaller.InstallCustom(target, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            var result = SkillInstaller.UninstallSkill(target, "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(Directory.Exists(target), Is.False,
                "With no user files keeping it alive, the destination directory must be gone entirely.");
        }

        [Test]
        public void Uninstall_NotInstalled_ReturnsFalse()
        {
            // Drive the internal seam against a guaranteed-missing temp dir so the test never touches a
            // real install (CI/dev machines may or may not have the skill installed globally or per-project).
            var result = SkillInstaller.UninstallSkill(Path.Combine(_tempRoot, "missing"), "TestAgent");

            Assert.That(result.success, Is.False);
            StringAssert.Contains("not installed", result.message);
        }
    }
}

// Producer:Betsy
