using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// 把本 Unity 实例注册到一个全局文件，供客户端发现当前活跃的 Unity 实例及其端口。
    /// </summary>
    [InitializeOnLoad]
    public static class RegistryService
    {
        private static readonly string GlobalConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity_skills");
        private static readonly string RegistryFile = Path.Combine(GlobalConfigDir, "registry.json");

        public static string InstanceId { get; private set; }
        public static string ProjectName { get; private set; }
        public static string ProjectPath { get; private set; }

        static RegistryService()
        {
            try
            {
                ProjectName = Application.productName;
                ProjectPath = Directory.GetParent(Application.dataPath).FullName;

                var pathHash = ComputeStableHash(ProjectPath);
                var cleanName = System.Text.RegularExpressions.Regex.Replace(ProjectName, "[^a-zA-Z0-9]", "");
                InstanceId = $"{cleanName}_{pathHash}";

                if (!Directory.Exists(GlobalConfigDir))
                    Directory.CreateDirectory(GlobalConfigDir);

                EditorApplication.quitting += Unregister;
                // 程序集重载时的清理由 SkillsHttpServer 调用 Stop() 负责，此处不重复挂钩
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] RegistryService init failed: " + ex);
                InstanceId = InstanceId ?? "unknown_0";
                ProjectName = ProjectName ?? "unknown";
                ProjectPath = ProjectPath ?? string.Empty;
            }
        }

        public static void Register(int port)
        {
            try
            {
                AtomicReadModifyWrite(registry =>
                {
                    UnityCliService.GetRegistryBinding(out var cliBound, out var cliPath);
                    var info = new InstanceInfo
                    {
                        id = InstanceId,
                        name = ProjectName,
                        path = ProjectPath,
                        port = port,
                        pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        last_active = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        unityVersion = Application.unityVersion,
                        cliBound = cliBound,
                        cliPath = cliPath
                    };

                    registry[ProjectPath] = info;

                    // 清理陈旧条目：心跳超过 120 秒，或进程已死
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var keysToRemove = registry
                        .Where(k => k.Value.pid != info.pid &&
                            (now - k.Value.last_active > 120 || !IsProcessAlive(k.Value.pid)))
                        .Select(k => k.Key).ToList();
                    foreach (var key in keysToRemove)
                        registry.Remove(key);
                });
                SkillsLogger.LogVerbose($"Registered instance '{InstanceId}' on port {port}");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to register instance: {ex.Message}");
            }
        }

        /// <summary>
        /// Unity CLI 绑定变化时同步注册表条目（面板 Bind/Unbind 调用）。
        /// 条目尚不存在（服务器未启动过）时不落任何数据 —— Register 时会带上最新绑定状态。
        /// </summary>
        public static void UpdateCliBinding(bool bound, string cliPath)
        {
            try
            {
                AtomicReadModifyWrite(registry =>
                {
                    if (registry.TryGetValue(ProjectPath, out var existing))
                    {
                        existing.cliBound = bound;
                        existing.cliPath = bound ? cliPath : null;
                    }
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to sync CLI binding to registry: {ex.Message}");
            }
        }

        public static void Unregister()
        {
            try
            {
                if (!File.Exists(RegistryFile)) return;

                AtomicReadModifyWrite(registry =>
                {
                    registry.Remove(ProjectPath);
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to unregister: {ex.Message}");
            }
        }

        private static int _heartbeatCount = 0;

        public static void Heartbeat(int port)
        {
            try
            {
                _heartbeatCount++;
                bool doStaleCleanup = _heartbeatCount % 5 == 0;

                AtomicReadModifyWrite(registry =>
                {
                    if (registry.TryGetValue(ProjectPath, out var existing))
                    {
                        existing.last_active = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        existing.port = port;
                    }
                    else
                    {
                        // 心跳早于 Register 到达，此时需要写入完整条目
                        UnityCliService.GetRegistryBinding(out var cliBound, out var cliPath);
                        registry[ProjectPath] = new InstanceInfo
                        {
                            id = InstanceId,
                            name = ProjectName,
                            path = ProjectPath,
                            port = port,
                            pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                            last_active = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            unityVersion = Application.unityVersion,
                            cliBound = cliBound,
                            cliPath = cliPath
                        };
                    }

                    if (doStaleCleanup)
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var myPid = System.Diagnostics.Process.GetCurrentProcess().Id;
                        var keysToRemove = registry
                            .Where(k => k.Value.pid != myPid &&
                                (now - k.Value.last_active > 120 || !IsProcessAlive(k.Value.pid)))
                            .Select(k => k.Key).ToList();
                        foreach (var key in keysToRemove)
                            registry.Remove(key);
                    }
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to heartbeat: {ex.Message}");
            }
        }

        /// <summary>
        /// 带跨进程文件锁的原子读-改-写：以 FileStream(FileShare.None) 互斥，
        /// 并借助 .tmp 文件保证写入的原子性。
        /// </summary>
        private static void AtomicReadModifyWrite(Action<Dictionary<string, InstanceInfo>> modifier)
        {
            const int maxRetries = 5;
            const int retryDelayMs = 100;

            // 从中断的写入中恢复：.tmp 存在而主文件缺失或为空时，用 .tmp 还原
            var tmpFile = RegistryFile + ".tmp";
            if (File.Exists(tmpFile) && (!File.Exists(RegistryFile) || new FileInfo(RegistryFile).Length == 0))
            {
                try { File.Copy(tmpFile, RegistryFile, true); File.Delete(tmpFile); } catch { }
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                FileStream lockStream = null;
                try
                {
                    lockStream = new FileStream(
                        RegistryFile,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);

                    var registry = new Dictionary<string, InstanceInfo>();
                    if (lockStream.Length > 0)
                    {
                        using (var reader = new StreamReader(lockStream, Encoding.UTF8, true, 4096, leaveOpen: true))
                        {
                            var json = reader.ReadToEnd();
                            registry = JsonConvert.DeserializeObject<Dictionary<string, InstanceInfo>>(json)
                                       ?? new Dictionary<string, InstanceInfo>();
                        }
                    }

                    modifier(registry);

                    // 先写 .tmp，再整体替换，保证原子性
                    var newJson = JsonConvert.SerializeObject(registry, Formatting.Indented);
                    File.WriteAllText(tmpFile, newJson, Encoding.UTF8);

                    lockStream.SetLength(0);
                    lockStream.Seek(0, SeekOrigin.Begin);
                    var bytes = Encoding.UTF8.GetBytes(newJson);
                    lockStream.Write(bytes, 0, bytes.Length);
                    lockStream.Flush();

                    try { File.Delete(tmpFile); } catch { }

                    return;
                }
                catch (IOException) when (attempt < maxRetries - 1)
                {
                    // 文件被其他进程占用，退避重试
                    System.Threading.Thread.Sleep(retryDelayMs * (attempt + 1));
                }
                finally
                {
                    lockStream?.Dispose();
                }
            }

            throw new IOException($"Failed to acquire lock on registry file after {maxRetries} attempts");
        }

        /// <summary>
        /// 用 SHA256 的前 4 字节算出稳定哈希字符串。
        /// 与 GetHashCode() 不同，它跨进程、跨运行时都是确定的。
        /// </summary>
        private static string ComputeStableHash(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", "");
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    return proc != null;
            }
            catch { return false; }
        }

        [Serializable]
        public class InstanceInfo
        {
            public string id;
            public string name;
            public string path;
            public int port;
            public int pid;
            public long last_active;
            public string unityVersion;
            // Unity CLI 绑定：AI 客户端跨项目发现"可冷启动"的实例用。
            // 详情契约在 <project>/Library/UnitySkills/cli_config.json。
            public bool cliBound;
            public string cliPath;
        }
    }
}

// Producer:Betsy
