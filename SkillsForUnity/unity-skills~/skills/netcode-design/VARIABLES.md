# Netcode - NetworkVariable & NetworkList

All rules come from `Runtime/NetworkVariable/NetworkVariable.cs`, `NetworkVariableBase.cs`, `NetworkVariablePermission.cs`, and `Collections/NetworkList.cs`.

## NetworkVariable&lt;T&gt;

### Signature
```csharp
public class NetworkVariable<T> : NetworkVariableBase
{
    public NetworkVariable(
        T value = default,
        NetworkVariableReadPermission  readPerm  = NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission writePerm = NetworkVariableWritePermission.Server);

    public T Value { get; set; }
    public event System.Action<T, T> OnValueChanged;   // (previousValue, newValue)
    public bool CheckDirtyState() { ... }
}
```
Source: `Runtime/NetworkVariable/NetworkVariable.cs:12`.

### Constraints on `T` (ILPP enforced at compile time)
1. `unmanaged` struct (primitives, enums, `Vector3`, `Quaternion`, `FixedString32Bytes`, etc.)
2. Or a type implementing `INetworkSerializable`

**Not allowed**: `string` (use `FixedString*`), `List<T>` (use `NetworkList<T>`), `class`, interfaces, `object`, delegates.

### Permissions
```csharp
public enum NetworkVariableReadPermission  { Everyone, Owner }
public enum NetworkVariableWritePermission { Server,   Owner }
```
Source: `Runtime/NetworkVariable/NetworkVariablePermission.cs:10, 25`.

### UpdateTraits (optional throttling)
```csharp
public struct NetworkVariableUpdateTraits {
    public float MinSecondsBetweenUpdates;
    public int   TickRateDivisor;  // send at most once every N ticks
}
```
Source: `NetworkVariableBase.cs:11`. Apply via `SetUpdateTraits(new NetworkVariableUpdateTraits { ... })`.

## NetworkList&lt;T&gt;

```csharp
public class NetworkList<T> : NetworkVariableBase
    where T : unmanaged, IEquatable<T>
{
    public event OnListChangedDelegate<T> OnListChanged;
    public int Count { get; }
    public T this[int index] { get; set; }
    public void Add(T item);
    public void Insert(int index, T item);
    public void RemoveAt(int index);
    public bool Remove(T item);
    public void Clear();
}
```
Source: `Runtime/NetworkVariable/Collections/NetworkList.cs:14`.

The `OnListChanged` event argument is `NetworkListEvent<T>` (`NetworkList.cs:720`) with `Type` (Add/Remove/Insert/Clear/Value), `Index`, `Value`, `PreviousValue`.

### Do not use `NetworkVariable<List<T>>`
```csharp
// ❌ Compile error — List<T> is not unmanaged
public NetworkVariable<List<int>> Bad = new NetworkVariable<List<int>>();

// ✅ Use NetworkList
public NetworkList<int> Good = new NetworkList<int>();
```

## AnticipatedNetworkVariable&lt;T&gt; (advanced, optional)

```csharp
public class AnticipatedNetworkVariable<T> : NetworkVariableBase
```
Source: `Runtime/NetworkVariable/AnticipatedNetworkVariable.cs`. Used for client-side prediction + authoritative rollback (FPS movement, ability resolution). Skip it for most projects.

## Strings and small collections

When you want a "string" or a small array, use fixed-size types from `Unity.Collections`:

| Type | Use case |
|------|----------|
| `FixedString32Bytes` | Short text (nicknames) |
| `FixedString64Bytes` / `128Bytes` / `512Bytes` / `4096Bytes` | Longer text |
| `NativeList<T>` / `NativeArray<T>` | Usually not embedded in NetworkVariable; fine as RPC parameters |

## ❌ Anti-patterns vs ✅ Correct patterns

### 1. Constructing NetworkVariable in OnNetworkSpawn

