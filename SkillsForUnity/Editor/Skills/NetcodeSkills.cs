using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;

#if NETCODE_GAMEOBJECTS
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
#endif

namespace UnitySkills
{
    /// <summary>
    /// Netcode for GameObjects skills — NetworkManager setup, NetworkObject/NetworkTransform
    /// management, prefab list registration, host/server/client runtime control.
    ///
    /// Requires com.unity.netcode.gameobjects (2.x). Gracefully degrades when the package is not
    /// installed (each skill returns a clear NoNetcode() error).
    ///
    /// All API calls are anchored to NGO 2.x source (see netcode-design advisory module for the
    /// contract: lifecycle, ownership, RPC, variables, spawning, scene, transport, pitfalls).
    /// </summary>
    public static class NetcodeSkills
    {
#if !NETCODE_GAMEOBJECTS
        private static object NoNetcode() => new
        {
            error = "Netcode for GameObjects package (com.unity.netcode.gameobjects) is not installed. " +
                    "Install via: Window > Package Manager > Unity Registry > Netcode for GameObjects"
        };
#endif

        // ==================================================================================
        // 1. Setup & Validation (5 skills)
        // ==================================================================================

        [UnitySkill("netcode_check_setup",
            "Full Netcode setup validation: package, NetworkManager count, Transport, PlayerPrefab, PrefabsList",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Analyze,
            Tags = new[] { "netcode", "ngo", "multiplayer", "validation", "diagnostic" },
            Outputs = new[] { "installed", "managerCount", "transportType", "issueCount", "issues" },
            ReadOnly = true)]
        public static object CheckSetup(bool verbose = false)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var issues = new List<string>();
            var info = new Dictionary<string, object>();
            info["installed"] = true;
            info["packageVersion"] = "2.x";

            var managers = FindHelper.FindAll<NetworkManager>(includeInactive: true);
            info["managerCount"] = managers.Length;
            if (managers.Length == 0)
                issues.Add("No NetworkManager in scene. Use netcode_create_manager.");
            if (managers.Length > 1)
                issues.Add($"Multiple NetworkManagers found ({managers.Length}). Only one Singleton is supported.");

            if (managers.Length > 0)
            {
                var nm = managers[0];
                var transport = nm.NetworkConfig?.NetworkTransport;
                info["transportType"] = transport != null ? transport.GetType().Name : null;
                info["tickRate"] = nm.NetworkConfig?.TickRate ?? 0;
                info["enableSceneManagement"] = nm.NetworkConfig?.EnableSceneManagement ?? false;
                info["connectionApproval"] = nm.NetworkConfig?.ConnectionApproval ?? false;
                info["playerPrefab"] = nm.NetworkConfig?.PlayerPrefab != null
                    ? nm.NetworkConfig.PlayerPrefab.name : null;

                if (transport == null)
                    issues.Add("NetworkConfig.NetworkTransport is null. Use netcode_create_manager or add UnityTransport manually.");

                if (nm.NetworkConfig?.PlayerPrefab != null)
                {
                    var pp = nm.NetworkConfig.PlayerPrefab;
                    if (pp.GetComponent<NetworkObject>() == null)
                        issues.Add($"PlayerPrefab '{pp.name}' lacks NetworkObject component.");
                    var list = nm.NetworkConfig.Prefabs?.NetworkPrefabsLists;
                    var inList = pp.GetComponent<NetworkObject>() != null && list != null && list.Any(l =>
                        l != null && l.PrefabList.Any(p => p.Prefab == pp));
                    info["playerPrefabInList"] = inList;
                    if (!inList && list != null && list.Count > 0)
                        issues.Add($"PlayerPrefab '{pp.name}' not found in any assigned NetworkPrefabsList.");
                }

                // Scene-placed NetworkObjects (not yet spawned in edit mode)
                var netObjects = FindHelper.FindAll<NetworkObject>(includeInactive: true);
                info["scenePlacedNetworkObjects"] = netObjects.Length;

                if (Application.isPlaying)
                {
                    info["isListening"] = nm.IsListening;
                    info["isHost"] = nm.IsHost;
                    info["isServer"] = nm.IsServer;
                    info["isClient"] = nm.IsClient;
                }
            }

            info["issueCount"] = issues.Count;
            info["issues"] = issues;

            if (verbose && managers.Length > 0)
            {
                var nm = managers[0];
                info["verboseConfig"] = new
                {
                    protocolVersion = nm.NetworkConfig.ProtocolVersion,
                    spawnTimeout = nm.NetworkConfig.SpawnTimeout,
                    loadSceneTimeOut = nm.NetworkConfig.LoadSceneTimeOut,
                    autoSpawnPlayerPrefabClientSide = nm.NetworkConfig.AutoSpawnPlayerPrefabClientSide,
                    ensureNetworkVariableLengthSafety = nm.NetworkConfig.EnsureNetworkVariableLengthSafety,
                    forceSamePrefabs = nm.NetworkConfig.ForceSamePrefabs,
                    recycleNetworkIds = nm.NetworkConfig.RecycleNetworkIds,
                    networkTopology = nm.NetworkConfig.NetworkTopology.ToString(),
                };
            }
            return info;
#endif
        }

        [UnitySkill("netcode_create_manager",
            "Create a NetworkManager GameObject with UnityTransport attached. Fails if one already exists.",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "manager", "setup" },
            Outputs = new[] { "success", "name", "instanceId", "transportType" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object CreateManager(string name = "NetworkManager")
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var existing = FindHelper.FindAll<NetworkManager>(includeInactive: true);
            if (existing.Length > 0)
                return new { error = $"NetworkManager already exists: '{existing[0].gameObject.name}' (instanceId={existing[0].gameObject.GetInstanceID()}). Only one is supported." };

            var go = new GameObject(string.IsNullOrEmpty(name) ? "NetworkManager" : name);
            Undo.RegisterCreatedObjectUndo(go, "Create NetworkManager");
            var nm = Undo.AddComponent<NetworkManager>(go);
            var transport = Undo.AddComponent<UnityTransport>(go);
            nm.NetworkConfig = new NetworkConfig { NetworkTransport = transport };
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                transportType = nameof(UnityTransport)
            };
#endif
        }

