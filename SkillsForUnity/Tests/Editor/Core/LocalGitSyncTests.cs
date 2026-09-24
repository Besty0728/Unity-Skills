using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Track A of the local self-update: the pure parsers and guards, then the whole Probe / Prepare / Apply flow against
    /// throwaway repositories under the temp folder. The "official" repository is a local bare repo injected through
    /// GitSyncContext, so nothing touches the network, the real package folder or LocalSelfUpdateService.Start/Check.
    /// Integration tests are ignored when no git is installed.
    /// </summary>
    [TestFixture]
    public class LocalGitSyncTests
    {
        private string _root;
        private string _git;
        private Version _gitVersion;
        private string _hooks;
        private string _official;
        private string _seed;
        private string _clone;
        private string _rootCommit;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "us-gitsync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root == null || !Directory.Exists(_root)) return;
            // git marks pack files read-only, which Directory.Delete refuses on Windows.
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }

        // ================= pure: repository discovery and URL recognition =================

        [Test]
        public void TryFindGitMarker_FindsDirectoriesFilesNearestAndStart()
        {
            var repo = Dir("repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var package = Dir("repo", "Packages", "pkg");
            Assert.That(LocalGitSync.TryFindGitMarker(package, out var marker, _root), Is.True);
            Assert.That(marker, Is.EqualTo(repo).IgnoreCase);

            // A worktree or submodule has a .git file; the nearest marker wins.
            var nested = Dir("repo", "Packages", "sub");
            File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: ../../.git/modules/sub");
            Assert.That(LocalGitSync.TryFindGitMarker(Dir("repo", "Packages", "sub", "Editor"), out marker, _root), Is.True);
            Assert.That(marker, Is.EqualTo(nested).IgnoreCase);

            Assert.That(LocalGitSync.TryFindGitMarker(repo, out marker, _root), Is.True, "The start folder itself counts.");
            Assert.That(marker, Is.EqualTo(repo).IgnoreCase);

            Assert.That(LocalGitSync.TryFindGitMarker(Dir("plain", "pkg"), out marker, _root), Is.False);
            Assert.That(marker, Is.Null);
        }

        [TestCase("https://github.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://github.com/Besty0728/Unity-Skills")]
        [TestCase("https://github.com/besty0728/unity-skills")]
        [TestCase("https://github.com/Besty0728/Unity-Skills/")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git/")]
        [TestCase("http://github.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://user:token@github.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://www.github.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://github.com:443/Besty0728/Unity-Skills.git")]
        [TestCase("git@github.com:Besty0728/Unity-Skills.git")]
        [TestCase("github.com:Besty0728/Unity-Skills")]
        [TestCase("ssh://git@github.com/Besty0728/Unity-Skills.git")]
        [TestCase("ssh://git@ssh.github.com:443/Besty0728/Unity-Skills.git")]
        [TestCase("git://github.com/Besty0728/Unity-Skills.git")]
        [TestCase("  https://github.com/Besty0728/Unity-Skills.git \n")]
        public void IsOfficialRepoUrl_AcceptsTheOfficialRepositoryInEveryForm(string url)
        {
            Assert.That(LocalGitSync.IsOfficialRepoUrl(url), Is.True, url);
        }

        [TestCase("https://github.com/someone/Unity-Skills.git")]
        [TestCase("https://github.com/Besty0728/Unity-Skills-Fork.git")]
        [TestCase("https://github.com/Besty0728/Unity-Skills/tree/main")]
        [TestCase("https://gitee.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://github.com.evil.example/Besty0728/Unity-Skills.git")]
        [TestCase("https://evilgithub.com/Besty0728/Unity-Skills.git")]
        [TestCase("https://evil.example/Besty0728/Unity-Skills.git")]
        [TestCase("git@gitlab.com:Besty0728/Unity-Skills.git")]
        [TestCase("C:/repos/Unity-Skills")]
        [TestCase("/Users/x/Unity-Skills")]
        [TestCase("file:///x/Unity-Skills.git")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git.evil")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.gitx")]
        [TestCase("ftp://github.com/Besty0728/Unity-Skills.git")]
        [TestCase("")]
        [TestCase(null)]
        public void IsOfficialRepoUrl_RejectsEverythingElse(string url)
        {
            Assert.That(LocalGitSync.IsOfficialRepoUrl(url), Is.False, url ?? "<null>");
        }

        [Test]
        public void SameRepositoryUrl_IgnoresFileSchemeAndTrailingSlash()
        {
            Assert.That(LocalGitSync.SameRepositoryUrl("file:///tmp/x/official.git", "/tmp/x/official.git/"), Is.True);
            Assert.That(LocalGitSync.SameRepositoryUrl("/tmp/x/official.git", "/tmp/x/other.git"), Is.False);
            Assert.That(LocalGitSync.SameRepositoryUrl(null, "/tmp/x"), Is.False);
        }

        [Test]
        public void IsCloneShaped_OnlyForSkillsForUnityDirectlyUnderTheRepositoryRoot()
        {
            var repo = Dir("clone");
            Assert.That(LocalGitSync.IsCloneShaped(Path.Combine(repo, "SkillsForUnity"), repo), Is.True);
            Assert.That(LocalGitSync.IsCloneShaped(Path.Combine(repo, "Packages", "SkillsForUnity"), repo), Is.False);
            Assert.That(LocalGitSync.IsCloneShaped(Path.Combine(repo, "Packages", "com.besty.unity-skills"), repo), Is.False);
        }

        // ================= pure: git output parsers =================

        [Test]
        public void ParsePorcelainZ_ListsEntriesAndConsumesRenameOrigins()
        {
            Assert.That(LocalGitSync.ParsePorcelainZ(string.Empty, out var count), Is.Empty);
            Assert.That(count, Is.EqualTo(0));

            Assert.That(LocalGitSync.ParsePorcelainZ(" M a.txt\0", out count), Is.EqualTo(new[] { " M a.txt" }));

            var entries = LocalGitSync.ParsePorcelainZ("R  new.txt\0old.txt\0 M b.txt\0", out count);
            Assert.That(entries, Is.EqualTo(new[] { "R  new.txt", " M b.txt" }), "The rename origin is not an entry of its own.");
            Assert.That(count, Is.EqualTo(2));

            entries = LocalGitSync.ParsePorcelainZ("C  copy.txt\0src.txt\0?? dir/\0UU conflict.cs\0", out count);
            Assert.That(entries, Is.EqualTo(new[] { "C  copy.txt", "?? dir/", "UU conflict.cs" }));

            entries = LocalGitSync.ParsePorcelainZ("?? Assets/有 空格/文件.cs\0", out count);
            Assert.That(entries, Is.EqualTo(new[] { "?? Assets/有 空格/文件.cs" }));

            // Anything non-empty is dirty, even if it does not look like a status record.
            LocalGitSync.ParsePorcelainZ("M", out count);
            Assert.That(count, Is.EqualTo(1));
        }

        [TestCase(0, "main", "Allowed", "main")]
        [TestCase(0, "beta", "Allowed", "beta")]
        [TestCase(0, "main\n", "Allowed", "main")]
        [TestCase(0, "Main", "Unsupported", "Main")]
        [TestCase(0, "feature/x", "Unsupported", "feature/x")]
        [TestCase(1, "", "Detached", null)]
        [TestCase(128, "", "Failed", null)]
        public void EvaluateBranch_OnlyMainAndBetaMayUpdate(int exitCode, string stdout, string expected, string branch)
        {
            // Enum names as strings: a public test method cannot take the internal enum as a parameter.
            Assert.That(LocalGitSync.EvaluateBranch(exitCode, stdout, out var name).ToString(), Is.EqualTo(expected));
            Assert.That(name, Is.EqualTo(branch));
        }

        [TestCase("SkillsForUnity/", "Clone")]
        [TestCase("skillsforunity/", "Clone")]
        [TestCase("", "PackageIsRepoRoot")]
        [TestCase("Packages/com.besty.unity-skills/", "Foreign")]
        [TestCase("SkillsForUnity/Editor/", "Foreign")]
        public void ClassifyPrefix_Cases(string prefix, string expected)
        {
            Assert.That(LocalGitSync.ClassifyPrefix(prefix).ToString(), Is.EqualTo(expected));
        }

        [Test]
        public void SelectOfficialRemote_PrefersOriginThenUpstream_AndDerivesTrackingRefs()
        {
            const string official = "https://github.com/Besty0728/Unity-Skills.git";
            var config =
                "remote.upstream.url\n" + official + "\0" +
                "remote.upstream.fetch\n+refs/heads/*:refs/remotes/upstream/*\0" +
                "remote.origin.url\n" + official + "\0" +
                "remote.origin.url\nhttps://example.invalid/second.git\0" +
                "remote.origin.fetch\n+refs/heads/main:refs/remotes/origin/main\0" +
                "remote.my.fork.url\nhttps://github.com/someone/Unity-Skills.git\0" +
                "remote.origin.pushurl\nhttps://example.invalid/push.git\0";
            var remotes = LocalGitSync.ParseRemoteConfig(config);

            Assert.That(remotes.Select(r => r.Name), Is.EqualTo(new[] { "upstream", "origin", "my.fork" }), "Dotted names stay whole.");
            Assert.That(remotes[1].Urls, Is.EqualTo(new[] { official, "https://example.invalid/second.git" }));

            Assert.That(LocalGitSync.SelectOfficialRemote(remotes, LocalGitSync.IsOfficialRepoUrl, "main", out var name, out var tracking), Is.True);
            Assert.That(name, Is.EqualTo("origin"));
            Assert.That(tracking, Is.EqualTo("refs/remotes/origin/main"), "A single-branch refspec maps its own branch.");

            Assert.That(LocalGitSync.SelectOfficialRemote(remotes, LocalGitSync.IsOfficialRepoUrl, "beta", out name, out tracking), Is.True);
            Assert.That(tracking, Is.Null, "origin only maps main, so beta has no tracking ref to refresh.");

            var withoutOrigin = remotes.Where(r => r.Name != "origin").ToList();
            LocalGitSync.SelectOfficialRemote(withoutOrigin, LocalGitSync.IsOfficialRepoUrl, "beta", out name, out tracking);
            Assert.That(name, Is.EqualTo("upstream"));
            Assert.That(tracking, Is.EqualTo("refs/remotes/upstream/beta"), "The default wildcard refspec maps every branch.");
        }

        [Test]
        public void SelectOfficialRemote_CustomRefspecOrUnsafeName_HasNoTrackingRef()
        {
            const string official = "https://github.com/Besty0728/Unity-Skills.git";
            var remotes = LocalGitSync.ParseRemoteConfig(
                "remote.origin.url\n" + official + "\0remote.origin.fetch\n+refs/heads/*:refs/remotes/mirror/*\0" +
                "remote.it's.url\n" + official + "\0remote.it's.fetch\n+refs/heads/*:refs/remotes/it's/*\0");

            LocalGitSync.SelectOfficialRemote(remotes, LocalGitSync.IsOfficialRepoUrl, "main", out var name, out var tracking);
            Assert.That(name, Is.EqualTo("origin"));
            Assert.That(tracking, Is.Null);

            LocalGitSync.SelectOfficialRemote(remotes.Where(r => r.Name != "origin").ToList(), LocalGitSync.IsOfficialRepoUrl, "main",
                out name, out tracking);
            Assert.That(name, Is.EqualTo("it's"));
            Assert.That(tracking, Is.Null, "A quote in the ref would not survive the process argument guard.");

            Assert.That(LocalGitSync.SelectOfficialRemote(LocalGitSync.ParseRemoteConfig(
                "remote.origin.url\nhttps://github.com/someone/Unity-Skills.git\0"), LocalGitSync.IsOfficialRepoUrl, "main", out _, out _), Is.False);
        }

        [Test]
        public void TryParseLsRemote_MatchesTheExactRef()
        {
            var sha40 = new string('a', 40);
            var sha64 = new string('b', 64);
            var output = $"{new string('c', 40)}\trefs/heads/main-old\n{sha40}\trefs/heads/main\n{sha64}\trefs/heads/beta\n";

            Assert.That(LocalGitSync.TryParseLsRemote(output, "refs/heads/main", out var sha), Is.True);
            Assert.That(sha, Is.EqualTo(sha40));
            Assert.That(LocalGitSync.TryParseLsRemote(output, "refs/heads/beta", out sha), Is.True);
            Assert.That(sha, Is.EqualTo(sha64));
            Assert.That(LocalGitSync.TryParseLsRemote(string.Empty, "refs/heads/main", out _), Is.False);
            Assert.That(LocalGitSync.TryParseLsRemote("nothex\trefs/heads/main\n", "refs/heads/main", out _), Is.False);
        }

        [Test]
        public void ParseReleaseTags_PeelsAnnotatedTags_AndKeepsOnlyVersionTags()
        {
            string Sha(char c) => new string(c, 40);
            var output =
                $"{Sha('1')}\trefs/tags/v1.0.0\n" +
                $"{Sha('2')}\trefs/tags/v1.1.0\n{Sha('3')}\trefs/tags/v1.1.0^{{}}\n" +
                $"{Sha('4')}\trefs/tags/v1.2.0-rc1\n" +
                $"{Sha('5')}\trefs/tags/nightly\n" +
                $"{Sha('6')}\trefs/tags/v1.10.0\n";

            var tags = LocalGitSync.ParseReleaseTags(output).ToDictionary(t => t.Name);

            Assert.That(tags.Keys, Is.EquivalentTo(new[] { "v1.0.0", "v1.1.0", "v1.10.0" }));
            Assert.That(tags["v1.0.0"].Commit, Is.EqualTo(Sha('1')), "Lightweight tag: the ref is the commit.");
            Assert.That(tags["v1.1.0"].Commit, Is.EqualTo(Sha('3')), "Annotated tag: the peeled line is the commit.");
            Assert.That(tags.Values.OrderByDescending(t => t.Version).First().Name, Is.EqualTo("v1.10.0"),
                "Versions compare numerically, not as text.");
        }

        [Test]
        public void ParseOverwrittenPaths_ReadsTheTabIndentedList()
        {
            var stderr =
                "error: The following untracked working tree files would be overwritten by merge:\n" +
                "\tSkillsForUnity/Editor/x.asset\n" +
                "\tSkillsForUnity/编辑器/说明.md\n" +
                "Please move or remove them before you merge.\n" +
                "Aborting\n";

            Assert.That(LocalGitSync.ParseOverwrittenPaths(stderr),
                Is.EqualTo(new[] { "SkillsForUnity/Editor/x.asset", "SkillsForUnity/编辑器/说明.md" }));
            Assert.That(LocalGitSync.ParseOverwrittenPaths("fatal: something else\n"), Is.Empty);
        }

        // ================= pure: process layer =================

        [TestCase("git version 2.54.0", 2, 54, 0)]
        [TestCase("git version 2.45.1.windows.1", 2, 45, 1)]
        [TestCase("git version 2.50.1 (Apple Git-155)\n", 2, 50, 1)]
        [TestCase("git version 2.15", 2, 15, 0)]
        public void TryParseGitVersion_ReadsTheVersionFields(string output, int major, int minor, int patch)
        {
            Assert.That(GitCliRunner.TryParseGitVersion(output, out var version), Is.True);
            Assert.That(version, Is.EqualTo(new Version(major, minor, patch)));
        }

        [TestCase("")]
        [TestCase("hello")]
        [TestCase("git version two")]
        public void TryParseGitVersion_RejectsGarbage(string output)
        {
            Assert.That(GitCliRunner.TryParseGitVersion(output, out _), Is.False);
        }

        [Test]
        public void IsSafeArgument_GuardsWhatMonoWouldMangle()
        {
            Assert.That(GitCliRunner.IsSafeArgument("+refs/heads/main:refs/remotes/origin/main", windows: false), Is.True);
            Assert.That(GitCliRunner.IsSafeArgument("HEAD^{commit}", windows: false), Is.True);
            Assert.That(GitCliRunner.IsSafeArgument("credential.helper=", windows: false), Is.True);
            Assert.That(GitCliRunner.IsSafeArgument("it's", windows: false), Is.False);
            Assert.That(GitCliRunner.IsSafeArgument("a\\b", windows: false), Is.False);
            Assert.That(GitCliRunner.IsSafeArgument("\"x\"", windows: false), Is.False);
            Assert.That(GitCliRunner.IsSafeArgument("line\nbreak", windows: false), Is.False);
            Assert.That(GitCliRunner.IsSafeArgument(@"C:\dir\x", windows: true), Is.True);
            Assert.That(GitCliRunner.IsSafeArgument("\"x\"", windows: true), Is.False);
            Assert.That(GitCliRunner.IsSafeArgument(null, windows: true), Is.False);
        }

        [Test]
        public void BuildCandidateList_OnlyAbsolutePaths_AndNeverTheMacShim()
        {
            var mac = GitCliRunner.BuildCandidateList(false, true, "relative/bin:/usr/bin:/usr//bin/:/opt/homebrew/bin::", _ => null);
            Assert.That(mac, Does.Not.Contain("/usr/bin/git"), "The Command Line Tools shim pops the installer when the tools are missing.");
            Assert.That(mac.First(), Is.EqualTo("/opt/homebrew/bin/git"));
            Assert.That(mac, Does.Contain("/Library/Developer/CommandLineTools/usr/bin/git"));
            Assert.That(mac.All(Path.IsPathRooted), Is.True);
            Assert.That(mac.Count, Is.EqualTo(mac.Distinct().Count()));

            var linux = GitCliRunner.BuildCandidateList(false, false, "/usr/bin", _ => null);
            Assert.That(linux, Does.Contain("/usr/bin/git"));
        }

        [Test]
        public void Runner_MissingExecutable_IsStartFailed()
        {
            var run = GitCliRunner.Run(Path.Combine(_root, "no-such-git"), null, new[] { "--version" }, 5000);
            Assert.That(run.StartFailed, Is.True);
            Assert.That(run.Succeeded, Is.False);
        }

        [Test]
        public void Runner_UnsafeArgument_ThrowsBeforeStarting()
        {
            Assert.That(() => GitCliRunner.Run(Path.Combine(_root, "no-such-git"), null, new[] { "a\nb" }, 5000),
                Throws.ArgumentException);
        }

        [Test]
        public void Runner_Timeout_KillsTheProcess()
        {
            if (GitCliRunner.IsWindows) Assert.Ignore("Uses /bin/sh.");
            var run = GitCliRunner.Run("/bin/sh", null, new[] { "-c", "sleep 30" }, 500);
            Assert.That(run.TimedOut, Is.True);
            Assert.That(run.ElapsedMs, Is.LessThan(5000));
        }

        [Test]
        public void Runner_Cancellation_KillsTheProcess()
        {
            if (GitCliRunner.IsWindows) Assert.Ignore("Uses /bin/sh.");
            var started = DateTime.UtcNow;
            var run = GitCliRunner.Run("/bin/sh", null, new[] { "-c", "sleep 30" }, 60000,
                () => (DateTime.UtcNow - started).TotalMilliseconds > 300);
            Assert.That(run.Cancelled, Is.True);
            Assert.That(run.ElapsedMs, Is.LessThan(5000));
        }

        [Test]
        public void Runner_ClosesStdin_SoPromptsReadEndOfFile()
        {
            if (GitCliRunner.IsWindows) Assert.Ignore("Uses /bin/cat.");
            var run = GitCliRunner.Run("/bin/cat", null, Array.Empty<string>(), 5000);
            Assert.That(run.Succeeded, Is.True, "cat would block forever on an open stdin.");
        }

        // ================= no git: filesystem fallback =================

        [Test]
        public void NoGit_NoMarker_IsPlainArchive()
        {
            var package = Dir("zip", "SkillsForUnity");
            var probe = LocalGitSync.Probe(NoGitContext(package));
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.Archive));
        }

        [Test]
        public void NoGit_MarkerCloneShaped_Blocks()
        {
            var repo = Dir("clone");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var probe = LocalGitSync.Probe(NoGitContext(Dir("clone", "SkillsForUnity")));
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.Blocked));
            Assert.That(probe.Blocker, Is.EqualTo(LocalUpdateOutcome.GitMissingForRepository),
                "Covering a clone with an archive is the bug this whole track exists to fix.");

            var old = NoGitContext(Dir("clone", "SkillsForUnity"));
            old.GitVersion = new Version(2, 10);
            Assert.That(LocalGitSync.Probe(old).Blocker, Is.EqualTo(LocalUpdateOutcome.GitTooOld));
        }

        [Test]
        public void NoGit_MarkerForeign_IsUnverifiedArchive()
        {
            Directory.CreateDirectory(Path.Combine(Dir("game"), ".git"));
            var probe = LocalGitSync.Probe(NoGitContext(Dir("game", "Packages", "com.besty.unity-skills")));
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.ArchiveUnverified));
            Assert.That(probe.MarkerRoot, Is.EqualTo(Dir("game")).IgnoreCase);
        }

        // ================= integration: track A against a local "official" repository =================

        [Test]
        public void FastForwardsMainToOfficialHead()
        {
            BuildOfficial();
            var head = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(Head(_clone), Is.EqualTo(head));
            Assert.That(G(_clone, "rev-list", "--merges", "HEAD").StdOut.Trim(), Is.Empty, "Fast-forward only: no merge commit.");
            Assert.That(Status(_clone), Is.Empty);
            Assert.That(G(_clone, "rev-parse", "refs/remotes/origin/main").StdOut.Trim(), Is.EqualTo(head),
                "An official origin gets its tracking ref refreshed, like the user's own fetch would.");
            Assert.That(File.ReadAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "b.cs")), Is.EqualTo("b"));
            Assert.That(result.FromSha, Is.EqualTo(_rootCommit));
            Assert.That(result.ToSha, Is.EqualTo(head));
        }

        [Test]
        public void CheckRemote_ReportsTheBranchTargetWithoutWriting()
        {
            BuildOfficial();
            var head = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            var before = Snapshot(_clone);

            var ctx = Context(ClonePackage);
            var result = LocalGitSync.CheckRemote(ctx, LocalGitSync.Probe(ctx));

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.UpdateAvailable));
            Assert.That(result.TargetLabel, Is.EqualTo("main @ " + head.Substring(0, 7)));
            Assert.That(result.Probe.Track, Is.EqualTo(LocalUpdateTrack.GitSync));
            Assert.That(Snapshot(_clone), Is.EqualTo(before), "Checking never writes.");
            Assert.That(File.Exists(Path.Combine(_clone, ".git", "FETCH_HEAD")), Is.False, "Checking never fetches.");
            Assert.That(G(_clone, "rev-parse", "refs/remotes/origin/main").StdOut.Trim(), Is.EqualTo(_rootCommit));
        }

        [Test]
        public void BetaBranchFollowsOfficialBeta_MainUntouched()
        {
            BuildOfficial();
            G(_clone, "checkout", "-q", "-b", "beta", "origin/beta");
            var betaHead = CommitUpstream("beta", "SkillsForUnity/Editor/beta.cs", "beta");
            var mainBefore = G(_clone, "rev-parse", "refs/heads/main").StdOut.Trim();

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(Head(_clone), Is.EqualTo(betaHead));
            Assert.That(G(_clone, "rev-parse", "refs/heads/main").StdOut.Trim(), Is.EqualTo(mainBefore));
        }

        [Test]
        public void AlreadyUpToDate_NoChange()
        {
            BuildOfficial();
            var before = Snapshot(_clone);

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.AlreadyUpToDate));
            Assert.That(Snapshot(_clone), Is.EqualTo(before));
        }

        [Test]
        public void LocalAhead_ReportsUpToDate_NoChange()
        {
            BuildOfficial();
            Commit(_clone, "SkillsForUnity/Editor/mine.cs", "mine");
            var before = Snapshot(_clone);

            var ctx = Context(ClonePackage);
            Assert.That(LocalGitSync.CheckRemote(ctx, LocalGitSync.Probe(ctx)).Outcome, Is.EqualTo(LocalUpdateOutcome.AlreadyUpToDate));
            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.AlreadyUpToDate));
            Assert.That(Snapshot(_clone), Is.EqualTo(before));
        }

        [Test]
        public void Diverged_Refuses_NothingChanged()
        {
            BuildOfficial();
            Commit(_clone, "SkillsForUnity/Editor/mine.cs", "mine");
            CommitUpstream("main", "SkillsForUnity/Editor/theirs.cs", "theirs");
            var before = Snapshot(_clone);

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.Diverged));
            Assert.That(Snapshot(_clone), Is.EqualTo(before), "No merge commit, no rebase, nothing.");
        }

        [Test]
        public void DirtyTracked_Refuses_ListsPath()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            File.WriteAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "a.cs"), "my edit");
            var before = Snapshot(_clone);

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.DirtyWorktree));
            Assert.That(result.Entries, Has.Some.EndsWith("SkillsForUnity/Editor/a.cs"));
            Assert.That(result.EntryCount, Is.EqualTo(1));
            Assert.That(Snapshot(_clone), Is.EqualTo(before));
            Assert.That(File.ReadAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "a.cs")), Is.EqualTo("my edit"));
        }

        [Test]
        public void StagedChange_Refuses()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            File.WriteAllText(Path.Combine(_clone, "README.md"), "staged");
            G(_clone, "add", "README.md");

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.DirtyWorktree),
                "The whole repository counts, not just the package folder.");
        }

        [Test]
        public void UntrackedFile_Refuses()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            File.WriteAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "Scratch.cs"), "untracked");

            var result = Update(ClonePackage);
            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.DirtyWorktree));
            Assert.That(result.Entries, Has.Some.StartsWith("??"));
        }

        [Test]
        public void UntrackedHiddenByUserConfig_StillRefuses()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            G(_clone, "config", "status.showUntrackedFiles", "no");
            File.WriteAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "Scratch.cs"), "untracked");

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.DirtyWorktree));
        }

        [Test]
        public void IgnoredFileCollision_Refuses_ContentPreserved()
        {
            BuildOfficial();
            // Upstream force-adds a path the repository ignores; locally that path holds the user's own ignored file.
            WriteFile(_seed, "SkillsForUnity/Editor/x.asset", "upstream");
            G(_seed, "add", "-f", "SkillsForUnity/Editor/x.asset");
            G(_seed, "commit", "-q", "-m", "force-add ignored path");
            G(_seed, "push", "-q", "origin", "main");
            WriteFile(_clone, "SkillsForUnity/Editor/x.asset", "precious local data");
            var headBefore = Head(_clone);

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.WouldOverwriteLocalFiles));
            Assert.That(result.Entries, Is.EqualTo(new[] { "SkillsForUnity/Editor/x.asset" }));
            Assert.That(File.ReadAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "x.asset")), Is.EqualTo("precious local data"),
                "git overwrites ignored files silently by default; --no-overwrite-ignore must stop it.");
            Assert.That(Head(_clone), Is.EqualTo(headBefore));
        }

        [Test]
        public void FeatureBranch_Refuses_ReportsName()
        {
            BuildOfficial();
            G(_clone, "checkout", "-q", "-b", "feature/x");
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            var before = Snapshot(_clone);

            var probe = LocalGitSync.Probe(Context(ClonePackage));
            Assert.That(probe.Blocker, Is.EqualTo(LocalUpdateOutcome.UnsupportedBranch));
            Assert.That(probe.Branch, Is.EqualTo("feature/x"));
            Assert.That(probe.IsOfficialClone, Is.True, "The drawer still shows the branch.");
            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.UnsupportedBranch));
            Assert.That(Snapshot(_clone), Is.EqualTo(before));
        }

        [Test]
        public void DetachedHead_NotAtAReleaseTag_Refuses()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            G(_clone, "checkout", "-q", "--detach", "HEAD");
            var before = Snapshot(_clone);

            var ctx = Context(ClonePackage);
            var probe = LocalGitSync.Probe(ctx);
            Assert.That(probe.Blocker, Is.EqualTo(LocalUpdateOutcome.DetachedHead));
            Assert.That(probe.HeadAtReleaseTag, Is.False);
            Assert.That(probe.HeadLabel, Is.EqualTo(_rootCommit.Substring(0, 7)));
            Assert.That(LocalGitSync.CheckRemote(ctx, probe).Outcome, Is.EqualTo(LocalUpdateOutcome.DetachedHead));
            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.DetachedHead));
            Assert.That(Snapshot(_clone), Is.EqualTo(before));
        }

        [Test]
        public void DetachedAtReleaseTag_SwitchesToTheNewestReleaseTag()
        {
            BuildOfficial();
            G(_seed, "tag", "v1.0.0");
            G(_seed, "push", "-q", "origin", "v1.0.0");
            G(_clone, "fetch", "-q", "--tags", "origin");
            G(_clone, "checkout", "-q", "--detach", "v1.0.0");
            var release = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            G(_seed, "tag", "-a", "v1.1.0", "-m", "release 1.1.0");
            G(_seed, "tag", "v1.2.0-rc1");
            CommitUpstream("main", "SkillsForUnity/Editor/unreleased.cs", "c");
            G(_seed, "push", "-q", "origin", "v1.1.0", "v1.2.0-rc1");

            var ctx = Context(ClonePackage);
            var check = LocalGitSync.CheckRemote(ctx, LocalGitSync.Probe(ctx));
            Assert.That(check.Outcome, Is.EqualTo(LocalUpdateOutcome.UpdateAvailable));
            Assert.That(check.TargetLabel, Is.EqualTo("v1.1.0"), "Only vX.Y.Z tags count as releases.");
            Assert.That(check.Probe.Track, Is.EqualTo(LocalUpdateTrack.GitTag));
            Assert.That(check.Probe.HeadLabel, Is.EqualTo("v1.0.0"));

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(Head(_clone), Is.EqualTo(release), "Annotated tags resolve to their commit.");
            Assert.That(GTry(_clone, "symbolic-ref", "-q", "HEAD").ExitCode, Is.EqualTo(1), "Still detached.");
            Assert.That(G(_clone, "rev-parse", "refs/tags/v1.1.0^{commit}").StdOut.Trim(), Is.EqualTo(release));
            Assert.That(Status(_clone), Is.Empty);
            Assert.That(File.Exists(Path.Combine(_clone, "SkillsForUnity", "Editor", "unreleased.cs")), Is.False);
        }

        [Test]
        public void DetachedAtNewestReleaseTag_IsUpToDate()
        {
            BuildOfficial();
            G(_seed, "tag", "v1.0.0");
            G(_seed, "push", "-q", "origin", "v1.0.0");
            G(_clone, "fetch", "-q", "--tags", "origin");
            G(_clone, "checkout", "-q", "--detach", "v1.0.0");
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.AlreadyUpToDate),
                "Newer branch commits are not a release; a pinned tag only moves to a newer tag.");
        }

        [Test]
        public void DetachedAtReleaseTag_IgnoredFileCollision_Refuses()
        {
            BuildOfficial();
            G(_seed, "tag", "v1.0.0");
            G(_seed, "push", "-q", "origin", "v1.0.0");
            G(_clone, "fetch", "-q", "--tags", "origin");
            G(_clone, "checkout", "-q", "--detach", "v1.0.0");
            WriteFile(_seed, "SkillsForUnity/Editor/x.asset", "upstream");
            G(_seed, "add", "-f", "SkillsForUnity/Editor/x.asset");
            G(_seed, "commit", "-q", "-m", "force-add ignored path");
            G(_seed, "tag", "v1.1.0");
            G(_seed, "push", "-q", "origin", "main", "v1.1.0");
            WriteFile(_clone, "SkillsForUnity/Editor/x.asset", "precious local data");
            var headBefore = Head(_clone);

            var result = Update(ClonePackage);

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.WouldOverwriteLocalFiles));
            Assert.That(File.ReadAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "x.asset")), Is.EqualTo("precious local data"));
            Assert.That(Head(_clone), Is.EqualTo(headBefore));
        }

        [Test]
        public void NoOfficialRemote_PullsInjectedOfficial_NotTheFork()
        {
            BuildOfficial();
            var fork = Path.Combine(_root, "fork.git");
            G(_root, "clone", "-q", "--bare", _official, fork);
            var forkWork = Path.Combine(_root, "fork-work");
            G(_root, "clone", "-q", fork, forkWork);
            var forkCommit = Commit(forkWork, "SkillsForUnity/Editor/fork.cs", "fork");
            G(forkWork, "push", "-q", "origin", "main");

            var forkClone = Path.Combine(_root, "fork-clone");
            G(_root, "clone", "-q", _official, forkClone);
            G(forkClone, "config", "core.hooksPath", _hooks);
            G(forkClone, "remote", "set-url", "origin", fork);
            G(forkClone, "fetch", "-q", "origin");
            var trackingBefore = G(forkClone, "rev-parse", "refs/remotes/origin/main").StdOut.Trim();
            var head = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");

            var result = Update(Path.Combine(forkClone, "SkillsForUnity"));

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.Updated), "Identity comes from the official root commit.");
            Assert.That(Head(forkClone), Is.EqualTo(head));
            Assert.That(GTry(forkClone, "merge-base", "--is-ancestor", forkCommit, "HEAD").ExitCode, Is.EqualTo(1),
                "The fork's commits must never be merged in.");
            Assert.That(G(forkClone, "rev-parse", "refs/remotes/origin/main").StdOut.Trim(), Is.EqualTo(trackingBefore),
                "A non-official origin keeps its tracking ref.");
        }

        [Test]
        public void RenamedOfficialRemote_UpdatesItsTrackingRef()
        {
            BuildOfficial();
            G(_clone, "remote", "rename", "origin", "upstream");
            var head = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");

            Assert.That(Update(ClonePackage).Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(G(_clone, "rev-parse", "refs/remotes/upstream/main").StdOut.Trim(), Is.EqualTo(head));
        }

        [Test]
        public void Worktree_UpdatesTheWorktreeBranch()
        {
            BuildOfficial();
            G(_clone, "branch", "beta", "origin/beta");
            var worktree = Path.Combine(_root, "wt");
            G(_clone, "worktree", "add", worktree, "beta");
            Assert.That(File.Exists(Path.Combine(worktree, ".git")), Is.True, "A worktree has a .git file.");
            var betaHead = CommitUpstream("beta", "SkillsForUnity/Editor/beta.cs", "beta");

            var result = Update(Path.Combine(worktree, "SkillsForUnity"));

            Assert.That(result.Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(Head(worktree), Is.EqualTo(betaHead));
            Assert.That(Head(_clone), Is.EqualTo(_rootCommit), "The main worktree's branch is untouched.");
        }

        [Test]
        public void ShallowClone_FastForwards()
        {
            BuildOfficial();
            var shallow = Path.Combine(_root, "shallow");
            G(_root, "clone", "-q", "--depth", "1", new Uri(_official).AbsoluteUri, shallow);
            G(shallow, "config", "core.hooksPath", _hooks);
            G(shallow, "remote", "remove", "origin");
            var head = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");

            var probe = LocalGitSync.Probe(Context(Path.Combine(shallow, "SkillsForUnity")));
            Assert.That(probe.IsShallow, Is.True);
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.GitSync), "Shallow clones lack the root commit but are accepted.");
            Assert.That(Update(Path.Combine(shallow, "SkillsForUnity")).Outcome, Is.EqualTo(LocalUpdateOutcome.Updated));
            Assert.That(Head(shallow), Is.EqualTo(head));
        }

        [Test]
        public void ApplyRechecks_BranchSwitchedAfterPrepare_MergesNothing()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            var ctx = Context(ClonePackage);
            var prepared = LocalGitSync.Prepare(ctx, LocalGitSync.Probe(ctx));
            Assert.That(prepared.Outcome, Is.EqualTo(LocalUpdateOutcome.UpdateAvailable));
            G(_clone, "checkout", "-q", "-b", "feature/y");

            Assert.That(LocalGitSync.Apply(ctx, prepared).Outcome, Is.EqualTo(LocalUpdateOutcome.UnsupportedBranch));
            Assert.That(Head(_clone), Is.EqualTo(_rootCommit));
            Assert.That(G(_clone, "rev-parse", "refs/heads/main").StdOut.Trim(), Is.EqualTo(_rootCommit));
        }

        [Test]
        public void ApplyRechecks_EditedAfterPrepare_Refuses()
        {
            BuildOfficial();
            CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            var ctx = Context(ClonePackage);
            var prepared = LocalGitSync.Prepare(ctx, LocalGitSync.Probe(ctx));
            File.WriteAllText(Path.Combine(_clone, "SkillsForUnity", "Editor", "a.cs"), "edited meanwhile");

            Assert.That(LocalGitSync.Apply(ctx, prepared).Outcome, Is.EqualTo(LocalUpdateOutcome.DirtyWorktree));
            Assert.That(Head(_clone), Is.EqualTo(_rootCommit));
        }

        [Test]
        public void Vendored_CleanSubtree_IsArchiveInRepository_NeverFetchesOfficial()
        {
            BuildOfficial();
            var official = CommitUpstream("main", "SkillsForUnity/Editor/b.cs", "b");
            var game = NewRepo("game");
            WriteFile(game, "Packages/com.besty.unity-skills/package.json", "{\"name\":\"com.besty.unity-skills\",\"version\":\"1.0.0\"}");
            WriteFile(game, "Packages/com.besty.unity-skills/Editor/a.cs", "a");
            Commit(game, "Assets/Main.cs", "game code");

            var ctx = Context(Path.Combine(game, "Packages", "com.besty.unity-skills"));
            var probe = LocalGitSync.Probe(ctx);
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.ArchiveInRepository));
            Assert.That(LocalGitSync.Prepare(ctx, probe).Outcome, Is.EqualTo(LocalUpdateOutcome.ReleaseCheckRequired));
            Assert.That(File.Exists(Path.Combine(game, ".git", "FETCH_HEAD")), Is.False);
            Assert.That(GTry(game, "cat-file", "-e", official + "^{commit}").ExitCode, Is.Not.EqualTo(0),
                "Official history must never land in someone else's repository.");
        }

        [Test]
        public void Vendored_DirtySubtree_Blocks_WithEntries()
        {
            BuildOfficial();
            var game = NewRepo("game");
            WriteFile(game, "Packages/com.besty.unity-skills/package.json", "{}");
            Commit(game, "Packages/com.besty.unity-skills/Editor/a.cs", "a");
            File.WriteAllText(Path.Combine(game, "Packages", "com.besty.unity-skills", "Editor", "a.cs"), "local edit");
            WriteFile(game, "Packages/com.besty.unity-skills/Editor/New.cs", "untracked");
            WriteFile(game, "Assets/Unrelated.cs", "outside the package");

            var probe = LocalGitSync.Probe(Context(Path.Combine(game, "Packages", "com.besty.unity-skills")));

            Assert.That(probe.Blocker, Is.EqualTo(LocalUpdateOutcome.VendoredSubtreeModified));
            Assert.That(probe.EntryCount, Is.EqualTo(2), "Only the package subtree counts; untracked files inside it do.");
        }

        [Test]
        public void Vendored_UntrackedSubtree_IsPlainArchive()
        {
            BuildOfficial();
            var game = NewRepo("game");
            WriteFile(game, ".gitignore", "Packages/\n");
            Commit(game, "Assets/Main.cs", "game code");
            WriteFile(game, "Packages/com.besty.unity-skills/package.json", "{}");

            var probe = LocalGitSync.Probe(Context(Path.Combine(game, "Packages", "com.besty.unity-skills")));
            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.Archive));
        }

        [Test]
        public void RootLevelSkillsForUnityInForeignRepo_IsTreatedAsVendored()
        {
            BuildOfficial();
            var foreign = NewRepo("foreign");
            WriteFile(foreign, "SkillsForUnity/package.json", "{}");
            Commit(foreign, "SkillsForUnity/Editor/a.cs", "a");

            var ctx = Context(Path.Combine(foreign, "SkillsForUnity"));
            var probe = LocalGitSync.Probe(ctx);

            Assert.That(probe.Track, Is.EqualTo(LocalUpdateTrack.ArchiveInRepository),
                "No official remote, no official root commit, not shallow: not an official clone.");
            Assert.That(File.Exists(Path.Combine(foreign, ".git", "FETCH_HEAD")), Is.False);
        }

        [Test]
        public void PackageDirIsRepoRoot_Blocks_GitDirIntact()
        {
            RequireGit();
            var package = NewRepo("pkg");
            Commit(package, "package.json", "{}");

            var probe = LocalGitSync.Probe(Context(package));

            Assert.That(probe.Blocker, Is.EqualTo(LocalUpdateOutcome.PackageDirIsRepository));
            Assert.That(Directory.Exists(Path.Combine(package, ".git")), Is.True);
        }

        // ================= fixture helpers =================

        private string ClonePackage => Path.Combine(_clone, "SkillsForUnity");

        private LocalUpdateResult Update(string packageDir)
        {
            var ctx = Context(packageDir);
            var prepared = LocalGitSync.Prepare(ctx, LocalGitSync.Probe(ctx));
            return prepared.Outcome == LocalUpdateOutcome.UpdateAvailable ? LocalGitSync.Apply(ctx, prepared) : prepared;
        }

        private GitSyncContext Context(string packageDir) => new GitSyncContext
        {
            GitPath = _git,
            GitVersion = _gitVersion,
            PackageDir = packageDir,
            OfficialUrl = _official,
            OfficialRootCommit = _rootCommit ?? new string('0', 40),
            MarkerCeiling = _root,
        };

        private GitSyncContext NoGitContext(string packageDir) => new GitSyncContext
        {
            PackageDir = packageDir,
            MarkerCeiling = _root,
        };

        private void RequireGit()
        {
            if (_git != null) return;
            if (!GitCliRunner.TryResolveGit(out _git, out _gitVersion))
                Assert.Ignore("No git " + GitCliRunner.MinimumVersion + "+ on this machine.");
            _hooks = Dir("no-hooks");
        }

        /// <summary>official.git (bare) with main and beta at one root commit, a seed clone to push from, and the clone under test.</summary>
        private void BuildOfficial()
        {
            RequireGit();
            _official = Path.Combine(_root, "official.git");
            G(_root, "init", "-q", "--bare", _official);
            G(_official, "symbolic-ref", "HEAD", "refs/heads/main");

            _seed = NewRepo("seed");
            WriteFile(_seed, ".gitignore", "*.asset\n");
            WriteFile(_seed, "README.md", "readme");
            WriteFile(_seed, "SkillsForUnity/package.json", "{\"name\":\"com.besty.unity-skills\",\"version\":\"1.0.0\"}");
            _rootCommit = Commit(_seed, "SkillsForUnity/Editor/a.cs", "a");
            G(_seed, "branch", "beta");
            G(_seed, "remote", "add", "origin", _official);
            G(_seed, "push", "-q", "origin", "main", "beta");

            _clone = Path.Combine(_root, "clone");
            G(_root, "clone", "-q", _official, _clone);
            // The code under test runs with the user's global config; keep their hooks out of these repositories.
            G(_clone, "config", "core.hooksPath", _hooks);
        }

        private string NewRepo(string name)
        {
            var dir = Dir(name);
            G(dir, "init", "-q");
            G(dir, "symbolic-ref", "HEAD", "refs/heads/main");
            G(dir, "config", "core.hooksPath", _hooks);
            return dir;
        }

        private string CommitUpstream(string branch, string relativePath, string content)
        {
            G(_seed, "checkout", "-q", branch);
            var sha = Commit(_seed, relativePath, content);
            G(_seed, "push", "-q", "origin", branch);
            G(_seed, "checkout", "-q", "main");
            return sha;
        }

        private string Commit(string repo, string relativePath, string content)
        {
            WriteFile(repo, relativePath, content);
            G(repo, "add", "-A");
            G(repo, "commit", "-q", "-m", "update " + relativePath);
            return Head(repo);
        }

        private static void WriteFile(string repo, string relativePath, string content)
        {
            var path = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private string Head(string repo) => G(repo, "rev-parse", "HEAD").StdOut.Trim();

        private string Status(string repo) =>
            G(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all").StdOut;

        /// <summary>HEAD, index/working-tree state (ignored files included) and every file's content: "nothing changed" means all equal.</summary>
        private string Snapshot(string repo)
        {
            var text = new StringBuilder();
            text.Append(G(repo, "rev-parse", "HEAD").StdOut.Trim()).Append('\n');
            text.Append(GTry(repo, "symbolic-ref", "-q", "HEAD").StdOut.Trim()).Append('\n');
            text.Append(G(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignored").StdOut).Append('\n');
            var root = Path.GetFullPath(repo);
            var gitEntry = Path.DirectorySeparatorChar + ".git";
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                         .Where(path => path.Substring(root.Length) != gitEntry &&
                                        !path.Substring(root.Length).StartsWith(gitEntry + Path.DirectorySeparatorChar))
                         .OrderBy(path => path, StringComparer.Ordinal))
                text.Append(file.Substring(root.Length)).Append('=').Append(File.ReadAllText(file)).Append('\n');
            return text.ToString();
        }

        private ProcessRunResult G(string workDir, params string[] args) => RunTestGit(workDir, args, false);

        private ProcessRunResult GTry(string workDir, params string[] args) => RunTestGit(workDir, args, true);

        private ProcessRunResult RunTestGit(string workDir, string[] args, bool allowFailure)
        {
            var full = new List<string>
            {
                "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false",
                "-c", "tag.gpgsign=false", "-c", "core.hooksPath=" + _hooks, "-c", "advice.detachedHead=false",
            };
            full.AddRange(args);
            var run = GitCliRunner.RunGit(_git, workDir, full, 60000);
            if (!allowFailure)
                Assert.That(run.Succeeded, Is.True, $"git {string.Join(" ", args)} in {workDir}: exit {run.ExitCode}\n{run.StdErr}");
            return run;
        }

        private string Dir(params string[] parts)
        {
            var segments = new List<string> { _root };
            segments.AddRange(parts);
            var dir = Path.Combine(segments.ToArray());
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

// Producer:Betsy
