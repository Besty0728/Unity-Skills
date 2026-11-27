using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>How a local install gets updated; decided on every run by <see cref="LocalGitSync.Probe"/>.</summary>
    internal enum LocalUpdateTrack
    {
        /// <summary>No repository around the package: the folder is replaced with the release archive.</summary>
        Archive,
        /// <summary>Copied into another repository and clean there: archive, which that repository then shows as a diff.</summary>
        ArchiveInRepository,
        /// <summary>Inside another repository but no git to prove the folder clean: archive only after the user confirms.</summary>
        ArchiveUnverified,
        /// <summary>An official clone on beta: fast-forward to the official branch head.</summary>
        GitSync,
        /// <summary>An official clone on main or detached at an official release tag: move to the target release tag.</summary>
        GitTag,
        /// <summary>Nothing may be written; <see cref="LocalUpdateProbe.Blocker"/> says why.</summary>
        Blocked,
    }

    /// <summary>Results of the local self-update. UI only: no REST skill starts a self-update, so none of these are wire codes.</summary>
    internal enum LocalUpdateOutcome
    {
        Updated,
        AlreadyUpToDate,
        UpdateAvailable,
        /// <summary>Archive and release-tag tracks: the caller compares against the latest stable release itself.</summary>
        ReleaseCheckRequired,
        /// <summary>The install changed between check and update (another branch, the repository went away); check again.</summary>
        RecheckRequired,
        Cancelled,
        Busy,
        PackageRootNotFound,
        NetworkError,
        DiskError,
        InvalidPackage,
        GitMissingForRepository,
        GitTooOld,
        GitUnsafeRepository,
        GitRepositoryLocked,
        GitCommandFailed,
        DirtyWorktree,
        DetachedHead,
        UnsupportedBranch,
        Diverged,
        WouldOverwriteLocalFiles,
        MergeInterrupted,
        VendoredSubtreeModified,
        PackageDirIsRepository,
        PackageDirIsLink,
    }

    internal sealed class LocalUpdateProbe
    {
        public LocalUpdateTrack Track;
        public LocalUpdateOutcome Blocker;
        public string PackageDir, RepoRoot, GitDir, MarkerRoot, Branch, HeadSha, HeadLabel, OfficialRemote, TrackingRef;
        public bool IsShallow;
        /// <summary>Detached with a local vX.Y.Z tag at HEAD; whether it is an official release is only known after ls-remote.</summary>
        public bool HeadAtReleaseTag;
        /// <summary>ArchiveInRepository: the package subtree has uncommitted changes in the host repository.</summary>
        public bool HasVendoredChanges;
        public IReadOnlyList<string> Entries = Array.Empty<string>();
        public int EntryCount;

        /// <summary>An official clone, whatever the verdict: the settings drawer shows its branch or tag.</summary>
        public bool IsOfficialClone => Track == LocalUpdateTrack.GitSync || Track == LocalUpdateTrack.GitTag ||
            (Track == LocalUpdateTrack.Blocked &&
             (Blocker == LocalUpdateOutcome.DetachedHead || Blocker == LocalUpdateOutcome.UnsupportedBranch));

        public bool IsBranchTag => Track == LocalUpdateTrack.GitTag && !string.IsNullOrEmpty(Branch);
        public bool IsDetachedTag => Track == LocalUpdateTrack.GitTag && string.IsNullOrEmpty(Branch);
    }

    internal sealed class LocalUpdateResult
    {
        public LocalUpdateOutcome Outcome;
        public LocalUpdateProbe Probe;
        public string FromSha, ToSha, Version, BackupPath;
        /// <summary>What the update moves to, language neutral: "main @ 1a2b3c4" or "v2.9.0".</summary>
        public string TargetLabel;
        /// <summary>GitTag: the verified local tag ref the switch checks out.</summary>
        public string TargetRef;
        /// <summary>True when a legacy ZIP update overwrote an official clone and left HEAD on an older version.</summary>
        public bool IsLegacyArchiveClone;
        /// <summary>True when Prepare expects a 3-way stash-apply merge for overlapping tracked/staged edits.</summary>
        public bool NeedsThreeWayMerge;
        /// <summary>True when main must be aligned to an official release tag (Star History / docs commits on main).</summary>
        public bool NeedsBranchAlign;
        /// <summary>True when offering "save changes to git stash and sync" is safe for this refusal.</summary>
        public bool CanStashAndSync;
        public string HeadPackageVersion, DiskPackageVersion;
        public IReadOnlyList<string> Entries = Array.Empty<string>();
        public int EntryCount;
    }

    internal enum GitLogLevel { Verbose, Warning, Error }

    internal readonly struct GitLogLine
    {
        public readonly GitLogLevel Level;
        public readonly string Message;
        public GitLogLine(GitLogLevel level, string message) { Level = level; Message = message; }
    }

    internal enum GitSyncPhase { Probing, Fetching, Applying }

    /// <summary>
    /// Inputs of one self-update run, captured on the main thread. Log lines are collected here and written through
    /// SkillsLogger by the main thread, so the worker thread never touches a Unity API.
    /// </summary>
    internal sealed class GitSyncContext
    {
        public string GitPath;
        public Version GitVersion;
        public string PackageDir;
        public string OfficialUrl = LocalGitSync.OfficialRepoHttpsUrl;
        public string OfficialRootCommit = LocalGitSync.OfficialRootCommitSha;
        public string TargetVersion;
        public string CurrentVersion;
        /// <summary>Tests only: the .git marker walk never climbs above this directory.</summary>
        public string MarkerCeiling;
        public Func<bool> IsCancelled;

        private volatile int _phase;
        private volatile string _phaseTarget;
        private volatile int _phaseTargetIsTag;
        private readonly List<GitLogLine> _log = new List<GitLogLine>();

        /// <summary>What the worker is doing, for the progress bar the main thread draws.</summary>
        public GitSyncPhase Phase
        {
            get => (GitSyncPhase)_phase;
            set => _phase = (int)value;
        }

        /// <summary>The branch or tag the current phase works on.</summary>
        public string PhaseTarget
        {
            get => _phaseTarget;
            set => _phaseTarget = value;
        }

        /// <summary>True when <see cref="PhaseTarget"/> is a release tag rather than a branch.</summary>
        public bool PhaseTargetIsTag
        {
            get => _phaseTargetIsTag != 0;
            set => _phaseTargetIsTag = value ? 1 : 0;
        }

        public void Add(GitLogLevel level, string message)
        {
            lock (_log) _log.Add(new GitLogLine(level, message));
        }

        public List<GitLogLine> DrainLog()
        {
            lock (_log)
            {
                var lines = new List<GitLogLine>(_log);
                _log.Clear();
                return lines;
            }
        }
    }

    internal sealed class GitRemote
    {
        public string Name;
        public readonly List<string> Urls = new List<string>();
        public readonly List<string> FetchRefspecs = new List<string>();
    }

    internal sealed class ReleaseTag
    {
        public string Name;
        public Version Version;
        public string Commit;
    }

    internal enum BranchVerdict { Allowed, Unsupported, Detached, Failed }

    internal enum PrefixKind { Clone, PackageIsRepoRoot, Foreign }

    /// <summary>
    /// Track A of the local self-update: an official git clone is fast-forwarded to the official branch head, never
    /// pulled (no merge commits, no rebase, user pull config ignored), and only when the whole repository is clean.
    /// Every step is synchronous and Unity-free, so the service runs it on a worker thread (the single write, the
    /// fast-forward, on the main thread) and tests run it directly against throwaway repositories.
    /// </summary>
    internal static class LocalGitSync
    {
        internal const string OfficialRepoHttpsUrl = "https://github.com/Besty0728/Unity-Skills.git";
        internal const string OfficialRootCommitSha = "3924526a1bc4636da03b550ad3c4fb9735f03eed";
        internal const string ClonePackageFolder = "SkillsForUnity";

        /// <summary>Manual verification only (set by reflection from a throwaway project): fetch from this URL instead.</summary>
        internal static string OfficialUrlOverrideForTesting;

        internal const int MaxListedEntries = 8;

        private const int ReadTimeoutMs = 15000;
        private const int StatusTimeoutMs = 60000;
        private const int RemoteTimeoutMs = 60000;
        private const int FetchTimeoutMs = 300000;
        private const int WriteTimeoutMs = 60000;
        private const int MaxLoggedStderr = 2000;

        // git switch arrived in 2.23; older supported versions use the equivalent checkout --detach.
        private static readonly Version SwitchVersion = new Version(2, 23);

        private static readonly string[] AllowedBranches = { "main", "beta" };
        private static readonly string[] OfficialHttpHosts = { "github.com", "www.github.com", "ssh.github.com" };
        private static readonly string[] OfficialScpHosts = { "github.com", "ssh.github.com" };
        private static readonly string[] UrlSchemes = { "https", "http", "ssh", "git", "git+ssh", "ssh+git" };
        private const string OfficialRepoPath = "Besty0728/Unity-Skills";

        // Refnames may contain a single quote, which the argument guard rejects; tracking refs are only derived for
        // remote names that stay safe.
        private static readonly Regex SafeRemoteName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant);
        private static readonly Regex ReleaseTagName = new Regex(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);
        private static readonly Regex HexSha = new Regex(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant);

        // ===== Probe =====

        /// <summary>
        /// Decides the track (see the decision table in the self-update spec), top to bottom, first match wins. Read-only:
        /// no network, no writes. Git decides clone versus copy; the filesystem walk for .git only backs it up when no
        /// usable git exists.
        /// </summary>
        internal static LocalUpdateProbe Probe(GitSyncContext ctx)
        {
            ctx.Phase = GitSyncPhase.Probing;
            var probe = new LocalUpdateProbe { PackageDir = ctx.PackageDir };
            TryFindGitMarker(ctx.PackageDir, out var markerRoot, ctx.MarkerCeiling);
            probe.MarkerRoot = markerRoot;

            // Replacing a folder that is itself a repository would delete its .git with it.
            if (HasGitMarker(ctx.PackageDir))
                return Block(probe, LocalUpdateOutcome.PackageDirIsRepository);

            if (string.IsNullOrEmpty(ctx.GitPath))
            {
                if (markerRoot == null) return ArchiveTrack(probe, LocalUpdateTrack.Archive);
                // Never cover a clone with an archive: that is exactly how HEAD and the working tree drift apart.
                if (IsCloneShaped(ctx.PackageDir, markerRoot))
                    return Block(probe, ctx.GitVersion != null ? LocalUpdateOutcome.GitTooOld : LocalUpdateOutcome.GitMissingForRepository);
                return ArchiveTrack(probe, LocalUpdateTrack.ArchiveUnverified);
            }

            var rev = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--is-inside-work-tree", "--show-prefix",
                "--show-toplevel", "--absolute-git-dir", "--is-shallow-repository");
            if (!rev.Succeeded)
            {
                if (rev.Cancelled) return Block(probe, LocalUpdateOutcome.Cancelled);
                if (Contains(rev.StdErr, "dubious ownership"))
                {
                    LogFailure(ctx, "rev-parse", rev);
                    return Block(probe, LocalUpdateOutcome.GitUnsafeRepository);
                }
                if (rev.ExitCode == 128 && Contains(rev.StdErr, "not a git repository"))
                {
                    if (markerRoot == null) return ArchiveTrack(probe, LocalUpdateTrack.Archive);
                    // A .git on disk that git cannot read (stale worktree, filesystem boundary): stay conservative.
                    LogFailure(ctx, "rev-parse", rev);
                    return Block(probe, LocalUpdateOutcome.GitCommandFailed);
                }
                LogFailure(ctx, "rev-parse", rev);
                return Block(probe, LocalUpdateOutcome.GitCommandFailed);
            }

            var lines = rev.StdOut.Replace("\r", string.Empty).Split('\n');
            if (lines.Length < 5 || lines[0] != "true")
            {
                LogFailure(ctx, "rev-parse (unexpected output)", rev);
                return Block(probe, LocalUpdateOutcome.GitCommandFailed);
            }
            probe.RepoRoot = lines[2];
            probe.GitDir = lines[3];
            probe.IsShallow = lines[4] == "true";

            switch (ClassifyPrefix(lines[1]))
            {
                case PrefixKind.PackageIsRepoRoot:
                    return Block(probe, LocalUpdateOutcome.PackageDirIsRepository);
                case PrefixKind.Clone:
                    var remotes = ReadRemotes(ctx, out var remotesOk);
                    if (!remotesOk) return Block(probe, LocalUpdateOutcome.GitCommandFailed);
                    if (IsOfficialIdentity(ctx, probe, remotes))
                        return ProbeBranch(ctx, probe, remotes);
                    // A foreign repository that happens to have SkillsForUnity/ at its root: never fetch official
                    // history into it; treat it as a copy.
                    return ProbeVendored(ctx, probe);
                default:
                    return ProbeVendored(ctx, probe);
            }
        }

        private static bool IsOfficialIdentity(GitSyncContext ctx, LocalUpdateProbe probe, List<GitRemote> remotes)
        {
            if (remotes.Any(r => r.Urls.Count > 0 && IsOfficialRemoteUrl(ctx, r.Urls[0]))) return true;
            if (probe.IsShallow) return true;
            return HasCommit(ctx, ctx.OfficialRootCommit);
        }

        private static LocalUpdateProbe ProbeBranch(GitSyncContext ctx, LocalUpdateProbe probe, List<GitRemote> remotes)
        {
            var symbolic = Git(ctx, ReadTimeoutMs, false, "symbolic-ref", "-q", "--short", "HEAD");
            string branch = null;
            var verdict = symbolic.StartFailed || symbolic.TimedOut || symbolic.Cancelled
                ? BranchVerdict.Failed
                : EvaluateBranch(symbolic.ExitCode, symbolic.StdOut, out branch);

            var head = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--verify", "-q", "HEAD^{commit}");
            if (!head.Succeeded)
            {
                // An unborn branch has no commit to fast-forward from.
                LogFailure(ctx, "rev-parse HEAD", head);
                return Block(probe, LocalUpdateOutcome.GitCommandFailed);
            }
            probe.HeadSha = head.StdOut.Trim();

            switch (verdict)
            {
                case BranchVerdict.Allowed:
                    probe.Branch = branch;
                    probe.HeadLabel = branch;
                    if (SelectOfficialRemote(remotes, url => IsOfficialRemoteUrl(ctx, url), branch,
                            out var remoteName, out var trackingRef))
                    {
                        probe.OfficialRemote = remoteName;
                        probe.TrackingRef = trackingRef;
                    }
                    probe.Track = string.Equals(branch, "main", StringComparison.Ordinal)
                        ? LocalUpdateTrack.GitTag
                        : LocalUpdateTrack.GitSync;
                    return probe;
                case BranchVerdict.Unsupported:
                    probe.Branch = branch;
                    probe.HeadLabel = branch;
                    return Block(probe, LocalUpdateOutcome.UnsupportedBranch);
                case BranchVerdict.Detached:
                    LabelDetachedHead(ctx, probe);
                    return Block(probe, LocalUpdateOutcome.DetachedHead);
                default:
                    LogFailure(ctx, "symbolic-ref", symbolic);
                    return Block(probe, LocalUpdateOutcome.GitCommandFailed);
            }
        }

        private static void LabelDetachedHead(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            var tags = Git(ctx, ReadTimeoutMs, false, "tag", "--points-at", "HEAD");
            if (tags.Succeeded)
            {
                var newest = tags.StdOut.Split('\n')
                    .Select(line => line.Trim())
                    .Select(name => TryParseReleaseTag(name, out var version) ? new ReleaseTag { Name = name, Version = version } : null)
                    .Where(tag => tag != null)
                    .OrderByDescending(tag => tag.Version)
                    .FirstOrDefault();
                if (newest != null)
                {
                    probe.HeadAtReleaseTag = true;
                    probe.HeadLabel = newest.Name;
                    return;
                }
            }

            var describe = Git(ctx, ReadTimeoutMs, false, "describe", "--tags", "--exact-match", "HEAD");
            probe.HeadLabel = describe.Succeeded && describe.StdOut.Trim().Length > 0
                ? describe.StdOut.Trim()
                : ShortSha(probe.HeadSha);
        }

        private static LocalUpdateProbe ProbeVendored(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            var tracked = Git(ctx, ReadTimeoutMs, false, "ls-files", "-z", "--", ".");
            if (!tracked.Succeeded)
            {
                LogFailure(ctx, "ls-files", tracked);
                return Block(probe, tracked.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed);
            }
            // Untracked or ignored by the surrounding repository (a dotfiles repo in the home folder, say).
            if (tracked.StdOut.Split('\0').All(segment => segment.Length == 0))
                return ArchiveTrack(probe, LocalUpdateTrack.Archive);

            var verdict = CheckVendoredSubtree(ctx, out var entries, out var count);
            if (verdict != null)
            {
                probe.Entries = entries.Take(MaxListedEntries).ToList();
                probe.EntryCount = count;
                if (verdict == LocalUpdateOutcome.VendoredSubtreeModified)
                {
                    // Allow update checks (and unmodified previous self-update swaps) to proceed; Track B verifies
                    // fingerprint / asks before overwriting manual edits.
                    probe.HasVendoredChanges = true;
                    return ArchiveTrack(probe, LocalUpdateTrack.ArchiveInRepository);
                }
                return Block(probe, verdict.Value);
            }
            return ArchiveTrack(probe, LocalUpdateTrack.ArchiveInRepository);
        }

        /// <summary>
        /// A package tracked by another repository must be clean there, untracked files included, before the archive
        /// replaces it. Null when clean. The service reruns this on the main thread right before the swap.
        /// </summary>
        internal static LocalUpdateOutcome? CheckVendoredSubtree(GitSyncContext ctx, out IReadOnlyList<string> entries, out int count)
        {
            entries = Array.Empty<string>();
            count = 0;
            var status = Git(ctx, StatusTimeoutMs, false, "--no-optional-locks", "status", "--porcelain=v1", "-z",
                "--untracked-files=all", "--", ".");
            if (!status.Succeeded)
            {
                LogFailure(ctx, "status", status);
                return status.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed;
            }
            entries = ParsePorcelainZ(status.StdOut, out count);
            return count > 0 ? LocalUpdateOutcome.VendoredSubtreeModified : (LocalUpdateOutcome?)null;
        }

        private static LocalUpdateProbe ArchiveTrack(LocalUpdateProbe probe, LocalUpdateTrack track)
        {
            var guard = CheckArchiveGuards(probe.PackageDir);
            if (guard != null) return Block(probe, guard.Value);
            probe.Track = track;
            return probe;
        }

        private static LocalUpdateProbe Block(LocalUpdateProbe probe, LocalUpdateOutcome blocker)
        {
            probe.Track = LocalUpdateTrack.Blocked;
            probe.Blocker = blocker;
            return probe;
        }

        // ===== Check (settings drawer: is there anything to update to) =====

        /// <summary>Asks the official remote what the probed install would move to. Writes nothing.</summary>
        internal static LocalUpdateResult CheckRemote(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            switch (probe.Track)
            {
                case LocalUpdateTrack.GitSync:
                {
                    var remote = LsRemoteBranch(ctx, probe.Branch, out var target);
                    if (remote != null) return Result(remote.Value, probe);
                    if (target == probe.HeadSha || IsLocalAncestor(ctx, target, probe.HeadSha))
                        return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);
                    var available = Result(LocalUpdateOutcome.UpdateAvailable, probe);
                    available.FromSha = probe.HeadSha;
                    available.ToSha = target;
                    available.TargetLabel = BranchTargetLabel(probe.Branch, target);
                    return available;
                }
                case LocalUpdateTrack.GitTag when probe.IsBranchTag:
                    return CheckRemoteMainTag(ctx, probe);
                case LocalUpdateTrack.Blocked when probe.Blocker == LocalUpdateOutcome.DetachedHead && probe.HeadAtReleaseTag:
                    return CheckRemoteDetachedTag(ctx, probe);
                case LocalUpdateTrack.Blocked:
                    return Result(probe.Blocker, probe);
                default:
                    return Result(LocalUpdateOutcome.ReleaseCheckRequired, probe);
            }
        }

        private static LocalUpdateResult CheckRemoteMainTag(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            if (string.IsNullOrWhiteSpace(ctx.TargetVersion))
                return Result(LocalUpdateOutcome.ReleaseCheckRequired, probe);
            if (!TryNormalizeReleaseTag(ctx.TargetVersion, out var tagName, out var targetVer))
                return Result(LocalUpdateOutcome.RecheckRequired, probe);
            if (TryNormalizeReleaseTag(ctx.CurrentVersion, out _, out var curVer) && targetVer <= curVer)
                return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var remote = LsRemoteReleaseTag(ctx, tagName, out var tag);
            if (remote != null) return Result(remote.Value, probe);
            if (tag.Commit == probe.HeadSha || IsLocalAncestor(ctx, tag.Commit, probe.HeadSha))
                return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var available = Result(LocalUpdateOutcome.UpdateAvailable, probe);
            available.FromSha = probe.HeadSha;
            available.ToSha = tag.Commit;
            available.TargetLabel = tag.Name;
            return available;
        }

        private static LocalUpdateResult CheckRemoteDetachedTag(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            var run = Git(ctx, RemoteTimeoutMs, true, "ls-remote", "--tags", ctx.OfficialUrl);
            if (!run.Succeeded)
            {
                LogFailure(ctx, "ls-remote --tags", run);
                return Result(run.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.NetworkError, probe);
            }

            var tags = ParseReleaseTags(run.StdOut);
            var current = tags.Where(tag => tag.Commit == probe.HeadSha).OrderByDescending(tag => tag.Version).FirstOrDefault();
            if (current == null) return Result(LocalUpdateOutcome.DetachedHead, probe);

            probe.Track = LocalUpdateTrack.GitTag;
            probe.HeadLabel = current.Name;

            if (string.IsNullOrWhiteSpace(ctx.TargetVersion))
                return Result(LocalUpdateOutcome.ReleaseCheckRequired, probe);
            if (!TryNormalizeReleaseTag(ctx.TargetVersion, out var tagName, out _))
                return Result(LocalUpdateOutcome.RecheckRequired, probe);

            var targetTag = tags.FirstOrDefault(tag => string.Equals(tag.Name, tagName, StringComparison.Ordinal));
            if (targetTag == null) return Result(LocalUpdateOutcome.GitCommandFailed, probe);

            if (targetTag.Version <= current.Version ||
                (TryNormalizeReleaseTag(ctx.CurrentVersion, out _, out var curVer) && targetTag.Version <= curVer))
                return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var available = Result(LocalUpdateOutcome.UpdateAvailable, probe);
            available.FromSha = probe.HeadSha;
            available.ToSha = targetTag.Commit;
            available.TargetLabel = targetTag.Name;
            return available;
        }

        // ===== Prepare (worker thread; reads, network, fetch -- never touches the working tree) =====

        /// <summary>
        /// Everything before the single write: whole-repository clean check, the official head or release tag by ls-remote,
        /// fetch, and the ancestry test. UpdateAvailable here means "ready": <see cref="Apply"/> moves to ToSha.
        /// </summary>
        internal static LocalUpdateResult Prepare(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            if (probe.Track == LocalUpdateTrack.GitSync) return PrepareBranch(ctx, probe);
            if (probe.IsBranchTag) return PrepareMainTag(ctx, probe);
            if (probe.Track == LocalUpdateTrack.Blocked && probe.Blocker == LocalUpdateOutcome.DetachedHead && probe.HeadAtReleaseTag)
                return PrepareDetachedTag(ctx, probe);
            return Result(probe.Track == LocalUpdateTrack.Blocked ? probe.Blocker : LocalUpdateOutcome.ReleaseCheckRequired, probe);
        }

        private static LocalUpdateResult PrepareBranch(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            if (HasUnmergedEntries(ctx, out var conflictOutcome))
                return Result(conflictOutcome, probe);

            var remote = LsRemoteBranch(ctx, probe.Branch, out var target);
            if (remote != null) return Result(remote.Value, probe);
            if (target == probe.HeadSha) return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var refspec = probe.TrackingRef != null
                ? $"+refs/heads/{probe.Branch}:{probe.TrackingRef}"
                : $"refs/heads/{probe.Branch}";
            ctx.PhaseTarget = probe.Branch;
            ctx.PhaseTargetIsTag = false;
            var fetched = Fetch(ctx, refspec);
            if (fetched != null) return Result(fetched.Value, probe);

            if (!HasCommit(ctx, target))
            {
                ctx.Add(GitLogLevel.Error, $"Self-update: the fetched official {probe.Branch} head {target} is missing from {probe.RepoRoot}.");
                return Result(LocalUpdateOutcome.GitCommandFailed, probe);
            }

            var forward = MergeBase(ctx, probe.HeadSha, target);
            if (forward == 1)
            {
                var behind = MergeBase(ctx, target, probe.HeadSha);
                if (behind == 0) return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);
                return Result(behind == 1 ? LocalUpdateOutcome.Diverged : LocalUpdateOutcome.GitCommandFailed, probe);
            }
            if (forward != 0) return Result(LocalUpdateOutcome.GitCommandFailed, probe);

            var targetLabel = BranchTargetLabel(probe.Branch, target);
            var overlap = CheckOverlappingChanges(ctx, probe, probe.HeadSha, target, targetLabel, null, out var needsThreeWay, out _);
            if (overlap != null) return overlap;

            var ready = Result(LocalUpdateOutcome.UpdateAvailable, probe);
            ready.FromSha = probe.HeadSha;
            ready.ToSha = target;
            ready.TargetLabel = targetLabel;
            ready.NeedsThreeWayMerge = needsThreeWay;
            return ready;
        }

        private static LocalUpdateResult PrepareMainTag(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            if (HasUnmergedEntries(ctx, out var conflictOutcome))
                return Result(conflictOutcome, probe);

            if (!TryNormalizeReleaseTag(ctx.TargetVersion, out var tagName, out var targetVer))
                return Result(LocalUpdateOutcome.RecheckRequired, probe);

            if (TryNormalizeReleaseTag(ctx.CurrentVersion, out _, out var curVer) && targetVer <= curVer)
                return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var remote = LsRemoteReleaseTag(ctx, tagName, out var targetTag);
            if (remote != null) return Result(remote.Value, probe);
            if (targetTag.Commit == probe.HeadSha) return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            var tagRef = "refs/tags/" + targetTag.Name;
            ctx.PhaseTarget = targetTag.Name;
            ctx.PhaseTargetIsTag = true;
            var fetched = Fetch(ctx, tagRef + ":" + tagRef);
            if (fetched == LocalUpdateOutcome.Cancelled) return Result(LocalUpdateOutcome.Cancelled, probe);

            var local = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--verify", "-q", tagRef + "^{commit}");
            if (!local.Succeeded)
                return Result(fetched ?? LocalUpdateOutcome.GitCommandFailed, probe);
            if (local.StdOut.Trim() != targetTag.Commit)
            {
                ctx.Add(GitLogLevel.Error,
                    $"Self-update: the local tag {targetTag.Name} in {probe.RepoRoot} points at {ShortSha(local.StdOut.Trim())}, not at the official release {ShortSha(targetTag.Commit)}; it was left untouched.");
                return Result(LocalUpdateOutcome.GitCommandFailed, probe);
            }

            var transition = EvaluateMainBranchTransition(ctx, probe, probe.HeadSha, targetTag.Commit, out var needsBranchAlign);
            if (transition != null) return transition;

            var overlap = CheckOverlappingChanges(ctx, probe, probe.HeadSha, targetTag.Commit, targetTag.Name, tagRef, out var needsThreeWay, out _);
            if (overlap != null)
            {
                overlap.NeedsBranchAlign = needsBranchAlign;
                return overlap;
            }

            var ready = Result(LocalUpdateOutcome.UpdateAvailable, probe);
            ready.FromSha = probe.HeadSha;
            ready.ToSha = targetTag.Commit;
            ready.TargetLabel = targetTag.Name;
            ready.TargetRef = tagRef;
            ready.NeedsThreeWayMerge = needsThreeWay;
            ready.NeedsBranchAlign = needsBranchAlign;
            return ready;
        }

        private static LocalUpdateResult PrepareDetachedTag(GitSyncContext ctx, LocalUpdateProbe probe)
        {
            if (HasUnmergedEntries(ctx, out var conflictOutcome))
                return Result(conflictOutcome, probe);

            if (!TryNormalizeReleaseTag(ctx.TargetVersion, out var tagName, out _))
                return Result(LocalUpdateOutcome.RecheckRequired, probe);

            var run = Git(ctx, RemoteTimeoutMs, true, "ls-remote", "--tags", ctx.OfficialUrl);
            if (!run.Succeeded)
            {
                LogFailure(ctx, "ls-remote --tags", run);
                return Result(run.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.NetworkError, probe);
            }

            var tags = ParseReleaseTags(run.StdOut);
            var current = tags.Where(tag => tag.Commit == probe.HeadSha).OrderByDescending(tag => tag.Version).FirstOrDefault();
            if (current == null) return Result(LocalUpdateOutcome.DetachedHead, probe);

            probe.Track = LocalUpdateTrack.GitTag;
            probe.HeadLabel = current.Name;

            var targetTag = tags.FirstOrDefault(tag => string.Equals(tag.Name, tagName, StringComparison.Ordinal));
            if (targetTag == null) return Result(LocalUpdateOutcome.GitCommandFailed, probe);

            if (targetTag.Version <= current.Version ||
                (TryNormalizeReleaseTag(ctx.CurrentVersion, out _, out var curVer) && targetTag.Version <= curVer))
                return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);

            // Non-forcing refspec: a local tag of that name pointing elsewhere is never overwritten; fetch refuses.
            var tagRef = "refs/tags/" + targetTag.Name;
            ctx.PhaseTarget = targetTag.Name;
            ctx.PhaseTargetIsTag = true;
            var fetched = Fetch(ctx, tagRef + ":" + tagRef);
            if (fetched == LocalUpdateOutcome.Cancelled) return Result(LocalUpdateOutcome.Cancelled, probe);

            // A tag already present locally at the official commit works even when the fetch itself failed.
            var local = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--verify", "-q", tagRef + "^{commit}");
            if (local.Succeeded && local.StdOut.Trim() == targetTag.Commit)
            {
                var overlap = CheckOverlappingChanges(ctx, probe, probe.HeadSha, targetTag.Commit, targetTag.Name, tagRef, out var needsThreeWay, out _);
                if (overlap != null) return overlap;

                var ready = Result(LocalUpdateOutcome.UpdateAvailable, probe);
                ready.FromSha = probe.HeadSha;
                ready.ToSha = targetTag.Commit;
                ready.TargetLabel = targetTag.Name;
                ready.TargetRef = tagRef;
                ready.NeedsThreeWayMerge = needsThreeWay;
                return ready;
            }
            if (local.Succeeded)
            {
                ctx.Add(GitLogLevel.Error,
                    $"Self-update: the local tag {targetTag.Name} in {probe.RepoRoot} points at {ShortSha(local.StdOut.Trim())}, not at the official release {ShortSha(targetTag.Commit)}; it was left untouched.");
                return Result(LocalUpdateOutcome.GitCommandFailed, probe);
            }
            return Result(fetched ?? LocalUpdateOutcome.GitCommandFailed, probe);
        }

        private static LocalUpdateResult EvaluateMainBranchTransition(GitSyncContext ctx, LocalUpdateProbe probe,
            string headSha, string targetCommit, out bool needsBranchAlign)
        {
            needsBranchAlign = false;
            var forward = MergeBase(ctx, headSha, targetCommit);
            if (forward == 0)
            {
                needsBranchAlign = false;
                return null;
            }
            if (forward != 1) return Result(LocalUpdateOutcome.GitCommandFailed, probe);

            var behind = MergeBase(ctx, targetCommit, headSha);
            if (behind == 0) return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);
            if (behind != 1) return Result(LocalUpdateOutcome.GitCommandFailed, probe);

            if (!string.IsNullOrEmpty(probe.TrackingRef) && HasCommit(ctx, probe.TrackingRef) &&
                MergeBase(ctx, headSha, probe.TrackingRef) == 0)
            {
                needsBranchAlign = true;
                return null;
            }

            if (IsStarHistoryOnlyDivergence(ctx, targetCommit, headSha))
            {
                needsBranchAlign = true;
                return null;
            }

            return Result(LocalUpdateOutcome.Diverged, probe);
        }

        /// <summary>Parses `git cherry` output ("+ sha" / "- sha"), returning unmatched "+" commit SHAs and total valid lines.</summary>
        internal static List<string> ParseCherryAddedCommits(string output, out int totalCommits)
        {
            var added = new List<string>();
            totalCommits = 0;
            foreach (var raw in (output ?? string.Empty).Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length < 3 || line[1] != ' ' || (line[0] != '+' && line[0] != '-')) continue;
                var sha = line.Substring(2).Trim();
                if (!HexSha.IsMatch(sha)) continue;
                totalCommits++;
                if (line[0] == '+') added.Add(sha);
            }
            return added;
        }

        private static bool IsStarHistoryCommit(GitSyncContext ctx, string sha)
        {
            var showRun = Git(ctx, ReadTimeoutMs, false, "show", "-s", "--format=%an%x09%s", sha);
            if (!showRun.Succeeded) return false;

            var line = (showRun.StdOut ?? string.Empty).TrimEnd('\r', '\n');
            var tab = line.IndexOf('\t');
            if (tab <= 0) return false;

            var author = line.Substring(0, tab);
            var subject = line.Substring(tab + 1);
            if (!IsStarHistoryCommitRecord(author, subject)) return false;

            var diffRun = Git(ctx, ReadTimeoutMs, false, "diff-tree", "--no-commit-id", "--name-only", "-r", "-z", sha);
            if (!diffRun.Succeeded) return false;

            var paths = diffRun.StdOut.Split('\0').Where(p => p.Length > 0).ToList();
            return paths.Count > 0 && paths.All(IsStarHistoryPath);
        }

        private static bool IsStarHistoryOnlyDivergence(GitSyncContext ctx, string targetCommit, string headSha)
        {
            var cherryRun = Git(ctx, ReadTimeoutMs, false, "cherry", targetCommit, headSha);
            if (cherryRun.Succeeded)
            {
                var added = ParseCherryAddedCommits(cherryRun.StdOut, out var totalCommits);
                if (totalCommits > 0)
                    return added.All(sha => IsStarHistoryCommit(ctx, sha));
            }

            var logRun = Git(ctx, ReadTimeoutMs, false, "log", "--format=%H%x09%an%x09%s", "-z", $"{targetCommit}..{headSha}");
            if (!logRun.Succeeded) return false;

            var records = logRun.StdOut.Split('\0').Where(r => r.Length > 0).ToList();
            if (records.Count == 0) return false;

            foreach (var record in records)
            {
                var firstTab = record.IndexOf('\t');
                var secondTab = firstTab >= 0 ? record.IndexOf('\t', firstTab + 1) : -1;
                if (firstTab <= 0 || secondTab <= firstTab) return false;

                var sha = record.Substring(0, firstTab).Trim();
                var author = record.Substring(firstTab + 1, secondTab - firstTab - 1);
                var subject = record.Substring(secondTab + 1);
                if (!HexSha.IsMatch(sha) || !IsStarHistoryCommitRecord(author, subject))
                    return false;

                var diffRun = Git(ctx, ReadTimeoutMs, false, "diff-tree", "--no-commit-id", "--name-only", "-r", "-z", sha);
                if (!diffRun.Succeeded) return false;

                var paths = diffRun.StdOut.Split('\0').Where(p => p.Length > 0).ToList();
                if (paths.Count == 0 || !paths.All(IsStarHistoryPath))
                    return false;
            }
            return true;
        }

        // ===== Apply (main thread, inside LockReloadAssemblies) =====

        /// <summary>
        /// The only step that writes the working tree. Re-verifies branch, HEAD and cleanliness first (the user may have
        /// used git in between); if anything moved it re-classifies rather than continuing. Then one fast-forward (or,
        /// for GitTag, one detached switch), neither of which overwrites ignored files.
        /// </summary>
        internal static LocalUpdateResult Apply(GitSyncContext ctx, LocalUpdateResult prepared)
        {
            ctx.Phase = GitSyncPhase.Applying;
            var probe = prepared.Probe;
            var target = prepared.ToSha;

            var symbolic = Git(ctx, ReadTimeoutMs, false, "symbolic-ref", "-q", "--short", "HEAD");
            var head = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--verify", "-q", "HEAD^{commit}");
            if (!head.Succeeded)
            {
                LogFailure(ctx, "rev-parse HEAD", head);
                return Result(LocalUpdateOutcome.GitCommandFailed, probe);
            }
            var headNow = head.StdOut.Trim();
            string branchNow = null;
            var verdict = symbolic.StartFailed || symbolic.TimedOut
                ? BranchVerdict.Failed
                : EvaluateBranch(symbolic.ExitCode, symbolic.StdOut, out branchNow);

            var needsBranchAlign = false;
            if (probe.IsDetachedTag)
            {
                if (verdict != BranchVerdict.Detached || headNow != probe.HeadSha)
                    return Result(LocalUpdateOutcome.RecheckRequired, probe);
            }
            else if (probe.IsBranchTag)
            {
                if (verdict == BranchVerdict.Detached) return Result(LocalUpdateOutcome.DetachedHead, probe);
                if (verdict == BranchVerdict.Failed) return Result(LocalUpdateOutcome.GitCommandFailed, probe);
                if (!string.Equals(branchNow, probe.Branch, StringComparison.Ordinal))
                    return Result(verdict == BranchVerdict.Unsupported ? LocalUpdateOutcome.UnsupportedBranch : LocalUpdateOutcome.RecheckRequired, probe);

                var transition = EvaluateMainBranchTransition(ctx, probe, headNow, target, out needsBranchAlign);
                if (transition != null) return transition;
            }
            else
            {
                if (verdict == BranchVerdict.Detached) return Result(LocalUpdateOutcome.DetachedHead, probe);
                if (verdict == BranchVerdict.Failed) return Result(LocalUpdateOutcome.GitCommandFailed, probe);
                // Another branch now: the fetched head belongs to the old one, so it must never be merged here.
                if (!string.Equals(branchNow, probe.Branch, StringComparison.Ordinal))
                    return Result(verdict == BranchVerdict.Unsupported ? LocalUpdateOutcome.UnsupportedBranch : LocalUpdateOutcome.RecheckRequired, probe);
                // Same branch, moved since Prepare (a commit, a pull): still fine if it is a fast-forward to target.
                if (headNow != probe.HeadSha)
                {
                    var forward = MergeBase(ctx, headNow, target);
                    if (forward == 1)
                    {
                        var behind = MergeBase(ctx, target, headNow);
                        if (behind == 0) return Result(LocalUpdateOutcome.AlreadyUpToDate, probe);
                        return Result(behind == 1 ? LocalUpdateOutcome.Diverged : LocalUpdateOutcome.GitCommandFailed, probe);
                    }
                    if (forward != 0) return Result(LocalUpdateOutcome.GitCommandFailed, probe);
                }
            }

            if (HasUnmergedEntries(ctx, out var conflictOutcome))
                return Result(conflictOutcome, probe);

            var overlap = CheckOverlappingChanges(ctx, probe, headNow, target, prepared.TargetLabel, prepared.TargetRef, out var needsThreeWay, out var overlappingEntries);
            if (overlap != null)
            {
                overlap.NeedsBranchAlign = needsBranchAlign;
                return overlap;
            }

            if (needsThreeWay)
                return ApplyWithThreeWayMerge(ctx, probe, headNow, target, prepared.TargetLabel, prepared.TargetRef, needsBranchAlign, overlappingEntries);

            return RunDirectWrite(ctx, probe, headNow, target, prepared.TargetLabel, prepared.TargetRef, needsBranchAlign);
        }

        private static LocalUpdateResult RunDirectWrite(GitSyncContext ctx, LocalUpdateProbe probe,
            string headNow, string target, string targetLabel, string targetRef, bool needsBranchAlign)
        {
            var preStatus = Git(ctx, StatusTimeoutMs, false, "--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal");
            if (!preStatus.Succeeded)
            {
                LogFailure(ctx, "status", preStatus);
                return Result(preStatus.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed, probe);
            }

            var write = ExecuteWriteCommand(ctx, probe, target, targetRef, needsBranchAlign);
            var outcome = ClassifyWrite(ctx, probe, write, target, preStatus.StdOut, out var entries, out var count);
            var result = Result(outcome, probe);
            result.FromSha = headNow;
            result.ToSha = target;
            result.TargetLabel = targetLabel;
            result.TargetRef = targetRef;
            result.NeedsBranchAlign = needsBranchAlign;
            result.Entries = entries;
            result.EntryCount = count;
            if (outcome == LocalUpdateOutcome.WouldOverwriteLocalFiles &&
                TryDetectLegacyOverwrittenClone(ctx, probe.PackageDir, out var headVer, out var diskVer))
            {
                result.IsLegacyArchiveClone = true;
                result.CanStashAndSync = true;
                result.HeadPackageVersion = headVer;
                result.DiskPackageVersion = diskVer;
            }
            return result;
        }

        private static ProcessRunResult ExecuteWriteCommand(GitSyncContext ctx, LocalUpdateProbe probe,
            string target, string targetRef, bool needsBranchAlign)
        {
            if (probe.IsDetachedTag)
            {
                return ctx.GitVersion != null && ctx.GitVersion < SwitchVersion
                    ? Git(ctx, WriteTimeoutMs, true, "checkout", "--detach", "--no-overwrite-ignore", "--quiet", targetRef)
                    : Git(ctx, WriteTimeoutMs, true, "switch", "--detach", "--no-overwrite-ignore", "--quiet", targetRef);
            }
            if (probe.IsBranchTag && needsBranchAlign)
            {
                return ctx.GitVersion != null && ctx.GitVersion < SwitchVersion
                    ? Git(ctx, WriteTimeoutMs, true, "checkout", "-B", probe.Branch, "--no-overwrite-ignore", "--quiet", target)
                    : Git(ctx, WriteTimeoutMs, true, "switch", "-C", probe.Branch, "--no-overwrite-ignore", "--quiet", target);
            }
            return Git(ctx, WriteTimeoutMs, true, "merge", "--ff-only", "--no-overwrite-ignore", "--quiet", target);
        }

        /// <summary>
        /// Attempts a clean 3-way merge of tracked/staged local edits across a fast-forward or tag switch.
        /// If any line conflicts, rolls HEAD, index and working tree back byte-for-byte before returning WouldOverwriteLocalFiles.
        /// </summary>
        private static LocalUpdateResult ApplyWithThreeWayMerge(GitSyncContext ctx, LocalUpdateProbe probe,
            string headNow, string target, string targetLabel, string targetRef, bool needsBranchAlign, IReadOnlyList<string> overlappingEntries)
        {
            var stashPush = Git(ctx, WriteTimeoutMs, true,
                "-c", "user.name=UnitySkills",
                "-c", "user.email=unity-skills@local",
                "stash", "push", "-m", "unity-skills-selfupdate-merge");
            if (!stashPush.Succeeded)
            {
                if (stashPush.Cancelled) return Result(LocalUpdateOutcome.Cancelled, probe);
                LogFailure(ctx, "stash push (3-way)", stashPush);
                return Result(Contains(stashPush.StdErr, "index.lock")
                    ? LocalUpdateOutcome.GitRepositoryLocked
                    : LocalUpdateOutcome.GitCommandFailed, probe);
            }

            if (Contains(stashPush.StdOut, "No local changes to save"))
                return RunDirectWrite(ctx, probe, headNow, target, targetLabel, targetRef, needsBranchAlign);

            var write = ExecuteWriteCommand(ctx, probe, target, targetRef, needsBranchAlign);
            if (!write.Succeeded && !HeadIs(ctx, target))
            {
                RollbackToHeadWithPop(ctx, headNow);
                var outcome = ClassifyWrite(ctx, probe, write, target, string.Empty, out var entries, out var count);
                var failed = Result(outcome, probe);
                failed.FromSha = headNow;
                failed.ToSha = target;
                failed.TargetLabel = targetLabel;
                failed.TargetRef = targetRef;
                failed.NeedsBranchAlign = needsBranchAlign;
                failed.Entries = entries;
                failed.EntryCount = count;
                return failed;
            }

            var apply = Git(ctx, WriteTimeoutMs, true, "stash", "apply", "--index", "--quiet");
            if (apply.Succeeded)
            {
                Git(ctx, WriteTimeoutMs, true, "stash", "drop", "--quiet");
                var updated = Result(LocalUpdateOutcome.Updated, probe);
                updated.FromSha = headNow;
                updated.ToSha = target;
                updated.TargetLabel = targetLabel;
                updated.TargetRef = targetRef;
                updated.NeedsBranchAlign = needsBranchAlign;
                return updated;
            }

            var conflicts = ReadConflictingPaths(ctx);
            if (conflicts.Count == 0)
                conflicts = ParseOverwrittenPaths(apply.StdErr);
            if (conflicts.Count == 0 && overlappingEntries != null)
                conflicts = overlappingEntries.ToList();
            RollbackToHeadWithPop(ctx, headNow);

            var conflictResult = Result(LocalUpdateOutcome.WouldOverwriteLocalFiles, probe);
            conflictResult.FromSha = headNow;
            conflictResult.ToSha = target;
            conflictResult.TargetLabel = targetLabel;
            conflictResult.TargetRef = targetRef;
            conflictResult.NeedsBranchAlign = needsBranchAlign;
            conflictResult.CanStashAndSync = true;
            conflictResult.Entries = conflicts.Take(MaxListedEntries).ToList();
            conflictResult.EntryCount = conflicts.Count;
            return conflictResult;
        }

        private static List<string> ReadConflictingPaths(GitSyncContext ctx)
        {
            var run = Git(ctx, ReadTimeoutMs, false, "diff", "--name-only", "-z", "--diff-filter=U");
            if (!run.Succeeded || string.IsNullOrEmpty(run.StdOut))
                return new List<string>();
            return run.StdOut.Split('\0').Where(p => p.Length > 0).ToList();
        }

        private static void RollbackToHeadWithPop(GitSyncContext ctx, string headNow)
        {
            var reset = Git(ctx, WriteTimeoutMs, true, "reset", "--hard", "--quiet", headNow);
            if (!reset.Succeeded)
                LogFailure(ctx, "reset --hard (rollback)", reset);

            var popIndex = Git(ctx, WriteTimeoutMs, true, "stash", "pop", "--index", "--quiet");
            if (!popIndex.Succeeded)
            {
                var popPlain = Git(ctx, WriteTimeoutMs, true, "stash", "pop", "--quiet");
                if (!popPlain.Succeeded)
                    LogFailure(ctx, "stash pop (rollback)", popPlain);
            }
        }

        /// <summary>
        /// Saves the entire dirty tree (tracked, staged and untracked) to git stash before running <see cref="Apply"/>.
        /// Only called after explicit user confirmation for a legacy archive-overwritten clone or conflict fallback; never pops afterwards.
        /// </summary>
        internal static LocalUpdateResult StashAndApply(GitSyncContext ctx, LocalUpdateResult prepared)
        {
            var probe = prepared.Probe;
            var stash = Git(ctx, WriteTimeoutMs, true,
                "-c", "user.name=UnitySkills",
                "-c", "user.email=unity-skills@local",
                "stash", "push", "-u", "-m", "unity-skills-selfupdate-backup");
            if (!stash.Succeeded)
            {
                if (stash.Cancelled) return Result(LocalUpdateOutcome.Cancelled, probe);
                LogFailure(ctx, "stash push", stash);
                return Result(Contains(stash.StdErr, "index.lock")
                    ? LocalUpdateOutcome.GitRepositoryLocked
                    : LocalUpdateOutcome.GitCommandFailed, probe);
            }

            ctx.Add(GitLogLevel.Warning,
                "Self-update: saved the previous working tree to git stash (unity-skills-selfupdate-backup) before syncing.");
            return Apply(ctx, prepared);
        }

        private static LocalUpdateOutcome ClassifyWrite(GitSyncContext ctx, LocalUpdateProbe probe, ProcessRunResult write,
            string target, string preWriteStatus, out IReadOnlyList<string> entries, out int count)
        {
            entries = Array.Empty<string>();
            count = 0;
            // Exit 0 is trusted: HEAD was verified against the prepared state just before, so the move went to target.
            if (write.Succeeded) return LocalUpdateOutcome.Updated;

            if (Contains(write.StdErr, "would be overwritten by"))
            {
                var paths = ParseOverwrittenPaths(write.StdErr);
                count = paths.Count;
                entries = paths.Take(MaxListedEntries).ToList();
                return LocalUpdateOutcome.WouldOverwriteLocalFiles;
            }
            if (Contains(write.StdErr, "index.lock"))
            {
                LogFailure(ctx, "fast-forward", write);
                return LocalUpdateOutcome.GitRepositoryLocked;
            }

            if (HeadIs(ctx, target))
            {
                // Noise after a successful move (a post-merge / post-checkout hook failing, say).
                ctx.Add(GitLogLevel.Warning, $"Self-update: git reported {DescribeRun(write)} but HEAD reached {ShortSha(target)}: {Clip(write.StdErr)}");
                return LocalUpdateOutcome.Updated;
            }

            LogFailure(ctx, "fast-forward", write);
            var status = Git(ctx, StatusTimeoutMs, false, "--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal");
            if (status.Succeeded && string.Equals(status.StdOut, preWriteStatus, StringComparison.Ordinal))
                return LocalUpdateOutcome.GitCommandFailed;
            ctx.Add(GitLogLevel.Error,
                $"Self-update: the working tree of {probe.RepoRoot} may hold part of the update. If git still has an index.lock there after the editor is idle, remove it; then run git status.");
            return LocalUpdateOutcome.MergeInterrupted;
        }

        // ===== Shared steps =====

        private static bool HasUnmergedEntries(GitSyncContext ctx, out LocalUpdateOutcome outcome)
        {
            outcome = LocalUpdateOutcome.GitCommandFailed;
            var unmerged = Git(ctx, ReadTimeoutMs, false, "diff", "--name-only", "-z", "--diff-filter=U");
            if (!unmerged.Succeeded)
            {
                LogFailure(ctx, "diff --diff-filter=U", unmerged);
                outcome = unmerged.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed;
                return true;
            }
            if (!string.IsNullOrEmpty(unmerged.StdOut))
            {
                outcome = LocalUpdateOutcome.MergeInterrupted;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Allows non-conflicting local changes (modified, staged or untracked files untouched by <paramref name="headSha"/>..<paramref name="targetSha"/>)
        /// to stay in place across the fast-forward or tag switch. Only files overlapping with the update block and list themselves.
        /// </summary>
        private static LocalUpdateResult CheckOverlappingChanges(GitSyncContext ctx, LocalUpdateProbe probe,
            string headSha, string targetSha, string targetLabel, string targetRef, out bool needsThreeWayMerge, out List<string> overlappingEntries)
        {
            needsThreeWayMerge = false;
            overlappingEntries = new List<string>();
            var status = Git(ctx, StatusTimeoutMs, false, "--no-optional-locks", "status", "--porcelain=v1", "-z", "--untracked-files=normal");
            if (!status.Succeeded)
            {
                LogFailure(ctx, "status", status);
                return Result(status.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed, probe);
            }
            var dirtyEntries = ParseStatusEntriesZ(status.StdOut);
            if (dirtyEntries.Count == 0) return null;

            var diff = Git(ctx, ReadTimeoutMs, false, "diff", "--name-only", "-z", headSha, targetSha);
            if (!diff.Succeeded)
            {
                LogFailure(ctx, "diff --name-only", diff);
                return Result(diff.Cancelled ? LocalUpdateOutcome.Cancelled : LocalUpdateOutcome.GitCommandFailed, probe);
            }
            var upstreamPaths = diff.StdOut.Split('\0')
                .Where(p => p.Length > 0)
                .Select(NormalizeGitPath)
                .ToList();
            if (upstreamPaths.Count == 0) return null;

            var overlapping = new List<string>();
            bool hasUntrackedOverlap = false;
            foreach (var entry in dirtyEntries)
            {
                if (upstreamPaths.Any(u => PathsOverlap(entry.Path1, u) ||
                                           (!string.IsNullOrEmpty(entry.Path2) && PathsOverlap(entry.Path2, u))))
                {
                    overlapping.Add(entry.Raw);
                    if (entry.Raw.StartsWith("??", StringComparison.Ordinal))
                        hasUntrackedOverlap = true;
                }
            }
            if (overlapping.Count == 0) return null;

            if (TryDetectLegacyOverwrittenClone(ctx, probe.PackageDir, out var headVer, out var diskVer))
            {
                var legacyBlocked = Result(LocalUpdateOutcome.WouldOverwriteLocalFiles, probe);
                legacyBlocked.FromSha = headSha;
                legacyBlocked.ToSha = targetSha;
                legacyBlocked.TargetLabel = targetLabel;
                legacyBlocked.TargetRef = targetRef;
                legacyBlocked.Entries = overlapping.Take(MaxListedEntries).ToList();
                legacyBlocked.EntryCount = overlapping.Count;
                legacyBlocked.IsLegacyArchiveClone = true;
                legacyBlocked.CanStashAndSync = true;
                legacyBlocked.HeadPackageVersion = headVer;
                legacyBlocked.DiskPackageVersion = diskVer;
                return legacyBlocked;
            }

            if (hasUntrackedOverlap)
            {
                var untrackedBlocked = Result(LocalUpdateOutcome.WouldOverwriteLocalFiles, probe);
                untrackedBlocked.FromSha = headSha;
                untrackedBlocked.ToSha = targetSha;
                untrackedBlocked.TargetLabel = targetLabel;
                untrackedBlocked.TargetRef = targetRef;
                untrackedBlocked.Entries = overlapping.Take(MaxListedEntries).ToList();
                untrackedBlocked.EntryCount = overlapping.Count;
                untrackedBlocked.CanStashAndSync = true;
                return untrackedBlocked;
            }

            // Tracked/staged edits overlapping with upstream files: Apply attempts an atomic 3-way merge
            // and rolls back cleanly if any line conflicts.
            needsThreeWayMerge = true;
            overlappingEntries = overlapping;
            return null;
        }

        private static bool TryDetectLegacyOverwrittenClone(GitSyncContext ctx, string packageDir, out string headVersion, out string diskVersion)
        {
            headVersion = null;
            diskVersion = null;
            var headPkg = Git(ctx, ReadTimeoutMs, false, "show", $"HEAD:{ClonePackageFolder}/package.json");
            if (!headPkg.Succeeded) return false;
            try
            {
                headVersion = JObject.Parse(headPkg.StdOut).Value<string>("version")?.Trim();
                var diskPath = Path.Combine(packageDir, "package.json");
                if (!File.Exists(diskPath)) return false;
                diskVersion = JObject.Parse(File.ReadAllText(diskPath)).Value<string>("version")?.Trim();
            }
            catch
            {
                return false;
            }

            if (!Version.TryParse(headVersion, out var headSemver) || !Version.TryParse(diskVersion, out var diskSemver))
                return false;
            return headSemver < diskSemver;
        }

        private readonly struct StatusEntry
        {
            public readonly string Raw;
            public readonly string Path1;
            public readonly string Path2;
            public StatusEntry(string raw, string path1, string path2) { Raw = raw; Path1 = path1; Path2 = path2; }
        }

        private static List<StatusEntry> ParseStatusEntriesZ(string output)
        {
            var entries = new List<StatusEntry>();
            if (string.IsNullOrEmpty(output)) return entries;
            var fields = output.Split('\0');
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field.Length < 4) continue;
                var path1 = NormalizeGitPath(field.Substring(3));
                string path2 = null;
                if (field[0] == 'R' || field[0] == 'C' || field[1] == 'R' || field[1] == 'C')
                {
                    if (i + 1 < fields.Length) path2 = NormalizeGitPath(fields[++i]);
                }
                entries.Add(new StatusEntry(field, path1, path2));
            }
            return entries;
        }

        private static string NormalizeGitPath(string path) =>
            (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        internal static bool PathsOverlap(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var na = NormalizeGitPath(a);
            var nb = NormalizeGitPath(b);
            return PathEquals(na, nb) || IsFolderPrefix(na, nb) || IsFolderPrefix(nb, na);
        }

        private static bool IsFolderPrefix(string folder, string path) =>
            path.Length > folder.Length &&
            path[folder.Length] == '/' &&
            PathEquals(path.Substring(0, folder.Length), folder);

        /// <summary>Null on success with the official head in <paramref name="sha"/>; otherwise the failure outcome.</summary>
        private static LocalUpdateOutcome? LsRemoteBranch(GitSyncContext ctx, string branch, out string sha)
        {
            sha = null;
            var refName = "refs/heads/" + branch;
            var run = Git(ctx, RemoteTimeoutMs, true, "ls-remote", "--exit-code", ctx.OfficialUrl, refName);
            if (run.Cancelled) return LocalUpdateOutcome.Cancelled;
            if (!run.Succeeded)
            {
                LogFailure(ctx, "ls-remote", run);
                return run.ExitCode == 2 ? LocalUpdateOutcome.GitCommandFailed : LocalUpdateOutcome.NetworkError;
            }
            if (TryParseLsRemote(run.StdOut, refName, out sha)) return null;
            LogFailure(ctx, "ls-remote (unexpected output)", run);
            return LocalUpdateOutcome.GitCommandFailed;
        }

        private static LocalUpdateOutcome? LsRemoteReleaseTag(GitSyncContext ctx, string tagName, out ReleaseTag tag)
        {
            tag = null;
            var refName = "refs/tags/" + tagName;
            var run = Git(ctx, RemoteTimeoutMs, true, "ls-remote", "--exit-code", "--tags", ctx.OfficialUrl, refName, refName + "^{}");
            if (run.Cancelled) return LocalUpdateOutcome.Cancelled;
            if (!run.Succeeded)
            {
                LogFailure(ctx, "ls-remote --tags", run);
                return run.ExitCode == 2 ? LocalUpdateOutcome.GitCommandFailed : LocalUpdateOutcome.NetworkError;
            }
            tag = ParseReleaseTags(run.StdOut).FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.Ordinal));
            if (tag != null && !string.IsNullOrEmpty(tag.Commit)) return null;
            LogFailure(ctx, "ls-remote --tags (unexpected output)", run);
            return LocalUpdateOutcome.GitCommandFailed;
        }

        private static LocalUpdateOutcome? Fetch(GitSyncContext ctx, string refspec)
        {
            ctx.Phase = GitSyncPhase.Fetching;
            // No --quiet: it would also swallow the "[rejected]" line that explains a refused ref update.
            var run = Git(ctx, FetchTimeoutMs, true, "fetch", "--no-tags", "--no-recurse-submodules", ctx.OfficialUrl, refspec);
            if (run.Succeeded) return null;
            if (run.Cancelled) return LocalUpdateOutcome.Cancelled;
            LogFailure(ctx, "fetch", run);
            return Contains(run.StdErr, ".lock") ? LocalUpdateOutcome.GitRepositoryLocked : LocalUpdateOutcome.NetworkError;
        }

        /// <summary>0: ancestor; 1: not an ancestor; anything else: the command failed.</summary>
        private static int MergeBase(GitSyncContext ctx, string ancestor, string descendant)
        {
            var run = Git(ctx, ReadTimeoutMs, false, "merge-base", "--is-ancestor", ancestor, descendant);
            if (run.StartFailed || run.TimedOut || run.Cancelled) return -1;
            if (run.ExitCode != 0 && run.ExitCode != 1) LogFailure(ctx, "merge-base", run);
            return run.ExitCode;
        }

        private static bool IsLocalAncestor(GitSyncContext ctx, string ancestor, string descendant) =>
            HasCommit(ctx, ancestor) && MergeBase(ctx, ancestor, descendant) == 0;

        private static bool HasCommit(GitSyncContext ctx, string sha) =>
            !string.IsNullOrEmpty(sha) && Git(ctx, ReadTimeoutMs, false, "cat-file", "-e", sha + "^{commit}").Succeeded;

        private static bool HeadIs(GitSyncContext ctx, string sha)
        {
            var head = Git(ctx, ReadTimeoutMs, false, "rev-parse", "--verify", "-q", "HEAD^{commit}");
            return head.Succeeded && head.StdOut.Trim() == sha;
        }

        private static List<GitRemote> ReadRemotes(GitSyncContext ctx, out bool ok)
        {
            // Filtering by key happens in the parser: a regex with parentheses would be one more thing to trust Mono's
            // command-line splitting with.
            var run = Git(ctx, ReadTimeoutMs, false, "config", "-z", "--get-regexp", "^remote[.]");
            ok = run.Succeeded || (run.ExitCode == 1 && !run.StartFailed && !run.TimedOut && !run.Cancelled);
            if (!ok) LogFailure(ctx, "config", run);
            return run.Succeeded ? ParseRemoteConfig(run.StdOut) : new List<GitRemote>();
        }

        private static bool IsOfficialRemoteUrl(GitSyncContext ctx, string url) =>
            IsOfficialRepoUrl(url) || (!string.IsNullOrEmpty(ctx.OfficialUrl) && SameRepositoryUrl(url, ctx.OfficialUrl));

        private static ProcessRunResult Git(GitSyncContext ctx, int timeoutMs, bool isolated, params string[] args)
        {
            var run = GitCliRunner.RunGit(ctx.GitPath, ctx.PackageDir, args, timeoutMs, ctx.IsCancelled, isolated);
            ctx.Add(GitLogLevel.Verbose, $"git {string.Join(" ", args)} -> {DescribeRun(run)} in {run.ElapsedMs} ms");
            return run;
        }

        private static void LogFailure(GitSyncContext ctx, string what, ProcessRunResult run)
        {
            if (run.Cancelled) return;
            ctx.Add(GitLogLevel.Error, $"Self-update: git {what} failed in {ctx.PackageDir} ({DescribeRun(run)}): {Clip(run.StdErr)}");
        }

        private static LocalUpdateResult Result(LocalUpdateOutcome outcome, LocalUpdateProbe probe) =>
            new LocalUpdateResult { Outcome = outcome, Probe = probe };

        private static string DescribeRun(ProcessRunResult run) =>
            run.StartFailed ? "did not start" : run.TimedOut ? "timed out" : run.Cancelled ? "cancelled" : "exit " + run.ExitCode;

        private static string Clip(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length <= MaxLoggedStderr ? trimmed : trimmed.Substring(0, MaxLoggedStderr) + "…";
        }

        private static bool Contains(string text, string fragment) =>
            text != null && text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Outcomes where the update deliberately left everything untouched (logged as a warning, not an error).</summary>
        internal static bool IsRefusal(LocalUpdateOutcome outcome)
        {
            switch (outcome)
            {
                case LocalUpdateOutcome.RecheckRequired:
                case LocalUpdateOutcome.GitMissingForRepository:
                case LocalUpdateOutcome.GitTooOld:
                case LocalUpdateOutcome.GitUnsafeRepository:
                case LocalUpdateOutcome.GitRepositoryLocked:
                case LocalUpdateOutcome.DirtyWorktree:
                case LocalUpdateOutcome.DetachedHead:
                case LocalUpdateOutcome.UnsupportedBranch:
                case LocalUpdateOutcome.Diverged:
                case LocalUpdateOutcome.WouldOverwriteLocalFiles:
                case LocalUpdateOutcome.VendoredSubtreeModified:
                case LocalUpdateOutcome.PackageDirIsRepository:
                case LocalUpdateOutcome.PackageDirIsLink:
                    return true;
                default:
                    return false;
            }
        }

        internal static string ShortSha(string sha) => string.IsNullOrEmpty(sha) || sha.Length <= 7 ? sha ?? string.Empty : sha.Substring(0, 7);

        internal static string BranchTargetLabel(string branch, string sha) => $"{branch} @ {ShortSha(sha)}";

        // ===== Pure helpers =====

        /// <summary>Nearest directory at or above <paramref name="startDir"/> holding a .git directory or file (worktrees, submodules).</summary>
        internal static bool TryFindGitMarker(string startDir, out string markerRoot, string ceiling = null)
        {
            markerRoot = null;
            if (string.IsNullOrEmpty(startDir)) return false;
            var ceilingFull = string.IsNullOrEmpty(ceiling) ? null : TrimSeparators(Path.GetFullPath(ceiling));
            for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir != null; dir = dir.Parent)
            {
                if (HasGitMarker(dir.FullName))
                {
                    markerRoot = TrimSeparators(dir.FullName);
                    return true;
                }
                if (ceilingFull != null && PathEquals(TrimSeparators(dir.FullName), ceilingFull)) break;
            }
            return false;
        }

        private static bool HasGitMarker(string dir)
        {
            var marker = Path.Combine(dir, ".git");
            return Directory.Exists(marker) || File.Exists(marker);
        }

        /// <summary>A package folder named SkillsForUnity directly under the repository root: the layout of a clone.</summary>
        internal static bool IsCloneShaped(string packageDir, string markerRoot)
        {
            if (string.IsNullOrEmpty(packageDir) || string.IsNullOrEmpty(markerRoot)) return false;
            var full = TrimSeparators(Path.GetFullPath(packageDir));
            var parent = Path.GetDirectoryName(full);
            return PathEquals(Path.GetFileName(full), ClonePackageFolder) &&
                   parent != null && PathEquals(TrimSeparators(parent), TrimSeparators(Path.GetFullPath(markerRoot)));
        }

        /// <summary>
        /// Guards shared by every archive track: a package folder that is itself a repository (the swap would delete its
        /// .git), or a symbolic link / junction (the swap would replace the link with a real folder). Null when safe.
        /// </summary>
        internal static LocalUpdateOutcome? CheckArchiveGuards(string packageDir)
        {
            if (HasGitMarker(packageDir)) return LocalUpdateOutcome.PackageDirIsRepository;
            try
            {
                if ((File.GetAttributes(TrimSeparators(packageDir)) & FileAttributes.ReparsePoint) != 0)
                    return LocalUpdateOutcome.PackageDirIsLink;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return LocalUpdateOutcome.DiskError;
            }
            return null;
        }

        /// <summary>
        /// Strict recognition of the official GitHub repository in any URL form git accepts (https/http/ssh/git, scp-like),
        /// credentials and ports ignored. A substring test would also accept forks such as Unity-Skills-Fork or look-alike
        /// hosts, so the host and the exact owner/name path are parsed out and compared.
        /// </summary>
        internal static bool IsOfficialRepoUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var text = url.Trim().TrimEnd('/');
            if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);
            text = text.TrimEnd('/');

            string host;
            string path;
            var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                var scheme = text.Substring(0, schemeEnd);
                if (!UrlSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)) return false;
                var rest = text.Substring(schemeEnd + 3);
                var slash = rest.IndexOf('/');
                if (slash <= 0) return false;
                host = rest.Substring(0, slash);
                path = rest.Substring(slash + 1);
                var at = host.LastIndexOf('@');
                if (at >= 0) host = host.Substring(at + 1);
                var port = host.IndexOf(':');
                if (port >= 0) host = host.Substring(0, port);
                if (!OfficialHttpHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return false;
            }
            else
            {
                var colon = text.IndexOf(':');
                if (colon <= 0) return false;
                host = text.Substring(0, colon);
                // A slash before the colon means a local path; a single letter is a Windows drive (C:/...).
                if (host.IndexOf('/') >= 0 || host.IndexOf('\\') >= 0) return false;
                var at = host.LastIndexOf('@');
                if (at >= 0) host = host.Substring(at + 1);
                if (host.Length <= 1 || !OfficialScpHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return false;
                path = text.Substring(colon + 1).TrimStart('/');
            }
            return string.Equals(path, OfficialRepoPath, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Test-injected official URLs (local bare repositories): file:// and trailing slashes do not matter.</summary>
        internal static bool SameRepositoryUrl(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return PathEquals(NormalizeLocalUrl(a), NormalizeLocalUrl(b));
        }

        private static string NormalizeLocalUrl(string url)
        {
            var text = url.Trim();
            if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) text = text.Substring("file://".Length);
            return TrimSeparators(text.Replace('\\', '/'));
        }

        /// <summary>Parses <c>git config -z --get-regexp ^remote[.]</c> (records "key\nvalue\0") into remotes in first-seen order.</summary>
        internal static List<GitRemote> ParseRemoteConfig(string output)
        {
            var remotes = new List<GitRemote>();
            if (string.IsNullOrEmpty(output)) return remotes;
            foreach (var record in output.Split('\0'))
            {
                if (record.Length == 0) continue;
                var newline = record.IndexOf('\n');
                var key = newline >= 0 ? record.Substring(0, newline) : record;
                var value = newline >= 0 ? record.Substring(newline + 1) : null;
                if (value == null || !key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase)) continue;
                var lastDot = key.LastIndexOf('.');
                if (lastDot <= "remote.".Length) continue;
                var name = key.Substring("remote.".Length, lastDot - "remote.".Length);
                var variable = key.Substring(lastDot + 1);
                var isUrl = variable.Equals("url", StringComparison.OrdinalIgnoreCase);
                var isFetch = variable.Equals("fetch", StringComparison.OrdinalIgnoreCase);
                if (!isUrl && !isFetch) continue;

                var remote = remotes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
                if (remote == null)
                {
                    remote = new GitRemote { Name = name };
                    remotes.Add(remote);
                }
                (isUrl ? remote.Urls : remote.FetchRefspecs).Add(value);
            }
            return remotes;
        }

        /// <summary>
        /// Among remotes whose (first) URL is official: origin, then upstream, then the ordinal first. The tracking ref is
        /// only reported for the default mapping of that remote, so refreshing it matches what the user's own fetch does.
        /// </summary>
        internal static bool SelectOfficialRemote(IReadOnlyList<GitRemote> remotes, Func<string, bool> isOfficial, string branch,
            out string remoteName, out string trackingRef)
        {
            remoteName = null;
            trackingRef = null;
            var official = remotes.Where(r => r.Urls.Count > 0 && isOfficial(r.Urls[0])).ToList();
            if (official.Count == 0) return false;

            var chosen = official.FirstOrDefault(r => r.Name == "origin")
                ?? official.FirstOrDefault(r => r.Name == "upstream")
                ?? official.OrderBy(r => r.Name, StringComparer.Ordinal).First();
            remoteName = chosen.Name;

            if (string.IsNullOrEmpty(branch) || !SafeRemoteName.IsMatch(chosen.Name)) return true;
            var wildcard = $"refs/heads/*:refs/remotes/{chosen.Name}/*";
            var single = $"refs/heads/{branch}:refs/remotes/{chosen.Name}/{branch}";
            foreach (var spec in chosen.FetchRefspecs)
            {
                var plain = spec.Trim().TrimStart('+');
                if (plain == wildcard || plain == single)
                {
                    trackingRef = $"refs/remotes/{chosen.Name}/{branch}";
                    break;
                }
            }
            return true;
        }

        /// <summary>From <c>git symbolic-ref -q --short HEAD</c>: exit 1 is a detached HEAD; only main and beta may update.</summary>
        internal static BranchVerdict EvaluateBranch(int exitCode, string stdout, out string branch)
        {
            branch = null;
            if (exitCode == 1) return BranchVerdict.Detached;
            if (exitCode != 0) return BranchVerdict.Failed;
            branch = (stdout ?? string.Empty).Trim();
            if (branch.Length == 0) return BranchVerdict.Failed;
            return AllowedBranches.Contains(branch, StringComparer.Ordinal) ? BranchVerdict.Allowed : BranchVerdict.Unsupported;
        }

        internal static PrefixKind ClassifyPrefix(string prefix)
        {
            var trimmed = (prefix ?? string.Empty).Trim();
            if (trimmed.Length == 0) return PrefixKind.PackageIsRepoRoot;
            return string.Equals(trimmed, ClonePackageFolder + "/", StringComparison.OrdinalIgnoreCase)
                ? PrefixKind.Clone
                : PrefixKind.Foreign;
        }

        /// <summary>
        /// Entries of <c>status --porcelain=v1 -z</c> as "XY path" for display. Rename and copy records carry the original
        /// path as an extra NUL field, which is consumed. Any non-empty output is dirty, even if it does not parse.
        /// </summary>
        internal static List<string> ParsePorcelainZ(string output, out int count)
        {
            var entries = new List<string>();
            count = 0;
            if (string.IsNullOrEmpty(output)) return entries;
            var fields = output.Split('\0');
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field.Length == 0) continue;
                entries.Add(field);
                if (field.Length >= 2 && (field[0] == 'R' || field[0] == 'C' || field[1] == 'R' || field[1] == 'C'))
                    i++;
            }
            count = entries.Count;
            return entries;
        }

        /// <summary>The SHA of exactly <paramref name="refName"/> in ls-remote output (40 or 64 hex digits).</summary>
        internal static bool TryParseLsRemote(string output, string refName, out string sha)
        {
            sha = null;
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(refName)) return false;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0 || line.Substring(tab + 1) != refName) continue;
                var candidate = line.Substring(0, tab).Trim();
                if (!HexSha.IsMatch(candidate)) continue;
                sha = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Release tags (vX.Y.Z only) from <c>ls-remote --tags</c>, each resolved to its commit: an annotated tag's peeled
        /// "^{}" line wins over the tag object line.
        /// </summary>
        internal static List<ReleaseTag> ParseReleaseTags(string output)
        {
            var byName = new Dictionary<string, ReleaseTag>(StringComparer.Ordinal);
            var peeled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in (output ?? string.Empty).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var sha = line.Substring(0, tab).Trim();
                var refName = line.Substring(tab + 1);
                if (!HexSha.IsMatch(sha) || !refName.StartsWith("refs/tags/", StringComparison.Ordinal)) continue;

                var name = refName.Substring("refs/tags/".Length);
                var isPeeled = name.EndsWith("^{}", StringComparison.Ordinal);
                if (isPeeled) name = name.Substring(0, name.Length - 3);
                if (!TryParseReleaseTag(name, out var version)) continue;

                if (!byName.TryGetValue(name, out var tag))
                {
                    tag = new ReleaseTag { Name = name, Version = version };
                    byName[name] = tag;
                }
                if (isPeeled)
                {
                    tag.Commit = sha;
                    peeled.Add(name);
                }
                else if (!peeled.Contains(name))
                {
                    tag.Commit = sha;
                }
            }
            return byName.Values.ToList();
        }

        internal static bool TryParseReleaseTag(string name, out Version version)
        {
            version = null;
            var match = ReleaseTagName.Match(name ?? string.Empty);
            if (!match.Success) return false;
            if (!int.TryParse(match.Groups[1].Value, out var major) ||
                !int.TryParse(match.Groups[2].Value, out var minor) ||
                !int.TryParse(match.Groups[3].Value, out var patch))
                return false;
            version = new Version(major, minor, patch);
            return true;
        }

        internal static bool TryNormalizeReleaseTag(string versionOrTag, out string tagName, out Version version)
        {
            tagName = null;
            version = null;
            if (string.IsNullOrWhiteSpace(versionOrTag)) return false;
            var trimmed = versionOrTag.Trim();
            var candidate = trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? "v" + trimmed.Substring(1)
                : "v" + trimmed;
            if (!TryParseReleaseTag(candidate, out version)) return false;
            tagName = candidate;
            return true;
        }

        internal static bool IsStarHistoryCommitRecord(string author, string subject) =>
            string.Equals(author, "github-actions[bot]", StringComparison.Ordinal) &&
            string.Equals(subject, "docs: update Star History charts", StringComparison.Ordinal);

        internal static bool IsStarHistoryPath(string path) =>
            string.Equals(path, "docs/star-history-light.svg", StringComparison.Ordinal) ||
            string.Equals(path, "docs/star-history-dark.svg", StringComparison.Ordinal);

        /// <summary>
        /// Paths git lists under "…would be overwritten by merge/checkout:" (tab-indented, repository-relative, UTF-8
        /// verbatim with core.quotePath=false).
        /// </summary>
        internal static List<string> ParseOverwrittenPaths(string stderr)
        {
            var paths = new List<string>();
            var inList = false;
            foreach (var raw in (stderr ?? string.Empty).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.IndexOf("would be overwritten by", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    inList = true;
                    continue;
                }
                if (!inList) continue;
                if (line.StartsWith("\t", StringComparison.Ordinal))
                {
                    var path = line.Trim();
                    if (path.Length > 0) paths.Add(path);
                }
                else
                {
                    inList = false;
                }
            }
            return paths;
        }

        private static string TrimSeparators(string path) =>
            path.Length > 1 ? path.TrimEnd('/', '\\') : path;

        // Windows and macOS file systems are case-insensitive by default.
        private static bool PathEquals(string a, string b) =>
            string.Equals(a, b, GitCliRunner.IsWindows || GitCliRunner.IsMac ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

// Producer:Betsy
