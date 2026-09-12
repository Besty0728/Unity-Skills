using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the key behaviors of auto-syncing already-installed AI tools after a package upgrade: the version
    /// gate, "installed targets only" filtering, the per-copy version stamp that stops a lagging project from
    /// downgrading a shared copy, and record writeback. Everything fakes install targets in a temp
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

        // ===== Per-copy version stamp =====

        [Test]
        public void ShouldRefreshTarget_OlderCopy_IsTrue()
        {
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("2.8.1", "2.8.3"), Is.True);
        }

        [Test]
        public void ShouldRefreshTarget_SameVersion_IsFalse()
        {
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("2.8.3", "2.8.3"), Is.False);
        }

        [Test]
        public void ShouldRefreshTarget_NewerCopy_IsFalse()
        {
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("2.9.0", "2.8.3"), Is.False,
                "A copy refreshed by a project on a newer package must never be downgraded.");
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("2.8.10", "2.8.3"), Is.False,
                "Comparison must be numeric, not lexical.");
        }

        [Test]
        public void ShouldRefreshTarget_UnknownOrUnparseable_IsTrue()
        {
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget(null, "2.8.3"), Is.True);
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("", "2.8.3"), Is.True);
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("dev", "2.8.3"), Is.True);
            Assert.That(SkillInstallSyncService.ShouldRefreshTarget("2.8.3", "not-a-version"), Is.True);
        }

        [Test]
        public void CompareInstalledVersion_ClassifiesEveryState()
        {
            Assert.That(SkillInstaller.CompareInstalledVersion("2.8.1", "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Older));
            Assert.That(SkillInstaller.CompareInstalledVersion("2.8.3", "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Current));
            Assert.That(SkillInstaller.CompareInstalledVersion("2.8.10", "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Newer));
            Assert.That(SkillInstaller.CompareInstalledVersion(" 2.8.3 ", "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Current), "Whitespace must not break the parse.");
            Assert.That(SkillInstaller.CompareInstalledVersion(null, "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Unknown));
            Assert.That(SkillInstaller.CompareInstalledVersion("dev", "2.8.3"), Is.EqualTo(SkillInstaller.InstalledVersionState.Unknown));
            Assert.That(SkillInstaller.CompareInstalledVersion("2.8.3", "dev"), Is.EqualTo(SkillInstaller.InstalledVersionState.Unknown));
        }

        [Test]
        public void ReadInstalledVersion_FreshInstall_ReturnsPackageVersion()
        {
            var installed = Path.Combine(_tempRoot, "stamped");
            var seed = SkillInstaller.InstallCustom(installed, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            Assert.That(SkillInstaller.ReadInstalledVersion(installed), Is.EqualTo(SkillsLogger.Version));
            StringAssert.Contains("\"version\": \"" + SkillsLogger.Version + "\"",
                File.ReadAllText(Path.Combine(installed, "scripts", "agent_config.json")));
        }

        [Test]
        public void ReadInstalledVersion_WithoutAgentConfig_FallsBackToPythonClient()
        {
            var installed = Path.Combine(_tempRoot, "legacy");
            var seed = SkillInstaller.InstallCustom(installed, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            // Copies made by packages before the stamp existed have no "version" in agent_config.json.
            File.Delete(Path.Combine(installed, "scripts", "agent_config.json"));

            Assert.That(SkillInstaller.ReadInstalledVersion(installed), Is.EqualTo(SkillsLogger.Version));
        }

        [Test]
        public void ReadInstalledVersion_AgentConfigWins_OverPythonClient()
        {
            var installed = Path.Combine(_tempRoot, "config-wins");
            var seed = SkillInstaller.InstallCustom(installed, "TestAgent");
            Assert.That(seed.success, Is.True, "Test fixture could not seed an install: " + seed.message);

            File.WriteAllText(Path.Combine(installed, "scripts", "agent_config.json"),
                "{\"agentId\": \"TestAgent\", \"version\": \"99.0.0\"}");

            Assert.That(SkillInstaller.ReadInstalledVersion(installed), Is.EqualTo("99.0.0"));
        }

        [Test]
        public void ReadInstalledVersion_EmptyOrMissingDirectory_ReturnsNull()
        {
            Assert.That(SkillInstaller.ReadInstalledVersion(Path.Combine(_tempRoot, "nope")), Is.Null);
            Assert.That(SkillInstaller.ReadInstalledVersion(null), Is.Null);
        }

        [Test]
        public void SyncTargets_SkipsTargetWithNewerInstalledVersion()
        {
            var path = Path.Combine(_tempRoot, "newer");
            bool installCalled = false;

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Claude Code (Global)",
                    Path = path,
                    IsInstalled = () => true,
                    InstalledVersion = () => "99.0.0",
                    Install = () => { installCalled = true; return (true, path); }
                }
            });

            Assert.That(installCalled, Is.False, "A lagging project must not overwrite a copy another project already upgraded.");
            Assert.That(report.Updated, Is.Empty);
            Assert.That(report.Failed, Is.Empty);
            Assert.That(report.SkippedUpToDate, Is.EqualTo(0));
            Assert.That(report.SkippedNewer, Is.EqualTo(new[] { "Claude Code (Global) (99.0.0)" }));
        }

        [Test]
        public void SyncTargets_SkipsTargetAlreadyAtCurrentVersion()
        {
            var path = Path.Combine(_tempRoot, "current");
            bool installCalled = false;

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                new SkillInstaller.InstallTarget
                {
                    DisplayName = "Cursor (Global)",
                    Path = path,
                    IsInstalled = () => true,
                    InstalledVersion = () => SkillsLogger.Version,
                    Install = () => { installCalled = true; return (true, path); }
                }
            });

            Assert.That(installCalled, Is.False, "Re-copying a copy already at this version is wasted IO.");
            Assert.That(report.Updated, Is.Empty);
            Assert.That(report.SkippedUpToDate, Is.EqualTo(1));
            Assert.That(report.SkippedNewer, Is.Empty);
        }

        [Test]
        public void SyncTargets_RefreshesTargetWithOlderOrUnknownVersion()
        {
            var olderPath = Path.Combine(_tempRoot, "older");
            var unknownPath = Path.Combine(_tempRoot, "unknown");
            var installed = new List<string>();

            SkillInstaller.InstallTarget Make(string name, string path, Func<string> version) => new SkillInstaller.InstallTarget
            {
                DisplayName = name,
                Path = path,
                IsInstalled = () => true,
                InstalledVersion = version,
                Install = () => { installed.Add(name); return (true, path); }
            };

            var report = SkillInstallSyncService.SyncTargets(new[]
            {
                Make("Older", olderPath, () => "0.0.1"),
                Make("Unknown", unknownPath, () => null)
            });

            Assert.That(installed, Is.EqualTo(new[] { "Older", "Unknown" }));
            Assert.That(report.Updated, Is.EqualTo(new[] { "Older", "Unknown" }));
            Assert.That(report.SkippedNewer, Is.Empty);
            Assert.That(report.SkippedUpToDate, Is.EqualTo(0));
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
            Assert.That(targets.All(target => target.IsInstalled != null && target.Install != null && target.InstalledVersion != null), Is.True);
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
