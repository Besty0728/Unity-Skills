---
name: unity-netcode-pitfalls
description: "Netcode for GameObjects pitfalls checklist — the bugs AI agents most often introduce, each with a source anchor; scan before writing or reviewing netcode. Netcode for GameObjects 陷阱清单(AI 最易引入的 bug,逐条附源码出处;编写或审查 netcode 前先扫一遍)。"
type: reference
---

# Netcode - Pitfalls Checklist

A checklist of the bugs AI agents most often introduce when writing Netcode code. Every item cites a source anchor. Scan before writing or reviewing Netcode code.

## Startup and lifecycle

### ❌ P1. Multiple NetworkManagers in the scene
- Symptom: `NetworkManager.Singleton` is whichever one wins the `Awake` race; behavior is non-deterministic
- Source: `NetworkManager.cs:881` singleton assignment
- ✅ Fix: keep exactly one NetworkManager project-wide. If several scenes spawn one, use `DontDestroyOnLoad` + a single-owner bootstrap scene.

### ❌ P2. Reading `IsOwner` / `IsSpawned` in Awake / Start
- Symptom: always false; subsequent network logic misfires
- Source: `NetworkObject.cs:1214, 1224` — values are only valid after `OnNetworkSpawn`
- ✅ Fix: do all network-state checks in `OnNetworkSpawn` and later (in `Update`, guard with `if (!IsSpawned) return;`)

### ❌ P3. Reading NetworkVariable right after `NetworkManager.Shutdown()`
- Symptom: wrong value, NRE, or disposed-object access
- ✅ Fix: do final reads and cleanup in `OnNetworkDespawn`

### ❌ P4. Calling `NetworkManager.Singleton` before it is created
- Symptom: NRE
- ✅ Fix: `if (NetworkManager.Singleton == null) return;` — or read the instance from the `NetworkBehaviour.NetworkManager` property (`NetworkBehaviour.cs:455`)

## Ownership and permissions

### ❌ P5. Client calls `networkObject.Spawn()` / `Despawn()` / `ChangeOwnership()`
- Symptom: InvalidOperationException or silently ignored
- Source: `NetworkObject.cs:1884, 1921, 1971`
- ✅ Fix: go through a `[Rpc(SendTo.Server)]` and have the server do it

### ❌ P6. Client writes a default-permission NetworkVariable
- Symptom: client-side assignment is dropped; UI appears changed locally but server state is unchanged
- Source: `NetworkVariablePermission.cs:25` defaults to `Server`
- ✅ Fix: set `WritePermission = Owner`, or send a ServerRpc

### ❌ P7. Using `IsOwner` inside a ServerRpc to identify the sender
- Symptom: `IsOwner` reflects the server's ownership view, not the sending client
- ✅ Fix: accept `RpcParams p = default`, then compare `p.Receive.SenderClientId == OwnerClientId`

## RPC

### ❌ P8. Legacy `[ServerRpc]` method name does not end with `ServerRpc`
- Symptom: ILPP compile error "ServerRpc methods must end with 'ServerRpc'"
- Source: `Editor/CodeGen/` ILPP validators
- ✅ Fix: rename to add the suffix, or switch to `[Rpc(SendTo.Server)]` (no naming constraint)

### ❌ P9. Treating `SendTo.NotServer` as "all clients"
- Symptom: the Host's client side does not run; you expected only the real server to be skipped
- Source: `Runtime/Messaging/RpcTargets/NotServerRpcTarget.cs`
- ✅ Fix: for "every client instance (including the Host's client half)" use `SendTo.ClientsAndHost`

### ❌ P10. RPC parameters of type `List<T>` / `class` / illegal arrays
- Symptom: ILPP reports "Parameter type not supported"
- Source: `RpcParams.cs` + the ILPP RPC generator
- ✅ Fix: allowed types are unmanaged / `INetworkSerializable` / `string` (single) / arrays of the former

### ❌ P11. RPC method returning Task / async
- Symptom: ILPP fails, or the call does not behave as expected
- ✅ Fix: RPCs must be void. Do async work in a separate method and reply via another RPC

### ❌ P12. Per-frame position sync using `RpcDelivery.Reliable`
- Symptom: bandwidth explosion, accumulating latency
- ✅ Fix: use `NetworkTransform` for position. If you must roll your own, use `RpcDelivery.Unreliable` plus NetworkVariable UpdateTraits

## NetworkVariable / NetworkList

