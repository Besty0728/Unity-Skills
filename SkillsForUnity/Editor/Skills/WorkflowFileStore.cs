using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Content-addressed blob store of the workflow history (<see cref="WorkflowStore"/>): snapshot payloads and asset
    /// bytes, each stored under its own SHA1 hash as the name, so identical content is stored once.
    /// </summary>
    internal static class WorkflowFileStore
    {
        /// <summary>Directory holding every blob: workflow/blobs.</summary>
        public static string StoreRoot => WorkflowStore.BlobsRoot;

        /// <summary>
        /// Blobs stored within this time window are never reclaimed as "unreferenced":
        /// the caller may still be assembling the snapshot that will reference them.
        /// </summary>
        internal static readonly TimeSpan RecentWriteGrace = TimeSpan.FromMinutes(10);

        // Bytes of blobs written since the last TakeNewBlobBytes; the store measures itself once they add up.
        private static long _newBlobBytes;

        /// <summary>Extension used for blobs whose content no longer matches their own hash.</summary>
        private const string CorruptSuffix = ".corrupt";

        // Content hash of a source asset by (path, length, last write time), so snapshotting the same unchanged texture again
        // costs one stat instead of a full read. A file written within the last RacyWindow is never cached: a second write
        // inside the timestamp's resolution could keep both length and time.
        private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);
        private const int MaxFileHashCacheEntries = 1024;
        private static readonly Dictionary<string, (long length, DateTime lastWriteUtc, string hash)> _fileHashCache =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.Ordinal);
        internal static int FileHashComputationsForTests;

        /// <summary>
        /// Stores an asset file in the content-addressed store, optionally deleting the source file.
        /// The companion .meta file is addressed independently by its own content.
        /// </summary>
        /// <param name="assetPath">Project-relative asset path (e.g. "Assets/Materials/Red.mat").</param>
        /// <param name="move">If true, deletes the source file (and its meta) after storing.</param>
        /// <returns>SHA1 hash of the file content; returns null if the source file does not exist.</returns>
        public static string StoreFile(string assetPath, bool move)
        {
            return StoreFile(assetPath, move, out _);
        }

        public static string StoreFile(string assetPath, bool move, out string metaHash)
        {
            metaHash = null;
            if (!TryGetSafeAssetFullPath(assetPath, out string fullPath))
            {
                SkillsLogger.LogWarning($"[WorkflowFileStore] Unsafe or invalid asset path: {assetPath}");
                return null;
            }

            if (!File.Exists(fullPath))
                return null;

            string hash = ComputeSourceFileHash(fullPath);
            if (string.IsNullOrEmpty(hash))
                return null;

            string metaSourcePath = fullPath + ".meta";

            try
            {
                if (!StoreBlob(fullPath, hash))
                    return null;

                if (File.Exists(metaSourcePath))
                {
                    metaHash = ComputeSourceFileHash(metaSourcePath);
                    if (string.IsNullOrEmpty(metaHash) || !StoreBlob(metaSourcePath, metaHash))
                        return null;
                }

                // Only delete the source file after all required blobs have been persisted.
                if (move)
                {
                    ForgetSourceFileHash(fullPath);
                    SafeDelete(fullPath);
                    if (File.Exists(metaSourcePath))
                        SafeDelete(metaSourcePath);
                }

                return hash;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"[WorkflowFileStore] Failed to store file {assetPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores a stored file (and its independently addressed companion .meta file).
        /// </summary>
        /// <param name="hash">SHA1 hash of the stored content.</param>
        /// <param name="assetPath">Project-relative asset path to restore to.</param>
        /// <param name="removeFromStore">If true, deletes the store entry after restoring (used by the "redo create" path).</param>
        /// <returns>True if the restore succeeded.</returns>
        public static bool RestoreFile(string hash, string assetPath, bool removeFromStore)
        {
            return RestoreFile(hash, null, assetPath, removeFromStore);
        }

        public static bool RestoreFile(string hash, string metaHash, string assetPath, bool removeFromStore)
        {
            if (string.IsNullOrEmpty(hash) || !TryGetSafeAssetFullPath(assetPath, out string fullPath))
                return false;

            string hashPath = GetHashPath(hash);
            string metaHashPath = string.IsNullOrEmpty(metaHash) ? null : GetHashPath(metaHash);

            if (!File.Exists(hashPath))
                return false;

            // Verify before writing anything, so a corrupted blob never touches the project.
            if (!VerifyBlobIntegrity(hash))
                return false;
            if (metaHashPath != null && File.Exists(metaHashPath) && !VerifyBlobIntegrity(metaHash))
                return false;

            try
            {
                EnsureDirectoryExists(fullPath);
                ForgetSourceFileHash(fullPath);

                if (File.Exists(fullPath))
                    SafeDelete(fullPath);

                if (removeFromStore)
                    File.Move(hashPath, fullPath);
                else
                    File.Copy(hashPath, fullPath);

                // Restore the companion .meta file too, if present
                if (metaHashPath != null && File.Exists(metaHashPath))
                {
                    string metaDestPath = fullPath + ".meta";
                    if (File.Exists(metaDestPath))
                        SafeDelete(metaDestPath);

                    if (removeFromStore && !string.Equals(hash, metaHash, StringComparison.OrdinalIgnoreCase))
                        File.Move(metaHashPath, metaDestPath);
                    else
                        File.Copy(metaHashPath, metaDestPath);
                }

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"[WorkflowFileStore] Failed to restore file {assetPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes store entries whose hash is no longer referenced by any surviving snapshot.
        /// </summary>
        /// <param name="removedCount">Number of entries removed.</param>
        /// <param name="removedBytes">Total bytes reclaimed.</param>
        /// <param name="includeRecentWrites">
        /// Only set this when the caller can be sure the reference set is inherently complete (e.g. clearing all history);
        /// otherwise recently written blobs are preserved, see <see cref="RecentWriteGrace"/>.
        /// </param>
        public static void CollectGarbage(HashSet<string> referencedHashes, out int removedCount, out long removedBytes,
            Action<string> log = null, bool includeRecentWrites = false)
        {
            removedCount = 0;
            removedBytes = 0;

            if (!Directory.Exists(StoreRoot))
                return;

            var graceCutoff = DateTime.UtcNow - RecentWriteGrace;

            foreach (var entry in ListEntries())
            {
                if (referencedHashes.Contains(entry.hash))
                    continue;

                if (!includeRecentWrites && entry.lastWrite > graceCutoff)
                    continue;

                try
                {
                    string hashPath = GetHashPath(entry.hash);
                    if (File.Exists(hashPath))
                    {
                        removedBytes += new FileInfo(hashPath).Length;
                        SafeDelete(hashPath);
                    }

                    removedCount++;
                    log?.Invoke($"[WorkflowFileStore] Reclaimed unreferenced hash {entry.hash}");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[WorkflowFileStore] Failed to reclaim hash {entry.hash}: {ex.Message}");
                }
            }

            if (removedCount > 0)
            {
                SkillsLogger.LogWorkflow($"Reclaimed {removedCount} unreferenced store entries ({FormatBytes(removedBytes)})");
            }
        }

        /// <summary>
        /// Returns the total size of the store, in bytes.
        /// </summary>
        public static long GetStoreSizeBytes()
        {
            if (!Directory.Exists(StoreRoot))
                return 0;

            long total = 0;
            WorkflowStore.DirectoryEnumerationsForTests++;
            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* Ignore files that are locked by another process */ }
            }
            return total;
        }

        /// <summary>
        /// Lists all stored blobs, quarantined ones excluded.
        /// </summary>
        public static List<(string hash, long bytes, DateTime lastWrite)> ListEntries()
        {
            var result = new List<(string hash, long bytes, DateTime lastWrite)>();
            if (!Directory.Exists(StoreRoot))
                return result;

            WorkflowStore.DirectoryEnumerationsForTests++;
            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                // A quarantined blob is evidence of corruption and must not be reclaimed during cleanup.
                if (fileName.EndsWith(CorruptSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    result.Add((fileName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc));
                }
                catch { /* Ignore files that are locked by another process */ }
            }

            return result;
        }

        /// <summary>
        /// <see cref="ComputeFileHash"/> for a source asset about to be stored, through the (path, length, last write time)
        /// cache. Blob integrity checks never use it: they must read the bytes.
        /// </summary>
        private static string ComputeSourceFileHash(string fullPath)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (_fileHashCache.TryGetValue(fullPath, out var cached) &&
                    cached.length == info.Length && cached.lastWriteUtc == info.LastWriteTimeUtc)
                    return cached.hash;
            }
            catch (Exception)
            {
                return ComputeFileHash(fullPath);
            }

            string hash = ComputeFileHash(fullPath);
            if (hash != null && DateTime.UtcNow - info.LastWriteTimeUtc > RacyWindow)
            {
                if (_fileHashCache.Count >= MaxFileHashCacheEntries)
                    _fileHashCache.Clear();
                _fileHashCache[fullPath] = (info.Length, info.LastWriteTimeUtc, hash);
            }
            return hash;
        }

        private static void ForgetSourceFileHash(string fullPath)
        {
            _fileHashCache.Remove(fullPath);
            _fileHashCache.Remove(fullPath + ".meta");
        }

        /// <summary>
        /// Computes the SHA1 hash of a file's content.
        /// </summary>
        public static string ComputeFileHash(string fullPath)
        {
            FileHashComputationsForTests++;
            try
            {
                using (var sha1 = SHA1.Create())
                using (var stream = File.OpenRead(fullPath))
                {
                    byte[] hash = sha1.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"[WorkflowFileStore] Failed to compute hash for {fullPath}: {ex.Message}");
                return null;
            }
        }

        public static string StoreBytes(byte[] bytes)
        {
            if (bytes == null) return null;

            string hash = HashBytes(bytes);
            string destinationPath = GetHashPath(hash);
            if (File.Exists(destinationPath))
            {
                TouchBlob(destinationPath);
                return hash;
            }

            EnsureStoreDirectory();
            string tmpPath = destinationPath + ".tmp";
            try
            {
                File.WriteAllBytes(tmpPath, bytes);
                if (!File.Exists(destinationPath))
                {
                    File.Move(tmpPath, destinationPath);
                    _newBlobBytes += bytes.Length;
                }
                else
                    SafeDelete(tmpPath);
                return hash;
            }
            catch (Exception ex)
            {
                SafeDelete(tmpPath);
                SkillsLogger.LogError($"[WorkflowFileStore] Failed to store byte blob: {ex.Message}");
                return null;
            }
        }

        public static bool BlobExists(string hash)
        {
            return !string.IsNullOrEmpty(hash) && File.Exists(GetHashPath(hash));
        }

        public static bool RestoreBlob(string hash, string destinationPath, bool removeFromStore = false)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(destinationPath))
                return false;

            string sourcePath = GetHashPath(hash);
            if (!File.Exists(sourcePath))
                return false;

            if (!VerifyBlobIntegrity(hash))
                return false;

            try
            {
                EnsureDirectoryExists(destinationPath);
                if (File.Exists(destinationPath))
                    SafeDelete(destinationPath);

                if (removeFromStore)
                    File.Move(sourcePath, destinationPath);
                else
                    File.Copy(sourcePath, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"[WorkflowFileStore] Failed to restore blob to {destinationPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads a blob, verified against its name; a blob whose bytes no longer match is quarantined, as on restore.
        /// <paramref name="error"/> says why nothing was read.
        /// </summary>
        internal static bool TryReadBlob(string hash, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            string path = string.IsNullOrEmpty(hash) ? null : GetHashPath(hash);
            if (path == null || !File.Exists(path))
            {
                error = $"Backup blob {hash ?? "<none>"} is missing from the workflow store, so this change cannot be restored.";
                return false;
            }

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                error = $"Backup blob {hash} could not be read: {ex.Message}";
                return false;
            }

            string actual = HashBytes(bytes);
            if (string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                return true;
            bytes = null;
            Quarantine(hash, actual);
            error = LastIntegrityError;
            return false;
        }

        /// <summary>Bytes of blobs written since the previous call.</summary>
        internal static long TakeNewBlobBytes()
        {
            long bytes = _newBlobBytes;
            _newBlobBytes = 0;
            return bytes;
        }

        private static string HashBytes(byte[] bytes)
        {
            using (var sha1 = SHA1.Create())
                return BitConverter.ToString(sha1.ComputeHash(bytes)).Replace("-", "").ToUpperInvariant();
        }

        /// <summary>
        /// Resolves a project-relative asset path to an absolute path, with safety validation.
        /// </summary>
        public static bool TryGetSafeAssetFullPath(string assetPath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (Validate.SafePath(assetPath, "assetPath") is object) return false;

            fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            return true;
        }

        private static string GetHashPath(string hash)
        {
            return Path.Combine(StoreRoot, hash.ToUpperInvariant());
        }

        private static bool StoreBlob(string sourcePath, string hash)
        {
            string hashPath = GetHashPath(hash);
            if (File.Exists(hashPath))
            {
                TouchBlob(hashPath);
                return true;
            }

            EnsureStoreDirectory();
            WriteAtomically(hashPath, sourcePath);
            TouchBlob(hashPath);
            if (!File.Exists(hashPath))
                return false;
            _newBlobBytes += new FileInfo(hashPath).Length;
            return true;
        }

        /// <summary>
        /// Stamps the blob with the "stored at" timestamp. File.Copy carries over the source asset's timestamp,
        /// but cleanup measures how long an entry has sat in the store, not how old the asset was at backup time.
        /// </summary>
        private static void TouchBlob(string hashPath)
        {
            try
            {
                if (File.Exists(hashPath))
                    File.SetLastWriteTimeUtc(hashPath, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[WorkflowFileStore] Failed to stamp blob {Path.GetFileName(hashPath)}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reason the most recent restore was refused; null after a clean completion. The undo path reports
        /// failures per-snapshot, while an integrity abort is otherwise indistinguishable from any other failure
        /// (both surface as "Unknown failure") — yet that's exactly when the caller most needs to know "the problem is in the backup, not the target".
        /// </summary>
        internal static string LastIntegrityError { get; private set; }

        internal static void ClearLastIntegrityError() => LastIntegrityError = null;

        /// <summary>
        /// Confirms a stored blob's content still hashes to the name it was filed under; quarantines it as
        /// "&lt;hash&gt;.corrupt" if not.
        /// </summary>
        private static bool VerifyBlobIntegrity(string hash)
        {
            string actual = ComputeFileHash(GetHashPath(hash));
            if (!string.IsNullOrEmpty(actual) && string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                return true;
            Quarantine(hash, actual);
            return false;
        }

        private static void Quarantine(string hash, string actual)
        {
            string hashPath = GetHashPath(hash);
            string quarantinePath = hashPath + CorruptSuffix;
            try
            {
                SafeDelete(quarantinePath);
                File.Move(hashPath, quarantinePath);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowFileStore] Failed to quarantine corrupt blob {hash}: {ex.Message}");
            }

            LastIntegrityError =
                $"Backup blob {hash} is damaged (contents hash to {actual ?? "unreadable"}); it was quarantined as " +
                $"{Path.GetFileName(quarantinePath)} and the restore was aborted rather than writing bad data.";

            SkillsLogger.LogError($"[WorkflowFileStore] {LastIntegrityError}");
        }

        private static void EnsureStoreDirectory()
        {
            if (!Directory.Exists(StoreRoot))
                Directory.CreateDirectory(StoreRoot);
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void WriteAtomically(string destinationPath, string sourcePath)
        {
            string tmpPath = destinationPath + ".tmp";
            try
            {
                File.Copy(sourcePath, tmpPath, overwrite: true);
                if (File.Exists(destinationPath))
                    SafeDelete(destinationPath);
                File.Move(tmpPath, destinationPath);
            }
            catch
            {
                if (File.Exists(tmpPath))
                    SafeDelete(tmpPath);
                throw;
            }
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowFileStore] Failed to delete {path}: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }

    /// <summary>
    /// Registry for restoring setting-type snapshots that can't go through the regular asset/scene path.
    /// Settings are identified by key and restored from JSON-encoded prior values.
    /// </summary>
    internal static class WorkflowSettingRestorerRegistry
    {
        private sealed class Handlers
        {
            public Func<string> Getter;          // Reads the current value as a JSON string (null if not provided).
            public Func<string, bool> Restorer;  // Applies a JSON-encoded value; returns true on success.
        }

        private static readonly Dictionary<string, Handlers> _handlers =
            new Dictionary<string, Handlers>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a restorer (setter) for a setting key. This is the legacy overload with no getter;
        /// keys registered this way cannot capture the redo-side value.
        /// </summary>
        public static void Register(string key, Func<string, bool> restorer)
        {
            if (string.IsNullOrEmpty(key) || restorer == null)
                return;

            _handlers[key] = new Handlers { Getter = null, Restorer = restorer };
        }

        /// <summary>
        /// Registers a getter/setter pair for a setting key. The getter returns the current value as a JSON string
        /// (used to capture the redo value on undo); the setter applies a JSON-encoded value and returns true on success.
        /// </summary>
        public static void Register(string key, Func<string> getter, Func<string, bool> setter)
        {
            if (string.IsNullOrEmpty(key) || setter == null)
                return;

            _handlers[key] = new Handlers { Getter = getter, Restorer = setter };
        }

        /// <summary>
        /// Unregisters a setting handler.
        /// </summary>
        public static void Unregister(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _handlers.Remove(key);
        }

        /// <summary>
        /// Returns true if a handler is registered for this key.
        /// </summary>
        public static bool IsRegistered(string key)
        {
            return !string.IsNullOrEmpty(key) && _handlers.ContainsKey(key);
        }

        /// <summary>
        /// Reads a setting's current value as a JSON string using its registered getter.
        /// Returns null if no getter is registered for the key, or if the getter throws.
        /// </summary>
        public static string TryGetCurrentValue(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            if (!_handlers.TryGetValue(key, out var handlers) || handlers?.Getter == null)
                return null;

            try
            {
                return handlers.Getter();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowSettingRestorerRegistry] Getter for '{key}' threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Attempts to restore a setting from a JSON-encoded value.
        /// </summary>
        public static bool TryRestore(string key, string valueJson)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            if (!_handlers.TryGetValue(key, out var handlers) || handlers?.Restorer == null)
                return false;

            try
            {
                return handlers.Restorer(valueJson);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[WorkflowSettingRestorerRegistry] Restorer for '{key}' threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears all registered handlers; primarily for test use.
        /// </summary>
        public static void Clear()
        {
            _handlers.Clear();
        }
    }
}

// Producer:Betsy
