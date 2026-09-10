using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.Networking;

namespace UnitySkills
{
    /// <summary>
    /// In-place self-update for local installs (ZIP-extracted or embedded). Downloads the repo
    /// archive of the target tag, extracts only the SkillsForUnity subtree, validates it, then
    /// swaps the package directory and triggers recompilation. Same main-thread driver pattern as
    /// VersionCheckService: UnityWebRequest + completed callback, with beforeAssemblyReload /
    /// quitting cancellation hooks. System.IO.Compression stays fully qualified (no using), same
    /// as the GZipStream usage in SkillsHttpServer.
    /// </summary>
    [InitializeOnLoad]
    internal static class LocalSelfUpdateService
    {
        /// <summary>
        /// Failure texts surfaced to callers, same style as PackageManagerHelper.BusyMessage: they
        /// stay English on the wire; the editor UI matches these constants to localized reasons.
        /// </summary>
        internal const string NetworkErrorMessage = "Download failed";
        internal const string DiskErrorMessage = "Disk operation failed";
        internal const string InvalidPackageErrorMessage = "Downloaded package failed validation";
        internal const string CancelledMessage = "Cancelled by user";
        internal const string PackageRootNotFoundMessage = "Package path not found";

        private const string DownloadUrlFormat =
            "https://codeload.github.com/Besty0728/Unity-Skills/zip/refs/tags/v{0}";
        private const string ExpectedPackageName = "com.besty.unity-skills";
        private const string SelfUpdateDirName = "selfupdate";
        private const string DoneFileName = "selfupdate_done.json";

        private static string LibraryRoot =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Library"));
        private static string UnitySkillsLibraryDir => Path.Combine(LibraryRoot, "UnitySkills");
        private static string WorkDir => Path.Combine(UnitySkillsLibraryDir, SelfUpdateDirName);
        private static string ZipPath => Path.Combine(WorkDir, "pkg.zip");
        // Staging dir itself is the new package root (contains package.json / Editor/ / unity-skills~/).
        private static string StagingDir => Path.Combine(WorkDir, "staging");
        private static string DoneFilePath => Path.Combine(UnitySkillsLibraryDir, DoneFileName);

        private static UnityWebRequest _activeRequest;
        private static Action<bool, string> _callback;
        private static string _targetVersion;
        private static string _targetDir;
        private static bool _cancelled;

        internal static bool IsRunning { get; private set; }

        static LocalSelfUpdateService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += CancelActiveRequest;
            EditorApplication.quitting += CancelActiveRequest;
        }

        /// <summary>
        /// After a domain reload the fresh domain reads the marker written by the pre-reload
        /// update, logs one success line and removes it.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void CheckDoneMarkerOnLoad()
        {
            EditorApplication.delayCall += CheckDoneMarker;
        }

