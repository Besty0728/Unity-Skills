# Netcode - RPC Rules

All rules come from `Runtime/Messaging/RpcAttributes.cs`, `Runtime/Messaging/RpcTargets/RpcTarget.cs`, and `Runtime/Messaging/RpcParams.cs`.

## Three RPC models (ordered by preference)

### 1. Universal RPC (recommended, 2.x) — `[Rpc(SendTo.X)]`

```csharp
[Rpc(SendTo.Server)]                             // same as [ServerRpc]
void AttackServerRpc(int targetId) { ... }

[Rpc(SendTo.NotServer)]                          // same as [ClientRpc]
void ShowDamageRpc(int damage) { ... }

[Rpc(SendTo.Owner)]                              // runs only on the owning client
void GrantLootRpc(int itemId) { ... }

[Rpc(SendTo.SpecifiedInParams)]                  // target chosen at call time
void TellClientRpc(int msg, RpcParams p = default) { ... }
```

**No method-name constraint** (no need to end with `Rpc` or `ServerRpc`).

### 2. Legacy ServerRpc / ClientRpc (from 1.x, still supported)

```csharp
[ServerRpc]
void AttackServerRpc(int targetId) { ... }       // ⚠ method name must end with ServerRpc

[ClientRpc]
void ShowDamageClientRpc(int damage) { ... }     // ⚠ method name must end with ClientRpc
```

`[ServerRpc]` inherits from `RpcAttribute` and its constructor calls `base(SendTo.Server)` (`RpcAttributes.cs:160`).
`[ClientRpc]` does the same with `SendTo.NotServer` (`RpcAttributes.cs:176`).

### 3. Custom Messages (low-level) — `CustomMessageManager` / `MessagingSystem`

Rarely needed. Only consider it when you must hand-serialize the payload. Not expanded here.

## SendTo enum — all 11 values

Source: `Runtime/Messaging/RpcTargets/RpcTarget.cs:9-80`.

| Value | Target set |
|-------|------------|
| `Owner` | Only the current owner of this NetworkObject |
| `NotOwner` | All visible observers except the owner |
| `Server` | Only the Server (including the Server side of a Host) |
| `NotServer` | Everyone except the Server; includes the Host's client side but **not** the Host's server side |
| `Me` | Local execution only (does not traverse the network) |
| `NotMe` | All observers except this machine |
| `Everyone` | All observers (including the Server) |
| `ClientsAndHost` | Every client instance (including the Host's client side) |
| `Authority` | The authority (Server under ClientServer; per-object authority under DistributedAuthority) |
| `NotAuthority` | All non-authority peers |
| `SpecifiedInParams` | Target(s) supplied at runtime via `RpcParams` |

> **`NotServer` vs `ClientsAndHost`**: `NotServer` skips the Host's server-side but runs once on the Host's client-side; `ClientsAndHost` achieves the same effective set. They are interchangeable in most cases — pick the one whose name matches intent.

## RpcDelivery

```
RpcDelivery.Reliable   (default)   reliable, ordered, fragmented if large
RpcDelivery.Unreliable             best-effort, suitable for high-frequency state (prefer NetworkTransform for position)
```

Source: `RpcAttributes.cs:8-19, 88`.

## InvokePermission (who may invoke)

```csharp
public enum RpcInvokePermission {
    Everyone = 0,   // any client may invoke
    Server,         // Server only (rarely used)
    Owner,          // only the owner may invoke
}
```

Source: `RpcAttributes.cs:24-40`.

### `RequireOwnership` (deprecated) — migration table

| Legacy | Modern |
|--------|--------|
| `[ServerRpc]` (default `RequireOwnership = true`) | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]` |
| `[ServerRpc(RequireOwnership = false)]` | `[Rpc(SendTo.Server)]` (Everyone is the default) |

Source note: `RpcAttributes.cs:140-154`.

## RpcParams (runtime targeting / sender info)

```csharp
// Read the sender's ClientId on the receiving side
[Rpc(SendTo.Server)]
void MyServerRpc(int payload, RpcParams rpcParams = default) {
    ulong sender = rpcParams.Receive.SenderClientId;
    // ...
}

// Send to a specific client
void SendTo(ulong targetClient) {
    MyTargetRpc(42, RpcTarget.Single(targetClient, RpcTargetUse.Temp));
}