```csharp
// ❌ WRONG — the field is uninitialized at load time, so ILPP has nothing to track
public NetworkVariable<int> Health;

public override void OnNetworkSpawn() {
    Health = new NetworkVariable<int>(100);  // too late
}

// ✅ CORRECT — initialize at field declaration
public NetworkVariable<int> Health = new NetworkVariable<int>(0);

public override void OnNetworkSpawn() {
    if (IsServer) Health.Value = 100;
}
```

### 2. Using string as a NetworkVariable type

```csharp
// ❌ WRONG — string is not unmanaged
public NetworkVariable<string> Name = new NetworkVariable<string>("");

// ✅ CORRECT — use FixedStringNBytes
using Unity.Collections;
public NetworkVariable<FixedString32Bytes> Name =
    new NetworkVariable<FixedString32Bytes>(new FixedString32Bytes(""));
```

### 3. Subscribing without unsubscribing (memory leak)

```csharp
// ❌ WRONG — no OnNetworkDespawn unsubscription; repeated Spawn/Despawn cycles accumulate handlers
public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHp;
}

// ✅ CORRECT — mirror subscribe/unsubscribe
public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHp;
}
public override void OnNetworkDespawn() {
    Health.OnValueChanged -= OnHp;
}
```

### 4. Reading NetworkVariable in OnDestroy

```csharp
// ❌ WRONG — the variable may already be disposed after OnNetworkDespawn
void OnDestroy() {
    Debug.Log(Health.Value);  // can throw or return garbage
}

// ✅ CORRECT — finalize in OnNetworkDespawn
public override void OnNetworkDespawn() {
    Debug.Log($"Final Hp: {Health.Value}");
    Health.OnValueChanged -= OnHp;
}
```

### 5. Client writing a default-permission NetworkVariable

```csharp
// ❌ WRONG — default is Server-write; client assignments are dropped
public NetworkVariable<int> Score = new NetworkVariable<int>();

void UI_OnClientScoreClick() {
    Score.Value++;   // no-op on the client
}

// ✅ Option A — Owner write permission
public NetworkVariable<int> Score = new NetworkVariable<int>(
    0,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);

// ✅ Option B — keep Server-write, request via RPC
[Rpc(SendTo.Server)] void IncServerRpc() { Score.Value++; }
```

### 6. Custom struct that forgot INetworkSerializable

```csharp
// A struct with reference-type fields is not unmanaged. It must implement INetworkSerializable.
public struct PlayerInfo : INetworkSerializable
{
    public FixedString32Bytes Name;
    public int Level;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Level);
    }
}

public NetworkVariable<PlayerInfo> Info = new NetworkVariable<PlayerInfo>();
```

### 7. High-frequency writes without throttling

```csharp
// ❌ WRONG — per-frame writes trigger a packet every tick
void Update() {
    if (IsServer) Accuracy.Value = ComputeAccuracy();
}

// ✅ Throttle with UpdateTraits
void Awake() {
    Accuracy.SetUpdateTraits(new NetworkVariableUpdateTraits {
        MinSecondsBetweenUpdates = 0.1f   // cap at 10 Hz
    });
}
```

## NetworkList event handling template

```csharp
using Unity.Netcode;
using Unity.Collections;

public class Inventory : NetworkBehaviour
{
    public NetworkList<int> Items = new NetworkList<int>();

    public override void OnNetworkSpawn() {
        Items.OnListChanged += OnItemsChanged;
    }
    public override void OnNetworkDespawn() {
        Items.OnListChanged -= OnItemsChanged;
    }

    void OnItemsChanged(NetworkListEvent<int> e) {
        switch (e.Type) {
            case NetworkListEvent<int>.EventType.Add:    /* ... */ break;
            case NetworkListEvent<int>.EventType.Remove: /* ... */ break;
            case NetworkListEvent<int>.EventType.Clear:  /* ... */ break;
            case NetworkListEvent<int>.EventType.Value:  /* index was reassigned */ break;
        }
    }

    [Rpc(SendTo.Server)]
    void AddItemServerRpc(int itemId) { Items.Add(itemId); }
}
```
