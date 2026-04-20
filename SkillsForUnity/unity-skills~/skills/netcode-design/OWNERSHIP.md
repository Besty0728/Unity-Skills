# Netcode - Ownership & Authority

All rules here come from `Runtime/Core/NetworkBehaviour.cs:455-547`, `Runtime/Core/NetworkObject.cs:1172-2215`, and `Runtime/Configuration/NetworkConfig.cs:169`.

## Role properties

| Property | Meaning | Source |
|----------|---------|--------|
| `IsServer` | Current process is a Server or Host | `NetworkBehaviour.cs:505` |
| `IsClient` | Current process is a Client or Host | `NetworkBehaviour.cs:530` |
| `IsHost` | Equivalent to `IsServer && IsClient` | `NetworkBehaviour.cs:536` |
| `IsOwner` | This NetworkObject's `OwnerClientId == LocalClientId` and it is spawned | `NetworkObject.cs:1214` |
| `IsLocalPlayer` | This NetworkBehaviour's NetworkObject is the local machine's PlayerObject | `NetworkBehaviour.cs:495` |
| `OwnerClientId` | Current owner's ClientId | `NetworkObject.cs:1177` |
| `LocalClientId` | Local machine's ID on the network (Server = 0) | `NetworkManager.cs:588` |

**Invariants**:
- Host = Server + Client in one process. When `IsHost` is true, both `IsServer` and `IsClient` are true.
- Only Server / Host may call `Spawn`, `Despawn`, `ChangeOwnership`, `RemoveOwnership` on a NetworkObject.
- In Server-only mode (no Host), `IsClient` is false. In pure Client mode, `IsServer` is false.

## Permission matrix

| Operation | Server | Host | Client (non-owner) | Client (owner) | Notes |
|-----------|:------:|:----:|:------------------:|:--------------:|-------|
| `networkObject.Spawn()` | ✅ | ✅ | ❌ | ❌ | Other callers throw or are dropped |
| `networkObject.Despawn()` | ✅ | ✅ | ❌ | ❌ | Same as above |
| `ChangeOwnership(id)` | ✅ | ✅ | ❌ | ❌ | `NetworkObject.cs:1971` |
| `RemoveOwnership()` | ✅ | ✅ | ❌ | ❌ | `NetworkObject.cs:1954` |
| Write `NetworkVariable` (Server write permission) | ✅ | ✅ | ❌ | ❌ | Default |
| Write `NetworkVariable` (Owner write permission) | ❌ | ✅* | ❌ | ✅ | *Host only when Host is the owner |
| Invoke `[Rpc(SendTo.Server)]` | — | ✅ | ✅ | ✅ | Server can send to itself |
| Invoke `[Rpc(SendTo.X)]` with `InvokePermission=Owner` | — | ✅* | ❌ | ✅ | *Host only when Host is the owner |
| Read `NetworkVariable` | ✅ | ✅ | ✅ | ✅ | Default read permission is `Everyone` |
| `NetworkSceneManager.LoadScene` | ✅ | ✅ | ❌ | ❌ | `NetworkSceneManager.cs:1496` |
| Direct `transform` assignment with sync expectation | ❌ | ❌ | ❌ | ❌ | Use `NetworkTransform` or an authoritative RPC |

## Distributed Authority mode

When `NetworkConfig.NetworkTopology = NetworkTopologyTypes.DistributedAuthority`, the permission model shifts:

- No dedicated Server role. Each NetworkObject has its own **Authority** (defaults to Owner).
- Use `SendTo.Authority` / `SendTo.NotAuthority` in place of `SendTo.Server` / `SendTo.NotServer`.
- An owner may directly write NetworkVariables on objects it owns and may Spawn new objects.
- `NetworkObject.SetOwnershipStatus` + `OwnershipStatus` flags govern who may claim ownership.

> For typical games, start with ClientServer (default) and switch to Distributed Authority only when P2P or decentralized ownership is required.

## Ownership status and locks

`NetworkObject.Ownership` is an `OwnershipStatus` flag set (`NetworkObject.cs:1023`):

- `None` — default, ownership is fixed
- `Distributable` — the system may auto-transfer ownership
- `Transferable` — ownership may be transferred on request
- `RequestRequired` — transfer requires approval
- `SessionOwner` — transfers to the "session owner" (room host)

`IsOwnershipLocked` (`NetworkObject.cs:491`) can lock out transfers.

## Transferring / releasing ownership

```csharp
// Called by the Server
networkObject.ChangeOwnership(targetClientId);   // NetworkObject.cs:1971
networkObject.RemoveOwnership();                 // NetworkObject.cs:1954 (returns ownership to Server)
```

Both trigger `OnGainedOwnership` / `OnLostOwnership` on the relevant peers.

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Spawning from the client

```csharp
// ❌ WRONG — rejected by the network layer
if (Input.GetKeyDown(KeyCode.F)) {
    Instantiate(bulletPrefab).GetComponent<NetworkObject>().Spawn();
}

// ✅ CORRECT — client sends an RPC, server spawns
[Rpc(SendTo.Server)]
void FireBulletServerRpc(Vector3 pos, Vector3 dir) {
    var bullet = Instantiate(bulletPrefab, pos, Quaternion.LookRotation(dir));
    bullet.GetComponent<NetworkObject>().Spawn();
}
```

### 2. Using `IsOwner` inside a ServerRpc to identify the sender

```csharp
// ❌ WRONG — the ServerRpc body runs on the Server. IsOwner is the Server's view of this object,
//            not the sending client.
[Rpc(SendTo.Server)]
void DoSomethingServerRpc() {
    if (IsOwner) { ... }  // always reflects the Server's ownership view
}

// ✅ CORRECT — read SenderClientId from RpcParams and compare to OwnerClientId
[Rpc(SendTo.Server)]
void DoSomethingServerRpc(RpcParams rpcParams = default) {
    ulong sender = rpcParams.Receive.SenderClientId;
    if (sender == OwnerClientId) { /* request came from the actual owner */ }
}
```

### 3. Client writing a NetworkVariable directly

```csharp
// ❌ WRONG — default Server write permission, so client writes are dropped
public NetworkVariable<int> Score = new NetworkVariable<int>();

void OnClientClicksButton() {
    Score.Value++;  // no-op on the client
}

// ✅ CORRECT — client requests via RPC, server writes
[Rpc(SendTo.Server)]
void IncrementScoreServerRpc() {
    Score.Value++;
}

void OnClientClicksButton() {
    IncrementScoreServerRpc();
}

// ✅ ALTERNATIVE — if the design allows, declare Owner write permission
public NetworkVariable<int> Score = new NetworkVariable<int>(
    0,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);
```

### 4. Expecting plain `transform.position = ...` to sync

```csharp
// ❌ WRONG — direct transform writes are not replicated
void Update() {
    if (IsServer) transform.position += Vector3.forward * Time.deltaTime;
}

// ✅ CORRECT — attach a NetworkTransform, then modify transform normally; NetworkTransform syncs it
//             (or maintain NetworkVariable<Vector3> + interpolation yourself)
```

### 5. Believing `SendTo.NotServer` includes the Host's client half

```csharp
// ❌ WRONG — SendTo.NotServer does NOT deliver to the Host. To include the Host's client side, use ClientsAndHost.
[Rpc(SendTo.NotServer)]
void AnnounceToClientsRpc() { ... }

// ✅ CORRECT — to reach every client instance (including Host's client half), use ClientsAndHost
[Rpc(SendTo.ClientsAndHost)]
void AnnounceToClientsRpc() { ... }
```
