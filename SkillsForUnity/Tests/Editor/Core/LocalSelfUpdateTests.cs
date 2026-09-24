using NUnit.Framework;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class LocalSelfUpdateTests
    {
        private string _tempDir;
        private bool _permissionsChanged;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "us-selfupdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            LocalSelfUpdateService.MoveDirectoryHookForTests = null;
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                if (_permissionsChanged) RunTool("/bin/chmod", "-R", "u+rwx", _tempDir);
                Directory.Delete(_tempDir, true);
            }
        }

        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/package.json", "package.json")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/Editor/Skills/Foo.cs", "Editor/Skills/Foo.cs")]
        [TestCase("Unity-Skills-2.9.0/SkillsForUnity/package.json", "package.json")]
        public void TryMapArchiveEntry_MapsSkillsForUnityEntries(string entryName, string expected)
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(entryName, out var relativePath), Is.True);
            Assert.That(relativePath, Is.EqualTo(expected));
        }

        [TestCase("Unity-Skills-2.8.2/README.md")]
        [TestCase("Unity-Skills-2.8.2/docs/SETUP_GUIDE.md")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/")] // 目录条目
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/../evil.txt")] // Zip Slip 防护
        [TestCase("")]
        public void TryMapArchiveEntry_RejectsNonPackageEntries(string entryName)
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(entryName, out _), Is.False);
        }

        [Test]
        public void TryMapArchiveEntry_RejectsNull()
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(null, out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ClassifySpec_TreatsMissingSpecAsLocal(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Local));
        }

        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v2.8.2")]
        public void ClassifySpec_GitUrlWithoutBetaFragmentIsStable(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Stable));
        }

        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#BETA")]
        public void ClassifySpec_BetaFragmentIsCaseInsensitive(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Beta));
        }

        [TestCase("file:/Users/x/Unity-Skills/SkillsForUnity")]
        [TestCase("file:../local/copy")]
        public void ClassifySpec_FileSpecIsLocal(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Local));
        }

        [Test]
        public void ValidateStagedPackage_AcceptsMatchingPackage()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.True);
        }

        [Test]
        public void ValidateStagedPackage_RejectsVersionMismatch()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.1");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsNameMismatch()
        {
            WritePackageJson("com.example.other", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingPackageJson()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMalformedPackageJson()
        {
            File.WriteAllText(Path.Combine(_tempDir, "package.json"), "not json");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingEditorDir()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingSkillsDir()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void SwapDirectories_ReplacesContentAndCleansUp()
        {
            var target = Path.Combine(_tempDir, "target");
            var staging = Path.Combine(_tempDir, "staging");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(target, "package.json"), "{ \"version\": \"2.8.1\" }");
            File.WriteAllText(Path.Combine(staging, "package.json"), "{ \"version\": \"2.8.2\" }");
            File.WriteAllText(Path.Combine(staging, "new.txt"), "new");

            var report = LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(File.Exists(Path.Combine(target, "old.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(target, "package.json")), Does.Contain("2.8.2"));
            Assert.That(File.ReadAllText(Path.Combine(target, "new.txt")), Is.EqualTo("new"));
            Assert.That(Directory.Exists(staging), Is.False);
            AssertNoSwapLeftovers();
            Assert.That(report.Warnings, Is.Empty);
        }

        // ---------- Track B hardening ----------

        [Test]
        public void SwapDirectories_KeepsReplacedVersionAsBackup()
        {
            var (target, staging) = PrepareSwap();

            var report = LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(report.BackupPath, Is.EqualTo(BackupDir));
            Assert.That(File.ReadAllText(Path.Combine(BackupDir, "old.txt")), Is.EqualTo("old"),
                "Local edits to a non-git copy must stay recoverable after the archive replaced it.");
            Assert.That(File.ReadAllText(Path.Combine(BackupDir, "package.json")), Does.Contain("2.8.1"));
            Assert.That(File.ReadAllText(Path.Combine(target, "new.txt")), Is.EqualTo("new"));
        }

        [Test]
        public void SwapDirectories_ReplacesThePreviousBackup_KeepingOneGeneration()
        {
            Directory.CreateDirectory(BackupDir);
            File.WriteAllText(Path.Combine(BackupDir, "generation0.txt"), "older");
            var (target, staging) = PrepareSwap();

            LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(File.Exists(Path.Combine(BackupDir, "generation0.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(BackupDir, "old.txt")), Is.EqualTo("old"));
            Assert.That(Directory.GetDirectories(Path.GetDirectoryName(BackupDir), "previous.stale-*"), Is.Empty);
        }

        [Test]
        public void SwapDirectories_DoesNotTouchPreexistingOldDirs()
        {
            // A leftover of an earlier failed run may be the only good copy; it is never deleted by name (PID reuse).
            var leftover = Path.Combine(_tempDir, ".unityskills-old-1234");
            Directory.CreateDirectory(leftover);
            File.WriteAllText(Path.Combine(leftover, "keep.txt"), "keep");
            var (target, staging) = PrepareSwap();

            LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(File.ReadAllText(Path.Combine(leftover, "keep.txt")), Is.EqualTo("keep"));
        }

        [Test]
        public void SwapDirectories_TargetRenameFails_TargetIntact()
        {
            var (target, staging) = PrepareSwap();
            var before = Fingerprint(target);
            LocalSelfUpdateService.MoveDirectoryHookForTests = (from, to) =>
            {
                if (SamePath(from, target)) throw new IOException("simulated sharing violation");
            };

            Assert.That(() => LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir), Throws.InstanceOf<IOException>());

            Assert.That(Fingerprint(target), Is.EqualTo(before), "A failed swap must leave the live package byte for byte intact.");
            AssertNoSwapLeftovers();
            Assert.That(Directory.Exists(BackupDir), Is.False);
        }

        [Test]
        public void SwapDirectories_NewTreeRenameFails_RollsBackTheOriginal()
        {
            var (target, staging) = PrepareSwap();
            var before = Fingerprint(target);
            LocalSelfUpdateService.MoveDirectoryHookForTests = (from, to) =>
            {
                if (SamePath(to, target) && Path.GetFileName(from).StartsWith(".unityskills-new-", StringComparison.Ordinal))
                    throw new IOException("simulated failure moving the new tree in");
            };

            Assert.That(() => LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir), Throws.InstanceOf<IOException>());

            Assert.That(Fingerprint(target), Is.EqualTo(before), "The original must be renamed back after the new tree failed to move in.");
            AssertNoSwapLeftovers();
        }

        [Test]
        public void SwapDirectories_RollbackAlsoFails_ThrowsWithRecoveryInstruction_OriginalKept()
        {
            var (target, staging) = PrepareSwap();
            var before = Fingerprint(target);
            LocalSelfUpdateService.MoveDirectoryHookForTests = (from, to) =>
            {
                if (SamePath(to, target)) throw new IOException("simulated failure");
            };

            var error = Assert.Throws<IOException>(() => LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir));

            var old = Directory.GetDirectories(_tempDir, ".unityskills-old-*").Single();
            Assert.That(error.Message, Does.Contain(old).And.Contain("back to"));
            Assert.That(Fingerprint(old), Is.EqualTo(before), "The only good copy must still be complete where the message says.");
        }

        [Test]
        public void SwapDirectories_BackupMoveFails_UpdateStands_PreviousVersionKeptBesideIt()
        {
            var (target, staging) = PrepareSwap();
            LocalSelfUpdateService.MoveDirectoryHookForTests = (from, to) =>
            {
                if (SamePath(to, BackupDir)) throw new UnauthorizedAccessException("simulated");
            };

            var report = LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(File.ReadAllText(Path.Combine(target, "new.txt")), Is.EqualTo("new"));
            var old = Directory.GetDirectories(_tempDir, ".unityskills-old-*").Single();
            Assert.That(report.BackupPath, Is.EqualTo(old));
            Assert.That(File.ReadAllText(Path.Combine(old, "old.txt")), Is.EqualTo("old"));
            Assert.That(File.Exists(Path.Combine(old, "package.json")), Is.False,
                "A leftover next to an embedded package must not read as a second copy of the package.");
            Assert.That(File.Exists(Path.Combine(old, "package.json.unityskills-old")), Is.True);
            Assert.That(report.Warnings, Has.Some.Contains(old));
        }

        [Test]
        public void SwapDirectories_CrossVolumeStaging_FallsBackToCopy()
        {
            var (target, staging) = PrepareSwap();
            Directory.CreateDirectory(Path.Combine(staging, "Editor", "Deep"));
            File.WriteAllText(Path.Combine(staging, "Editor", "Deep", "x.cs"), "x");
            LocalSelfUpdateService.MoveDirectoryHookForTests = (from, to) =>
            {
                if (SamePath(from, staging)) throw new IOException("simulated: not the same device");
            };

            LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir);

            Assert.That(File.ReadAllText(Path.Combine(target, "Editor", "Deep", "x.cs")), Is.EqualTo("x"));
            Assert.That(File.ReadAllText(Path.Combine(target, "new.txt")), Is.EqualTo("new"));
            AssertNoSwapLeftovers();
        }

        [Test]
        public void SwapDirectories_ParentNotWritable_TargetIntact()
        {
            if (GitCliRunner.IsWindows) Assert.Ignore("POSIX permissions only; the hook-based tests cover the same paths on Windows.");
            var parent = Path.Combine(_tempDir, "locked");
            var target = Path.Combine(parent, "target");
            var staging = Path.Combine(_tempDir, "staging");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(staging, "new.txt"), "new");
            var before = Fingerprint(target);
            _permissionsChanged = true;
            RunTool("/bin/chmod", "555", parent);
            try
            {
                Assert.That(() => LocalSelfUpdateService.SwapDirectories(target, staging, BackupDir), Throws.Exception);
            }
            finally
            {
                RunTool("/bin/chmod", "755", parent);
            }

            Assert.That(Fingerprint(target), Is.EqualTo(before));
            Assert.That(Directory.GetDirectories(parent), Is.EquivalentTo(new[] { target }));
        }

        [Test]
        public void ArchiveGuards_PackageDirWithGitDirectory_IsRepository()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
            Assert.That(LocalGitSync.CheckArchiveGuards(_tempDir), Is.EqualTo(LocalUpdateOutcome.PackageDirIsRepository));
        }

        [Test]
        public void ArchiveGuards_PackageDirWithGitFile_IsRepository()
        {
            File.WriteAllText(Path.Combine(_tempDir, ".git"), "gitdir: /elsewhere/.git/worktrees/x");
            Assert.That(LocalGitSync.CheckArchiveGuards(_tempDir), Is.EqualTo(LocalUpdateOutcome.PackageDirIsRepository));
        }

        [Test]
        public void ArchiveGuards_SymlinkedPackageDir_IsLink()
        {
            if (GitCliRunner.IsWindows) Assert.Ignore("Creating a junction needs cmd /c mklink; covered on Unix.");
            var real = Path.Combine(_tempDir, "real");
            var link = Path.Combine(_tempDir, "link");
            Directory.CreateDirectory(real);
            RunTool("/bin/ln", "-s", real, link);

            Assert.That(LocalGitSync.CheckArchiveGuards(link), Is.EqualTo(LocalUpdateOutcome.PackageDirIsLink));
            Assert.That(LocalGitSync.CheckArchiveGuards(real), Is.Null);
        }

        [Test]
        public void DescribeDoneMarker_NamesTheTrack()
        {
            Assert.That(LocalSelfUpdateService.DescribeDoneMarker(JObject.Parse(
                    "{\"version\":\"2.8.5\",\"mode\":\"git\",\"branch\":\"main\",\"from\":\"1a2b3c4\",\"to\":\"5d6e7f8\"}")),
                Is.EqualTo("Self-update via local Git sync (main 1a2b3c4..5d6e7f8, 2.8.5) completed successfully."));
            Assert.That(LocalSelfUpdateService.DescribeDoneMarker(JObject.Parse(
                    "{\"version\":\"2.9.0\",\"mode\":\"git\",\"tag\":\"v2.9.0\",\"from\":\"v2.8.4\",\"to\":\"5d6e7f8\"}")),
                Is.EqualTo("Self-update via local Git sync (v2.8.4..v2.9.0, 2.9.0) completed successfully."));
            Assert.That(LocalSelfUpdateService.DescribeDoneMarker(JObject.Parse(
                    "{\"version\":\"2.8.5\",\"backup\":\"/p/Library/UnitySkills/selfupdate/previous\"}")),
                Is.EqualTo("Self-update to 2.8.5 completed successfully. The previous version was kept at /p/Library/UnitySkills/selfupdate/previous."));
            Assert.That(LocalSelfUpdateService.DescribeDoneMarker(null), Is.EqualTo("Self-update completed successfully."));
            Assert.That(LocalSelfUpdateService.DescribeDoneMarker(new JObject()), Does.Not.Contain("[UnitySkills]"),
                "SkillsLogger adds the prefix itself.");
        }

        // ---------- shared result presentation (drawer and banner) ----------

        private static readonly LocalUpdateOutcome[] NonFailureOutcomes =
        {
            LocalUpdateOutcome.Updated, LocalUpdateOutcome.AlreadyUpToDate, LocalUpdateOutcome.UpdateAvailable,
            LocalUpdateOutcome.ReleaseCheckRequired, LocalUpdateOutcome.Cancelled,
        };

        [Test]
        public void ReasonKey_EveryFailureHasItsOwnLocalizedReason()
        {
            var saved = SkillsLocalization.Current;
            try
            {
                foreach (LocalUpdateOutcome outcome in Enum.GetValues(typeof(LocalUpdateOutcome)))
                {
                    if (NonFailureOutcomes.Contains(outcome)) continue;
                    var key = SelfUpdateFeedback.ReasonKey(outcome);
                    Assert.That(key, Is.Not.EqualTo("update_check_reason_unknown"), outcome.ToString());
                    foreach (var language in new[] { SkillsLocalization.Language.English, SkillsLocalization.Language.Chinese, SkillsLocalization.Language.Russian })
                    {
                        SkillsLocalization.Current = language;
                        Assert.That(SkillsLocalization.TryGet(key, out _), Is.True, $"{language}: {key}");
                    }
                }
            }
            finally
            {
                SkillsLocalization.Current = saved;
            }
        }

        [Test]
        public void Refusals_ExplainTheirPaths_OtherFailuresStayOnTheStatusLine()
        {
            var probe = new LocalUpdateProbe { Track = LocalUpdateTrack.GitSync, RepoRoot = "/repo", Branch = "main", PackageDir = "/repo/SkillsForUnity" };
            var entries = Enumerable.Range(1, 10).Select(i => $" M file{i}.cs").ToList();
            var dirty = new LocalUpdateResult { Outcome = LocalUpdateOutcome.DirtyWorktree, Probe = probe, Entries = entries.Take(8).ToList(), EntryCount = 10 };

            var body = SelfUpdateFeedback.BuildRefusalBody(dirty);

            Assert.That(body, Does.Contain("/repo").And.Contain("file1.cs").And.Contain("file8.cs"));
            Assert.That(body, Does.Not.Contain("file9.cs"));
            Assert.That(body, Does.Not.Contain("{0}").And.Not.Contain("{1}"));
            Assert.That(SelfUpdateFeedback.BuildRefusalBody(new LocalUpdateResult { Outcome = LocalUpdateOutcome.NetworkError, Probe = probe }), Is.Null);
            Assert.That(SelfUpdateFeedback.FormatEntries(entries.Take(8).ToList(), 10).Split('\n').Length, Is.EqualTo(9),
                "Eight paths, then one line for the other two.");
        }

        [Test]
        public void ModeHint_ShowsTheBranchForClones_TheArchiveForCopies_NothingOtherwise()
        {
            Assert.That(SelfUpdateFeedback.ModeHintKey(new LocalUpdateProbe { Track = LocalUpdateTrack.GitSync, Branch = "beta", HeadLabel = "beta" }, out var arg),
                Is.EqualTo("update_mode_git_fmt"));
            Assert.That(arg, Is.EqualTo("beta"));
            Assert.That(SelfUpdateFeedback.ModeHintKey(new LocalUpdateProbe
                    { Track = LocalUpdateTrack.Blocked, Blocker = LocalUpdateOutcome.DetachedHead, HeadLabel = "1a2b3c4" }, out arg),
                Is.EqualTo("update_mode_git_fmt"));
            Assert.That(arg, Is.EqualTo("1a2b3c4"));
            Assert.That(SelfUpdateFeedback.ModeHintKey(new LocalUpdateProbe { Track = LocalUpdateTrack.ArchiveInRepository }, out _),
                Is.EqualTo("update_mode_archive"));
            Assert.That(SelfUpdateFeedback.ModeHintKey(new LocalUpdateProbe
                { Track = LocalUpdateTrack.Blocked, Blocker = LocalUpdateOutcome.PackageDirIsLink }, out _), Is.Null);
            Assert.That(SelfUpdateFeedback.DoneStatusKey(new LocalUpdateResult { BackupPath = "/p/previous" }, out arg),
                Is.EqualTo("update_check_done_backup_fmt"));
            Assert.That(arg, Is.EqualTo("/p/previous"));
        }

        private string BackupDir => Path.Combine(_tempDir, "Library", "selfupdate", "previous");

        private (string target, string staging) PrepareSwap()
        {
            var target = Path.Combine(_tempDir, "target");
            var staging = Path.Combine(_tempDir, "staging");
            Directory.CreateDirectory(Path.Combine(target, "Editor"));
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(target, "Editor", "keep.cs"), "keep");
            File.WriteAllText(Path.Combine(target, "package.json"), "{ \"version\": \"2.8.1\" }");
            File.WriteAllText(Path.Combine(staging, "package.json"), "{ \"version\": \"2.8.2\" }");
            File.WriteAllText(Path.Combine(staging, "new.txt"), "new");
            return (target, staging);
        }

        private void AssertNoSwapLeftovers()
        {
            Assert.That(Directory.GetDirectories(_tempDir, ".unityskills-new-*"), Is.Empty);
            Assert.That(Directory.GetDirectories(_tempDir, ".unityskills-old-*"), Is.Empty);
        }

        private static string Fingerprint(string dir)
        {
            var root = Path.GetFullPath(dir);
            return string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => path.Substring(root.Length) + "=" + File.ReadAllText(path)));
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

        private static void RunTool(string exe, params string[] args)
        {
            var run = GitCliRunner.Run(exe, null, args, 10000);
            Assert.That(run.ExitCode, Is.EqualTo(0), exe + " " + string.Join(" ", args) + ": " + run.StdErr);
        }

        private void WritePackageJson(string name, string version)
        {
            File.WriteAllText(Path.Combine(_tempDir, "package.json"),
                $"{{ \"name\": \"{name}\", \"version\": \"{version}\" }}");
        }

    }
}

// Producer:Betsy
