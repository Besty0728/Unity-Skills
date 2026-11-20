using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Every install writes the unity-skills folder and the unity-skills-eval folder side by side into the same
    /// skills directory. Each folder keeps its own manifest, and install, uninstall and retired-file cleanup act on
    /// both without touching files the user added.
    /// </summary>
    [TestFixture]
    public class SkillInstallerEvalSkillTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsEvalSkill_" + Guid.NewGuid().ToString("N"));
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
        public void Install_WritesEvalSkillBesideUnitySkills_EachWithItsOwnManifest()
        {
            var target = Path.Combine(_tempRoot, "skills", SkillInstaller.SkillFolderName);

            var result = SkillInstaller.InstallSkill(target, "Test", "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            var evalFolder = EvalFolder();
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(evalFolder, "SKILL.md")), Is.True, "The evaluation skill must be installed.");
            Assert.That(ManifestFiles(target).ContainsKey("SKILL.md"), Is.True);
            Assert.That(ManifestFiles(evalFolder).ContainsKey("SKILL.md"), Is.True, "The evaluation folder keeps its own manifest.");
        }

        [Test]
        public void Uninstall_RemovesBothTemplateTrees_KeepsUserFilesInEach()
        {
            var target = Path.Combine(_tempRoot, "skills", SkillInstaller.SkillFolderName);
            var seed = SkillInstaller.InstallSkill(target, "Test", "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);
            var evalFolder = EvalFolder();
            var userInMain = WriteUserFile(Path.Combine(target, "skills", "my-custom", "SKILL.md"));
            var userInEval = WriteUserFile(Path.Combine(evalFolder, "notes.md"));

            var result = SkillInstaller.UninstallSkill(target, "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.False, "Runtime template files must be removed.");
            Assert.That(File.Exists(Path.Combine(evalFolder, "SKILL.md")), Is.False, "Evaluation template files must be removed.");
            Assert.That(File.Exists(Path.Combine(evalFolder, "scripts", SkillInstaller.InstallManifestFileName)), Is.False);
            Assert.That(File.Exists(userInMain), Is.True, "Uninstall must never delete user-added content.");
            Assert.That(File.Exists(userInEval), Is.True, "Uninstall must never delete user-added content.");
        }

        [Test]
        public void Reinstall_RemovesRetiredFileFromEvalFolder()
        {
            var target = Path.Combine(_tempRoot, "skills", SkillInstaller.SkillFolderName);
            var seed = SkillInstaller.InstallSkill(target, "Test", "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);
            var evalFolder = EvalFolder();
            var retired = Path.Combine(evalFolder, "references", "OLD.md");
            Directory.CreateDirectory(Path.GetDirectoryName(retired));
            File.WriteAllText(retired, "old evaluation doc");
            AddManifestEntry(evalFolder, "references/OLD.md", Sha256(retired));

            var result = SkillInstaller.InstallSkill(target, "Test", "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(retired), Is.False, "A retired file of the evaluation folder must go on reinstall.");
            Assert.That(ManifestFiles(evalFolder).ContainsKey("references/OLD.md"), Is.False);
        }

        [Test]
        public void AntigravityMigration_InstallsBothSkillsAndCleansOnlyLegacyTemplates()
        {
            var target = Path.Combine(_tempRoot, "new", "skills", SkillInstaller.SkillFolderName);
            var legacy = Path.Combine(_tempRoot, "legacy", "skills", SkillInstaller.SkillFolderName);
            var seed = SkillInstaller.InstallSkill(legacy, "Test", "TestAgent");
            Assert.That(seed.success, Is.True, seed.message);
            string legacyEval = EvalBeside(legacy);
            string userInMain = WriteUserFile(Path.Combine(legacy, "notes.md"));
            string userInEval = WriteUserFile(Path.Combine(legacyEval, "notes.md"));

            var result = SkillInstaller.MigrateLegacyAntigravityGlobal(target, legacy,
                () => SkillInstaller.InstallSkill(target, "Test", "TestAgent"));

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(EvalBeside(target), "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(legacy, "SKILL.md")), Is.False);
            Assert.That(File.Exists(Path.Combine(legacyEval, "SKILL.md")), Is.False);
            Assert.That(File.Exists(userInMain), Is.True);
            Assert.That(File.Exists(userInEval), Is.True);
        }

        [Test]
        public void AntigravityMigration_FailedInstallLeavesLegacyUntouched()
        {
            var target = Path.Combine(_tempRoot, "new", "skills", SkillInstaller.SkillFolderName);
            var legacy = Path.Combine(_tempRoot, "legacy", "skills", SkillInstaller.SkillFolderName);
            var seed = SkillInstaller.InstallSkill(legacy, "Test", "TestAgent");
            Assert.That(seed.success, Is.True, seed.message);

            var result = SkillInstaller.MigrateLegacyAntigravityGlobal(target, legacy, () => (false, "boom"));

            Assert.That(result.success, Is.False);
            Assert.That(File.Exists(Path.Combine(legacy, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(EvalBeside(legacy), "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.False);
        }

        [Test]
        public void AntigravityMigration_WithoutLegacyRunsNormalInstall()
        {
            var target = Path.Combine(_tempRoot, "new", "skills", SkillInstaller.SkillFolderName);
            var legacy = Path.Combine(_tempRoot, "legacy", "skills", SkillInstaller.SkillFolderName);
            bool installCalled = false;

            var result = SkillInstaller.MigrateLegacyAntigravityGlobal(target, legacy, () =>
            {
                installCalled = true;
                return SkillInstaller.InstallSkill(target, "Test", "TestAgent");
            });

            Assert.That(result.success, Is.True, result.message);
            Assert.That(installCalled, Is.True);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(EvalBeside(target), "SKILL.md")), Is.True);
            Assert.That(Directory.Exists(legacy), Is.False);
        }

        [Test]
        public void AntigravityMigration_NewerCanonicalCleansLegacyWithoutReinstalling()
        {
            var target = Path.Combine(_tempRoot, "new", "skills", SkillInstaller.SkillFolderName);
            var legacy = Path.Combine(_tempRoot, "legacy", "skills", SkillInstaller.SkillFolderName);
            Assert.That(SkillInstaller.InstallSkill(target, "Test", "TestAgent").success, Is.True);
            Assert.That(SkillInstaller.InstallSkill(legacy, "Test", "TestAgent").success, Is.True);
            string config = Path.Combine(target, "scripts", "agent_config.json");
            File.WriteAllText(config, "{\"agentId\": \"TestAgent\", \"version\": \"99.0.0\"}");
            bool installCalled = false;

            var result = SkillInstaller.MigrateLegacyAntigravityGlobal(target, legacy, () =>
            {
                installCalled = true;
                return (false, "must not reinstall");
            });

            Assert.That(result.success, Is.True, result.message);
            Assert.That(installCalled, Is.False, "Cleanup-only migration must never downgrade the newer canonical copy.");
            Assert.That(SkillInstaller.ReadInstalledVersion(target), Is.EqualTo("99.0.0"));
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(EvalBeside(target), "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(legacy, "SKILL.md")), Is.False);
            Assert.That(File.Exists(Path.Combine(EvalBeside(legacy), "SKILL.md")), Is.False);
        }

        [Test]
        public void InstallCustom_WritesOnlyTheChosenFolder()
        {
            var target = Path.Combine(_tempRoot, "custom", "my-skill");

            var result = SkillInstaller.InstallCustom(target, "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_tempRoot, "custom", SkillInstaller.EvalSkillFolderName)), Is.False,
                "A custom path is the folder the user chose; nothing may be written beside it.");
        }

        private string EvalFolder() => EvalBeside(Path.Combine(_tempRoot, "skills", SkillInstaller.SkillFolderName));

        private static string EvalBeside(string target) =>
            Path.Combine(Path.GetDirectoryName(target), SkillInstaller.EvalSkillFolderName);

        private static string WriteUserFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "user content");
            return path;
        }

        private static JObject ManifestFiles(string folder) =>
            (JObject)JObject.Parse(File.ReadAllText(ManifestPath(folder)))["files"];

        private static void AddManifestEntry(string folder, string relativePath, string hash)
        {
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath(folder)));
            manifest["files"][relativePath] = hash;
            File.WriteAllText(ManifestPath(folder), manifest.ToString());
        }

        private static string ManifestPath(string folder) =>
            Path.Combine(folder, "scripts", SkillInstaller.InstallManifestFileName);

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
        }
    }
}

// Producer:Betsy
