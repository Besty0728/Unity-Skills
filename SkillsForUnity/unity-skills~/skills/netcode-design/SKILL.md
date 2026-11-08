---
name: unity-netcode-design
description: Source-anchored design rules for Netcode for GameObjects 2.x
---

# Netcode for GameObjects - Design Rules

**Version scope:** `com.unity.netcode.gameobjects` 2.x. Every rule is distilled from its source, with a file and line cited so the reasoning is auditable.

Advisory module. Every rule is distilled from `com.unity.netcode.gameobjects` 2.x source. Each rule cites a concrete file/line so the reasoning is auditable.

## When to use

Load before writing or reviewing any of:
- NetworkBehaviour scripts (OnNetworkSpawn / OnNetworkDespawn / RPCs / NetworkVariables)
- NetworkManager startup / shutdown / scene switching
- NetworkObject Spawn / Despawn / ChangeOwnership / TrySetParent
- UnityTransport configuration (direct connect / Relay)
- Any code that branches on IsHost / IsServer / IsClient / IsOwner

## Critical Rule Summary (memorize even if you skip the sub-docs)

| # | Rule | Source anchor |
|---|------|---------------|
| 1 | `Spawn()` / `Despawn()` must be called on Server/Host only; client-side calls fail. | `Runtime/Core/NetworkObject.cs:1884, 1921` |
| 2 | `OnNetworkSpawn()` runs **before** Unity `Start()` and **after** `Awake`/`OnEnable`. | `Runtime/Core/NetworkBehaviour.cs:704` + `InvokeBehaviourNetworkSpawn` callers |
| 3 | Legacy `[ServerRpc]` method names **must** end with `ServerRpc`; `[ClientRpc]` must end with `ClientRpc` (ILPP enforced at compile time). | `Editor/CodeGen/` ILPP validators |
| 4 | New `[Rpc(SendTo.X)]` has no naming constraint; `SendTo` has 11 values. | `Runtime/Messaging/RpcTargets/RpcTarget.cs:9-80` |
| 5 | `PlayerPrefab` needs a `NetworkObject`; `NetworkManager` registers it into the prefab list itself at start, so a list entry is only needed for overrides. | `Runtime/Configuration/NetworkConfig.cs:40` + `Runtime/Spawning/NetworkPrefabHandler.cs:452` |
| 6 | **Nested NetworkObjects are forbidden** (NetworkObject inside another NetworkObject prefab). Re-parent at runtime via `TrySetParent`. | `Runtime/Core/NetworkObject.cs:2135-2215` |
| 7 | `NetworkVariable<T>` requires `T` to be `unmanaged` or implement `INetworkSerializable`. `string`, `List<>`, `class` are rejected. | `Runtime/NetworkVariable/NetworkVariable.cs:12` + ILPP |
| 8 | `NetworkList<T>` requires `T: unmanaged, IEquatable<T>`. It is NOT `NetworkVariable<List<T>>`. | `Runtime/NetworkVariable/Collections/NetworkList.cs:14` |
| 9 | `NetworkSceneManager.LoadScene/UnloadScene` is Server-only. | `Runtime/SceneManagement/NetworkSceneManager.cs:1496, 1252` |
| 10 | `UnityTransport.SetRelayServerData` and `SetConnectionData` are **mutually exclusive**; use one or the other. | `Runtime/Transports/UTP/UnityTransport.cs:776-897` |

## Sub-doc Routing

| Sub-doc | When to read |
|---------|--------------|
| [LIFECYCLE.md](./LIFECYCLE.md) | Lifecycle, callback ordering, `Awake/OnNetworkSpawn/Start` differences |
| [OWNERSHIP.md](./OWNERSHIP.md) | IsOwner/IsServer/IsHost permission matrix, ChangeOwnership, Distributed Authority |
| [RPC.md](./RPC.md) | Choosing RPC attributes, `SendTo` semantics, `RpcInvokePermission`, deprecated paths |
| [VARIABLES.md](./VARIABLES.md) | NetworkVariable/NetworkList init and serialization constraints |
| [SPAWNING.md](./SPAWNING.md) | Prefab registration → Spawn → Despawn, GlobalObjectIdHash, SpawnAsPlayerObject |
| [SCENE.md](./SCENE.md) | NetworkSceneManager, EnableSceneManagement, in-scene object sync |
| [TRANSPORT.md](./TRANSPORT.md) | UnityTransport direct / Relay configuration; the removed DebugSimulator |
| [PITFALLS.md](./PITFALLS.md) | 30 concrete hallucination pitfalls |

## Routing to Other Modules

- Generating NetworkBehaviour scripts → use the functional `netcode` module's `netcode_add_network_behaviour_script`
- Bulk attaching NetworkObject/NetworkTransform in the scene → `netcode` module Components skills
- Architecture-level decisions (Server-authoritative vs Distributed Authority) → also load [architecture](../architecture/SKILL.md)

## Version Scope

This document targets `com.unity.netcode.gameobjects` **2.x** (validated against 2.13.3, Unity 6000.0+). Some APIs (`SendTo.Authority`, `RpcInvokePermission`, the universal `[Rpc]` attribute) do not exist in 1.x.

NGO **3.x** needs Unity 6000.7 (beta) and the `netcode` module has not been adapted to it yet; the 2.x line is still maintained, and a project on 3.x can pin 2.13.3. Most file:line anchors were taken from earlier 2.x releases, so expect them to be off by a few lines in 2.13.3; the file and symbol names still hold.
