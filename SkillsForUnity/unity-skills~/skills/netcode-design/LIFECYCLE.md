# Netcode - Lifecycle & Call Order

所有规则来自 `Runtime/Core/NetworkBehaviour.cs`、`Runtime/Core/NetworkObject.cs`、`Runtime/Spawning/NetworkSpawnManager.cs`。

## 场景内预置 NetworkObject 的启动顺序

```
Unity Awake          (所有 MonoBehaviour / NetworkBehaviour)
Unity OnEnable
  ↓ (首帧前)
NetworkManager.StartHost/StartServer/StartClient
  ↓
SpawnManager 扫描场景中已有的 NetworkObject
  ↓
NetworkBehaviour.OnNetworkSpawn()  ← 这里 IsSpawned == true，IsOwner 可用
  ↓
Unity Start
  ↓ (持续)
Unity Update / FixedUpdate ...
```

**关键**：`OnNetworkSpawn` 先于 `Start`。任何依赖网络状态的初始化都应放 `OnNetworkSpawn` 而非 `Start`/`Awake`。

## 运行时 Spawn 对象的启动顺序

```
Instantiate(prefab)                    ← Server/Host 调用
  ↓
Unity Awake                            ← 此时 IsSpawned == false
  ↓
Unity OnEnable
  ↓
networkObject.Spawn()                  ← Server/Host 调用
  ↓
NetworkBehaviour.OnNetworkSpawn()      ← IsSpawned = true
  ↓
Unity Start                            ← 首帧
```

## 卸载顺序

```
NetworkObject.Despawn(true)  或 NetworkManager.Shutdown()
  ↓
NetworkBehaviour.OnNetworkDespawn()
  ↓
Unity OnDisable / OnDestroy
```

**注意**：`OnDestroy` 里访问 `NetworkVariable` 已经不安全（变量可能已被释放）；清理订阅请在 `OnNetworkDespawn`。

## 关键源码锚点

| 回调 | 声明位置 | 说明 |
|------|---------|------|
| `OnNetworkSpawn()` | `NetworkBehaviour.cs:704` | `public virtual void OnNetworkSpawn() { }` |
| `OnNetworkDespawn()` | `NetworkBehaviour.cs:749` | `public virtual void OnNetworkDespawn() { }` |
| `OnGainedOwnership()` | `NetworkBehaviour.cs:926` | 所有权迁入时触发 |
| `OnLostOwnership()` | `NetworkBehaviour.cs:962` | 所有权迁出时触发 |
| `OnNetworkObjectParentChanged(NetworkObject)` | `NetworkBehaviour.cs` | 父 NetworkObject 变更 |
| `NetworkObject.IsSpawned` | `NetworkObject.cs:1224` | `public bool IsSpawned { get; internal set; }` |
| `NetworkObject.NetworkObjectId` | `NetworkObject.cs:1172` | Spawn 后才有效 |

## ❌ 常见错误 vs ✅ 正确模式

### 1. 在 Awake / Start 访问网络状态

```csharp
// ❌ WRONG — NetworkManager.Singleton 可能 null，IsOwner 一定 false
void Awake() {
    if (NetworkManager.Singleton.IsServer) { ... }
    m_Health.Value = 100;
}

// ✅ CORRECT — OnNetworkSpawn 里 IsServer/IsOwner/IsClient 都已正确
public override void OnNetworkSpawn() {
    if (IsServer) {
        m_Health.Value = 100;
    }
}
```

### 2. 在 OnNetworkSpawn 里 `new` NetworkVariable

```csharp
// ❌ WRONG — NetworkVariable 必须字段声明时实例化，ILPP 在编译期绑定
public override void OnNetworkSpawn() {
    m_Health = new NetworkVariable<int>(100);  // 太晚了
}

// ✅ CORRECT — 字段声明处 new，OnNetworkSpawn 只做订阅/初值
public NetworkVariable<int> Health = new NetworkVariable<int>(0);

public override void OnNetworkSpawn() {
    Health.OnValueChanged += OnHealthChanged;
    if (IsServer) Health.Value = 100;
}

public override void OnNetworkDespawn() {
    Health.OnValueChanged -= OnHealthChanged;
}
```

### 3. 依赖 OnGainedOwnership 在初次 Spawn 触发

```csharp
// ❌ WRONG — 对象初次 Spawn 给 owner 时，OnGainedOwnership 不会被调用
//             只有 ChangeOwnership 迁移时才触发
public override void OnGainedOwnership() {
    InitForLocalPlayer();  // 初次 Spawn 的 owner 永远不会进来
}

// ✅ CORRECT — 初次 Owner 初始化放 OnNetworkSpawn
public override void OnNetworkSpawn() {
    if (IsOwner) InitForLocalPlayer();
}
public override void OnGainedOwnership() {
    InitForLocalPlayer();  // 仅在后续 ChangeOwnership 迁入时执行
}
```

### 4. Update 里轮询 NetworkManager.Singleton

```csharp
// ❌ WRONG — Singleton 可能瞬时切换（关机再开）；每帧访问也浪费
void Update() {
    if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening) {
        DoNetworkThing();
    }
}

// ✅ CORRECT — 用 NetworkBehaviour 的 IsSpawned 判断，或订阅生命周期事件
void Update() {
    if (!IsSpawned) return;
    DoNetworkThing();
}
```

## 何时用 `NetworkManager.OnServerStarted` / `OnClientStarted` / `OnClientConnectedCallback`

- `OnServerStarted` — Server/Host 成功启动后，NetworkManager 级别的一次性事件
- `OnClientConnectedCallback(ulong clientId)` — Server 侧：新客户端连入；Client 侧：本机连接完成（`clientId == LocalClientId`）
- `OnClientDisconnectCallback(ulong clientId)` — 对称
- 所有订阅都应在对应 `OnNetworkSpawn` 订阅、`OnNetworkDespawn` 解订，或者管理单例的 Awake/OnDestroy（需小心 Singleton 生命周期）

## 正确的 NetworkBehaviour 模板骨架

```csharp
using Unity.Netcode;
using UnityEngine;

public class MyNetworkBehaviour : NetworkBehaviour
{
    // 1. NetworkVariable — 字段声明处实例化
    public NetworkVariable<int> Health = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        // 2. 订阅 + 权威端写初值
        Health.OnValueChanged += OnHealthChanged;
        if (IsServer) Health.Value = 100;
        if (IsOwner)  InitLocalPlayer();
    }

    public override void OnNetworkDespawn()
    {
        // 3. 解订 — 镜像 OnNetworkSpawn
        Health.OnValueChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(int oldVal, int newVal) { /* UI 等 */ }

    private void InitLocalPlayer() { /* 只有拥有此对象的 client 执行 */ }
}
```
