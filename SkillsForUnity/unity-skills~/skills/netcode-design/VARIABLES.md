# Netcode - NetworkVariable & NetworkList

所有规则来自 `Runtime/NetworkVariable/NetworkVariable.cs`、`NetworkVariableBase.cs`、`NetworkVariablePermission.cs`、`Collections/NetworkList.cs`。

## NetworkVariable&lt;T&gt;

### 签名
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
源码位置：`Runtime/NetworkVariable/NetworkVariable.cs:12`。

### T 的约束（ILPP 编译期校验）
1. `unmanaged` struct（含 primitive / enum / `Vector3` / `Quaternion` / `FixedString32Bytes` 等）
2. 或实现 `INetworkSerializable`

**不合法**：`string`（用 `FixedString*`）、`List<T>`（用 `NetworkList<T>`）、`class`、接口、`object`、委托。

### Permissions
```csharp
public enum NetworkVariableReadPermission  { Everyone, Owner }
public enum NetworkVariableWritePermission { Server,   Owner }
```
源码：`Runtime/NetworkVariable/NetworkVariablePermission.cs:10, 25`。

### UpdateTraits（可选，控制同步频率）
```csharp
public struct NetworkVariableUpdateTraits {
    public float MinSecondsBetweenUpdates;
    public int   TickRateDivisor;  // 每 N tick 才发一次
}
```
源码：`NetworkVariableBase.cs:11`。用 `SetUpdateTraits(new NetworkVariableUpdateTraits { ... })` 设置。

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
源码：`Runtime/NetworkVariable/Collections/NetworkList.cs:14`。

`OnListChanged` 事件参数 `NetworkListEvent<T>`（`NetworkList.cs:720`）含 `Type`（Add/Remove/Insert/Clear/Value）、`Index`、`Value`、`PreviousValue`。

### 不要用 `NetworkVariable<List<T>>`
```csharp
// ❌ 编译失败 — List<T> 非 unmanaged
public NetworkVariable<List<int>> Bad = new NetworkVariable<List<int>>();

// ✅ 用 NetworkList
public NetworkList<int> Good = new NetworkList<int>();
```

## AnticipatedNetworkVariable&lt;T&gt;（高级，可选）

```csharp
public class AnticipatedNetworkVariable<T> : NetworkVariableBase
```
源码：`Runtime/NetworkVariable/AnticipatedNetworkVariable.cs`。用于客户端预测 + 权威服务器回滚场景（FPS 移动、技能判定）。一般项目先不用。

## 字符串与集合实用类型

当 T 需要"字符串"或"小数组"时，使用 `Unity.Collections` 的固定尺寸类型：

| 类型 | 用途 |
|------|------|
| `FixedString32Bytes` | 短文本（昵称） |
| `FixedString64Bytes` / `128Bytes` / `512Bytes` / `4096Bytes` | 更长文本 |
| `NativeList<T>` / `NativeArray<T>` | 通常不直接放 NetworkVariable；作为 RPC 参数 OK |

## ❌ 常见错误 vs ✅ 正确模式

### 1. 把 new 放到 OnNetworkSpawn 里

```csharp
// ❌ WRONG — 字段未初始化，ILPP 找不到要追踪的实例
public NetworkVariable<int> Health;

public override void OnNetworkSpawn() {
    Health = new NetworkVariable<int>(100);  // 太晚
}

// ✅ CORRECT — 字段声明时 new
public NetworkVariable<int> Health = new NetworkVariable<int>(0);

public override void OnNetworkSpawn() {
    if (IsServer) Health.Value = 100;
}
```

### 2. 用 string 做 NetworkVariable

```csharp
// ❌ WRONG — string 不是 unmanaged
public NetworkVariable<string> Name = new NetworkVariable<string>("");

// ✅ CORRECT — FixedStringNBytes
using Unity.Collections;
public NetworkVariable<FixedString32Bytes> Name =
    new NetworkVariable<FixedString32Bytes>(new FixedString32Bytes(""));
```

### 3. 订阅 / 解订 不对称导致泄漏

```csharp
// ❌ WRONG — 没 OnNetworkDespawn 解订；Spawn 再 Despawn 多次会重复订阅
public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHp;
}

// ✅ CORRECT — 镜像解订
public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHp;
}
public override void OnNetworkDespawn() {
    Health.OnValueChanged -= OnHp;
}
```

### 4. 在 OnDestroy 读 NetworkVariable

```csharp
// ❌ WRONG — NetworkVariable 在 OnNetworkDespawn 后就不再有效
void OnDestroy() {
    Debug.Log(Health.Value);  // 可能抛或返回无意义值
}

// ✅ CORRECT — 清理在 OnNetworkDespawn
public override void OnNetworkDespawn() {
    Debug.Log($"Final Hp: {Health.Value}");
    Health.OnValueChanged -= OnHp;
}
```

### 5. Client 写默认权限 NetworkVariable

```csharp
// ❌ WRONG — 默认 Server 写，Client 赋值被丢弃
public NetworkVariable<int> Score = new NetworkVariable<int>();

void UI_OnClientScoreClick() {
    Score.Value++;   // Client 无效
}

// ✅ 选项 A — Owner 写权限
public NetworkVariable<int> Score = new NetworkVariable<int>(
    0,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);

// ✅ 选项 B — 保持 Server 写，用 RPC 请求
[Rpc(SendTo.Server)] void IncServerRpc() { Score.Value++; }
```

### 6. 自定义 struct 忘实现 INetworkSerializable

```csharp
// struct 含引用类型字段就不是 unmanaged，必须实现 INetworkSerializable
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

### 7. 高频写入不节流

```csharp
// ❌ WRONG — 每帧写，NetworkVariable 会每 tick 发包
void Update() {
    if (IsServer) Accuracy.Value = ComputeAccuracy();
}

// ✅ 设置 UpdateTraits 节流
void Awake() {
    Accuracy.SetUpdateTraits(new NetworkVariableUpdateTraits {
        MinSecondsBetweenUpdates = 0.1f   // 最多 10Hz
    });
}
```

## NetworkList 事件处理模板

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
            case NetworkListEvent<int>.EventType.Value:  /* index 处被赋值 */ break;
        }
    }

    [Rpc(SendTo.Server)]
    void AddItemServerRpc(int itemId) { Items.Add(itemId); }
}
```
