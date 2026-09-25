using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.Networking;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// In-place self-update for local installs (file: or embedded), on one of two tracks that
    /// <see cref="LocalGitSync.Probe"/> picks on every run:
    /// Track A, an official git clone: fast-forward to the official branch head (or, detached at a release tag, move to
    /// the newest release tag). Reads, network and fetch run on a worker thread; the single write runs synchronously on
    /// the main thread inside LockReloadAssemblies, so no import or skill can interleave with git writing files.
    /// Track B, no repository or a clean copy inside another one: download the release archive of the target tag,
    /// extract the SkillsForUnity subtree, validate it, swap it in by same-volume renames with rollback, and keep the
    /// replaced version as a one-generation backup under Library/UnitySkills/selfupdate/previous.
    /// System.IO.Compression stays fully qualified (no using), same as the GZipStream usage in SkillsHttpServer.
    /// </summary>
    internal static class LocalSelfUpdateService
    {
        private const string DownloadUrlFormat =
            "https://codeload.github.com/Besty0728/Unity-Skills/zip/refs/tags/v{0}";
        private const string ExpectedPackageName = "com.besty.unity-skills";
        private const string SelfUpdateDirName = "selfupdate";
        private const string BackupDirName = "previous";
        private const string StaleBackupPrefix = BackupDirName + ".stale-";
        private const string DoneFileName = "selfupdate_done.json";
        private const string NeutralizedManifestName = "package.json.unityskills-old";
        private const int RenameAttempts = 5;

        private static string LibraryRoot =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Library"));
        private static string UnitySkillsLibraryDir => Path.Combine(LibraryRoot, "UnitySkills");
        private static string WorkDir => Path.Combine(UnitySkillsLibraryDir, SelfUpdateDirName);
        private static string ZipPath => Path.Combine(WorkDir, "pkg.zip");
        // Staging dir itself is the new package root (contains package.json / Editor/ / unity-skills~/).
        private static string StagingDir => Path.Combine(WorkDir, "staging");
        // Survives CleanupWorkDir on purpose: the package folder this update replaced.
        private static string BackupDir => Path.Combine(WorkDir, BackupDirName);
        private static string DoneFilePath => Path.Combine(UnitySkillsLibraryDir, DoneFileName);

        private static UnityWebRequest _activeRequest;
        private static Action<LocalUpdateResult> _callback;
        private static string _targetVersion;
        private static string _targetDir;
        private static LocalUpdateProbe _probe;
        private static GitSyncContext _git;
        private static bool _isUpdate;
        private static bool _cancelled;
        private static volatile bool _cancelRequested;

        /// <summary>Test seam: called before every directory move of the swap; throw from it to simulate a failing rename.</summary>
        internal static Action<string, string> MoveDirectoryHookForTests;

        private static LocalUpdateResult _workerResult;
        private static volatile bool _workerDone;
        private static Action<LocalUpdateResult> _workerContinuation;
        private static bool _workerShowsProgress;
        private static double _workerStartedAt;

        internal static bool IsRunning { get; private set; }

        private static bool IsBusy =>
            IsRunning || PackageManagerHelper.HasPendingOperation ||
            EditorApplication.isCompiling || EditorApplication.isUpdating;

        /// <summary>
        /// Hooks the cancellation paths, and after a domain reload lets the fresh domain read the marker written by the
        /// pre-reload update, log one success line and remove it.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterHooks()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CancelActiveRequest;
            EditorApplication.quitting += CancelActiveRequest;
            EditorApplication.delayCall += CheckDoneMarker;
        }

        private static void CheckDoneMarker()
        {
            try
            {
                if (!File.Exists(DoneFilePath)) return;

                JObject marker = null;
                try { marker = JObject.Parse(File.ReadAllText(DoneFilePath)); }
                catch { /* marker is best-effort; log even if it cannot be parsed */ }

                File.Delete(DoneFilePath);
                SkillsLogger.Log(DescribeDoneMarker(marker));
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to read self-update marker: " + ex.Message);
            }
        }

        /// <summary>The one success line printed after the post-update reload. Pure for tests.</summary>
        internal static string DescribeDoneMarker(JObject marker)
        {
            var version = marker?.Value<string>("version");
            var versionSuffix = string.IsNullOrEmpty(version) ? string.Empty : ", " + version;
            if (marker?.Value<string>("mode") == "git")
            {
                var tag = marker.Value<string>("tag");
                var span = string.IsNullOrEmpty(tag)
                    ? $"{marker.Value<string>("branch")} {marker.Value<string>("from")}..{marker.Value<string>("to")}"
                    : $"{marker.Value<string>("from")}..{tag}";
                return $"Self-update via local Git sync ({span}{versionSuffix}) completed successfully.";
            }

            var line = string.IsNullOrEmpty(version)
                ? "Self-update completed successfully."
                : $"Self-update to {version} completed successfully.";
            var backup = marker?.Value<string>("backup");
            return string.IsNullOrEmpty(backup) ? line : line + " The previous version was kept at " + backup + ".";
        }

        // With Check / Start and the LocalUpdate* result types, the UI's whole surface: SelfUpdateFeedback formats
        // results through these instead of reaching into LocalGitSync (see SelfUpdateBoundaryTests).
        internal const int MaxListedEntries = LocalGitSync.MaxListedEntries;

        internal static string ShortSha(string sha) => LocalGitSync.ShortSha(sha);

        /// <summary>
        /// What an update would do, without writing anything: git clones ask the official remote for their branch head
        /// (or newest release tag); archive installs answer <see cref="LocalUpdateOutcome.ReleaseCheckRequired"/> and the
        /// caller compares against the latest stable release. The callback runs on the main thread.
        /// </summary>
        internal static void Check(Action<LocalUpdateResult> callback)
        {
            if (IsRunning)
            {
                callback?.Invoke(new LocalUpdateResult { Outcome = LocalUpdateOutcome.Busy });
                return;
            }
            if (!PackageManagerHelper.TryGetSelfPackageRoot(out var packageDir))
            {
                callback?.Invoke(new LocalUpdateResult { Outcome = LocalUpdateOutcome.PackageRootNotFound });
                return;
            }

            BeginRun(callback, packageDir, null, isUpdate: false);
            var ctx = _git;
            RunWorker(showProgress: false, work: () =>
            {
                ResolveGit(ctx);
                return LocalGitSync.CheckRemote(ctx, LocalGitSync.Probe(ctx));
            }, then: Finish);
        }

        /// <summary>
        /// Runs the update. Track A ignores <paramref name="targetVersion"/>: the target is always the official head of
        /// the checked-out branch (or the newest release tag). Track B downloads v{targetVersion}. The callback runs on
        /// the main thread exactly once.
        /// </summary>
        internal static void Start(string targetVersion, Action<LocalUpdateResult> callback)
        {
            if (IsBusy)
            {
                callback?.Invoke(new LocalUpdateResult { Outcome = LocalUpdateOutcome.Busy });
                return;
            }
            if (!PackageManagerHelper.TryGetSelfPackageRoot(out var packageDir))
            {
                callback?.Invoke(new LocalUpdateResult { Outcome = LocalUpdateOutcome.PackageRootNotFound });
                return;
            }

            BeginRun(callback, packageDir, targetVersion, isUpdate: true);
            var ctx = _git;
            // Re-probe instead of trusting an earlier check: the user may have switched branch or edited files since.
            RunWorker(showProgress: true, work: () =>
            {
                ResolveGit(ctx);
                return LocalGitSync.Prepare(ctx, LocalGitSync.Probe(ctx));
            }, then: OnPrepared);
        }

        private static void BeginRun(Action<LocalUpdateResult> callback, string packageDir, string targetVersion, bool isUpdate)
        {
            IsRunning = true;
            _callback = callback;
            _targetDir = packageDir;
            _targetVersion = targetVersion;
            _isUpdate = isUpdate;
            _cancelled = false;
            _cancelRequested = false;
            _probe = null;
            _git = new GitSyncContext
            {
                PackageDir = packageDir,
                OfficialUrl = LocalGitSync.OfficialUrlOverrideForTesting ?? LocalGitSync.OfficialRepoHttpsUrl,
                IsCancelled = () => _cancelRequested,
            };
        }

        // Worker thread: only process and file-system work, no Unity API.
        private static void ResolveGit(GitSyncContext ctx)
        {
            if (GitCliRunner.TryResolveGit(out var gitPath, out var version))
            {
                ctx.GitPath = gitPath;
                ctx.GitVersion = version;
                return;
            }
            ctx.GitVersion = version;
            if (version != null)
                ctx.Add(GitLogLevel.Warning, $"Self-update: the git at {gitPath} is {version}; {GitCliRunner.MinimumVersion} or newer is required.");
        }

        private static void OnPrepared(LocalUpdateResult prepared)
        {
            _probe = prepared.Probe;
            var track = prepared.Probe?.Track;
            if (prepared.Outcome == LocalUpdateOutcome.UpdateAvailable &&
                (track == LocalUpdateTrack.GitSync || track == LocalUpdateTrack.GitTag))
            {
                ApplyGitUpdate(prepared);
                return;
            }
            if (prepared.Outcome == LocalUpdateOutcome.ReleaseCheckRequired)
            {
                BeginArchiveUpdate();
                return;
            }
            Finish(prepared);
        }

        // ===== Worker plumbing =====

        private static void RunWorker(bool showProgress, Func<LocalUpdateResult> work, Action<LocalUpdateResult> then)
        {
            var ctx = _git;
            _workerDone = false;
            _workerResult = null;
            _workerContinuation = then;
            _workerShowsProgress = showProgress;
            _workerStartedAt = EditorApplication.timeSinceStartup;

            var thread = new Thread(() =>
            {
                LocalUpdateResult result;
                try { result = work(); }
                catch (Exception ex)
                {
                    ctx.Add(GitLogLevel.Error, "Self-update failed unexpectedly: " + ex);
                    result = new LocalUpdateResult { Outcome = LocalUpdateOutcome.GitCommandFailed };
                }
                _workerResult = result;
                _workerDone = true;
            })
            {
                IsBackground = true,
                Name = "UnitySkills-SelfUpdate",
            };
            EditorApplication.update += PumpWorker;
            thread.Start();
        }

        private static void PumpWorker()
        {
            if (_workerDone)
            {
                EditorApplication.update -= PumpWorker;
                if (_workerShowsProgress) EditorUtility.ClearProgressBar();
                var result = _workerResult;
                var then = _workerContinuation;
                _workerResult = null;
                _workerContinuation = null;
                FlushGitLog();
                if (_cancelRequested && result.Outcome != LocalUpdateOutcome.Updated)
                    result = new LocalUpdateResult { Outcome = LocalUpdateOutcome.Cancelled, Probe = result.Probe };
                then?.Invoke(result);
                return;
            }
            if (!_workerShowsProgress || _git == null) return;

            // The worker cannot report real progress, so the bar creeps toward 90% and stays cancelable.
            var elapsed = EditorApplication.timeSinceStartup - _workerStartedAt;
            var progress = (float)(0.9 * (1.0 - Math.Exp(-elapsed / 20.0)));
            var info = _git.Phase == GitSyncPhase.Fetching
                ? SkillsLocalization.Get("update_git_fetching_fmt", _git.PhaseTarget ?? string.Empty)
                : SkillsLocalization.Get("update_check_checking");
            if (EditorUtility.DisplayCancelableProgressBar(SkillsLocalization.Get("drawer_update_check_label"), info, progress))
                _cancelRequested = true;
        }

        private static void FlushGitLog()
        {
            if (_git == null) return;
            foreach (var line in _git.DrainLog())
            {
                switch (line.Level)
                {
                    case GitLogLevel.Error: SkillsLogger.LogError(line.Message); break;
                    case GitLogLevel.Warning: SkillsLogger.LogWarning(line.Message); break;
                    default: SkillsLogger.LogVerbose(line.Message); break;
                }
            }
        }

        // ===== Track A: the one write, on the main thread =====

        private static void ApplyGitUpdate(LocalUpdateResult prepared)
        {
            var isTag = prepared.Probe.Track == LocalUpdateTrack.GitTag;
            var oldDependencies = ReadDependencies(_targetDir);
            LocalUpdateResult result;

            // LockReloadAssemblies keeps Unity from compiling a half-written tree; being synchronous keeps imports and
            // REST skills from interleaving with the files git writes.
            EditorApplication.LockReloadAssemblies();
            try
            {
                EditorUtility.DisplayProgressBar(SkillsLocalization.Get("drawer_update_check_label"),
                    isTag
                        ? SkillsLocalization.Get("update_git_switching_fmt", prepared.TargetLabel)
                        : SkillsLocalization.Get("update_git_merging_fmt", prepared.Probe.Branch),
                    1f);
                result = LocalGitSync.Apply(_git, prepared);
                FlushGitLog();
                if (result.Outcome == LocalUpdateOutcome.Updated)
                {
                    result.Version = ReadPackageVersion(_targetDir);
                    AfterPackageChanged(oldDependencies);
                }
            }
            catch (Exception ex)
            {
                FlushGitLog();
                SkillsLogger.LogError("Self-update via local Git sync failed: " + ex.Message);
                result = new LocalUpdateResult { Outcome = LocalUpdateOutcome.GitCommandFailed, Probe = prepared.Probe };
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorApplication.UnlockReloadAssemblies();
            }

            if (result.Outcome == LocalUpdateOutcome.Updated)
            {
                var marker = new JObject
                {
                    ["version"] = result.Version,
                    ["mode"] = "git",
                    ["from"] = isTag ? prepared.Probe.HeadLabel : LocalGitSync.ShortSha(result.FromSha),
                    ["to"] = LocalGitSync.ShortSha(result.ToSha),
                };
                if (isTag) marker["tag"] = prepared.TargetLabel;
                else marker["branch"] = prepared.Probe.Branch;
                WriteDoneMarker(marker);
            }
            Finish(result);
        }

        // ===== Track B: release archive =====

        private static void BeginArchiveUpdate()
        {
            // The check that produced the target version looked at a git clone; without a version there is no archive.
            if (string.IsNullOrEmpty(_targetVersion))
            {
                Finish(Outcome(LocalUpdateOutcome.RecheckRequired));
                return;
            }
            if (_probe.Track == LocalUpdateTrack.ArchiveUnverified && !SelfUpdateFeedback.ConfirmUnverifiedArchive(_probe))
            {
                Finish(Outcome(LocalUpdateOutcome.Cancelled));
                return;
            }

            CleanupWorkDir();
            try
            {
                Directory.CreateDirectory(WorkDir);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("Failed to prepare self-update work dir: " + ex.Message);
                Finish(Outcome(LocalUpdateOutcome.DiskError));
                return;
            }

            BeginDownload();
        }

        private static void BeginDownload()
        {
            var url = string.Format(DownloadUrlFormat, _targetVersion);
            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequest.Get(url);
                request.timeout = 300;
                request.downloadHandler = new DownloadHandlerFile(ZipPath);
                _activeRequest = request;

                var operation = request.SendWebRequest();
                operation.completed += _ => CompleteDownload(request);
                EditorApplication.update += TrackDownloadProgress;
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_activeRequest, request))
                    _activeRequest = null;
                request?.Dispose();
                SkillsLogger.LogError("Self-update download failed to start: " + ex.Message);
                Finish(Outcome(LocalUpdateOutcome.NetworkError));
            }
        }

        private static void TrackDownloadProgress()
        {
            var request = _activeRequest;
            if (request == null) return;

            var progress = request.downloadProgress;
            int percent = (int)(progress * 100f);
            bool cancel = EditorUtility.DisplayCancelableProgressBar(
                SkillsLocalization.Get("drawer_update_check_label"),
                SkillsLocalization.Get("update_check_downloading_fmt", percent + "%"),
                progress);
            if (cancel)
            {
                _cancelled = true;
                // Abort() makes the completed callback fire, which handles cleanup uniformly.
                request.Abort();
            }
        }

        private static void CompleteDownload(UnityWebRequest request)
        {
            if (!ReferenceEquals(_activeRequest, request))
                return;
            _activeRequest = null;
            EditorApplication.update -= TrackDownloadProgress;

            try
            {
                if (_cancelled)
                {
                    Finish(Outcome(LocalUpdateOutcome.Cancelled));
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SkillsLogger.LogError("Self-update download failed: " + request.error);
                    Finish(Outcome(LocalUpdateOutcome.NetworkError));
                    return;
                }

                ExtractValidateAndSwap();
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Synchronous disk stage after the download finished: extract the SkillsForUnity subtree, validate the staging
        /// dir, look at the live folder once more, swap, and trigger recompilation. Every failure maps to an outcome;
        /// nothing escapes to the caller.
        /// </summary>
        private static void ExtractValidateAndSwap()
        {
            try
            {
                Directory.CreateDirectory(StagingDir);
                ExtractPackageSubtree(ZipPath, StagingDir);
            }
            catch (InvalidDataException)
            {
                Finish(Outcome(LocalUpdateOutcome.InvalidPackage));
                return;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("Self-update extraction failed: " + ex.Message);
                Finish(Outcome(LocalUpdateOutcome.DiskError));
                return;
            }

            if (!ValidateStagedPackage(StagingDir, _targetVersion))
            {
                Finish(Outcome(LocalUpdateOutcome.InvalidPackage));
                return;
            }

            // The download took a while; the folder may have become a link or a repository, or picked up edits.
            var guard = LocalGitSync.CheckArchiveGuards(_targetDir);
            if (guard != null)
            {
                Finish(Outcome(guard.Value));
                return;
            }
            if (_probe.Track == LocalUpdateTrack.ArchiveInRepository)
            {
                var verdict = LocalGitSync.CheckVendoredSubtree(_git, out var entries, out var count);
                FlushGitLog();
                if (verdict != null)
                {
                    _probe.Entries = entries;
                    _probe.EntryCount = count;
                    var refused = Outcome(verdict.Value);
                    refused.Entries = entries;
                    refused.EntryCount = count;
                    Finish(refused);
                    return;
                }
            }

            var oldDependencies = ReadDependencies(_targetDir);
            SwapReport report;
            // LockReloadAssemblies keeps Unity from compiling the half-swapped directory tree.
            EditorApplication.LockReloadAssemblies();
            try
            {
                try
                {
                    report = SwapDirectories(_targetDir, StagingDir, BackupDir);
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogError("Self-update directory swap failed: " + ex.Message);
                    Finish(Outcome(LocalUpdateOutcome.DiskError));
                    return;
                }
                foreach (var warning in report.Warnings)
                    SkillsLogger.LogWarning(warning);
                AfterPackageChanged(oldDependencies);
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }

            WriteDoneMarker(new JObject { ["version"] = _targetVersion, ["backup"] = report.BackupPath });
            var updated = Outcome(LocalUpdateOutcome.Updated);
            updated.Version = _targetVersion;
            updated.BackupPath = report.BackupPath;
            Finish(updated);
        }

        /// <summary>
        /// After either track changed the package on disk. Failures here only lose the automatic refresh (the files are
        /// already in place), so they are warnings. Package Manager only re-resolves when package.json dependencies moved.
        /// </summary>
        private static void AfterPackageChanged(JToken oldDependencies)
        {
            try { AssetDatabase.Refresh(); }
            catch (Exception ex) { SkillsLogger.LogWarning("Self-update: asset refresh failed: " + ex.Message); }
            try { CompilationPipeline.RequestScriptCompilation(); }
            catch { /* editor may refuse during certain lifecycle moments */ }

            if (JToken.DeepEquals(oldDependencies, ReadDependencies(_targetDir))) return;
            try { UnityEditor.PackageManager.Client.Resolve(); }
            catch (Exception ex) { SkillsLogger.LogWarning("Self-update: Package Manager resolve failed: " + ex.Message); }
        }

        private static JToken ReadDependencies(string packageDir)
        {
            try { return JObject.Parse(File.ReadAllText(Path.Combine(packageDir, "package.json")))["dependencies"]; }
            catch { return null; }
        }

        private static string ReadPackageVersion(string packageDir)
        {
            try { return JObject.Parse(File.ReadAllText(Path.Combine(packageDir, "package.json"))).Value<string>("version"); }
            catch { return null; }
        }

        /// <summary>
        /// Maps a repo-archive entry to the path of the file inside the package root. The archive
        /// always has a single top-level directory ({repo}-{tag}/); only entries below its
        /// SkillsForUnity/ subtree belong to the package, everything else is skipped. Pure
        /// function so the mapping is testable without a real archive.
        /// </summary>
        internal static bool TryMapArchiveEntry(string entryName, out string relativePath)
        {
            relativePath = null;
            if (string.IsNullOrEmpty(entryName)) return false;

            var normalized = entryName.Replace('\\', '/').TrimStart('/');
            var firstSlash = normalized.IndexOf('/');
            if (firstSlash < 0 || firstSlash == normalized.Length - 1) return false;

            var remainder = normalized.Substring(firstSlash + 1);
            const string prefix = "SkillsForUnity/";
            if (!remainder.StartsWith(prefix, StringComparison.Ordinal)) return false;

            relativePath = remainder.Substring(prefix.Length);
            if (relativePath.Length == 0) return false;

            // Zip Slip guard: no ".." segment may escape the package root.
            foreach (var segment in relativePath.Split('/'))
            {
                if (segment == "..")
                {
                    relativePath = null;
                    return false;
                }
            }
            return true;
        }

        // internal for tests: the real-archive extraction is exercised end-to-end against a temp dir.
        internal static void ExtractPackageSubtree(string zipPath, string stagingDir)
        {
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
            using (var archive = new System.IO.Compression.ZipArchive(
                fs, System.IO.Compression.ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!TryMapArchiveEntry(entry.FullName, out var relPath)) continue;
                    // Directory entries carry no content; their files create the dirs on demand.
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                        entry.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;

                    // Zip Slip guard: no ".." segment may escape the staging dir.
                    foreach (var segment in relPath.Split('/'))
                    {
                        if (segment == "..")
                            throw new InvalidDataException("Unsafe path in archive: " + entry.FullName);
                    }

                    var destPath = Path.Combine(
                        stagingDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath));

                    using (var input = entry.Open())
                    using (var output = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        /// <summary>
        /// Validates the staging dir before it replaces the live package: package.json must name
        /// this package and match the requested version, and Editor/ plus unity-skills~/ must
        /// exist (a repo archive without them is not a usable package).
        /// </summary>
        internal static bool ValidateStagedPackage(string stagingDir, string expectedVersion)
        {
            try
            {
                var packageJsonPath = Path.Combine(stagingDir, "package.json");
                if (!File.Exists(packageJsonPath)) return false;

                var json = JObject.Parse(File.ReadAllText(packageJsonPath));
                if (!string.Equals(json.Value<string>("name"), ExpectedPackageName, StringComparison.Ordinal))
                    return false;
                if (!string.Equals(json.Value<string>("version"), expectedVersion, StringComparison.Ordinal))
                    return false;

                return Directory.Exists(Path.Combine(stagingDir, "Editor")) &&
                       Directory.Exists(Path.Combine(stagingDir, "unity-skills~"));
            }
            catch
            {
                return false;
            }
        }

        internal sealed class SwapReport
        {
            /// <summary>Where the replaced package now lives; null only if it could not be kept anywhere.</summary>
            public string BackupPath;
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// Replaces <paramref name="targetDir"/> with <paramref name="stagingDir"/> so that at every moment either the old
        /// or the new package is complete at the target path. The new tree first moves next to the target (same volume),
        /// then two renames swap them; a failed second rename renames the original back. Nothing is ever copied over or
        /// deleted out of the live folder, and leftovers of earlier runs are never touched. Throws only while the target
        /// is still (or again) the original. Afterwards the replaced tree becomes the one-generation backup at
        /// <paramref name="backupDir"/>; that part never throws and never deletes the only copy of anything.
        /// </summary>
        internal static SwapReport SwapDirectories(string targetDir, string stagingDir, string backupDir)
        {
            var target = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(target);
            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sibling = Path.Combine(parent, ".unityskills-new-" + token);
            var old = Path.Combine(parent, ".unityskills-old-" + token);

            try
            {
                MoveOrCopy(stagingDir, sibling);
            }
            catch
            {
                TryDeleteDirectory(sibling);
                throw;
            }

            try
            {
                RenameWithRetry(target, old);
            }
            catch
            {
                TryDeleteDirectory(sibling);
                throw;
            }

            try
            {
                RenameWithRetry(sibling, target);
            }
            catch (Exception swapError)
            {
                try
                {
                    RenameWithRetry(old, target);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException(
                        $"could not move the new package into {target} ({swapError.Message}) nor move the original back " +
                        $"({rollbackError.Message}). The original package is intact at {old}: rename {old} back to {target}.",
                        swapError);
                }
                TryDeleteDirectory(sibling);
                throw;
            }

            var report = new SwapReport();
            report.BackupPath = KeepPreviousVersion(old, backupDir, token, report.Warnings);
            return report;
        }

        private static string KeepPreviousVersion(string old, string backupDir, string token, List<string> warnings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupDir));
                if (Directory.Exists(backupDir))
                {
                    // One generation only; the rename is instant and frees the name even if deleting then stalls.
                    var stale = Path.Combine(Path.GetDirectoryName(backupDir), StaleBackupPrefix + token);
                    MoveDirectory(backupDir, stale);
                    TryDeleteDirectory(stale);
                }

                try
                {
                    MoveDirectory(old, backupDir);
                    return backupDir;
                }
                catch (IOException)
                {
                    // Another volume (a file: package outside the project). Copy, and let the original go only once the
                    // copy is complete.
                    try { CopyDirectory(old, backupDir); }
                    catch { TryDeleteDirectory(backupDir); throw; }
                }

                if (!TryDeleteDirectory(old))
                {
                    NeutralizeManifest(old, warnings);
                    warnings.Add($"Self-update kept the previous version at {backupDir} but could not remove {old}; delete it once the editor is idle.");
                }
                return backupDir;
            }
            catch (Exception ex)
            {
                // Still on disk next to the package; never delete the only copy of the previous version.
                NeutralizeManifest(old, warnings);
                warnings.Add($"Self-update could not move the previous version to {backupDir} ({ex.Message}); it stays at {old}.");
                return old;
            }
        }

        /// <summary>
        /// A leftover folder next to an embedded package would still read as a second copy of this package to the
        /// Package Manager; renaming its manifest takes it out of resolution without deleting anything.
        /// </summary>
        private static void NeutralizeManifest(string dir, List<string> warnings)
        {
            var manifest = Path.Combine(dir, "package.json");
            try
            {
                if (File.Exists(manifest)) File.Move(manifest, Path.Combine(dir, NeutralizedManifestName));
            }
            catch (Exception ex)
            {
                warnings.Add($"Self-update could not rename {manifest} ({ex.Message}); remove the folder {dir} by hand.");
            }
        }

        private static void MoveOrCopy(string from, string to)
        {
            try
            {
                MoveDirectory(from, to);
            }
            catch (IOException)
            {
                // Library/ may sit on another volume than a file: package; the swap itself stays on one volume.
                CopyDirectory(from, to);
            }
        }

        /// <summary>
        /// Directory.Move with backoff (100/200/400/800 ms) for the transient locks antivirus and indexers take on
        /// Windows. Never falls back to copy + delete: that is how a live package used to end up half deleted.
        /// </summary>
        private static void RenameWithRetry(string from, string to)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    MoveDirectory(from, to);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < RenameAttempts)
                {
                    Thread.Sleep(100 << (attempt - 1));
                }
            }
        }

        private static void MoveDirectory(string from, string to)
        {
            MoveDirectoryHookForTests?.Invoke(from, to);
            Directory.Move(from, to);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            var source = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(destDir);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destDir, dir.Substring(source.Length + 1)));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destDir, file.Substring(source.Length + 1)), overwrite: true);
        }

        private static bool TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void WriteDoneMarker(JObject marker)
        {
            try
            {
                marker["utc"] = DateTime.UtcNow.ToString("o");
                File.WriteAllText(DoneFilePath, marker.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                // The update already succeeded; a missing marker only loses the success log line.
                SkillsLogger.LogWarning("Failed to write self-update marker: " + ex.Message);
            }
        }

        private static LocalUpdateResult Outcome(LocalUpdateOutcome outcome) =>
            new LocalUpdateResult { Outcome = outcome, Probe = _probe };

        private static void Finish(LocalUpdateResult result)
        {
            var isUpdate = _isUpdate;
            var probe = result.Probe ?? _probe;
            IsRunning = false;
            _activeRequest = null;
            _targetVersion = null;
            _targetDir = null;
            _probe = null;
            EditorApplication.update -= TrackDownloadProgress;
            EditorApplication.update -= PumpWorker;
            EditorUtility.ClearProgressBar();
            if (isUpdate) CleanupWorkDir();
            FlushGitLog();
            _git = null;

            if (result.Probe == null) result.Probe = probe;
            if (isUpdate && LocalGitSync.IsRefusal(result.Outcome))
            {
                var count = result.EntryCount > 0 ? result.EntryCount : probe?.EntryCount ?? 0;
                SkillsLogger.LogWarning($"Self-update refused: {result.Outcome}" +
                    (count > 0 ? $" ({count} entries)" : string.Empty) +
                    $" in {probe?.RepoRoot ?? probe?.MarkerRoot ?? probe?.PackageDir}");
            }

            // UnityWebRequest.completed and EditorApplication.update both run on the main thread; no delayCall needed.
            var callback = _callback;
            _callback = null;
            callback?.Invoke(result);
        }

        /// <summary>Removes the download and staging leftovers; the one-generation backup stays.</summary>
        private static void CleanupWorkDir()
        {
            try
            {
                if (File.Exists(ZipPath)) File.Delete(ZipPath);
                if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
                if (Directory.Exists(WorkDir))
                {
                    foreach (var stale in Directory.GetDirectories(WorkDir, StaleBackupPrefix + "*"))
                        Directory.Delete(stale, recursive: true);
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to clean self-update work dir: " + ex.Message);
            }
        }

        private static void CancelActiveRequest()
        {
            _cancelRequested = true;
            GitCliRunner.KillActive();
            var request = _activeRequest;
            _activeRequest = null;
            EditorApplication.update -= TrackDownloadProgress;
            if (!IsRunning) return;

            if (request != null)
            {
                _cancelled = true;
                try { request.Abort(); }
                catch { }
                request.Dispose();
            }

            // Domain is unloading or quitting; the callback only matters for the live UI case.
            Finish(Outcome(LocalUpdateOutcome.Cancelled));
        }
    }
}

// Producer:Betsy
