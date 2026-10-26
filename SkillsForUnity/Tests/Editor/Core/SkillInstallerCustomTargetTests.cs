using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Custom-path installs are remembered machine-wide so the AI Config tab lists them and the post-upgrade sync
    /// refreshes them. The fixture uses its own EditorPrefs key and never touches the real list.
    /// </summary>
    [TestFixture]
    public class SkillInstallerCustomTargetTests
    {
        private const string PrefKey = "UnitySkills_Test_CustomInstallTargets";
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            EditorPrefs.DeleteKey(PrefKey);
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsCustomTarget_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(PrefKey);
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
        public void Remember_SameFolderSpelledThreeWays_KeepsOneEntryWithTheLatestName()
        {
            var path = Path.Combine(_tempRoot, "tools", "my-skill");

            SkillInstaller.RememberCustomTarget("A", path, PrefKey);
            SkillInstaller.RememberCustomTarget("B", path + Path.DirectorySeparatorChar, PrefKey);
            SkillInstaller.RememberCustomTarget("C", Path.Combine(_tempRoot, "tools", "x", "..", "my-skill"), PrefKey);

            var targets = SkillInstaller.ReadCustomTargets(PrefKey);
            Assert.That(targets.Count, Is.EqualTo(1));
            Assert.That(targets[0].name, Is.EqualTo("C"));
            Assert.That(targets[0].path, Is.EqualTo(Path.GetFullPath(path)));
        }

        [Test]
        public void Forget_DropsTheEntry_KeepsTheInstalledFiles()
        {
            var path = Path.Combine(_tempRoot, "my-skill");
            Assert.That(SkillInstaller.InstallSkill(path, "Custom Path", "Mine", withEvalSkill: false).success, Is.True);
            SkillInstaller.RememberCustomTarget("Mine", path, PrefKey);

            SkillInstaller.ForgetCustomTarget(path, PrefKey);

            Assert.That(SkillInstaller.ReadCustomTargets(PrefKey), Is.Empty);
            Assert.That(File.Exists(Path.Combine(path, "SKILL.md")), Is.True, "Removing from the list must not delete files.");
        }

        [Test]
        public void EnumerateCustomTargets_FollowsInstallState_AndInstallsTheMainSkillOnly()
        {
            var path = Path.Combine(_tempRoot, "custom", "my-skill");
            SkillInstaller.RememberCustomTarget("Mine", path, PrefKey);

            var target = SkillInstaller.EnumerateCustomTargets(PrefKey).Single();
            Assert.That(target.DisplayName, Is.EqualTo("Mine"));
            Assert.That(target.IsInstalled(), Is.False);

            var result = target.Install();

            Assert.That(result.success, Is.True, result.message);
            Assert.That(target.IsInstalled(), Is.True);
            Assert.That(Directory.Exists(Path.Combine(_tempRoot, "custom", SkillInstaller.EvalSkillFolderName)), Is.False,
                "A custom path is the folder the user chose; nothing may be written beside it.");

            Directory.Delete(path, true);
            Assert.That(target.IsInstalled(), Is.False, "A deleted folder stays listed but reads as not installed.");
        }

        [Test]
        public void SetIcon_StoresACopy_ThatOutlivesTheOriginal()
        {
            var path = Path.Combine(_tempRoot, "my-skill");
            SkillInstaller.RememberCustomTarget("Mine", path, PrefKey);
            var original = WriteImage("logo.png", 64);

            Assert.That(SkillInstaller.SetCustomTargetIcon(path, original, PrefKey, IconDir), Is.Null);

            var icon = SkillInstaller.ReadCustomTargets(PrefKey).Single().icon;
            Assert.That(Path.GetDirectoryName(icon), Is.EqualTo(IconDir));
            File.Delete(original);
            Assert.That(File.Exists(icon), Is.True, "The card keeps its icon after the picked original is gone.");
        }

        [Test]
        public void ResetIcon_ClearsTheRecord_AndDeletesACopyNobodyUses()
        {
            var a = Path.Combine(_tempRoot, "a");
            var b = Path.Combine(_tempRoot, "b");
            SkillInstaller.RememberCustomTarget("A", a, PrefKey);
            SkillInstaller.RememberCustomTarget("B", b, PrefKey);
            var image = WriteImage("logo.png", 64);
            SkillInstaller.SetCustomTargetIcon(a, image, PrefKey, IconDir);
            SkillInstaller.SetCustomTargetIcon(b, image, PrefKey, IconDir);
            var copy = SkillInstaller.ReadCustomTargets(PrefKey)[0].icon;

            SkillInstaller.SetCustomTargetIcon(a, null, PrefKey, IconDir);
            Assert.That(SkillInstaller.ReadCustomTargets(PrefKey)[0].icon, Is.Null);
            Assert.That(File.Exists(copy), Is.True, "B still uses the same copy.");

            SkillInstaller.ForgetCustomTarget(b, PrefKey, IconDir);
            Assert.That(File.Exists(copy), Is.False, "The copy goes once no remembered install uses it.");
        }

        [Test]
        public void SetIcon_RejectsOversizeOrNonImageFiles_AndKeepsTheRecord()
        {
            var path = Path.Combine(_tempRoot, "my-skill");
            SkillInstaller.RememberCustomTarget("Mine", path, PrefKey);

            Assert.That(SkillInstaller.SetCustomTargetIcon(path, WriteImage("big.png", (int)SkillInstaller.MaxIconBytes + 1), PrefKey, IconDir),
                Is.EqualTo("agent_custom_icon_invalid"));
            Assert.That(SkillInstaller.SetCustomTargetIcon(path, WriteImage("notes.txt", 16), PrefKey, IconDir),
                Is.EqualTo("agent_custom_icon_invalid"));
            Assert.That(SkillInstaller.ReadCustomTargets(PrefKey).Single().icon, Is.Null);
            Assert.That(Directory.Exists(IconDir), Is.False);
        }

        [Test]
        public void ReadCustomTargets_AcceptsRecordsWrittenBeforeIcons()
        {
            EditorPrefs.SetString(PrefKey, "[{\"name\":\"Old\",\"path\":\"/tmp/old-skill\"}]");

            var target = SkillInstaller.ReadCustomTargets(PrefKey).Single();

            Assert.That(target.name, Is.EqualTo("Old"));
            Assert.That(target.icon, Is.Null);
        }

        private string IconDir => Path.Combine(_tempRoot, "icons");

        private string WriteImage(string fileName, int size)
        {
            var file = Path.Combine(_tempRoot, fileName);
            File.WriteAllBytes(file, new byte[size]);
            return file;
        }

        [Test]
        public void InstallCustom_NeverAddsToTheRememberedList()
        {
            var before = EditorPrefs.GetString(SkillInstaller.CustomTargetsPrefKey, string.Empty);

            var result = SkillInstaller.InstallCustom(Path.Combine(_tempRoot, "scripted"), "TestAgent");

            Assert.That(result.success, Is.True, result.message);
            Assert.That(EditorPrefs.GetString(SkillInstaller.CustomTargetsPrefKey, string.Empty), Is.EqualTo(before),
                "Only the AI Config tab remembers an install; a scripted or test install into a temp folder must not.");
        }

        [Test]
        public void SyncTargets_RefreshesAnOutdatedRememberedInstall()
        {
            var path = Path.Combine(_tempRoot, "my-skill");
            SkillInstaller.RememberCustomTarget("Mine", path, PrefKey);
            var target = SkillInstaller.EnumerateCustomTargets(PrefKey).Single();
            Assert.That(target.Install().success, Is.True);
            File.WriteAllText(Path.Combine(path, "scripts", "agent_config.json"), "{\"agentId\":\"Mine\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(path, "SKILL.md"), "stale copy from an older package version");

            var report = SkillInstallSyncService.SyncTargets(SkillInstaller.EnumerateCustomTargets(PrefKey));

            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.Updated, Is.EqualTo(new[] { "Mine" }));
            Assert.That(File.ReadAllText(Path.Combine(path, "SKILL.md")), Does.Not.Contain("stale copy"));
        }
    }
}

// Producer:Betsy
