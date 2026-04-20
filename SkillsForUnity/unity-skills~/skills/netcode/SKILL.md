---
name: unity-netcode
description: "Netcode for GameObjects (NGO) multiplayer automation. Create NetworkManager, register NetworkPrefabsList, attach NetworkObject/NetworkTransform, generate NetworkBehaviour templates, and run Host/Server/Client lifecycle control. Triggers: netcode, NGO, multiplayer, NetworkManager, NetworkObject, NetworkBehaviour, NetworkTransform, NetworkVariable, ServerRpc, ClientRpc, Spawn, Despawn, host, client, server, relay, transport, 多人, 联机, 主机, 客户端, 服务器. Requires com.unity.netcode.gameobjects (2.x)."
---

# Unity Netcode for GameObjects Skills

Automation for Netcode for GameObjects (NGO) multiplayer setup and operations. Every skill is source-verified against NGO 2.x; when the package is absent, each skill returns a `NoNetcode()` error with install instructions.

> **Requires**: `com.unity.netcode.gameobjects` (2.x), Unity 6000.0+.
> **Strongly recommended**: before calling any `netcode_*` skill, load [netcode-design](../netcode-design/SKILL.md). NGO lifecycle and permission rules are strict; skills alone cannot prevent incorrect business code.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `netcode_spawn_object` / `netcode_spawn_player` — do not exist. Spawn must happen in runtime code (NetworkBehaviour) via `.Spawn()` or `NetworkManager.SpawnManager.InstantiateAndSpawn`. Skills do not proxy Spawn because Spawn requires a running NetworkManager.
- `netcode_register_scene` — does not exist. Scene registration goes through Build Settings + `EnableSceneManagement`. This module only exposes `netcode_configure_scene_management` for reading/writing the config.
- `netcode_set_tick_rate` / `netcode_set_protocol` as standalone skills — do not exist. Use `netcode_configure_manager` for all NetworkConfig edits.
- Do not assume `netcode_start_host` works in Edit Mode. All Runtime control skills require PlayMode.
- Do not assume `netcode_add_to_prefabs_list` automatically attaches a `NetworkObject` component. Call `netcode_add_network_object` first.

**Routing**:
- Plain GameObject hierarchy creation → `gameobject`
- Attach NetworkObject / NetworkTransform / other networking components → this module
- Generic player-prefab components (Rigidbody / Collider / Animator) → `component`
- Runtime scene switching (LoadScene) → call `NetworkManager.SceneManager.LoadScene` from the generated NetworkBehaviour; this module does not execute it
- Code-level design decisions (RPC direction, NetworkVariable permission) → [netcode-design](../netcode-design/SKILL.md) advisory

## Object Targeting

`netcode_add_*` / `netcode_configure_*` skills use the usual target parameters:
- `name` — scene object name
- `instanceId` — Unity InstanceID (exact)
- `path` — hierarchy path `Parent/Child`

Prefer `instanceId` when there is a chance of duplicate names.

## Skills

### Setup & Validation
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `netcode_check_setup` | Verify package, NetworkManager, Transport, PlayerPrefab, and PrefabsList consistency | `verbose?` |
| `netcode_create_manager` | Create NetworkManager + UnityTransport | `name?` |
| `netcode_configure_manager` | Bulk-edit NetworkConfig (TickRate, ConnectionApproval, EnableSceneManagement, NetworkTopology, ...) | `name?`, 15+ optional fields |
| `netcode_get_manager_info` | Read NetworkConfig + runtime state | `name?` |
| `netcode_remove_manager` | Delete the NetworkManager (must already be Shutdown) | `name?` |

### Transport
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `netcode_set_transport_address` | Direct connection: set Address / Port / ServerListenAddress | `address`, `port`, `serverListenAddress?` |
| `netcode_set_relay_server_data` | Relay mode (mutually exclusive with direct connection) | `address`, `port`, `allocationIdBase64`, `keyBase64`, `connectionDataBase64`, `hostConnectionDataBase64?`, `isSecure?` |
| `netcode_set_debug_simulator` | Simulate latency / jitter / packet loss (development only) | `packetDelay`, `packetJitter`, `dropRate` |
| `netcode_get_transport_info` | Read current transport info | `name?` |

### NetworkObject
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `netcode_add_network_object` | Attach NetworkObject to a GameObject | `name/instanceId/path` + NetworkObject fields |
| `netcode_configure_network_object` | Modify fields on an existing NetworkObject | same as above |
| `netcode_remove_network_object` | Remove a NetworkObject (must not be currently spawned) | same as above |
| `netcode_list_network_objects` | List all NetworkObjects in scene (including runtime state) | `includeInactive?` |
| `netcode_get_network_object_info` | Query a single NetworkObject in detail | same as above |