### ❌ P13. `NetworkVariable<string>` / `<List<T>>`
- Symptom: ILPP compile error
- ✅ Fix: use `FixedString32Bytes` for strings and `NetworkList<T>` for lists

### ❌ P14. Creating NetworkVariable in OnNetworkSpawn
- Symptom: ILPP cannot register it; values do not sync
- ✅ Fix: initialize at field declaration: `= new NetworkVariable<T>(...)`. Use `OnNetworkSpawn` only for subscriptions and initial values.

### ❌ P15. Subscribing to OnValueChanged without unsubscribing in OnNetworkDespawn
- Symptom: duplicate handlers after Spawn→Despawn→Spawn cycles; leaked references keep objects alive
- ✅ Fix: subscribe and unsubscribe in mirrored pairs

## Spawn / Prefab

### ❌ P16. Prefab without a `NetworkObject` component
- Symptom: `NetworkPrefab.Validate()` returns false and the entry is dropped, or NRE
- Source: `NetworkPrefab.cs:155-170`
- ✅ Fix: attach `NetworkObject` to the prefab's root

### ❌ P17. `PlayerPrefab` missing from NetworkPrefabsList / NetworkConfig.Prefabs
- Symptom: clients log a prefab mismatch on connect and the player either fails to spawn or the client is disconnected
- ✅ Fix: register the PlayerPrefab in the prefabs list as well (required by 2.x)

### ❌ P18. Nesting a NetworkObject inside another prefab
- Symptom: runtime warning; the nested child behaves erratically
- ✅ Fix: split into an independent prefab and re-parent via `TrySetParent` at runtime

### ❌ P19. `transform.parent = x` after Spawn, instead of TrySetParent
- Symptom: parent state is not replicated to clients
- Source: `NetworkObject.cs:2135-2215`
- ✅ Fix: call `networkObject.TrySetParent(newParent)` on the server

### ❌ P20. `Destroy(go)` on a spawned NetworkObject
- Symptom: other clients do not see the destruction and keep ghost references
- ✅ Fix: `networkObject.Despawn(destroy: true)`

## Scene

### ❌ P21. Client calling `NetworkSceneManager.LoadScene`
- Symptom: returns a `NotServer` error
- Source: `NetworkSceneManager.cs:1496`
- ✅ Fix: the server calls it; the client requests via ServerRpc

### ❌ P22. Switching scenes with `UnityEngine.SceneManagement.SceneManager.LoadScene`
- Symptom: only the local peer switches; others do not follow
- ✅ Fix: `NetworkManager.SceneManager.LoadScene(name, mode)`

### ❌ P23. Two LoadScene calls in the same frame
- Symptom: second call returns `SceneEventInProgress` and is rejected
- ✅ Fix: wait for `OnLoadComplete` before issuing the next load

## Transport

### ❌ P24. Client's `Address` set to "0.0.0.0"
- Symptom: connection fails with invalid-target error
- ✅ Fix: use the server's reachable IP

### ❌ P25. Configuring both ConnectionData and RelayServerData
- Symptom: inconsistent behavior; the connection may fail
- ✅ Fix: pick one — Relay or direct connect

### ❌ P26. Shipping with DebugSimulator enabled
- Symptom: end users see 100 ms latency and artificial packet loss
- ✅ Fix: wrap in `#if DEVELOPMENT_BUILD || UNITY_EDITOR`

## Miscellaneous

### ❌ P27. Inventing attributes / methods that do not exist
- Not real: `[ServerOnly]`, `[ClientOnly]`, `[NetworkRpc]`, `NetworkObject.Instantiate()`, `rpc.Invoke()`, `controller.Call()`
- ✅ Fix: stick to `[Rpc]` / `[ServerRpc]` / `[ClientRpc]`. Spawn path is `Instantiate` + `.Spawn()` or `InstantiateAndSpawn`.

### ❌ P28. Ignoring the return value of StartHost / StartServer / StartClient
- Source: `NetworkManager.cs:1309, 1371, 1426` — each returns a bool
- ✅ Fix: `if (!NetworkManager.Singleton.StartHost()) { ...handle failure... }`

### ❌ P29. Unsubscribing NetworkVariable handlers in `OnDestroy`
- Symptom: after OnNetworkDespawn the event source may already be null
- ✅ Fix: unsubscribe in `OnNetworkDespawn` instead

### ❌ P30. Blocking in a ServerRpc (long synchronous wait)
- Symptom: the server tick stalls, affecting every connected client
- ✅ Fix: kick off a coroutine or task (`_ = DoAsync();`) inside the RPC and reply with a separate RPC when finished
