using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers three key behaviors of auto-syncing already-installed AI tools after a package upgrade: the version
    /// gate, "installed targets only" filtering, and record writeback. Everything fakes install targets in a temp
    /// directory, never touching the user's real ~/.claude copies or the project's real Library/UnitySkills/install_sync.json.
    /// </summary>
    [TestFixture]
    public class SkillInstallSyncTests
    {
        private string _tempRoot;
        private string _savedStateOverride;

        [SetUp]
        public void SetUp()
        {
            _savedStateOverride = SkillInstallSyncService.StateFilePathOverride;
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsInstallSync_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            SkillInstallSyncService.StateFilePathOverride = Path.Combine(_tempRoot, "state", "install_sync.json");
        }

        [TearDown]
        public void TearDown()
        {
            SkillInstallSyncService.StateFilePathOverride = _savedStateOverride;
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

        // ===== Version gate =====

        [Test]
        public void NeedsSync_SameVersion_IsFalse()
        {
            Assert.That(SkillInstallSyncService.NeedsSync("2.7.0", "2.7.0"), Is.False);
        }

        [Test]
        public void NeedsSync_DifferentVersion_IsTrue()
        {
            Assert.That(SkillInstallSyncService.NeedsSync("2.6.2", "2.7.0"), Is.True);
        }

        [Test]
        public void NeedsSync_MissingRecord_IsTrue()
        {
            Assert.That(SkillInstallSyncService.NeedsSync(null, "2.7.0"), Is.True);
        }

        [Test]
        public void ReadRecordedVersion_WithoutStateFile_ReturnsNull()
        {
            Assert.That(File.Exists(SkillInstallSyncService.StateFilePath), Is.False);
            Assert.That(SkillInstallSyncService.ReadRecordedVersion(), Is.Null);
        }

        [Test]
        public void ReadRecordedVersion_WithCorruptStateFile_ReturnsNull()
        {
            var path = SkillInstallSyncService.StateFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{not json");

            Assert.That(SkillInstallSyncService.ReadRecordedVersion(), Is.Null);
        }

        // ===== Domain-reload immediacy gate =====

        [Test]
        public void ShouldSyncNow_WhenRecordedVersionMatches_IsFalse()
        {
            SkillInstallSyncService.WriteState(SkillsLogger.Version, new List<string>());

            Assert.That(SkillInstallSyncService.ShouldSyncNow(false), Is.False,
                "A domain reload at the recorded version must cost nothing but the state-file read.");
        }

        [Test]
        public void ShouldSyncNow_WhenRecordedVersionIsStale_IsTrue()
        {
            SkillInstallSyncService.WriteState("0.0.1-stale", new List<string>());

            Assert.That(SkillInstallSyncService.ShouldSyncNow(false), Is.True);
        }

        [Test]
        public void ShouldSyncNow_WithoutStateFile_IsTrue()
        {
            Assert.That(SkillInstallSyncService.ShouldSyncNow(false), Is.True,
                "A missing record means the upgrade this feature exists for was never synced.");
        }

        [Test]
        public void ShouldSyncNow_InBatchMode_IsFalse()
        {
            SkillInstallSyncService.WriteState("0.0.1-stale", new List<string>());

            Assert.That(SkillInstallSyncService.ShouldSyncNow(true), Is.False,
                "Headless unity test/run/build must never rewrite the user's skill copies.");
        }

        [Test]
        public void ShouldSyncNow_WhenToggleIsOff_IsFalse()
        {
            var saved = SkillInstallSyncService.Enabled;
            try
            {
                SkillInstallSyncService.WriteState("0.0.1-stale", new List<string>());
                SkillInstallSyncService.Enabled = false;

                Assert.That(SkillInstallSyncService.ShouldSyncNow(false), Is.False);
            }
            finally
            {
                SkillInstallSyncService.Enabled = saved;
            }
        }

        [Test]
        public void Enabled_WithNoStoredPreference_DefaultsToOn()
        {
            var key = SkillInstallSyncService.PrefEnabled;
            bool hadValue = EditorPrefs.HasKey(key);
            bool saved = hadValue && EditorPrefs.GetBool(key);
            try
            {
                EditorPrefs.DeleteKey(key);

                Assert.That(SkillInstallSyncService.Enabled, Is.True, "Auto-sync ships on by default.");
                StringAssert.StartsWith("UnitySkills_", key);
                StringAssert.Contains(RegistryService.InstanceId, key,
                    "The preference key must carry the instance id so two projects open at once cannot clobber each other.");
            }
            finally
            {
                if (hadValue) EditorPrefs.SetBool(key, saved);
                else EditorPrefs.DeleteKey(key);
            }
        }

        // ===== Record writeback =====

        [Test]
        public void WriteState_ThenRead_RoundTripsVersionAndTargets()
        {
            SkillInstallSyncService.WriteState("9.9.9", new List<string> { "Cursor (Project)" });

            Assert.That(File.Exists(SkillInstallSyncService.StateFilePath), Is.True);
            Assert.That(SkillInstallSyncService.ReadRecordedVersion(), Is.EqualTo("9.9.9"));

            var json = File.ReadAllText(SkillInstallSyncService.StateFilePath);
            StringAssert.Contains("Cursor (Project)", json);
            StringAssert.Contains("\"schemaVersion\": 1", json);
        }

        [Test]
        public void WriteState_AfterWriting_VersionGateCloses()
        {
            Assert.That(SkillInstallSyncService.NeedsSync(SkillInstallSyncService.ReadRecordedVersion(), "3.0.0"), Is.True);

            SkillInstallSyncService.WriteState("3.0.0", new List<string>());

            Assert.That(SkillInstallSyncService.NeedsSync(SkillInstallSyncService.ReadRecordedVersion(), "3.0.0"), Is.False);
        }

        // ===== Installed targets only =====

        [Test]
        public void SyncTargets_SkipsTargetThatIsNotInstalled()
        {
            var notInstalled = Path.Combine(_tempRoot, "absent");
            bool installCalled = false;

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Absent Tool",
                    Path = notInstalled,
                    IsInstalled = () => File.Exists(Path.Combine(notInstalled, "SKILL.md")),
                    Install = () => { installCalled = true; return (true, notInstalled); }
                }
            });

            Assert.That(installCalled, Is.False, "Never auto-install a target the user has not installed.");
            Assert.That(report.Updated, Is.Empty);
            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.SkippedNotInstalled, Is.EqualTo(1));
            Assert.That(Directory.Exists(notInstalled), Is.False);
        }

        [Test]
        public void SyncTargets_RefreshesInstalledTargetFromTemplate()
        {
            var installed = Path.Combine(_tempRoot, "installed");
            var seed = SkillInstaller.InstallCustom(installed, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            // Simulate an older-version copy: replace SKILL.md with stale content.
            var skillMd = Path.Combine(installed, "SKILL.md");
            Assert.That(File.Exists(skillMd), Is.True);
            File.WriteAllText(skillMd, "stale copy from an older package version");

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Installed Tool",
                    Path = installed,
                    IsInstalled = () => File.Exists(skillMd),
                    Install = () => SkillInstaller.InstallCustom(installed, "TestAgent")
                }
            });

            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.Updated, Is.EqualTo(new[] { "Installed Tool" }));
            Assert.That(File.ReadAllText(skillMd), Does.Not.Contain("stale copy"));
        }

        [Test]
        public void SyncTargets_DeduplicatesTargetsSharingOnePath()
        {
            var shared = Path.Combine(_tempRoot, "shared");
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "SKILL.md"), "present");
            int installCount = 0;

            SkillInstaller.InstallTarget Make(string name) => new SkillInstaller.InstallTarget
            {
                DisplayName = name,
                Path = shared,
                IsInstalled = () => true,
                Install = () => { installCount++; return (true, shared); }
            };

            var report = SkillInstallSyncService.SyncTargets(new[] { Make("Codex (Project)"), Make("Antigravity (Project)") });

            Assert.That(installCount, Is.EqualTo(1));
            Assert.That(report.Updated.Count, Is.EqualTo(1));
            Assert.That(report.SkippedDuplicatePath, Is.EqualTo(1));
        }

        [Test]
        public void SyncTargets_OneFailingTargetDoesNotStopTheOthers()
        {
            var okPath = Path.Combine(_tempRoot, "ok");
            var badPath = Path.Combine(_tempRoot, "bad");
            bool okInstalled = false;

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Throwing Tool",
                    Path = badPath,
                    IsInstalled = () => true,
                    Install = () => throw new IOException("disk on fire")
                },
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Healthy Tool",
                    Path = okPath,
                    IsInstalled = () => true,
                    Install = () => { okInstalled = true; return (true, okPath); }
                }
            });

            Assert.That(okInstalled, Is.True);
            Assert.That(report.Updated, Is.EqualTo(new[] { "Healthy Tool" }));
            Assert.That(report.Failed.Count, Is.EqualTo(1));
            StringAssert.Contains("disk on fire", report.Failed[0]);
        }

        // ===== Target table =====

        [Test]
        public void EnumerateTargets_CoversEveryToolAndScope()
        {
            var targets = SkillInstaller.EnumerateTargets().ToList();

            Assert.That(targets.Count, Is.EqualTo(12));
            Assert.That(targets.All(target => target.IsInstalled != null && target.Install != null), Is.True);
            Assert.That(targets.All(target => !string.IsNullOrEmpty(target.Path)), Is.True);
            foreach (var name in new[] { "Claude Code", "Codex", "Antigravity", "Cursor", "OpenCode", "Kimi Code" })
            {
                Assert.That(targets.Any(target => target.DisplayName == name + " (Project)"), Is.True, name + " project target missing");
                Assert.That(targets.Any(target => target.DisplayName == name + " (Global)"), Is.True, name + " global target missing");
            }
        }
    }
}

// Producer:Betsy
