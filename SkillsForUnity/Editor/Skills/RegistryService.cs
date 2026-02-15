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
    /// Handles registration of this Unity instance to a global file.
    /// Allows clients to discover active Unity instances and their ports.
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
            ProjectName = Application.productName;
            ProjectPath = Directory.GetParent(Application.dataPath).FullName;
            
            // Generate stable Instance ID based on SHA256 hash to identify this specific project instance
            // SHA256 is deterministic across processes/runtimes unlike GetHashCode()
            var pathHash = ComputeStableHash(ProjectPath);
            // Sanitize project name
            var cleanName = System.Text.RegularExpressions.Regex.Replace(ProjectName, "[^a-zA-Z0-9]", "");
            InstanceId = $"{cleanName}_{pathHash}";
            
            // Ensure config dir exists
            if (!Directory.Exists(GlobalConfigDir))
                Directory.CreateDirectory(GlobalConfigDir);
                
             // Clean up on quit
             EditorApplication.quitting += Unregister;
             // Assembly reload cleanup handled by SkillsHttpServer calling Stop()
        }

        public static void Register(int port)
        {
            try
            {
                AtomicReadModifyWrite(registry =>
                {
                    var info = new InstanceInfo
                    {
                        id = InstanceId,
                        name = ProjectName,
                        path = ProjectPath,
                        port = port,
                        pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                        last_active = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        unityVersion = Application.unityVersion
                    };

                    registry[ProjectPath] = info;

                    // Clean up stale entries (older than 120 seconds or dead process)
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

        public static void Heartbeat(int port)
        {
             // Re-register which updates the timestamp
             Register(port);
        }

        /// <summary>
        /// Atomic read-modify-write with cross-process file locking.
        /// Uses FileStream(FileShare.None) for mutual exclusion and .tmp file for atomic writes.
        /// </summary>
        private static void AtomicReadModifyWrite(Action<Dictionary<string, InstanceInfo>> modifier)
        {
            const int maxRetries = 5;
            const int retryDelayMs = 100;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                FileStream lockStream = null;
                try
                {
                    // Acquire exclusive lock on the registry file
                    lockStream = new FileStream(
                        RegistryFile,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);

                    // Read current content
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

                    // Apply modification
                    modifier(registry);

                    // Write to .tmp file first for atomic replacement
                    var tmpFile = RegistryFile + ".tmp";
                    var newJson = JsonConvert.SerializeObject(registry, Formatting.Indented);
                    File.WriteAllText(tmpFile, newJson, Encoding.UTF8);

                    // Truncate and overwrite the locked file
                    lockStream.SetLength(0);
                    lockStream.Seek(0, SeekOrigin.Begin);
                    var bytes = Encoding.UTF8.GetBytes(newJson);
                    lockStream.Write(bytes, 0, bytes.Length);
                    lockStream.Flush();

                    // Clean up tmp file
                    try { File.Delete(tmpFile); } catch { }

                    return; // Success
                }
                catch (IOException) when (attempt < maxRetries - 1)
                {
                    // File locked by another process, retry
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
        /// Computes a stable hash string from input using SHA256 (first 4 bytes).
        /// Unlike GetHashCode(), this is deterministic across processes and runtimes.
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
            try { return System.Diagnostics.Process.GetProcessById(pid) != null; }
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
        }
    }
}
