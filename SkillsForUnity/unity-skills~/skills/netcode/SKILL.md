---
name: unity-netcode
description: Set up Netcode for GameObjects (NGO 2.x) multiplayer
---

# Unity Netcode for GameObjects Skills

Setup and operation of Netcode for GameObjects (NGO) multiplayer. Parameters come from the schema (`GET /skills/schema?category=Netcode&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Walkthrough: [reference/quick-start.md](reference/quick-start.md). NGO 2.5+ attach features: [reference/ngo-2-5-features.md](reference/ngo-2-5-features.md). Code-level design (RPC direction, NetworkVariable permissions): [netcode-design](../netcode-design/SKILL.md).

## When to use
- Scaffolding multiplayer, or registering network prefabs
- Adding NetworkObject, NetworkTransform or NetworkRigidbody components
- Starting a host, server or client in Play Mode

## Rules
- Load [netcode-design](../netcode-design/SKILL.md) before writing NetworkBehaviour code; NGO lifecycle and permission rules are strict.
- Targets NGO 2.x, validated against 2.13.3, on Unity 6000.0+. Target by `entityId` (preferred), `name`, `instanceId` or `path` (`Parent/Child`).
- Spawn and Despawn are not skills. Call them from NetworkBehaviour code on the server, with a running NetworkManager.
- Listing the PlayerPrefab in a NetworkPrefabsList is recommended, not enforced. An explicit entry is the only way to set an override.
- `netcode_add_to_prefabs_list` does not add a NetworkObject. Call `netcode_add_network_object` first.
- Runtime control (`netcode_start_*`, `netcode_shutdown`, `netcode_get_status`) needs Play Mode; Edit Mode calls return an error.
- Address and ServerListenAddress differ: on a client `address` is the server; on a server `serverListenAddress` is the bind address.
- `useRigidbody2D` switches between NetworkRigidbody and NetworkRigidbody2D.
- Operating mode: query and info skills run in every mode. Creators and configurators need a grant under Approval.
- Refused under Approval and Auto, and run only in Bypass or when allowlisted: deletes (`netcode_remove_*`), `netcode_add_network_behaviour_script` (writes a `.cs`, triggers a reload) and the Play Mode controls (`netcode_start_*`, `netcode_shutdown`).
- Without `com.unity.netcode.gameobjects`, every skill returns a `NoNetcode()` error with install instructions. Outside 2.x every skill except `netcode_version` returns `SKILL_ERROR` with `reason: unsupported_package_version`.

## Pitfalls
- NGO 3.x is not supported: it needs Unity 6000.7 (beta) and this module has not been adapted to it. Tell the user they can pin `com.unity.netcode.gameobjects` to 2.13.3; the version change is their call.
- `netcode_set_debug_simulator` always returns an error and writes nothing: UnityTransport's debug simulator has done nothing since NGO 2.2.0. Use the Network Simulator in `com.unity.multiplayer.tools`.
- `netcode_configure_manager` is the one place for NetworkConfig edits such as tick rate and protocol.

## Skills Overview

| Group | Skills | Purpose |
|-------|--------|---------|
| Setup and validation | `netcode_check_setup`, `netcode_create_manager`, `netcode_configure_manager`, `netcode_get_manager_info`, `netcode_remove_manager` | Verify and create the NetworkManager; bulk-edit NetworkConfig |
| Transport | `netcode_set_transport_address`, `netcode_set_relay_server_data`, `netcode_set_debug_simulator`, `netcode_get_transport_info` | Direct or relay connection; the simulator is non-functional |
| NetworkObject | `netcode_add_network_object`, `netcode_configure_network_object`, `netcode_remove_network_object`, `netcode_list_network_objects`, `netcode_get_network_object_info` | Attach and inspect NetworkObjects |
| Prefabs list | `netcode_create_prefabs_list`, `netcode_add_to_prefabs_list`, `netcode_remove_from_prefabs_list`, `netcode_list_network_prefabs`, `netcode_set_player_prefab` | Network prefab registration and the player prefab |
| Components | `netcode_add_network_transform`, `netcode_configure_network_transform`, `netcode_add_network_rigidbody`, `netcode_add_network_animator`, `netcode_add_network_behaviour_script`, `netcode_list_network_behaviours` | NetworkTransform, NetworkRigidbody, NetworkAnimator, NetworkBehaviour templates |
| Scene and spawning | `netcode_configure_scene_management`, `netcode_get_spawn_manager_info`, `netcode_get_scene_manager_info` | Scene management config and spawn or scene state |
| Runtime control (Play Mode) | `netcode_start_host`, `netcode_start_server`, `netcode_start_client`, `netcode_shutdown`, `netcode_get_status` | Start and stop sessions; IsHost, IsServer, IsClient, NetworkTime |
| NGO 2.5+ attach | `netcode_version`, `netcode_attachable_info`, `netcode_attachable_add`, `netcode_attachable_node_add`, `netcode_component_controller_add`, `netcode_component_controller_configure` | AttachableBehaviour, AttachableNode and ComponentController; details in the reference |

Parameters of each skill come from the schema. Key parameters for the ones most often misread:
- `netcode_set_relay_server_data`: relay mode, mutually exclusive with a direct address.
- `netcode_configure_scene_management`: `EnableSceneManagement`, `LoadSceneTimeOut`, `ClientSynchronizationMode`.
- `netcode_shutdown`: optional `discardMessageQueue`.

**DO NOT** (common hallucinations):
- `netcode_spawn_object` and `netcode_spawn_player` do not exist: spawn from runtime code with `.Spawn()` or `NetworkManager.SpawnManager.InstantiateAndSpawn`.
- `netcode_register_scene` does not exist: scene registration goes through Build Settings and `EnableSceneManagement`.
- `netcode_set_tick_rate` and `netcode_set_protocol` do not exist: use `netcode_configure_manager`.
- Do not assume `netcode_start_host` works in Edit Mode.