[Rpc(SendTo.SpecifiedInParams)]
void MyTargetRpc(int v, RpcParams p = default) { ... }
```

The legacy `ServerRpcParams` / `ClientRpcParams` still work, but universal `RpcParams` is preferred.

## Parameter serialization constraints

RPC parameter types are validated at compile time by ILPP. Allowed types:

1. **unmanaged** primitives (int, float, bool, enum, unmanaged struct)
2. Unity's built-in serializable types (`Vector3`, `Quaternion`, `Color`, ...)
3. `string` (built-in support)
4. Types implementing `INetworkSerializable`
5. Arrays / `NativeArray<T>` of any of the above

**Forbidden**: non-string `class`, `List<T>`, `Dictionary<K,V>`, `object`, `Task`, `Func`/`Action`, arbitrary reference types.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Hallucinated attributes

```csharp
// ❌ WRONG — these attributes do not exist
[ServerOnly] void X() { }
[ClientOnly] void X() { }
[NetworkRpc] void X() { }
[RPC(Client)] void X() { }
```

Only `[Rpc]`, `[ServerRpc]`, and `[ClientRpc]` exist.

### 2. Legacy RPC with non-conforming method name

```csharp
// ❌ WRONG — ILPP fails: "ServerRpc methods must end with 'ServerRpc'"
[ServerRpc] void Attack(int id) { }

// ✅ CORRECT
[ServerRpc] void AttackServerRpc(int id) { }
// or switch to universal RPC
[Rpc(SendTo.Server)] void Attack(int id) { }
```

### 3. RPC returning Task / async

```csharp
// ❌ WRONG — RPC must be void. async Task fails at compile time or at runtime
[Rpc(SendTo.Server)]
async Task DoAsyncServerRpc() { await ...; }

// ✅ CORRECT — keep the RPC synchronous; do async work internally and reply via another RPC
[Rpc(SendTo.Server)]
void StartAsyncWorkRpc(int requestId) {
    _ = DoWorkInternal(requestId);
}
async Task DoWorkInternal(int id) {
    await ...;
    ReplyClientRpc(id, result);
}
[Rpc(SendTo.SpecifiedInParams)]
void ReplyClientRpc(int id, int result, RpcParams p = default) { ... }
```

### 4. Passing List / class / illegal array

```csharp
// ❌ WRONG — List<int> is not a legal RPC parameter
[Rpc(SendTo.Server)] void SetItemsServerRpc(List<int> items) { }

// ✅ CORRECT — use an array
[Rpc(SendTo.Server)] void SetItemsServerRpc(int[] items) { }
```

### 5. Firing RPCs at peers that are not yet spawned

An RPC requires the target NetworkObject to be spawned. It is fine to send from inside your own `OnNetworkSpawn`, but if you target *another* object make sure it is already spawned. When unsure, subscribe to `NetworkManager.OnClientConnectedCallback`.

### 6. Forgetting the `Rpc` suffix convention (universal RPC is lenient)

```csharp
// ✅ Universal RPC — no naming constraint, but the `Rpc` suffix makes call sites obvious
[Rpc(SendTo.Server)] void FireRpc() { }  // recommended
[Rpc(SendTo.Server)] void Fire() { }      // legal but less readable
```

### 7. Wrong Delivery for high-frequency state

```csharp
// ❌ WRONG — per-frame position with default Reliable wastes bandwidth
[Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Reliable)]
void SendPositionRpc(Vector3 p) { }

// ✅ BETTER — use NetworkTransform for position; if you must hand-roll it, use Unreliable
[Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Unreliable)]
void SendPositionRpc(Vector3 p) { }
```

## Recommended RPC template

```csharp
using Unity.Netcode;

public class MyBehaviour : NetworkBehaviour
{
    // Client → Server: request execution
    [Rpc(SendTo.Server)]
    void RequestFireServerRpc(Vector3 dir, RpcParams p = default)
    {
        if (p.Receive.SenderClientId != OwnerClientId) return;  // authority check
        // Server-authoritative execution
        SpawnBullet(dir);
        // optional: broadcast the result
        PlayFireSoundRpc();
    }

    // Server → Clients: broadcast effects
    [Rpc(SendTo.ClientsAndHost)]
    void PlayFireSoundRpc() { /* clients play VFX/SFX */ }

    // Server → specific client (e.g. loot drop)
    [Rpc(SendTo.SpecifiedInParams)]
    void GrantLootRpc(int itemId, RpcParams p = default) { /* ... */ }

    void ServerGrantsLoot(ulong receiverId, int itemId) {
        GrantLootRpc(itemId, RpcTarget.Single(receiverId, RpcTargetUse.Temp));
    }
}
```
