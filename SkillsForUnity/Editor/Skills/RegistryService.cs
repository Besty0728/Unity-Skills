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
    /// Registers this Unity instance in a global file so clients can discover active Unity instances and their ports.
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
                // Cleanup on assembly reload is handled by SkillsHttpServer calling Stop(); no duplicate hook is registered here
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

                    // Clean up stale entries: heartbeat older than 120 seconds, or the process is dead
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
        /// Syncs the registry entry when the Unity CLI binding changes (called by the panel's Bind/Unbind).
        /// If the entry doesn't exist yet (server never started), no data is written -- Register will carry the latest binding state.
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
                        // The heartbeat arrived before Register, so a full entry needs to be written here
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
        /// Atomic read-modify-write with a cross-process file lock: mutual exclusion via FileStream(FileShare.None),
        /// with atomicity of the write itself guaranteed by a .tmp file.
        /// </summary>
        private static void AtomicReadModifyWrite(Action<Dictionary<string, InstanceInfo>> modifier)
        {
            const int maxRetries = 5;
            const int retryDelayMs = 100;

            // Recovers from an interrupted write: if .tmp exists while the main file is missing or empty, restore from .tmp
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

                    // Write .tmp first, then swap it in wholesale, to guarantee atomicity
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
                    // File is held by another process; back off and retry
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
        /// Computes a stable hash string from the first 4 bytes of SHA256.
        /// Unlike GetHashCode(), it is deterministic across processes and across runtimes.
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
            // Unity CLI binding: used by AI clients to discover "cold-startable" instances across projects.
            // The detailed contract lives in <project>/Library/UnitySkills/cli_config.json.
            public bool cliBound;
            public string cliPath;
        }
    }
}

// Producer:Betsy
