using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Content-addressed file store for workflow snapshots.
    /// Each file blob is stored under its own SHA1 hash as the name; identical content is automatically deduplicated.
    /// </summary>
    internal static class WorkflowFileStore
    {
        /// <summary>
        /// Root directory for all workflow file blobs.
        /// </summary>
        internal static string OverrideStoreRootForTests;
        public static string StoreRoot => OverrideStoreRootForTests ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_files"));

        /// <summary>
        /// Blobs stored within this time window are never reclaimed as "unreferenced":
        /// the caller may still be assembling the snapshot that will reference them.
        /// </summary>
        private static readonly TimeSpan RecentWriteGrace = TimeSpan.FromMinutes(10);

        /// <summary>Extension used for blobs whose content no longer matches their own hash.</summary>
        private const string CorruptSuffix = ".corrupt";

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

            string hash = ComputeFileHash(fullPath);
            if (string.IsNullOrEmpty(hash))
                return null;

            string metaSourcePath = fullPath + ".meta";

            try
            {
                if (!StoreBlob(fullPath, hash))
                    return null;

                if (File.Exists(metaSourcePath))
                {
                    metaHash = ComputeFileHash(metaSourcePath);
                    if (string.IsNullOrEmpty(metaHash) || !StoreBlob(metaSourcePath, metaHash))
                        return null;
                }

                // Only delete the source file after all required blobs have been persisted.
                if (move)
                {
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
            string metaHashPath = !string.IsNullOrEmpty(metaHash)
                ? GetHashPath(metaHash)
                : GetLegacyMetaHashPath(hash);

            if (!File.Exists(hashPath))
                return false;

            // Verify before writing anything, so a corrupted blob never touches the project.
            if (!VerifyBlobIntegrity(hash))
                return false;
            if (!string.IsNullOrEmpty(metaHash) && File.Exists(metaHashPath) && !VerifyBlobIntegrity(metaHash))
                return false;

            try
            {
                EnsureDirectoryExists(fullPath);

                if (File.Exists(fullPath))
                    SafeDelete(fullPath);

                if (removeFromStore)
                    File.Move(hashPath, fullPath);
                else
                    File.Copy(hashPath, fullPath);

                // Restore the companion .meta file too, if present
                if (File.Exists(metaHashPath))
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
        /// <param name="removedCount">Number of primary hash entries removed.</param>
        /// <param name="removedBytes">Total bytes reclaimed (including .meta companion files).</param>
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
                    string metaHashPath = GetLegacyMetaHashPath(entry.hash);

                    if (File.Exists(hashPath))
                    {
                        removedBytes += new FileInfo(hashPath).Length;
                        SafeDelete(hashPath);
                    }
                    if (File.Exists(metaHashPath))
                    {
                        removedBytes += new FileInfo(metaHashPath).Length;
                        SafeDelete(metaHashPath);
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
            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* Ignore files that are locked by another process */ }
            }
            return total;
        }

        /// <summary>
        /// Lists all stored file entries (primary blobs only, excluding .meta companion files).
        /// </summary>
        public static List<(string hash, long bytes, DateTime lastWrite)> ListEntries()
        {
            var result = new List<(string hash, long bytes, DateTime lastWrite)>();
            if (!Directory.Exists(StoreRoot))
                return result;

            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
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
        /// First removes store entries older than <paramref name="olderThan"/>, then, if needed, deletes from
        /// oldest to newest until the total size is below <paramref name="maxTotalBytes"/>.
        /// Blobs still referenced by preserved history are never deleted.
        /// </summary>
        /// <returns>Number of primary hash entries removed.</returns>
        public static int PruneByAgeAndSize(DateTime? olderThan, long maxTotalBytes,
            HashSet<string> protectedHashes)
        {
            if (!Directory.Exists(StoreRoot))
                return 0;

            var entries = ListEntries().OrderBy(e => e.lastWrite).ToList();
            long totalBytes = GetStoreSizeBytes();
            int removed = 0;
            protectedHashes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (protectedHashes.Contains(entry.hash))
                    continue;

                // Same in-flight protection as CollectGarbage: a recently written blob may belong to a task not yet folded into the history reference set.
                bool recentWrite = entry.lastWrite >= DateTime.UtcNow - RecentWriteGrace;
                bool tooOld = olderThan.HasValue && entry.lastWrite < olderThan.Value;
                bool tooBig = maxTotalBytes > 0 && totalBytes > maxTotalBytes;
                if (recentWrite || (!tooOld && !tooBig))
                    continue;

                try
                {
                    string hashPath = GetHashPath(entry.hash);
                    string metaHashPath = GetLegacyMetaHashPath(entry.hash);

                    if (File.Exists(hashPath))
                    {
                        totalBytes -= new FileInfo(hashPath).Length;
                        SafeDelete(hashPath);
                    }
                    if (File.Exists(metaHashPath))
                    {
                        totalBytes -= new FileInfo(metaHashPath).Length;
                        SafeDelete(metaHashPath);
                    }

                    removed++;
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"[WorkflowFileStore] Failed to prune {entry.hash}: {ex.Message}");
                }
            }

            if (removed > 0)
            {
                SkillsLogger.LogWorkflow($"Pruned {removed} store entries; remaining size {FormatBytes(totalBytes)}");
            }
            return removed;
        }

        /// <summary>
        /// Computes the SHA1 hash of a file's content.
        /// </summary>
        public static string ComputeFileHash(string fullPath)
        {
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

            string hash;
            using (var sha1 = SHA1.Create())
            {
                hash = BitConverter.ToString(sha1.ComputeHash(bytes)).Replace("-", "").ToUpperInvariant();
            }

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
                    File.Move(tmpPath, destinationPath);
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

        public static string MigrateLegacyMetaHash(string fileHash)
        {
            if (string.IsNullOrEmpty(fileHash)) return null;
            string legacyPath = GetLegacyMetaHashPath(fileHash);
            if (!File.Exists(legacyPath)) return null;

            string metaHash = ComputeFileHash(legacyPath);
            return !string.IsNullOrEmpty(metaHash) && StoreBlob(legacyPath, metaHash)
                ? metaHash
                : null;
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

        private static string GetLegacyMetaHashPath(string hash)
        {
            return Path.Combine(StoreRoot, hash.ToUpperInvariant() + ".meta");
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
            return File.Exists(hashPath);
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
        /// "&lt;hash&gt;.corrupt" if not. Legacy "&lt;hash&gt;.meta" companion files are named after the primary
        /// file's hash rather than their own, so they are never verified here.
        /// </summary>
        private static bool VerifyBlobIntegrity(string hash)
        {
            string hashPath = GetHashPath(hash);
            string actual = ComputeFileHash(hashPath);
            if (!string.IsNullOrEmpty(actual) && string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                return true;

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
            return false;
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
