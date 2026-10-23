using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Files a template no longer ships must leave an installed copy on reinstall and uninstall: through the install
    /// manifest (only while unchanged since install), or through the frozen legacy list for copies made before
    /// manifests existed. Files the user added, and paths outside the copy, are never touched.
    /// </summary>
    [TestFixture]
    public class SkillInstallerRetireTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsRetire_" + Guid.NewGuid().ToString("N"));
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
        public void Reinstall_RemovesUnchangedRetiredFileAndItsEmptyDirectory()
        {
            var target = Install("retired");
            var retired = AddRetiredFile(target, "retired-dir/OLD.md", "old template doc");

            Reinstall(target);

            Assert.That(File.Exists(retired), Is.False, "A file the template no longer ships must go.");
            Assert.That(Directory.Exists(Path.Combine(target, "retired-dir")), Is.False, "The directory it leaves empty must go.");
            Assert.That(ManifestFiles(target).Properties().Select(p => p.Name), Has.No.Member("retired-dir/OLD.md"));
        }

        [Test]
        public void Reinstall_KeepsRetiredFileChangedSinceInstall()
        {
            var target = Install("edited");
            var retired = AddRetiredFile(target, "references/OLD.md", "old template doc");
            File.WriteAllText(retired, "edited by the user");

            Reinstall(target);

            Assert.That(File.ReadAllText(retired), Is.EqualTo("edited by the user"));
        }

        [Test]
        public void Reinstall_KeepsUserAddedFile()
        {
            var target = Install("user-file");
            var userFile = Path.Combine(target, "skills", "my-custom", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile));
            File.WriteAllText(userFile, "user module");

            Reinstall(target);

            Assert.That(File.Exists(userFile), Is.True, "Reinstall must never delete user-added content.");
        }

        [Test]
        public void Reinstall_WithoutManifest_RemovesLegacyRetiredFilesOnly()
        {
            var target = Install("legacy");
            File.Delete(ManifestPath(target));
            var legacy = Path.Combine(target, "references", "SKILL_FULL.md");
            var unknown = Path.Combine(target, "references", "notes.md");
            File.WriteAllText(legacy, "a 2.9 template doc");
            File.WriteAllText(unknown, "not ours");

            Reinstall(target);

            Assert.That(File.Exists(legacy), Is.False, "A copy made before manifests sheds the known retired names.");
            Assert.That(File.Exists(unknown), Is.True, "Any other file is the user's.");
            Assert.That(File.Exists(ManifestPath(target)), Is.True, "The reinstall writes a manifest.");
        }

        [Test]
        public void Reinstall_CaseOnlyRename_KeepsTheNewFile()
        {
            var target = Install("case");
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath(target)));
            manifest["files"]["skill.md"] = Sha256(Path.Combine(target, "SKILL.md"));
            File.WriteAllText(ManifestPath(target), manifest.ToString());

            Reinstall(target);

            Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.True,
                "On a case-insensitive file system the old lower-case entry names the file just written.");
        }

        [Test]
        public void Reinstall_IgnoresManifestPathsOutsideTheCopy()
        {
            var target = Install("escape");
            var outside = Path.Combine(_tempRoot, "outside.txt");
            File.WriteAllText(outside, "not in the copy");
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath(target)));
            manifest["files"]["../outside.txt"] = Sha256(outside);
            File.WriteAllText(ManifestPath(target), manifest.ToString());

            Reinstall(target);

            Assert.That(File.Exists(outside), Is.True);
        }

        [Test]
        public void Uninstall_RemovesRetiredFilesAndManifest_KeepsUserFiles()
        {
            var target = Install("uninstall");
            var retired = AddRetiredFile(target, "retired-dir/OLD.md", "old template doc");
            var userFile = Path.Combine(target, "skills", "my-custom", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile));
            File.WriteAllText(userFile, "user module");

            var result = SkillInstaller.UninstallSkill(target, "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(File.Exists(retired), Is.False);
            Assert.That(Directory.Exists(Path.Combine(target, "retired-dir")), Is.False);
            Assert.That(File.Exists(ManifestPath(target)), Is.False, "The installer-written manifest must be removed.");
            Assert.That(File.Exists(userFile), Is.True, "Uninstall must never delete user-added content.");
            Assert.That(Directory.Exists(target), Is.True, "A directory kept alive by user files must survive.");
        }

        [Test]
        public void Manifest_ListsEveryInstalledTemplateFileWithItsHash()
        {
            var target = Install("manifest");
            var listed = ManifestFiles(target).Properties().ToDictionary(p => p.Name, p => (string)p.Value);
            var onDisk = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(target, file).Replace('\\', '/'))
                .Where(path => path != "scripts/agent_config.json" && path != "scripts/" + SkillInstaller.InstallManifestFileName)
                .ToList();

            Assert.That(listed.Keys, Is.EquivalentTo(onDisk));
            foreach (var pair in listed)
                Assert.That(pair.Value, Is.EqualTo(Sha256(Path.Combine(target, pair.Key))).IgnoreCase, pair.Key);
        }

        private string Install(string name)
        {
            var target = Path.Combine(_tempRoot, name);
            var seed = SkillInstaller.InstallCustom(target, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);
            return target;
        }

        private static void Reinstall(string target)
        {
            var result = SkillInstaller.InstallCustom(target, "TestAgent");
            Assert.That(result.success, Is.True, result.message);
        }

        /// <summary>A file an earlier template installed: on disk and in the manifest with its hash.</summary>
        private static string AddRetiredFile(string target, string relativePath, string content)
        {
            var file = Path.Combine(target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content);
            var manifest = JObject.Parse(File.ReadAllText(ManifestPath(target)));
            manifest["files"][relativePath] = Sha256(file);
            File.WriteAllText(ManifestPath(target), manifest.ToString());
            return file;
        }

        private static string ManifestPath(string target) =>
            Path.Combine(target, "scripts", SkillInstaller.InstallManifestFileName);

        private static JObject ManifestFiles(string target) =>
            (JObject)JObject.Parse(File.ReadAllText(ManifestPath(target), Encoding.UTF8))["files"];

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty);
        }
    }
}

// Producer:Betsy