        [UnitySkill("netcode_configure_manager",
            "Batch-set NetworkConfig fields on an existing NetworkManager (TickRate, ConnectionApproval, EnableSceneManagement, etc.)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "manager", "config" },
            Outputs = new[] { "success", "applied" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object ConfigureManager(
            string name = null,
            uint? tickRate = null,
            ushort? protocolVersion = null,
            int? clientConnectionBufferTimeout = null,
            bool? connectionApproval = null,
            bool? enableTimeResync = null,
            int? timeResyncInterval = null,
            bool? ensureNetworkVariableLengthSafety = null,
            bool? enableSceneManagement = null,
            bool? forceSamePrefabs = null,
            bool? recycleNetworkIds = null,
            float? networkIdRecycleDelay = null,
            int? loadSceneTimeOut = null,
            float? spawnTimeout = null,
            bool? enableNetworkLogs = null,
            bool? autoSpawnPlayerPrefabClientSide = null,
            string networkTopology = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { error = $"NetworkManager not found (name={name ?? "<any>"})." };
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();

            Undo.RecordObject(nm, "Configure NetworkManager");
            var applied = new Dictionary<string, object>();

            if (tickRate.HasValue) { nm.NetworkConfig.TickRate = tickRate.Value; applied["tickRate"] = tickRate.Value; }
            if (protocolVersion.HasValue) { nm.NetworkConfig.ProtocolVersion = protocolVersion.Value; applied["protocolVersion"] = protocolVersion.Value; }
            if (clientConnectionBufferTimeout.HasValue) { nm.NetworkConfig.ClientConnectionBufferTimeout = clientConnectionBufferTimeout.Value; applied["clientConnectionBufferTimeout"] = clientConnectionBufferTimeout.Value; }
            if (connectionApproval.HasValue) { nm.NetworkConfig.ConnectionApproval = connectionApproval.Value; applied["connectionApproval"] = connectionApproval.Value; }
            if (enableTimeResync.HasValue) { nm.NetworkConfig.EnableTimeResync = enableTimeResync.Value; applied["enableTimeResync"] = enableTimeResync.Value; }
            if (timeResyncInterval.HasValue) { nm.NetworkConfig.TimeResyncInterval = timeResyncInterval.Value; applied["timeResyncInterval"] = timeResyncInterval.Value; }
            if (ensureNetworkVariableLengthSafety.HasValue) { nm.NetworkConfig.EnsureNetworkVariableLengthSafety = ensureNetworkVariableLengthSafety.Value; applied["ensureNetworkVariableLengthSafety"] = ensureNetworkVariableLengthSafety.Value; }
            if (enableSceneManagement.HasValue) { nm.NetworkConfig.EnableSceneManagement = enableSceneManagement.Value; applied["enableSceneManagement"] = enableSceneManagement.Value; }
            if (forceSamePrefabs.HasValue) { nm.NetworkConfig.ForceSamePrefabs = forceSamePrefabs.Value; applied["forceSamePrefabs"] = forceSamePrefabs.Value; }
            if (recycleNetworkIds.HasValue) { nm.NetworkConfig.RecycleNetworkIds = recycleNetworkIds.Value; applied["recycleNetworkIds"] = recycleNetworkIds.Value; }
            if (networkIdRecycleDelay.HasValue) { nm.NetworkConfig.NetworkIdRecycleDelay = networkIdRecycleDelay.Value; applied["networkIdRecycleDelay"] = networkIdRecycleDelay.Value; }
            if (loadSceneTimeOut.HasValue) { nm.NetworkConfig.LoadSceneTimeOut = loadSceneTimeOut.Value; applied["loadSceneTimeOut"] = loadSceneTimeOut.Value; }
            if (spawnTimeout.HasValue) { nm.NetworkConfig.SpawnTimeout = spawnTimeout.Value; applied["spawnTimeout"] = spawnTimeout.Value; }
            if (enableNetworkLogs.HasValue) { nm.NetworkConfig.EnableNetworkLogs = enableNetworkLogs.Value; applied["enableNetworkLogs"] = enableNetworkLogs.Value; }
            if (autoSpawnPlayerPrefabClientSide.HasValue) { nm.NetworkConfig.AutoSpawnPlayerPrefabClientSide = autoSpawnPlayerPrefabClientSide.Value; applied["autoSpawnPlayerPrefabClientSide"] = autoSpawnPlayerPrefabClientSide.Value; }
            if (!string.IsNullOrEmpty(networkTopology))
            {
                if (!Enum.TryParse<NetworkTopologyTypes>(networkTopology, true, out var topo))
                    return new { error = $"Invalid networkTopology '{networkTopology}'. Valid: ClientServer, DistributedAuthority." };
                nm.NetworkConfig.NetworkTopology = topo;
                applied["networkTopology"] = topo.ToString();
            }

            EditorUtility.SetDirty(nm);
            WorkflowManager.SnapshotObject(nm.gameObject);
            return new { success = true, applied };
#endif
        }

        [UnitySkill("netcode_get_manager_info",
            "Read full NetworkConfig + runtime state from the scene NetworkManager",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "manager", "info" },
            Outputs = new[] { "found", "name", "config", "runtime" },
            ReadOnly = true)]
        public static object GetManagerInfo(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { found = false, error = $"NetworkManager not found (name={name ?? "<any>"})." };

            var cfg = nm.NetworkConfig;
            object cfgSnap = cfg == null ? null : (object)new
            {
                protocolVersion = cfg.ProtocolVersion,
                tickRate = cfg.TickRate,
                clientConnectionBufferTimeout = cfg.ClientConnectionBufferTimeout,
                connectionApproval = cfg.ConnectionApproval,
                enableTimeResync = cfg.EnableTimeResync,
                timeResyncInterval = cfg.TimeResyncInterval,
                ensureNetworkVariableLengthSafety = cfg.EnsureNetworkVariableLengthSafety,
                enableSceneManagement = cfg.EnableSceneManagement,
                forceSamePrefabs = cfg.ForceSamePrefabs,
                recycleNetworkIds = cfg.RecycleNetworkIds,
                networkIdRecycleDelay = cfg.NetworkIdRecycleDelay,
                loadSceneTimeOut = cfg.LoadSceneTimeOut,
                spawnTimeout = cfg.SpawnTimeout,
                enableNetworkLogs = cfg.EnableNetworkLogs,
                autoSpawnPlayerPrefabClientSide = cfg.AutoSpawnPlayerPrefabClientSide,
                networkTopology = cfg.NetworkTopology.ToString(),
                transportType = cfg.NetworkTransport != null ? cfg.NetworkTransport.GetType().Name : null,
                playerPrefab = cfg.PlayerPrefab != null ? cfg.PlayerPrefab.name : null,
                prefabsListCount = cfg.Prefabs?.NetworkPrefabsLists?.Count ?? 0
            };

            object runtime = Application.isPlaying ? (object)new
            {
                isListening = nm.IsListening,
                isHost = nm.IsHost,
                isServer = nm.IsServer,
                isClient = nm.IsClient,
                isConnectedClient = nm.IsConnectedClient,
                localClientId = nm.LocalClientId,
                connectedClientsCount = nm.IsServer ? nm.ConnectedClientsIds?.Count ?? 0 : 0
            } : null;

            return new
            {
                found = true,
                name = nm.gameObject.name,
                instanceId = nm.gameObject.GetInstanceID(),
                config = cfgSnap,
                runtime
            };
#endif
        }

        [UnitySkill("netcode_remove_manager",
            "Remove the NetworkManager GameObject from the scene",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Delete,
            Tags = new[] { "netcode", "ngo", "manager" },
            Outputs = new[] { "success", "removed" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object RemoveManager(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { success = false, error = "NetworkManager not found." };
            if (Application.isPlaying && nm.IsListening)
                return new { success = false, error = "NetworkManager is currently running. Call netcode_shutdown first." };

            WorkflowManager.SnapshotObject(nm.gameObject);
            var goName = nm.gameObject.name;
            Undo.DestroyObjectImmediate(nm.gameObject);
            return new { success = true, removed = goName };
#endif
        }

        // ==================================================================================
        // 2. Transport Configuration (4 skills)
        // ==================================================================================

        [UnitySkill("netcode_set_transport_address",
            "Configure UnityTransport direct connection (address, port, server listen address)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "transport", "connection" },
            Outputs = new[] { "success", "address", "port", "serverListenAddress" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object SetTransportAddress(
            string address,
            int port,
            string serverListenAddress = null,
            string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.Required(address, "address") is object addrErr) return addrErr;
            if (Validate.InRange(port, 1, 65535, "port") is object portErr) return portErr;

            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) return new { error = "UnityTransport not found on NetworkManager." };

            Undo.RecordObject(utp, "Set Transport Address");
            utp.SetConnectionData(address, (ushort)port, serverListenAddress);
            EditorUtility.SetDirty(utp);
            WorkflowManager.SnapshotObject(utp);

            return new
            {
                success = true,
                address = utp.ConnectionData.Address,
                port = (int)utp.ConnectionData.Port,
                serverListenAddress = utp.ConnectionData.ServerListenAddress
            };
#endif
        }

        [UnitySkill("netcode_set_relay_server_data",
            "Configure UnityTransport to use Unity Relay. Mutually exclusive with direct connection.",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "transport", "relay" },
            Outputs = new[] { "success" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object SetRelayServerData(
            string address,
            int port,
            string allocationIdBase64,
            string keyBase64,
            string connectionDataBase64,
            string hostConnectionDataBase64 = null,
            bool isSecure = false,
            string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.Required(address, "address") is object e1) return e1;
            if (Validate.InRange(port, 1, 65535, "port") is object e2) return e2;
            if (Validate.Required(allocationIdBase64, "allocationIdBase64") is object e3) return e3;
            if (Validate.Required(keyBase64, "keyBase64") is object e4) return e4;
            if (Validate.Required(connectionDataBase64, "connectionDataBase64") is object e5) return e5;

            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) return new { error = "UnityTransport not found on NetworkManager." };

            byte[] alloc, key, conn, hostConn = null;
            try
            {
                alloc = Convert.FromBase64String(allocationIdBase64);
                key = Convert.FromBase64String(keyBase64);
                conn = Convert.FromBase64String(connectionDataBase64);
                if (!string.IsNullOrEmpty(hostConnectionDataBase64))
                    hostConn = Convert.FromBase64String(hostConnectionDataBase64);
            }
            catch (FormatException ex)
            {
                return new { error = $"Base64 decode failed: {ex.Message}" };
            }

            Undo.RecordObject(utp, "Set Relay Server Data");
            utp.SetRelayServerData(address, (ushort)port, alloc, key, conn, hostConn, isSecure);
            EditorUtility.SetDirty(utp);
            WorkflowManager.SnapshotObject(utp);
            return new { success = true, address, port, isSecure };
#endif
        }

        [UnitySkill("netcode_set_debug_simulator",
            "Set UnityTransport debug network simulator (delay/jitter/drop). Dev builds only.",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "transport", "debug", "simulator" },
            Outputs = new[] { "success", "packetDelay", "packetJitter", "dropRate" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object SetDebugSimulator(
            int packetDelay = 0,
            int packetJitter = 0,
            int dropRate = 0,
            string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.InRange(packetDelay, 0, 5000, "packetDelay") is object e1) return e1;
            if (Validate.InRange(packetJitter, 0, 5000, "packetJitter") is object e2) return e2;
            if (Validate.InRange(dropRate, 0, 100, "dropRate") is object e3) return e3;

            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) return new { error = "UnityTransport not found on NetworkManager." };

            Undo.RecordObject(utp, "Set Debug Simulator");
            utp.SetDebugSimulatorParameters(packetDelay, packetJitter, dropRate);
            EditorUtility.SetDirty(utp);
            WorkflowManager.SnapshotObject(utp);

            return new { success = true, packetDelay, packetJitter, dropRate };
#endif
        }

        [UnitySkill("netcode_get_transport_info",
            "Read current UnityTransport connection data and type",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "transport", "info" },
            Outputs = new[] { "found", "type", "address", "port", "serverListenAddress" },
            ReadOnly = true)]
        public static object GetTransportInfo(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { found = false, error = "NetworkManager not found." };
            var transport = nm.NetworkConfig?.NetworkTransport;
            if (transport == null) return new { found = false, error = "NetworkTransport not assigned." };

            var utp = transport as UnityTransport;
            if (utp == null)
            {
                return new { found = true, type = transport.GetType().Name, isUnityTransport = false };
            }
            return new
            {
                found = true,
                type = nameof(UnityTransport),
                isUnityTransport = true,
                address = utp.ConnectionData.Address,
                port = (int)utp.ConnectionData.Port,
                serverListenAddress = utp.ConnectionData.ServerListenAddress
            };
#endif
        }

        // ==================================================================================
        // 3. NetworkObject Management (5 skills)
        // ==================================================================================

        [UnitySkill("netcode_add_network_object",
            "Add a NetworkObject component to a GameObject (prerequisite for spawning/syncing)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "networkobject", "component" },
            Outputs = new[] { "success", "instanceId", "globalObjectIdHash" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddNetworkObject(
            string name = null,
            int instanceId = 0,
            string path = null,
            bool? alwaysReplicateAsRoot = null,
            bool? synchronizeTransform = null,
            bool? activeSceneSynchronization = null,
            bool? spawnWithObservers = null,
            bool? dontDestroyWithOwner = null,
            bool? autoObjectParentSync = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;

            var existing = go.GetComponent<NetworkObject>();
            if (existing != null)
                return new { error = $"GameObject '{go.name}' already has a NetworkObject (instanceId={existing.GetInstanceID()})." };

            var no = Undo.AddComponent<NetworkObject>(go);
            if (alwaysReplicateAsRoot.HasValue) no.AlwaysReplicateAsRoot = alwaysReplicateAsRoot.Value;
            if (synchronizeTransform.HasValue) no.SynchronizeTransform = synchronizeTransform.Value;
            if (activeSceneSynchronization.HasValue) no.ActiveSceneSynchronization = activeSceneSynchronization.Value;
            if (spawnWithObservers.HasValue) no.SpawnWithObservers = spawnWithObservers.Value;
            if (dontDestroyWithOwner.HasValue) no.DontDestroyWithOwner = dontDestroyWithOwner.Value;
            if (autoObjectParentSync.HasValue) no.AutoObjectParentSync = autoObjectParentSync.Value;

            EditorUtility.SetDirty(go);
            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                globalObjectIdHash = GetGlobalObjectIdHash(no)
            };
#endif
        }

        [UnitySkill("netcode_configure_network_object",
            "Modify fields of an existing NetworkObject (AlwaysReplicateAsRoot, DontDestroyWithOwner, etc.)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "networkobject", "configure" },
            Outputs = new[] { "success", "applied" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object ConfigureNetworkObject(
            string name = null,
            int instanceId = 0,
            string path = null,
            bool? alwaysReplicateAsRoot = null,
            bool? synchronizeTransform = null,
            bool? activeSceneSynchronization = null,
            bool? sceneMigrationSynchronization = null,
            bool? spawnWithObservers = null,
            bool? dontDestroyWithOwner = null,
            bool? autoObjectParentSync = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            var no = go.GetComponent<NetworkObject>();
            if (no == null) return new { error = $"'{go.name}' has no NetworkObject component. Use netcode_add_network_object first." };

            Undo.RecordObject(no, "Configure NetworkObject");
            var applied = new Dictionary<string, object>();
            if (alwaysReplicateAsRoot.HasValue) { no.AlwaysReplicateAsRoot = alwaysReplicateAsRoot.Value; applied["alwaysReplicateAsRoot"] = alwaysReplicateAsRoot.Value; }
            if (synchronizeTransform.HasValue) { no.SynchronizeTransform = synchronizeTransform.Value; applied["synchronizeTransform"] = synchronizeTransform.Value; }
            if (activeSceneSynchronization.HasValue) { no.ActiveSceneSynchronization = activeSceneSynchronization.Value; applied["activeSceneSynchronization"] = activeSceneSynchronization.Value; }
            if (sceneMigrationSynchronization.HasValue) { no.SceneMigrationSynchronization = sceneMigrationSynchronization.Value; applied["sceneMigrationSynchronization"] = sceneMigrationSynchronization.Value; }
            if (spawnWithObservers.HasValue) { no.SpawnWithObservers = spawnWithObservers.Value; applied["spawnWithObservers"] = spawnWithObservers.Value; }
            if (dontDestroyWithOwner.HasValue) { no.DontDestroyWithOwner = dontDestroyWithOwner.Value; applied["dontDestroyWithOwner"] = dontDestroyWithOwner.Value; }
            if (autoObjectParentSync.HasValue) { no.AutoObjectParentSync = autoObjectParentSync.Value; applied["autoObjectParentSync"] = autoObjectParentSync.Value; }

            EditorUtility.SetDirty(no);
            WorkflowManager.SnapshotObject(no);
            return new { success = true, applied };
#endif
        }

        [UnitySkill("netcode_remove_network_object",
            "Remove the NetworkObject component from a GameObject (must be unspawned)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Delete,
            Tags = new[] { "netcode", "ngo", "networkobject" },
            Outputs = new[] { "success" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object RemoveNetworkObject(string name = null, int instanceId = 0, string path = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            var no = go.GetComponent<NetworkObject>();
            if (no == null) return new { error = $"'{go.name}' has no NetworkObject component." };
            if (Application.isPlaying && no.IsSpawned)
                return new { error = $"NetworkObject '{go.name}' is currently spawned. Despawn before removing the component." };

            WorkflowManager.SnapshotObject(go);
            Undo.DestroyObjectImmediate(no);
            return new { success = true };
#endif
        }

        [UnitySkill("netcode_list_network_objects",
            "List all NetworkObject instances in loaded scenes (includes unspawned by default)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "networkobject", "list" },
            Outputs = new[] { "count", "objects" },
            ReadOnly = true)]
        public static object ListNetworkObjects(bool includeInactive = true)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var all = FindHelper.FindAll<NetworkObject>(includeInactive: includeInactive);
            var list = all.Select(no => new
            {
                name = no.gameObject.name,
                instanceId = no.gameObject.GetInstanceID(),
                globalObjectIdHash = GetGlobalObjectIdHash(no),
                isSpawned = Application.isPlaying && no.IsSpawned,
                networkObjectId = Application.isPlaying && no.IsSpawned ? (ulong?)no.NetworkObjectId : null,
                ownerClientId = Application.isPlaying && no.IsSpawned ? (ulong?)no.OwnerClientId : null,
                isPlayerObject = Application.isPlaying && no.IsSpawned && no.IsPlayerObject,
                activeInHierarchy = no.gameObject.activeInHierarchy,
                path = GameObjectFinder.GetPath(no.gameObject)
            }).ToArray();

            return new { count = list.Length, objects = list };
#endif
        }

        [UnitySkill("netcode_get_network_object_info",
            "Query a single NetworkObject's full state (identity, ownership, spawn, parent)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "networkobject", "info" },
            Outputs = new[] { "found", "networkObjectId", "ownerClientId", "isSpawned", "isPlayerObject" },
            ReadOnly = true)]
        public static object GetNetworkObjectInfo(string name = null, int instanceId = 0, string path = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return new { found = false, error = SkillResultHelper.TryGetError(findErr, out var message) ? message : "GameObject not found." };
            var no = go.GetComponent<NetworkObject>();
            if (no == null) return new { found = false, error = $"'{go.name}' has no NetworkObject." };

            return new
            {
                found = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                globalObjectIdHash = GetGlobalObjectIdHash(no),
                alwaysReplicateAsRoot = no.AlwaysReplicateAsRoot,
                synchronizeTransform = no.SynchronizeTransform,
                activeSceneSynchronization = no.ActiveSceneSynchronization,
                sceneMigrationSynchronization = no.SceneMigrationSynchronization,
                spawnWithObservers = no.SpawnWithObservers,
                dontDestroyWithOwner = no.DontDestroyWithOwner,
                autoObjectParentSync = no.AutoObjectParentSync,
                isSpawned = Application.isPlaying && no.IsSpawned,
                networkObjectId = Application.isPlaying && no.IsSpawned ? (ulong?)no.NetworkObjectId : null,
                ownerClientId = Application.isPlaying && no.IsSpawned ? (ulong?)no.OwnerClientId : null,
                isPlayerObject = Application.isPlaying && no.IsSpawned && no.IsPlayerObject,
                isOwner = Application.isPlaying && no.IsOwner,
                path = GameObjectFinder.GetPath(go)
            };
#endif
        }

        // ==================================================================================
        // 4. NetworkPrefabsList (5 skills)
        // ==================================================================================

        [UnitySkill("netcode_create_prefabs_list",
            "Create a NetworkPrefabsList ScriptableObject asset and optionally assign to NetworkManager",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "prefabs", "list", "scriptableobject" },
            Outputs = new[] { "success", "path" },
            MutatesAssets = true, MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object CreatePrefabsList(string path, bool assignToManager = true, string managerName = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.SafePath(path, "path") is object pathErr) return pathErr;
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return new { error = "path must end with '.asset'." };

            if (AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path) != null)
                return new { error = $"Asset already exists at {path}." };

            Validate.EnsureDirectoryExists(path);
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            AssetDatabase.CreateAsset(list, path);
            AssetDatabase.SaveAssets();

            bool assigned = false;
            if (assignToManager)
            {
                var nm = FindManager(managerName);
                if (nm != null)
                {
                    if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
                    Undo.RecordObject(nm, "Assign NetworkPrefabsList");
                    if (nm.NetworkConfig.Prefabs.NetworkPrefabsLists == null)
                        nm.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList>();
                    if (!nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Contains(list))
                        nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);
                    EditorUtility.SetDirty(nm);
                    assigned = true;
                }
            }

            WorkflowManager.SnapshotObject(list, SnapshotType.Created);
            return new { success = true, path, assignedToManager = assigned };
#endif
        }

        [UnitySkill("netcode_add_to_prefabs_list",
            "Add a prefab to a NetworkPrefabsList (prefab must have NetworkObject)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "prefabs", "list", "add" },
            Outputs = new[] { "success", "count" },
            MutatesAssets = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddToPrefabsList(
            string listPath,
            string prefabPath,
            string overrideMode = "None",
            string sourcePrefabPath = null,
            uint sourceHash = 0,
            string targetPrefabPath = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.Required(listPath, "listPath") is object e1) return e1;
            if (Validate.Required(prefabPath, "prefabPath") is object e2) return e2;
            if (!Enum.TryParse<NetworkPrefabOverride>(overrideMode, true, out var ovr))
                return new { error = $"Invalid overrideMode '{overrideMode}'. Valid: None, Prefab, Hash." };

            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
            if (list == null) return new { error = $"NetworkPrefabsList not found at {listPath}." };

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}." };
            if (prefab.GetComponent<NetworkObject>() == null)
                return new { error = $"Prefab '{prefab.name}' has no NetworkObject. Add it first via netcode_add_network_object." };

            var np = new NetworkPrefab { Override = ovr, Prefab = prefab };
            if (ovr == NetworkPrefabOverride.Prefab)
            {
                if (string.IsNullOrEmpty(sourcePrefabPath))
                    return new { error = "sourcePrefabPath required when overrideMode=Prefab." };
                np.SourcePrefabToOverride = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
                if (np.SourcePrefabToOverride == null) return new { error = $"sourcePrefab not found at {sourcePrefabPath}." };
                if (string.IsNullOrEmpty(targetPrefabPath)) return new { error = "targetPrefabPath required when overrideMode=Prefab." };
                np.OverridingTargetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetPrefabPath);
                if (np.OverridingTargetPrefab == null) return new { error = $"targetPrefab not found at {targetPrefabPath}." };
            }
            else if (ovr == NetworkPrefabOverride.Hash)
            {
                if (sourceHash == 0) return new { error = "sourceHash required (nonzero) when overrideMode=Hash." };
                np.SourceHashToOverride = sourceHash;
                if (string.IsNullOrEmpty(targetPrefabPath)) return new { error = "targetPrefabPath required when overrideMode=Hash." };
                np.OverridingTargetPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(targetPrefabPath);
                if (np.OverridingTargetPrefab == null) return new { error = $"targetPrefab not found at {targetPrefabPath}." };
            }

            if (list.Contains(prefab))
                return new { error = $"Prefab '{prefab.name}' already in list." };

            Undo.RecordObject(list, "Add to NetworkPrefabsList");
            list.Add(np);
            EditorUtility.SetDirty(list);
            AssetDatabase.SaveAssets();
            WorkflowManager.SnapshotObject(list);

            return new { success = true, count = list.PrefabList.Count };
#endif
        }

        [UnitySkill("netcode_remove_from_prefabs_list",
            "Remove a prefab from a NetworkPrefabsList",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Delete,
            Tags = new[] { "netcode", "ngo", "prefabs", "list", "remove" },
            Outputs = new[] { "success", "count" },
            MutatesAssets = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object RemoveFromPrefabsList(string listPath, string prefabPath)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
            if (list == null) return new { error = $"NetworkPrefabsList not found at {listPath}." };
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}." };

            var entry = list.PrefabList.FirstOrDefault(p => p.Prefab == prefab);
            if (entry == null) return new { error = $"Prefab '{prefab.name}' not in list." };

            Undo.RecordObject(list, "Remove from NetworkPrefabsList");
            list.Remove(entry);
            EditorUtility.SetDirty(list);
            AssetDatabase.SaveAssets();
            WorkflowManager.SnapshotObject(list);

            return new { success = true, count = list.PrefabList.Count };
#endif
        }

        [UnitySkill("netcode_list_network_prefabs",
            "List all entries in a NetworkPrefabsList (name, override mode, hash)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "prefabs", "list" },
            Outputs = new[] { "count", "entries" },
            ReadOnly = true)]
        public static object ListNetworkPrefabs(string listPath)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(listPath);
            if (list == null) return new { error = $"NetworkPrefabsList not found at {listPath}." };

            var entries = list.PrefabList.Select(p => new
            {
                prefab = p.Prefab != null ? p.Prefab.name : null,
                overrideMode = p.Override.ToString(),
                sourcePrefab = p.SourcePrefabToOverride != null ? p.SourcePrefabToOverride.name : null,
                sourceHash = p.SourceHashToOverride,
                targetPrefab = p.OverridingTargetPrefab != null ? p.OverridingTargetPrefab.name : null,
                sourceGlobalHash = SafeSourceHash(p),
                targetGlobalHash = SafeTargetHash(p)
            }).ToArray();
            return new { count = entries.Length, entries };
#endif
        }

#if NETCODE_GAMEOBJECTS
        private static uint GetGlobalObjectIdHash(NetworkObject networkObject)
        {
            return networkObject != null ? networkObject.PrefabIdHash : 0;
        }

        private static uint SafeSourceHash(NetworkPrefab p)
        {
            try { return p.SourcePrefabGlobalObjectIdHash; } catch { return 0; }
        }
        private static uint SafeTargetHash(NetworkPrefab p)
        {
            try { return p.TargetPrefabGlobalObjectIdHash; } catch { return 0; }
        }
#endif

        [UnitySkill("netcode_set_player_prefab",
            "Assign a prefab as the NetworkConfig.PlayerPrefab (prefab must have NetworkObject and be in a prefabs list)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "player", "prefab" },
            Outputs = new[] { "success", "prefab" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object SetPlayerPrefab(string prefabPath, string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.Required(prefabPath, "prefabPath") is object e) return e;

            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}." };
            if (prefab.GetComponent<NetworkObject>() == null)
                return new { error = $"Prefab '{prefab.name}' has no NetworkObject. Add it first." };

            Undo.RecordObject(nm, "Set PlayerPrefab");
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.PlayerPrefab = prefab;
            EditorUtility.SetDirty(nm);
            WorkflowManager.SnapshotObject(nm);

            var lists = nm.NetworkConfig.Prefabs?.NetworkPrefabsLists;
            bool inList = lists != null && lists.Any(l => l != null && l.Contains(prefab));

            return new
            {
                success = true,
                prefab = prefab.name,
                prefabInList = inList,
                warning = inList ? null : "PlayerPrefab is not in any assigned NetworkPrefabsList; Netcode 2.x may reject this at runtime."
            };
#endif
        }

        // ==================================================================================
        // 5. Components (6 skills)
        // ==================================================================================

        [UnitySkill("netcode_add_network_transform",
            "Add NetworkTransform component (syncs position/rotation/scale automatically)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "networktransform", "sync" },
            Outputs = new[] { "success" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddNetworkTransform(
            string name = null,
            int instanceId = 0,
            string path = null,
            bool? interpolate = null,
            bool? inLocalSpace = null,
            bool? syncPositionX = null, bool? syncPositionY = null, bool? syncPositionZ = null,
            bool? syncRotAngleX = null, bool? syncRotAngleY = null, bool? syncRotAngleZ = null,
            bool? syncScaleX = null, bool? syncScaleY = null, bool? syncScaleZ = null,
            bool? useHalfFloatPrecision = null,
            bool? useQuaternionSynchronization = null,
            bool? useQuaternionCompression = null,
            bool? slerpPosition = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            if (go.GetComponent<NetworkObject>() == null)
                return new { error = $"'{go.name}' lacks a NetworkObject component. Add one first (NetworkTransform requires NetworkBehaviour's NetworkObject)." };
            if (go.GetComponent<NetworkTransform>() != null)
                return new { error = $"'{go.name}' already has a NetworkTransform." };

            var nt = Undo.AddComponent<NetworkTransform>(go);
            ApplyNetworkTransformFields(nt, interpolate, inLocalSpace,
                syncPositionX, syncPositionY, syncPositionZ,
                syncRotAngleX, syncRotAngleY, syncRotAngleZ,
                syncScaleX, syncScaleY, syncScaleZ,
                useHalfFloatPrecision, useQuaternionSynchronization, useQuaternionCompression, slerpPosition);

            EditorUtility.SetDirty(go);
            WorkflowManager.SnapshotObject(go);
            return new { success = true };
#endif
        }

        [UnitySkill("netcode_configure_network_transform",
            "Modify fields of an existing NetworkTransform (sync axes, thresholds, compression)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "networktransform", "configure" },
            Outputs = new[] { "success" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object ConfigureNetworkTransform(
            string name = null,
            int instanceId = 0,
            string path = null,
            bool? interpolate = null,
            bool? inLocalSpace = null,
            bool? syncPositionX = null, bool? syncPositionY = null, bool? syncPositionZ = null,
            bool? syncRotAngleX = null, bool? syncRotAngleY = null, bool? syncRotAngleZ = null,
            bool? syncScaleX = null, bool? syncScaleY = null, bool? syncScaleZ = null,
            bool? useHalfFloatPrecision = null,
            bool? useQuaternionSynchronization = null,
            bool? useQuaternionCompression = null,
            bool? slerpPosition = null,
            float? positionThreshold = null,
            float? rotAngleThreshold = null,
            float? scaleThreshold = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            var nt = go.GetComponent<NetworkTransform>();
            if (nt == null) return new { error = $"'{go.name}' has no NetworkTransform." };

            Undo.RecordObject(nt, "Configure NetworkTransform");
            ApplyNetworkTransformFields(nt, interpolate, inLocalSpace,
                syncPositionX, syncPositionY, syncPositionZ,
                syncRotAngleX, syncRotAngleY, syncRotAngleZ,
                syncScaleX, syncScaleY, syncScaleZ,
                useHalfFloatPrecision, useQuaternionSynchronization, useQuaternionCompression, slerpPosition);
            if (positionThreshold.HasValue) nt.PositionThreshold = positionThreshold.Value;
            if (rotAngleThreshold.HasValue) nt.RotAngleThreshold = rotAngleThreshold.Value;
            if (scaleThreshold.HasValue) nt.ScaleThreshold = scaleThreshold.Value;

            EditorUtility.SetDirty(nt);
            WorkflowManager.SnapshotObject(nt);
            return new { success = true };
#endif
        }

#if NETCODE_GAMEOBJECTS
        private static void ApplyNetworkTransformFields(
            NetworkTransform nt,
            bool? interpolate, bool? inLocalSpace,
            bool? syncPositionX, bool? syncPositionY, bool? syncPositionZ,
            bool? syncRotAngleX, bool? syncRotAngleY, bool? syncRotAngleZ,
            bool? syncScaleX, bool? syncScaleY, bool? syncScaleZ,
            bool? useHalfFloatPrecision,
            bool? useQuaternionSynchronization,
            bool? useQuaternionCompression,
            bool? slerpPosition)
        {
            if (interpolate.HasValue) nt.Interpolate = interpolate.Value;
            if (inLocalSpace.HasValue) nt.InLocalSpace = inLocalSpace.Value;
            if (syncPositionX.HasValue) nt.SyncPositionX = syncPositionX.Value;
            if (syncPositionY.HasValue) nt.SyncPositionY = syncPositionY.Value;
            if (syncPositionZ.HasValue) nt.SyncPositionZ = syncPositionZ.Value;
            if (syncRotAngleX.HasValue) nt.SyncRotAngleX = syncRotAngleX.Value;
            if (syncRotAngleY.HasValue) nt.SyncRotAngleY = syncRotAngleY.Value;
            if (syncRotAngleZ.HasValue) nt.SyncRotAngleZ = syncRotAngleZ.Value;
            if (syncScaleX.HasValue) nt.SyncScaleX = syncScaleX.Value;
            if (syncScaleY.HasValue) nt.SyncScaleY = syncScaleY.Value;
            if (syncScaleZ.HasValue) nt.SyncScaleZ = syncScaleZ.Value;
            if (useHalfFloatPrecision.HasValue) nt.UseHalfFloatPrecision = useHalfFloatPrecision.Value;
            if (useQuaternionSynchronization.HasValue) nt.UseQuaternionSynchronization = useQuaternionSynchronization.Value;
            if (useQuaternionCompression.HasValue) nt.UseQuaternionCompression = useQuaternionCompression.Value;
            if (slerpPosition.HasValue) nt.SlerpPosition = slerpPosition.Value;
        }
#endif

        [UnitySkill("netcode_add_network_rigidbody",
            "Add NetworkRigidbody or NetworkRigidbody2D component (requires Rigidbody/Rigidbody2D)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "networkrigidbody", "physics" },
            Outputs = new[] { "success", "type" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddNetworkRigidbody(
            string name = null,
            int instanceId = 0,
            string path = null,
            bool useRigidbody2D = false,
            bool? useRigidBodyForMotion = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            if (go.GetComponent<NetworkObject>() == null)
                return new { error = $"'{go.name}' lacks a NetworkObject component. Add one first." };

            if (useRigidbody2D)
            {
                if (go.GetComponent<Rigidbody2D>() == null)
                    return new { error = $"'{go.name}' lacks a Rigidbody2D. Add it before NetworkRigidbody2D." };
                if (go.GetComponent<NetworkRigidbody2D>() != null)
                    return new { error = $"'{go.name}' already has a NetworkRigidbody2D." };
                var nrb2d = Undo.AddComponent<NetworkRigidbody2D>(go);
                if (useRigidBodyForMotion.HasValue) nrb2d.UseRigidBodyForMotion = useRigidBodyForMotion.Value;
                EditorUtility.SetDirty(go);
                WorkflowManager.SnapshotObject(go);
                return new { success = true, type = nameof(NetworkRigidbody2D) };
            }
            else
            {
                if (go.GetComponent<Rigidbody>() == null)
                    return new { error = $"'{go.name}' lacks a Rigidbody. Add it before NetworkRigidbody." };
                if (go.GetComponent<NetworkRigidbody>() != null)
                    return new { error = $"'{go.name}' already has a NetworkRigidbody." };
                var nrb = Undo.AddComponent<NetworkRigidbody>(go);
                if (useRigidBodyForMotion.HasValue) nrb.UseRigidBodyForMotion = useRigidBodyForMotion.Value;
                EditorUtility.SetDirty(go);
                WorkflowManager.SnapshotObject(go);
                return new { success = true, type = nameof(NetworkRigidbody) };
            }
#endif
        }

        [UnitySkill("netcode_add_network_animator",
            "Add NetworkAnimator component (requires Animator on same GameObject)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "networkanimator", "animation" },
            Outputs = new[] { "success" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddNetworkAnimator(string name = null, int instanceId = 0, string path = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return findErr;
            if (go.GetComponent<NetworkObject>() == null)
                return new { error = $"'{go.name}' lacks a NetworkObject component." };
            if (go.GetComponent<Animator>() == null)
                return new { error = $"'{go.name}' lacks an Animator. Add one first." };
            if (go.GetComponent<NetworkAnimator>() != null)
                return new { error = $"'{go.name}' already has a NetworkAnimator." };

            Undo.AddComponent<NetworkAnimator>(go);
            EditorUtility.SetDirty(go);
            WorkflowManager.SnapshotObject(go);
            return new { success = true };
#endif
        }

        [UnitySkill("netcode_add_network_behaviour_script",
            "Generate a NetworkBehaviour C# script template at the given path (OnNetworkSpawn/Despawn, optional NetworkVariable / Rpc / Ownership callbacks)",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Create,
            Tags = new[] { "netcode", "ngo", "networkbehaviour", "script", "template" },
            Outputs = new[] { "success", "path" },
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object AddNetworkBehaviourScript(
            string className,
            string path,
            bool includeRpc = true,
            bool includeNetworkVariable = true,
            bool includeOwnershipCallbacks = false,
            string namespaceName = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (Validate.Required(className, "className") is object e1) return e1;
            if (Validate.SafePath(path, "path") is object e2) return e2;
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return new { error = "path must end with '.cs'." };
            if (System.IO.File.Exists(path))
                return new { error = $"File already exists at {path}." };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("using Unity.Netcode;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            bool hasNs = !string.IsNullOrEmpty(namespaceName);
            if (hasNs) { sb.AppendLine($"namespace {namespaceName}"); sb.AppendLine("{"); }

            string indent = hasNs ? "    " : "";
            sb.AppendLine($"{indent}public class {className} : NetworkBehaviour");
            sb.AppendLine($"{indent}{{");

            if (includeNetworkVariable)
            {
                sb.AppendLine($"{indent}    // NetworkVariable — declared at field scope (required by ILPP).");
                sb.AppendLine($"{indent}    public NetworkVariable<int> ExampleValue = new NetworkVariable<int>(");
                sb.AppendLine($"{indent}        0,");
                sb.AppendLine($"{indent}        NetworkVariableReadPermission.Everyone,");
                sb.AppendLine($"{indent}        NetworkVariableWritePermission.Server);");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}    public override void OnNetworkSpawn()");
            sb.AppendLine($"{indent}    {{");
            sb.AppendLine($"{indent}        base.OnNetworkSpawn();");
            if (includeNetworkVariable)
            {
                sb.AppendLine($"{indent}        ExampleValue.OnValueChanged += OnExampleValueChanged;");
                sb.AppendLine($"{indent}        if (IsServer) ExampleValue.Value = 100;");
            }
            sb.AppendLine($"{indent}        // TODO: init local player state inside `if (IsOwner) {{ ... }}`");
            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    public override void OnNetworkDespawn()");
            sb.AppendLine($"{indent}    {{");
            if (includeNetworkVariable)
                sb.AppendLine($"{indent}        ExampleValue.OnValueChanged -= OnExampleValueChanged;");
            sb.AppendLine($"{indent}        base.OnNetworkDespawn();");
            sb.AppendLine($"{indent}    }}");

            if (includeNetworkVariable)
            {
                sb.AppendLine();
                sb.AppendLine($"{indent}    private void OnExampleValueChanged(int oldVal, int newVal) {{ /* UI / FX */ }}");
            }

            if (includeOwnershipCallbacks)
            {
                sb.AppendLine();
                sb.AppendLine($"{indent}    public override void OnGainedOwnership() {{ base.OnGainedOwnership(); }}");
                sb.AppendLine($"{indent}    public override void OnLostOwnership() {{ base.OnLostOwnership(); }}");
            }

            if (includeRpc)
            {
                sb.AppendLine();
                sb.AppendLine($"{indent}    // Client -> Server request");
                sb.AppendLine($"{indent}    [Rpc(SendTo.Server)]");
                sb.AppendLine($"{indent}    public void DoSomethingServerRpc(int payload, RpcParams rpcParams = default)");
                sb.AppendLine($"{indent}    {{");
                sb.AppendLine($"{indent}        // Optional owner check: rpcParams.Receive.SenderClientId == OwnerClientId");
                if (includeNetworkVariable)
                    sb.AppendLine($"{indent}        ExampleValue.Value += payload;");
                sb.AppendLine($"{indent}    }}");
                sb.AppendLine();
                sb.AppendLine($"{indent}    // Server -> clients broadcast");
                sb.AppendLine($"{indent}    [Rpc(SendTo.ClientsAndHost)]");
                sb.AppendLine($"{indent}    public void NotifyClientsRpc(int payload) {{ /* play fx / update UI */ }}");
            }

            sb.AppendLine($"{indent}}}");
            if (hasNs) sb.AppendLine("}");

            Validate.EnsureDirectoryExists(path);
            System.IO.File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);
            return new { success = true, path, className };
#endif
        }

        [UnitySkill("netcode_list_network_behaviours",
            "List all NetworkBehaviour subclass instances in the scene (by derived type)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "networkbehaviour", "list" },
            Outputs = new[] { "count", "behaviours" },
            ReadOnly = true)]
        public static object ListNetworkBehaviours(bool includeInactive = true)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var all = FindHelper.FindAll<NetworkBehaviour>(includeInactive: includeInactive);
            var list = all.Select(nb => new
            {
                type = nb.GetType().Name,
                gameObject = nb.gameObject.name,
                instanceId = nb.gameObject.GetInstanceID(),
                isSpawned = Application.isPlaying && nb.IsSpawned,
                isOwner = Application.isPlaying && nb.IsOwner
            }).ToArray();
            return new { count = list.Length, behaviours = list };
#endif
        }

        // ==================================================================================
        // 6. Scene & Spawning query (3 skills)
        // ==================================================================================

        [UnitySkill("netcode_configure_scene_management",
            "Toggle EnableSceneManagement and related scene config fields",
            TracksWorkflow = true,
            Category = SkillCategory.Netcode, Operation = SkillOperation.Modify,
            Tags = new[] { "netcode", "ngo", "scene", "config" },
            Outputs = new[] { "success", "enableSceneManagement" },
            MutatesScene = true, RiskLevel = "low", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object ConfigureSceneManagement(
            string name = null,
            bool? enable = null,
            int? timeout = null,
            string clientSynchronizationMode = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();

            Undo.RecordObject(nm, "Configure Scene Management");
            var applied = new Dictionary<string, object>();
            if (enable.HasValue) { nm.NetworkConfig.EnableSceneManagement = enable.Value; applied["enableSceneManagement"] = enable.Value; }
            if (timeout.HasValue) { nm.NetworkConfig.LoadSceneTimeOut = timeout.Value; applied["loadSceneTimeOut"] = timeout.Value; }

            if (!string.IsNullOrEmpty(clientSynchronizationMode))
            {
                if (!Enum.TryParse<LoadSceneMode>(clientSynchronizationMode, true, out var mode))
                    return new { error = $"Invalid clientSynchronizationMode '{clientSynchronizationMode}'. Valid: Single, Additive." };
                if (Application.isPlaying && nm.SceneManager != null)
                {
                    nm.SceneManager.SetClientSynchronizationMode(mode);
                    applied["clientSynchronizationMode"] = mode.ToString();
                    applied["note"] = "Applied to live SceneManager (runtime).";
                }
                else
                {
                    applied["warning"] = "clientSynchronizationMode is a runtime call; requires NetworkManager to be started.";
                }
            }

            EditorUtility.SetDirty(nm);
            WorkflowManager.SnapshotObject(nm);
            return new { success = true, applied };
#endif
        }

        [UnitySkill("netcode_get_spawn_manager_info",
            "Runtime: list SpawnManager.SpawnedObjects (requires NetworkManager started)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "spawn", "manager", "runtime" },
            Outputs = new[] { "running", "spawnedCount", "objects" },
            ReadOnly = true)]
        public static object GetSpawnManagerInfo(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying)
                return new { running = false, error = "SpawnManager only accessible in PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { running = false, error = "NetworkManager not found." };
            if (!nm.IsListening) return new { running = false, error = "NetworkManager is not started." };

            var sm = nm.SpawnManager;
            if (sm == null) return new { running = false, error = "SpawnManager not initialized." };

            var items = sm.SpawnedObjects.Values.Select(no => new
            {
                networkObjectId = no.NetworkObjectId,
                globalObjectIdHash = GetGlobalObjectIdHash(no),
                name = no.gameObject.name,
                ownerClientId = no.OwnerClientId,
                isPlayerObject = no.IsPlayerObject
            }).ToArray();

            return new { running = true, spawnedCount = items.Length, objects = items };
#endif
        }

        [UnitySkill("netcode_get_scene_manager_info",
            "Runtime: read NetworkSceneManager state (loaded scenes, client sync mode)",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "scene", "manager", "runtime" },
            Outputs = new[] { "running", "activeScene", "loadedScenes" },
            ReadOnly = true)]
        public static object GetSceneManagerInfo(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying)
                return new { running = false, error = "SceneManager info only available in PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { running = false, error = "NetworkManager not found." };
            if (!nm.IsListening) return new { running = false, error = "NetworkManager is not started." };
            if (nm.SceneManager == null) return new { running = false, error = "NetworkSceneManager not initialized (EnableSceneManagement=false?)." };

            var loaded = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                loaded.Add(new { name = s.name, isLoaded = s.isLoaded, buildIndex = s.buildIndex, isActive = s == SceneManager.GetActiveScene() });
            }
            return new
            {
                running = true,
                activeScene = SceneManager.GetActiveScene().name,
                loadedSceneCount = loaded.Count,
                loadedScenes = loaded
            };
#endif
        }

        // ==================================================================================
        // 7. Runtime Control (5 skills; all MayEnterPlayMode)
        // ==================================================================================

        [UnitySkill("netcode_start_host",
            "Call NetworkManager.Singleton.StartHost() at runtime",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Execute,
            Tags = new[] { "netcode", "ngo", "runtime", "host", "start" },
            Outputs = new[] { "success", "isHost" },
            MayEnterPlayMode = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object StartHost(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying) return new { success = false, error = "StartHost requires PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { success = false, error = "NetworkManager not found." };
            if (nm.IsListening) return new { success = false, error = "NetworkManager is already listening. Call netcode_shutdown first." };

            bool ok = nm.StartHost();
            return new { success = ok, isHost = nm.IsHost, isListening = nm.IsListening };
#endif
        }

        [UnitySkill("netcode_start_server",
            "Call NetworkManager.Singleton.StartServer() at runtime",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Execute,
            Tags = new[] { "netcode", "ngo", "runtime", "server", "start" },
            Outputs = new[] { "success", "isServer" },
            MayEnterPlayMode = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object StartServer(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying) return new { success = false, error = "StartServer requires PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { success = false, error = "NetworkManager not found." };
            if (nm.IsListening) return new { success = false, error = "NetworkManager is already listening." };

            bool ok = nm.StartServer();
            return new { success = ok, isServer = nm.IsServer, isListening = nm.IsListening };
#endif
        }

        [UnitySkill("netcode_start_client",
            "Call NetworkManager.Singleton.StartClient() at runtime",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Execute,
            Tags = new[] { "netcode", "ngo", "runtime", "client", "start" },
            Outputs = new[] { "success", "isClient" },
            MayEnterPlayMode = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object StartClient(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying) return new { success = false, error = "StartClient requires PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { success = false, error = "NetworkManager not found." };
            if (nm.IsListening) return new { success = false, error = "NetworkManager is already listening." };

            bool ok = nm.StartClient();
            return new { success = ok, isClient = nm.IsClient, isListening = nm.IsListening };
#endif
        }

        [UnitySkill("netcode_shutdown",
            "Call NetworkManager.Shutdown() to stop host/server/client",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Execute,
            Tags = new[] { "netcode", "ngo", "runtime", "shutdown" },
            Outputs = new[] { "success" },
            MayEnterPlayMode = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.netcode.gameobjects" })]
        public static object Shutdown(bool discardMessageQueue = false, string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            if (!Application.isPlaying) return new { success = false, error = "Shutdown requires PlayMode." };
            var nm = FindManager(name);
            if (nm == null) return new { success = false, error = "NetworkManager not found." };
            if (!nm.IsListening) return new { success = true, noop = true, message = "NetworkManager is not listening; nothing to shutdown." };

            nm.Shutdown(discardMessageQueue);
            return new { success = true, shutdownInProgress = nm.ShutdownInProgress };
#endif
        }

        [UnitySkill("netcode_get_status",
            "Runtime: read IsHost/IsServer/IsClient/IsListening/LocalClientId/ConnectedClients/NetworkTime",
            Category = SkillCategory.Netcode, Operation = SkillOperation.Query,
            Tags = new[] { "netcode", "ngo", "runtime", "status" },
            Outputs = new[] { "isListening", "isHost", "isServer", "isClient", "localClientId", "connectedClientsCount", "networkTime" },
            ReadOnly = true)]
        public static object GetStatus(string name = null)
        {
#if !NETCODE_GAMEOBJECTS
            return NoNetcode();
#else
            var nm = FindManager(name);
            if (nm == null) return new { error = "NetworkManager not found." };
            if (!Application.isPlaying)
                return new { isListening = false, note = "Not in PlayMode." };

            return new
            {
                isListening = nm.IsListening,
                isHost = nm.IsHost,
                isServer = nm.IsServer,
                isClient = nm.IsClient,
                isConnectedClient = nm.IsConnectedClient,
                localClientId = nm.LocalClientId,
                connectedClientsCount = nm.IsServer && nm.ConnectedClientsIds != null ? nm.ConnectedClientsIds.Count : 0,
                networkTime = nm.IsListening ? nm.ServerTime.Time : 0.0,
                tick = nm.IsListening ? nm.ServerTime.Tick : 0
            };
#endif
        }

        // ==================================================================================
        // Internal helpers
        // ==================================================================================

#if NETCODE_GAMEOBJECTS
        private static NetworkManager FindManager(string name)
        {
            var all = FindHelper.FindAll<NetworkManager>(includeInactive: true);
            if (all.Length == 0) return null;
            if (string.IsNullOrEmpty(name)) return all[0];
            return all.FirstOrDefault(n => string.Equals(n.gameObject.name, name, StringComparison.Ordinal))
                ?? all.FirstOrDefault(n => string.Equals(n.gameObject.name, name, StringComparison.OrdinalIgnoreCase));
        }
#endif
    }
}
