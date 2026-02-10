using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            
            // Generate stable Instance ID based on path hash to identify this specific project instance
            // We use a short hash of the path to keep it readable but unique
            var pathHash = Math.Abs(ProjectPath.GetHashCode()).ToString("X");
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
                var registry = LoadRegistry();
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

                // Remove entry if it exists (update it)
                registry[ProjectPath] = info;
                
                // Also clean up stale entries (older than 1 minute)
                // This is a simple form of garbage collection for crashed instances
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var keysToRemove = registry.Where(k => now - k.Value.last_active > 60 && k.Value.pid != info.pid).Select(k => k.Key).ToList();
                foreach (var key in keysToRemove)
                    registry.Remove(key);

                SaveRegistry(registry);
                Debug.Log($"[UnitySkills] Registered instance '{InstanceId}' on port {port}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Failed to register instance: {ex.Message}");
            }
        }

        public static void Unregister()
        {
            try
            {
                if (!File.Exists(RegistryFile)) return;

                var registry = LoadRegistry();
                if (registry.ContainsKey(ProjectPath))
                {
                    registry.Remove(ProjectPath);
                    SaveRegistry(registry);
                    // Debug.Log($"[UnitySkills] Unregistered instance '{InstanceId}'");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Failed to unregister: {ex.Message}");
            }
        }
        
        public static void Heartbeat(int port)
        {
             // For now, just re-register which updates the timestamp
             Register(port);
        }

        private static Dictionary<string, InstanceInfo> LoadRegistry()
        {
            if (!File.Exists(RegistryFile))
                return new Dictionary<string, InstanceInfo>();

            try
            {
                var json = File.ReadAllText(RegistryFile);
                return JsonConvert.DeserializeObject<Dictionary<string, InstanceInfo>>(json) 
                       ?? new Dictionary<string, InstanceInfo>();
            }
            catch
            {
                return new Dictionary<string, InstanceInfo>();
            }
        }

        private static void SaveRegistry(Dictionary<string, InstanceInfo> registry)
        {
            try
            {
                var json = JsonConvert.SerializeObject(registry, Formatting.Indented);
                File.WriteAllText(RegistryFile, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnitySkills] Could not save registry: {ex.Message}");
            }
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