        private static void CheckDoneMarker()
        {
            try
            {
                if (!File.Exists(DoneFilePath)) return;

                string version = null;
                try
                {
                    version = JObject.Parse(File.ReadAllText(DoneFilePath)).Value<string>("version");
                }
                catch { /* marker is best-effort; log even if it cannot be parsed */ }

                File.Delete(DoneFilePath);
                SkillsLogger.Log(string.IsNullOrEmpty(version)
                    ? "[UnitySkills] Self-update completed successfully."
                    : $"[UnitySkills] Self-update to {version} completed successfully.");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to read self-update marker: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts the update. The callback runs on the main thread with a sentinel message on
        /// failure (see the constants above) or null on success.
        /// </summary>
        internal static void Start(string targetVersion, Action<bool, string> callback)
        {
            if (IsRunning || PackageManagerHelper.HasPendingOperation)
            {
                callback?.Invoke(false, PackageManagerHelper.BusyMessage);
                return;
            }

            if (!PackageManagerHelper.TryGetSelfPackageRoot(out var targetDir))
            {
                callback?.Invoke(false, PackageRootNotFoundMessage);
                return;
            }

            IsRunning = true;
            _callback = callback;
            _targetVersion = targetVersion;
            _targetDir = targetDir;
            _cancelled = false;

            CleanupWorkDir();
            try
            {
                Directory.CreateDirectory(WorkDir);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("Failed to prepare self-update work dir: " + ex.Message);
                Finish(false, DiskErrorMessage);
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
                Finish(false, NetworkErrorMessage);
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
                    Finish(false, CancelledMessage);
                    return;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    SkillsLogger.LogError("Self-update download failed: " + request.error);
                    Finish(false, NetworkErrorMessage);
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
        /// Synchronous disk stage after the download finished: extract the SkillsForUnity subtree,
        /// validate the staging dir, swap it into the package root and trigger recompilation.
        /// Every failure is mapped to a sentinel; nothing escapes to the caller.
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
                Finish(false, InvalidPackageErrorMessage);
                return;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("Self-update extraction failed: " + ex.Message);
                Finish(false, DiskErrorMessage);
                return;
            }

            if (!ValidateStagedPackage(StagingDir, _targetVersion))
            {
                Finish(false, InvalidPackageErrorMessage);
                return;
            }

            // LockReloadAssemblies keeps Unity from compiling the half-swapped directory tree.
            EditorApplication.LockReloadAssemblies();
            try
            {
                SwapDirectories(_targetDir, StagingDir);
                AssetDatabase.Refresh();
                try { CompilationPipeline.RequestScriptCompilation(); }
                catch { /* editor may refuse during certain lifecycle moments */ }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("Self-update directory swap failed: " + ex.Message);
                Finish(false, DiskErrorMessage);
                return;
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }

            WriteDoneMarker(_targetVersion);
            Finish(true, null);
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

        /// <summary>
        /// Replaces the target directory with the staging dir, leaving no backup (a rollback
        /// mechanism was explicitly ruled out). May throw; the caller maps exceptions to
        /// DiskErrorMessage.
        /// </summary>
        internal static void SwapDirectories(string targetDir, string stagingDir)
        {
            var parentDir = Path.GetDirectoryName(targetDir);
            var oldDir = Path.Combine(parentDir, ".unityskills-old-" + Process.GetCurrentProcess().Id);
            if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true);

            try
            {
                Directory.Move(targetDir, oldDir);
            }
            catch (IOException)
            {
                // file: specs may point at another volume, where Move cannot rename -- fall back
                // to copy + delete.
                CopyDirectory(targetDir, oldDir);
                Directory.Delete(targetDir, true);
            }

            try
            {
                Directory.Move(stagingDir, targetDir);
            }
            catch (IOException)
            {
                // Staging lives under Library/, which may be on a different volume than a
                // file: install target -- fall back to copy + delete.
                CopyDirectory(stagingDir, targetDir);
                Directory.Delete(stagingDir, true);
            }
            catch
            {
                // Best effort to restore the original directory before rethrowing.
                try { Directory.Move(oldDir, targetDir); }
                catch { /* both halves are on disk; DiskErrorMessage surfaces the failure */ }
                throw;
            }

            Directory.Delete(oldDir, true);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
            }
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
            }
        }

        private static void WriteDoneMarker(string version)
        {
            try
            {
                var marker = new JObject
                {
                    ["version"] = version,
                    ["utc"] = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(DoneFilePath, marker.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                // The swap already succeeded; a missing marker only loses the success log line.
                SkillsLogger.LogWarning("Failed to write self-update marker: " + ex.Message);
            }
        }

        private static void Finish(bool success, string message)
        {
            IsRunning = false;
            _activeRequest = null;
            _targetVersion = null;
            _targetDir = null;
            EditorApplication.update -= TrackDownloadProgress;
            EditorUtility.ClearProgressBar();
            CleanupWorkDir();

            // UnityWebRequest.completed already runs on the main thread; delayCall is not needed.
            var callback = _callback;
            _callback = null;
            callback?.Invoke(success, message);
        }

        private static void CleanupWorkDir()
        {
            try
            {
                if (Directory.Exists(WorkDir))
                    Directory.Delete(WorkDir, recursive: true);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to clean self-update work dir: " + ex.Message);
            }
        }

        private static void CancelActiveRequest()
        {
            var request = _activeRequest;
            _activeRequest = null;
            EditorApplication.update -= TrackDownloadProgress;
            if (request == null || !IsRunning) return;

            _cancelled = true;
            try { request.Abort(); }
            catch { }
            request.Dispose();

            // Domain is unloading or quitting; the callback only matters for the live UI case.
            Finish(false, CancelledMessage);
        }
    }
}

// Producer:Betsy