### NetworkPrefabsList
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `netcode_create_prefabs_list` | Create a NetworkPrefabsList asset | `path`, `assignToManager?` |
| `netcode_add_to_prefabs_list` | Add a prefab (optional override: None/Prefab/Hash) | `listPath`, `prefabPath`, `overrideMode?`, ... |
| `netcode_remove_from_prefabs_list` | Remove a prefab entry | `listPath`, `prefabPath` |
| `netcode_list_network_prefabs` | List every entry with its hash | `listPath` |
| `netcode_set_player_prefab` | Set `NetworkConfig.PlayerPrefab` | `prefabPath`, `name?` |

### Components
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `netcode_add_network_transform` | Attach NetworkTransform with axis sync toggles | target + 15 optional fields |
| `netcode_configure_network_transform` | Edit fields / thresholds on an existing NT | includes PositionThreshold etc. |
| `netcode_add_network_rigidbody` | Attach NetworkRigidbody / NetworkRigidbody2D | `useRigidbody2D?`, `useRigidBodyForMotion?` |
| `netcode_add_network_animator` | Attach NetworkAnimator (Animator required) | target |
| `netcode_add_network_behaviour_script` | Generate a NetworkBehaviour script template (OnNetworkSpawn/Despawn + optional RPC/NetworkVariable/Ownership) | `className`, `path`, `includeRpc?`, `includeNetworkVariable?`, `includeOwnershipCallbacks?` |
| `netcode_list_network_behaviours` | List NetworkBehaviour subclass instances in the scene | `includeInactive?` |

### Scene & Spawning Query
| Skill | Purpose |
|-------|---------|
| `netcode_configure_scene_management` | Set EnableSceneManagement / LoadSceneTimeOut / ClientSynchronizationMode |
| `netcode_get_spawn_manager_info` | Runtime: list SpawnedObjects |
| `netcode_get_scene_manager_info` | Runtime: read scene load state |

### Runtime Control (PlayMode required)
| Skill | Purpose |
|-------|---------|
| `netcode_start_host` | Start Host |
| `netcode_start_server` | Start Server |
| `netcode_start_client` | Start Client |
| `netcode_shutdown` | Shut down (optional `discardMessageQueue`) |
| `netcode_get_status` | Read IsHost / IsServer / IsClient, LocalClientId, ConnectedClients, NetworkTime |

## Quick Start

```python
import unity_skills as u

# 1. Inspect current state
u.call_skill("netcode_check_setup")

# 2. Create NetworkManager + UnityTransport
u.call_skill("netcode_create_manager", name="NetworkManager")

# 3. Configure the NetworkConfig
u.call_skill("netcode_configure_manager",
    tickRate=30,
    connectionApproval=False,
    enableSceneManagement=True,
    networkTopology="ClientServer")

# 4. Transport
u.call_skill("netcode_set_transport_address",
    address="127.0.0.1", port=7777, serverListenAddress="0.0.0.0")

# 5. Player prefab setup
#    NOTE: add_network_object on a prefab path depends on GameObjectFinder support.
#    Safer route: instantiate the prefab in the scene first, then attach.
u.call_skill("netcode_add_network_object", path="Assets/Prefabs/Player.prefab")
u.call_skill("netcode_create_prefabs_list", path="Assets/NetworkPrefabs.asset")
u.call_skill("netcode_add_to_prefabs_list",
    listPath="Assets/NetworkPrefabs.asset",
    prefabPath="Assets/Prefabs/Player.prefab")
u.call_skill("netcode_set_player_prefab", prefabPath="Assets/Prefabs/Player.prefab")

# 6. Generate a NetworkBehaviour template
u.call_skill("netcode_add_network_behaviour_script",
    className="PlayerController",
    path="Assets/Scripts/PlayerController.cs",
    includeRpc=True, includeNetworkVariable=True)

# 7. Drive the session in PlayMode
u.call_skill("editor_play")   # enter PlayMode
u.call_skill("netcode_start_host")
u.call_skill("netcode_get_status")
u.call_skill("netcode_shutdown")
u.call_skill("editor_stop")
```

## Critical Rules (must read)

1. **Spawn/Despawn are not exposed as skills.** They must be called from NetworkBehaviour runtime code (Server authority). Skills handle prefab registration and NetworkManager lifecycle only.
2. **PlayerPrefab must be in a NetworkPrefabsList** (enforced at runtime on 2.x). Register it with `netcode_add_to_prefabs_list`.
3. **Runtime control skills (start_*/shutdown) require PlayMode.** Calls from Edit Mode return an error.
4. **Address vs ServerListenAddress have different meaning.** On a client, `Address` is the target server IP. On a server, `ServerListenAddress` is the bind address (usually `0.0.0.0`).
5. **`useRigidbody2D`** switches between NetworkRigidbody and NetworkRigidbody2D. Unrelated physics settings (e.g. `Physics2D.AutoSyncTransforms`) live elsewhere.

## Version Scope

Targets NGO **2.x** (validated against 2.11.0). Legacy 1.x (old prefabs list layout, different RPC model) is out of scope for this module.

## Exact Signatures

For exact parameter names, defaults, and return fields, query `GET /skills/schema` or `unity_skills.get_skill_schema()`. This document is a routing and best-practice guide, not the authoritative signature source.
