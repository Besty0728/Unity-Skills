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
    /// Stores asset file bytes by SHA1 hash, deduplicating identical contents.
    /// Also persists companion .meta files as &lt;hash&gt;.meta.
    /// </summary>
    internal static class WorkflowFileStore
    {
        /// <summary>
        /// Root directory for all stored workflow file blobs.
        /// </summary>
        public static string StoreRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_files"));

        /// <summary>
        /// Stores an asset file in the content-addressed store and optionally removes the source.
        /// Also moves the companion .meta file if present.
        /// </summary>
        /// <param name="assetPath">Project-relative asset path (e.g., "Assets/Materials/Red.mat").</param>
        /// <param name="move">If true, deletes the source file (and meta) after storing.</param>
        /// <returns>The SHA1 hash of the file contents, or null if the source does not exist.</returns>
        public static string StoreFile(string assetPath, bool move)
        {
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

            string hashPath = GetHashPath(hash);
            string metaHashPath = GetMetaHashPath(hash);
            string metaSourcePath = fullPath + ".meta";

            try
            {
                // Store main file if not already present (deduplication)
                if (!File.Exists(hashPath))
                {
                    EnsureStoreDirectory();
                    WriteAtomically(hashPath, fullPath);
                }

                // Store companion .meta if present
                if (File.Exists(metaSourcePath) && !File.Exists(metaHashPath))
                {
                    WriteAtomically(metaHashPath, metaSourcePath);
                }

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
        /// Restores a stored file (and its .meta companion) to the given asset path.
        /// </summary>
        /// <param name="hash">SHA1 hash of the stored contents.</param>
        /// <param name="assetPath">Project-relative asset path to restore to.</param>
        /// <param name="removeFromStore">If true, removes the store entry after restoring (used for redo-created paths).</param>
        /// <returns>True if the file was restored.</returns>
        public static bool RestoreFile(string hash, string assetPath, bool removeFromStore)
        {
            if (string.IsNullOrEmpty(hash) || !TryGetSafeAssetFullPath(assetPath, out string fullPath))
                return false;

            string hashPath = GetHashPath(hash);
            string metaHashPath = GetMetaHashPath(hash);

            if (!File.Exists(hashPath))
                return false;

            try
            {
                EnsureDirectoryExists(fullPath);

                // Overwrite destination if it already exists
                if (File.Exists(fullPath))
                    SafeDelete(fullPath);

                if (removeFromStore)
                    File.Move(hashPath, fullPath);
                else
                    File.Copy(hashPath, fullPath);

                // Restore .meta companion if present
                if (File.Exists(metaHashPath))
                {
                    string metaDestPath = fullPath + ".meta";
                    if (File.Exists(metaDestPath))
                        SafeDelete(metaDestPath);

                    if (removeFromStore)
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
        /// Removes store entries whose hashes are not referenced by any remaining snapshot.
        /// </summary>
        /// <param name="removedCount">Number of main hash entries removed.</param>
        /// <param name="removedBytes">Total bytes reclaimed (including .meta sidecars).</param>
        public static void CollectGarbage(HashSet<string> referencedHashes, out int removedCount, out long removedBytes, Action<string> log = null)
        {
            removedCount = 0;
            removedBytes = 0;

            if (!Directory.Exists(StoreRoot))
                return;

            foreach (var entry in ListEntries())
            {
                if (referencedHashes.Contains(entry.hash))
                    continue;

                try
                {
                    string hashPath = GetHashPath(entry.hash);
                    string metaHashPath = GetMetaHashPath(entry.hash);

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
        /// Returns the total size of the file store in bytes.
        /// </summary>
        public static long GetStoreSizeBytes()
        {
            if (!Directory.Exists(StoreRoot))
                return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* ignore locked files */ }
            }
            return total;
        }

        /// <summary>
        /// Lists all stored file entries (main blobs only, not .meta sidecars).
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

                try
                {
                    var info = new FileInfo(file);
                    result.Add((fileName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc));
                }
                catch { /* ignore locked files */ }
            }

            return result;
        }

        /// <summary>
        /// Prunes store entries older than <paramref name="olderThan"/u003e, then if necessary removes oldest
        /// entries until the total size is below <paramref name="maxTotalBytes"/u003e.
        /// </summary>
        /// <returns>Number of main hash entries removed.</returns>
        public static int PruneByAgeAndSize(DateTime olderThan, long maxTotalBytes)
        {
            if (!Directory.Exists(StoreRoot))
                return 0;

            var entries = ListEntries().OrderBy(e => e.lastWrite).ToList();
            long totalBytes = entries.Sum(e => e.bytes);
            int removed = 0;

            foreach (var entry in entries)
            {
                bool tooOld = entry.lastWrite < olderThan;
                bool tooBig = totalBytes > maxTotalBytes;
                if (!tooOld && !tooBig)
                    continue;

                try
                {
                    string hashPath = GetHashPath(entry.hash);
                    string metaHashPath = GetMetaHashPath(entry.hash);

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
        /// Computes the SHA1 hash of a file's contents.
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

        /// <summary>
        /// Resolves a project-relative asset path to an absolute path and validates it for safety.
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

        private static string GetMetaHashPath(string hash)
        {
            return Path.Combine(StoreRoot, hash.ToUpperInvariant() + ".meta");
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
    /// Registry for restoring setting snapshots that cannot be recovered via normal asset/scene paths.
    /// Settings are identified by a key and restored from a JSON-encoded old value.
    /// </summary>
    internal static class WorkflowSettingRestorerRegistry
    {
        private sealed class Handlers
        {
            public Func<string> Getter;          // Reads current value as a JSON string (null if not supplied).
            public Func<string, bool> Restorer;  // Applies a JSON-encoded value; returns true on success.
        }

        private static readonly Dictionary<string, Handlers> _handlers =
            new Dictionary<string, Handlers>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a restorer (setter) for a setting key. Legacy overload without a getter;
        /// redo-side value capture is unavailable for keys registered this way.
        /// </summary>
        public static void Register(string key, Func<string, bool> restorer)
        {
            if (string.IsNullOrEmpty(key) || restorer == null)
                return;

            _handlers[key] = new Handlers { Getter = null, Restorer = restorer };
        }

        /// <summary>
        /// Registers a getter/setter pair for a setting key. The getter returns the current
        /// value as a JSON string (used to capture the redo value during undo); the setter
        /// applies a JSON-encoded value and returns true on success.
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
        /// Returns true if a handler is registered for the key.
        /// </summary>
        public static bool IsRegistered(string key)
        {
            return !string.IsNullOrEmpty(key) && _handlers.ContainsKey(key);
        }

        /// <summary>
        /// Reads the current value of a setting as a JSON string using its registered getter.
        /// Returns null if no getter is registered for the key or the getter throws.
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
        /// Attempts to restore a setting from its JSON-encoded value.
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
        /// Clears all registered handlers. Used primarily in tests.
        /// </summary>
        public static void Clear()
        {
            _handlers.Clear();
        }
    }
}

// Producer:Betsy
