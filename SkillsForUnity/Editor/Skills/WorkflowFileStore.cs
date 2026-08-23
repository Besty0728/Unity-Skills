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
    /// 工作流快照用的内容寻址文件库。
    /// 每个文件 blob 以自身 SHA1 哈希为名存放，内容相同者自动去重。
    /// </summary>
    internal static class WorkflowFileStore
    {
        /// <summary>
        /// 所有工作流文件 blob 的根目录。
        /// </summary>
        internal static string OverrideStoreRootForTests;
        public static string StoreRoot => OverrideStoreRootForTests ??
            Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/UnitySkills/workflow_files"));

        /// <summary>
        /// 在此时间窗内入库的 blob 绝不会被当作"无引用"回收：
        /// 调用方可能仍在拼装那条将要引用它们的快照。
        /// </summary>
        private static readonly TimeSpan RecentWriteGrace = TimeSpan.FromMinutes(10);

        /// <summary>内容与自身哈希不再吻合的 blob 所用的扩展名。</summary>
        private const string CorruptSuffix = ".corrupt";

        /// <summary>
        /// 把资产文件存入内容寻址库，可选地删除源文件。
        /// 配套的 .meta 文件按自身内容独立寻址。
        /// </summary>
        /// <param name="assetPath">项目相对资产路径（如 "Assets/Materials/Red.mat"）。</param>
        /// <param name="move">为 true 时，入库后删除源文件（及其 meta）。</param>
        /// <returns>文件内容的 SHA1 哈希；源文件不存在时返回 null。</returns>
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

                // 只有在所有必需的 blob 都已落盘之后才删除源文件。
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
        /// 还原一个已入库的文件（及其独立寻址的 .meta 配套文件）。
        /// </summary>
        /// <param name="hash">入库内容的 SHA1 哈希。</param>
        /// <param name="assetPath">要还原到的项目相对资产路径。</param>
        /// <param name="removeFromStore">为 true 时还原后删除库中条目（用于"重做创建"路径）。</param>
        /// <returns>还原成功返回 true。</returns>
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

            // 在写入任何东西之前先校验，使损坏的 blob 不会碰到项目。
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

                // 存在配套 .meta 时一并还原
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
        /// 删除哈希已不被任何残留快照引用的库条目。
        /// </summary>
        /// <param name="removedCount">被删除的主哈希条目数。</param>
        /// <param name="removedBytes">回收的总字节数（含 .meta 附属文件）。</param>
        /// <param name="includeRecentWrites">
        /// 仅当调用方能确信引用集天然完整时才置位（例如清空全部历史）；
        /// 否则刚写入的 blob 会被保留，见 <see cref="RecentWriteGrace"/>。
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
        /// 返回文件库总大小（字节）。
        /// </summary>
        public static long GetStoreSizeBytes()
        {
            if (!Directory.Exists(StoreRoot))
                return 0;

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(StoreRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 忽略被占用的文件 */ }
            }
            return total;
        }

        /// <summary>
        /// 列出所有已入库的文件条目（只含主 blob，不含 .meta 附属文件）。
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
                // 被隔离的 blob 是损坏证据，清理时不得回收。
                if (fileName.EndsWith(CorruptSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(file);
                    result.Add((fileName.ToUpperInvariant(), info.Length, info.LastWriteTimeUtc));
                }
                catch { /* 忽略被占用的文件 */ }
            }

            return result;
        }

        /// <summary>
        /// 先清除早于 <paramref name="olderThan"/> 的库条目，必要时再从最旧的开始删，
        /// 直到总大小低于 <paramref name="maxTotalBytes"/>。
        /// 仍被保留历史引用的 blob 绝不删除。
        /// </summary>
        /// <returns>被删除的主哈希条目数。</returns>
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

                // 与 CollectGarbage 相同的在途保护：刚写入的 blob 可能属于尚未并入历史引用集的任务。
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
        /// 计算文件内容的 SHA1 哈希。
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
        /// 把项目相对资产路径解析为绝对路径，并做安全性校验。
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
        /// 给 blob 打上"入库时刻"的时间戳。File.Copy 会把源资产的时间戳带过来，
        /// 但清理逻辑衡量的是条目入库多久，而不是备份时那个资产本身有多旧。
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
        /// 最近一次还原被拒执行的原因；上次干净完成时为 null。
        /// 撤销路径按快照逐条上报失败，而完整性中止在其他情况下与任何失败都无法区分（都是 "Unknown failure"）——
        /// 可偏偏这正是调用方最需要知道"问题在备份本身、不在目标"的那一种情形。
        /// </summary>
        internal static string LastIntegrityError { get; private set; }

        internal static void ClearLastIntegrityError() => LastIntegrityError = null;

        /// <summary>
        /// 确认入库 blob 的内容仍散列成它所归档的那个名字，不符则隔离为 "&lt;hash&gt;.corrupt"。
        /// 遗留的 "&lt;hash&gt;.meta" 附属文件以主文件的哈希命名而非自身哈希，
        /// 故此处永不校验它们。
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
    /// 用于还原那些走不了常规资产/场景路径的设置类快照的注册表。
    /// 设置以 key 标识，从 JSON 编码的旧值还原。
    /// </summary>
    internal static class WorkflowSettingRestorerRegistry
    {
        private sealed class Handlers
        {
            public Func<string> Getter;          // 以 JSON 字符串读取当前值（未提供时为 null）。
            public Func<string, bool> Restorer;  // 应用 JSON 编码的值，成功返回 true。
        }

        private static readonly Dictionary<string, Handlers> _handlers =
            new Dictionary<string, Handlers>(StringComparer.Ordinal);

        /// <summary>
        /// 为某个设置 key 注册还原器（setter）。这是没有 getter 的遗留重载；
        /// 用此方式注册的 key 无法捕获重做侧的值。
        /// </summary>
        public static void Register(string key, Func<string, bool> restorer)
        {
            if (string.IsNullOrEmpty(key) || restorer == null)
                return;

            _handlers[key] = new Handlers { Getter = null, Restorer = restorer };
        }

        /// <summary>
        /// 为某个设置 key 注册 getter/setter 对。getter 以 JSON 字符串返回当前值
        /// （撤销时用它捕获重做值）；setter 应用 JSON 编码的值，成功返回 true。
        /// </summary>
        public static void Register(string key, Func<string> getter, Func<string, bool> setter)
        {
            if (string.IsNullOrEmpty(key) || setter == null)
                return;

            _handlers[key] = new Handlers { Getter = getter, Restorer = setter };
        }

        /// <summary>
        /// 注销某个设置处理器。
        /// </summary>
        public static void Unregister(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;

            _handlers.Remove(key);
        }

        /// <summary>
        /// 该 key 已注册处理器时返回 true。
        /// </summary>
        public static bool IsRegistered(string key)
        {
            return !string.IsNullOrEmpty(key) && _handlers.ContainsKey(key);
        }

        /// <summary>
        /// 用已注册的 getter 以 JSON 字符串读取某设置的当前值。
        /// 该 key 未注册 getter 或 getter 抛异常时返回 null。
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
        /// 尝试从 JSON 编码的值还原某项设置。
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
        /// 清空所有已注册的处理器，主要供测试使用。
        /// </summary>
        public static void Clear()
        {
            _handlers.Clear();
        }
    }
}

// Producer:Betsy
