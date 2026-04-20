# Netcode - Lifecycle & Call Order

All rules here come from `Runtime/Core/NetworkBehaviour.cs`, `Runtime/Core/NetworkObject.cs`, and `Runtime/Spawning/NetworkSpawnManager.cs`.

## Startup order for in-scene NetworkObjects

```
Unity Awake          (all MonoBehaviours / NetworkBehaviours)
Unity OnEnable
  ↓ (before first frame)
NetworkManager.StartHost / StartServer / StartClient
  ↓
SpawnManager scans existing in-scene NetworkObjects
  ↓
NetworkBehaviour.OnNetworkSpawn()  ← IsSpawned == true, IsOwner is now valid
  ↓
Unity Start
  ↓ (continuous)
Unity Update / FixedUpdate ...
```

**Key point**: `OnNetworkSpawn` runs before `Start`. Any initialization that depends on network state belongs in `OnNetworkSpawn`, not in `Start` or `Awake`.

## Startup order for runtime-spawned objects

```
Instantiate(prefab)                    ← called by Server/Host
  ↓
Unity Awake                            ← IsSpawned == false at this point
  ↓
Unity OnEnable
  ↓
networkObject.Spawn()                  ← called by Server/Host
  ↓
NetworkBehaviour.OnNetworkSpawn()      ← IsSpawned = true
  ↓
Unity Start                            ← first frame
```

## Teardown order

```
NetworkObject.Despawn(true)  or  NetworkManager.Shutdown()
  ↓
NetworkBehaviour.OnNetworkDespawn()
  ↓
Unity OnDisable / OnDestroy
```

**Note**: Accessing `NetworkVariable` in `OnDestroy` is unsafe — the variable may already be disposed. Unsubscribe and clean up in `OnNetworkDespawn` instead.

## Source anchors

| Callback | Declaration | Notes |
|----------|-------------|-------|
| `OnNetworkSpawn()` | `NetworkBehaviour.cs:704` | `public virtual void OnNetworkSpawn() { }` |
| `OnNetworkDespawn()` | `NetworkBehaviour.cs:749` | `public virtual void OnNetworkDespawn() { }` |
| `OnGainedOwnership()` | `NetworkBehaviour.cs:926` | Fires when ownership is transferred in |
| `OnLostOwnership()` | `NetworkBehaviour.cs:962` | Fires when ownership is transferred out |
| `OnNetworkObjectParentChanged(NetworkObject)` | `NetworkBehaviour.cs` | Parent NetworkObject changed |
| `NetworkObject.IsSpawned` | `NetworkObject.cs:1224` | `public bool IsSpawned { get; internal set; }` |
| `NetworkObject.NetworkObjectId` | `NetworkObject.cs:1172` | Only valid after Spawn |

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Reading network state in Awake / Start

```csharp
// ❌ WRONG — NetworkManager.Singleton may be null; IsOwner is always false here
void Awake() {
    if (NetworkManager.Singleton.IsServer) { ... }
    m_Health.Value = 100;
}

// ✅ CORRECT — IsServer / IsOwner / IsClient are valid in OnNetworkSpawn
public override void OnNetworkSpawn() {
    if (IsServer) {
        m_Health.Value = 100;
    }
}
```

### 2. `new`-ing NetworkVariable inside OnNetworkSpawn

```csharp
// ❌ WRONG — NetworkVariables must be field-initialized; ILPP binds them at compile time
public override void OnNetworkSpawn() {
    m_Health = new NetworkVariable<int>(100);  // too late
}

// ✅ CORRECT — field initializer; use OnNetworkSpawn only for subscriptions / initial values
public NetworkVariable<int> Health = new NetworkVariable<int>(0);

public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHealthChanged;
    if (IsServer) Health.Value = 100;
}

public override void OnNetworkDespawn() {
    Health.OnValueChanged -= OnHealthChanged;
}
```

### 3. Expecting OnGainedOwnership to fire on initial Spawn

```csharp
// ❌ WRONG — on the first spawn, the initial owner does NOT receive OnGainedOwnership.
//            It only fires on subsequent ChangeOwnership calls.
public override void OnGainedOwnership() {
    InitForLocalPlayer();  // initial owner never reaches this path
}

// ✅ CORRECT — perform initial-owner setup in OnNetworkSpawn
public override void OnNetworkSpawn() {
    if (IsOwner) InitForLocalPlayer();
}
public override void OnGainedOwnership() {
    InitForLocalPlayer();  // runs only on later ownership transfers
}
```

### 4. Polling `NetworkManager.Singleton` every frame

```csharp
// ❌ WRONG — Singleton can flip (shutdown + restart); per-frame access is wasted work
void Update() {
    if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
        DoNetworkThing();
    }
}

// ✅ CORRECT — use NetworkBehaviour's IsSpawned or subscribe to lifecycle events
void Update() {
    if (!IsSpawned) return;
    DoNetworkThing();
}
```

## When to use `NetworkManager.OnServerStarted` / `OnClientStarted` / `OnClientConnectedCallback`

- `OnServerStarted` — one-shot event after Server/Host startup succeeds (NetworkManager scope)
- `OnClientConnectedCallback(ulong clientId)` — on the server side: a new client connected. On the client side: local connection completed (`clientId == LocalClientId`).
- `OnClientDisconnectCallback(ulong clientId)` — symmetric to the above
- Subscribe in `OnNetworkSpawn` and unsubscribe in `OnNetworkDespawn`, or manage from a singleton's Awake/OnDestroy (mind the Singleton lifetime).

## Canonical NetworkBehaviour template

```csharp
using Unity.Netcode;
using UnityEngine;

public class MyNetworkBehaviour : NetworkBehaviour
{
    // 1. NetworkVariable — instantiated at field declaration time
    public NetworkVariable<int> Health = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        // 2. Subscribe, then seed initial value from the authority
        Health.OnValueChanged += OnHealthChanged;
        if (IsServer) Health.Value = 100;
        if (IsOwner)  InitLocalPlayer();
    }

    public override void OnNetworkDespawn()
    {
        // 3. Unsubscribe — mirror OnNetworkSpawn exactly
        Health.OnValueChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(int oldVal, int newVal) { /* UI update, etc. */ }

    private void InitLocalPlayer() { /* runs only on the client that owns this object */ }
}
```
