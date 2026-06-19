---
name: unity-netcode-design
description: Source-anchored design rules for Netcode for GameObjects 2.x — lifecycle, ownership, RPCs, NetworkVariables, spawning, scene management, transport, and pitfalls. Use when writing or reviewing multiplayer code, designing server/distributed authority, wiring RPCs or NetworkVariables, or debugging netcode, even if the user just says "联机" or "多人同步". 为 Netcode for GameObjects 2.x 提供源码锚定的设计规则(生命周期、所有权、RPC、NetworkVariable、生成、场景管理、传输、陷阱);当用户要编写或审查多人联机代码、设计服务器/分布式权威、连接 RPC 或网络变量、或排查 netcode 问题时使用。
---

# Netcode for GameObjects - Design Rules

Advisory module. Every rule is distilled from `com.unity.netcode.gameobjects` 2.x source. Each rule cites a concrete file/line so the reasoning is auditable.

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

## When to Load This Module

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
| 5 | `PlayerPrefab` must exist in `NetworkPrefabsList` or `NetworkConfig.Prefabs` or 2.x runtime rejects it. | `Runtime/Configuration/NetworkConfig.cs:40` + `NetworkPrefabsList.cs:14` |
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
| [TRANSPORT.md](./TRANSPORT.md) | UnityTransport direct / Relay / DebugSimulator configuration |
| [PITFALLS.md](./PITFALLS.md) | 30 concrete hallucination pitfalls |

## Routing to Other Modules

- Generating NetworkBehaviour scripts → use the functional `netcode` module's `netcode_add_network_behaviour_script`
- Bulk attaching NetworkObject/NetworkTransform in the scene → `netcode` module Components skills
- Architecture-level decisions (Server-authoritative vs Distributed Authority) → also load [architecture](../architecture/SKILL.md)

## Version Scope

This document targets `com.unity.netcode.gameobjects` **2.x** (validated against 2.11.0, Unity 6000.0+). Some APIs (`SendTo.Authority`, `RpcInvokePermission`, the universal `[Rpc]` attribute) do not exist in 1.x.
