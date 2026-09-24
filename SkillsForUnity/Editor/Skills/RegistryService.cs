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
        // Test hook (same idiom as WorkflowManager.OverrideHistoryFilePathForTests); volatile because GetLiveInstances reads it off the main thread.
        internal static volatile string OverrideRegistryFilePathForTests;
        private static string RegistryFile => OverrideRegistryFilePathForTests ?? Path.Combine(GlobalConfigDir, "registry.json");

        /// <summary>Entry lifecycle written to <see cref="InstanceInfo.status"/>. A missing status (pre-status writers) means running.</summary>
        internal const string StatusRunning = "running";
        internal const string StatusReloading = "reloading";
        internal const string StatusStopped = "stopped";

        /// <summary>A running entry whose heartbeat (every 30 s) is older than this is stale.</summary>
        internal const long StaleHeartbeatSeconds = 120;

        /// <summary>
        /// A reloading/stopped entry has no heartbeat and lives as long as its editor process. Past this age a live pid
        /// that no longer looks like a Unity process is taken as recycled and the entry is retired.
        /// </summary>
        internal const long NonRunningReuseCheckSeconds = 30 * 60;

        private static readonly int CurrentPid = System.Diagnostics.Process.GetCurrentProcess().Id;

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
                // A domain reload keeps the entry and marks it reloading (SkillsHttpServer.OnBeforeAssemblyReload calls MarkReloading)
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
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    registry[ProjectPath] = CreateSelfEntry(port, StatusRunning, now);
                    PruneStaleEntries(registry, now);
                });
                SkillsLogger.LogVerbose($"Registered instance '{InstanceId}' on port {port}");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to register instance: {ex.Message}");
            }
        }

        /// <summary>
        /// Keeps this instance's entry (port, pid) through a domain reload and marks it reloading, so a client whose
        /// working directory belongs to this project waits for the same port instead of falling back to another
        /// Editor. The Register call after the restart flips it back to running.
        /// </summary>
        public static void MarkReloading(int port)
        {
            try
            {
                AtomicReadModifyWrite(registry =>
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (registry.TryGetValue(ProjectPath, out var existing) && existing != null)
                        ApplyStatus(existing, StatusReloading, port, now);
                    else
                        registry[ProjectPath] = CreateSelfEntry(port, StatusReloading, now);
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to mark instance reloading: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks this instance's entry stopped for a non-permanent stop (watchdog restart, failed auto-restart): the Editor
        /// is still open and may serve again. Never creates an entry — a server that never registered stays unlisted.
        /// A port of 0 keeps the recorded one.
        /// </summary>
        public static void MarkStopped(int port)
        {
            try
            {
                if (!File.Exists(RegistryFile)) return;

                AtomicReadModifyWrite(registry =>
                {
                    if (registry.TryGetValue(ProjectPath, out var existing) && existing != null)
                        ApplyStatus(existing, StatusStopped, port, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to mark instance stopped: {ex.Message}");
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
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (registry.TryGetValue(ProjectPath, out var existing) && existing != null)
                    {
                        existing.last_active = now;
                        existing.port = port;
                        // Heartbeats only run while the server is up, so any other status here is left over from a reload or stop
                        if (!IsRunningStatus(existing.status))
                        {
                            existing.status = StatusRunning;
                            existing.statusSince = now;
                        }
                    }
                    else
                    {
                        // The heartbeat arrived before Register, so a full entry needs to be written here
                        registry[ProjectPath] = CreateSelfEntry(port, StatusRunning, now);
                    }

                    if (doStaleCleanup)
                        PruneStaleEntries(registry, now);
                });
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to heartbeat: {ex.Message}");
            }
        }

        /// <summary>
        /// The other instances a client could be pointed at, for error payloads (INSTANCE_MISMATCH). Read-only and free of
        /// Unity API, so it is safe on any thread: the file is opened with FileShare.ReadWrite and never locked, and a
        /// read that races a writer is retried briefly. Applies the same liveness rules as pruning (running entries need a
        /// fresh heartbeat, reloading/stopped entries only a live pid) and leaves out this instance's own entry.
        /// Returns an empty list when the registry is missing or unreadable.
        /// </summary>
        internal static List<InstanceInfo> GetLiveInstances()
        {
            var registry = ReadRegistrySnapshot();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var live = new List<InstanceInfo>();
            foreach (var pair in registry)
            {
                var entry = pair.Value;
                if (entry == null || string.Equals(pair.Key, ProjectPath, StringComparison.Ordinal))
                    continue;
                if (ShouldPrune(entry, now, IsProcessAlive, IsProcessReused))
                    continue;
                if (string.IsNullOrEmpty(entry.path))
                    entry.path = pair.Key;
                live.Add(entry);
            }
            return live;
        }

        /// <summary>True when the entry's status is running, including entries written before the status field existed.</summary>
        internal static bool IsRunningStatus(string status) =>
            !string.Equals(status, StatusReloading, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, StatusStopped, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The single staleness rule shared by pruning and <see cref="GetLiveInstances"/>. A running entry (or one without a
        /// status) is stale when its heartbeat is older than <see cref="StaleHeartbeatSeconds"/> or its process is gone. A
        /// reloading/stopped entry has no heartbeat to judge, so it is stale only when its process is gone, or when it is
        /// older than <see cref="NonRunningReuseCheckSeconds"/> and its pid now belongs to a different program.
        /// </summary>
        internal static bool ShouldPrune(InstanceInfo entry, long now, Func<int, bool> isProcessAlive, Func<int, bool> isPidReused)
        {
            if (entry == null)
                return true;

            if (IsRunningStatus(entry.status))
                return now - entry.last_active > StaleHeartbeatSeconds || !isProcessAlive(entry.pid);

            if (!isProcessAlive(entry.pid))
                return true;
            long since = entry.statusSince > 0 ? entry.statusSince : entry.last_active;
            return now - since > NonRunningReuseCheckSeconds && isPidReused(entry.pid);
        }

        private static InstanceInfo CreateSelfEntry(int port, string status, long now)
        {
            UnityCliService.GetRegistryBinding(out var cliBound, out var cliPath);
            return new InstanceInfo
            {
                id = InstanceId,
                name = ProjectName,
                path = ProjectPath,
                port = port,
                pid = CurrentPid,
                last_active = now,
                unityVersion = Application.unityVersion,
                cliBound = cliBound,
                cliPath = cliPath,
                status = status,
                statusSince = now
            };
        }

        private static void ApplyStatus(InstanceInfo entry, string status, int port, long now)
        {
            if (!string.Equals(entry.status, status, StringComparison.OrdinalIgnoreCase))
                entry.statusSince = now;
            entry.status = status;
            if (port > 0)
                entry.port = port;
            entry.pid = CurrentPid;
            entry.last_active = now;
        }

        private static void PruneStaleEntries(Dictionary<string, InstanceInfo> registry, long now)
        {
            var keysToRemove = registry
                .Where(k => k.Value == null ||
                    (k.Value.pid != CurrentPid && ShouldPrune(k.Value, now, IsProcessAlive, IsProcessReused)))
                .Select(k => k.Key).ToList();
            foreach (var key in keysToRemove)
                registry.Remove(key);
        }

        /// <summary>
        /// Lock-free read for callers that must not block or write (see <see cref="GetLiveInstances"/>). A writer truncates
        /// and rewrites the file in place, so an empty or half-written read is retried a few times before giving up.
        /// </summary>
        private static Dictionary<string, InstanceInfo> ReadRegistrySnapshot()
        {
            const int maxAttempts = 3;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var path = RegistryFile;
                    if (!File.Exists(path))
                        break;

                    string json;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        json = reader.ReadToEnd();

                    if (!string.IsNullOrWhiteSpace(json))
                        return JsonConvert.DeserializeObject<Dictionary<string, InstanceInfo>>(json)
                               ?? new Dictionary<string, InstanceInfo>();
                }
                catch (IOException) { }
                catch (JsonException) { }
                catch (UnauthorizedAccessException) { break; }

                System.Threading.Thread.Sleep(20 * (attempt + 1));
            }
            return new Dictionary<string, InstanceInfo>();
        }

        /// <summary>
        /// Atomic read-modify-write with a cross-process file lock: mutual exclusion via FileStream(FileShare.None),
        /// with atomicity of the write itself guaranteed by a .tmp file.
        /// </summary>
        private static void AtomicReadModifyWrite(Action<Dictionary<string, InstanceInfo>> modifier)
        {
            const int maxRetries = 5;
            const int retryDelayMs = 100;

            var registryFile = RegistryFile;
            var directory = Path.GetDirectoryName(registryFile);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            // Recovers from an interrupted write: if .tmp exists while the main file is missing or empty, restore from .tmp
            var tmpFile = registryFile + ".tmp";
            if (File.Exists(tmpFile) && (!File.Exists(registryFile) || new FileInfo(registryFile).Length == 0))
            {
                try { File.Copy(tmpFile, registryFile, true); File.Delete(tmpFile); } catch { }
            }

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                FileStream lockStream = null;
                try
                {
                    lockStream = new FileStream(
                        registryFile,
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

        /// <summary>
        /// True only when the pid is alive and its process name positively does not look like a Unity Editor. An
        /// unreadable name counts as not reused, so a doubt keeps the entry.
        /// </summary>
        private static bool IsProcessReused(int pid)
        {
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                {
                    var name = proc.ProcessName;
                    return !string.IsNullOrEmpty(name) && name.IndexOf("unity", StringComparison.OrdinalIgnoreCase) < 0;
                }
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
            // "running" | "reloading" | "stopped" (StatusRunning/StatusReloading/StatusStopped); null in entries from
            // writers that predate the field, which readers treat as running. statusSince is unix seconds.
            public string status;
            public long statusSince;
        }
    }
}

// Producer:Betsy
