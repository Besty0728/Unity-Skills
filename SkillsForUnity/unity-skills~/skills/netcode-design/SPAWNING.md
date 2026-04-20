# Netcode - Spawning & Prefab Registration

所有规则来自 `Runtime/Core/NetworkObject.cs:1884-2215`、`Runtime/Spawning/NetworkSpawnManager.cs`、`Runtime/Configuration/NetworkPrefab.cs`、`Runtime/Configuration/NetworkPrefabsList.cs`。

## 工作流（最短路径）

```
① 创建 NetworkPrefabsList 资产（或在 NetworkConfig.Prefabs 直接加）
② 把 prefab（必须挂 NetworkObject）加入列表
③ 若是 player prefab — 赋给 NetworkConfig.PlayerPrefab
④ Server/Host 启动 (StartHost/StartServer)
⑤ Server 侧：Instantiate(prefab) + .GetComponent<NetworkObject>().Spawn()
⑥ 所有 client 收到 Spawn 消息，创建对应实例并触发 OnNetworkSpawn
```

## 关键 API

### NetworkObject 上的 Spawn 变体

```csharp
public void Spawn(bool destroyWithScene = false);                              // NetworkObject.cs:1884
public void SpawnWithOwnership(ulong clientId, bool destroyWithScene = false); // :1902
public void SpawnAsPlayerObject(ulong clientId, bool destroyWithScene = false); // :1912
public void Despawn(bool destroy = true);                                      // :1921
```

- `Spawn()` — Server 拥有（OwnerClientId = Server/Host ID = 0）
- `SpawnWithOwnership(id)` — 指定 owner
- `SpawnAsPlayerObject(id)` — 既 spawn 又标记为该 client 的 PlayerObject（`IsLocalPlayer` 将在那个 client 上为 true）
- `Despawn(true)` — 默认销毁 GameObject；`false` 只是"从网络注销"，保留 GameObject

### NetworkSpawnManager 一站式

```csharp
public NetworkObject InstantiateAndSpawn(
    NetworkObject networkPrefab,
    ulong ownerClientId        = NetworkManager.ServerClientId,
    bool  destroyWithScene     = false,
    bool  isPlayerObject       = false,
    bool  forceOverride        = false,
    Vector3    position        = default,
    Quaternion rotation        = default);                             // NetworkSpawnManager.cs:736
```

比"手动 Instantiate + Spawn"更安全：自动处理 NetworkPrefabHandler 覆盖、场景关联、初始 transform。

### GetPlayerNetworkObject

```csharp
public NetworkObject GetPlayerNetworkObject(ulong clientId);  // NetworkSpawnManager.cs:372
```

找到某 client 的 PlayerObject。Server 上可获取任意 client 的；Client 上只能获取本机的。

## NetworkPrefab 结构

```csharp
[Serializable]
public class NetworkPrefab {
    public NetworkPrefabOverride Override;        // None / Prefab / Hash
    public GameObject Prefab;                     // 默认场景
    public GameObject SourcePrefabToOverride;     // Override=Prefab 时用
    public uint       SourceHashToOverride;       // Override=Hash 时用
    public GameObject OverridingTargetPrefab;     // 被替换进去的 prefab
    public uint SourcePrefabGlobalObjectIdHash { get; }
    public uint TargetPrefabGlobalObjectIdHash  { get; }
    public bool Validate(int index = -1);         // 返回 false 会被 Netcode 丢弃
}
```
源码：`Runtime/Configuration/NetworkPrefab.cs:32-247`。

### NetworkPrefabsList（ScriptableObject，可复用）

```csharp
[CreateAssetMenu(fileName = "NetworkPrefabsList", menuName = "Netcode/Network Prefabs List")]
public class NetworkPrefabsList : ScriptableObject {
    public IReadOnlyList<NetworkPrefab> PrefabList { get; }
    public void Add(NetworkPrefab prefab);
    public void Remove(NetworkPrefab prefab);
    public bool Contains(GameObject prefab);
    public bool Contains(NetworkPrefab prefab);
}
```
源码：`Runtime/Configuration/NetworkPrefabsList.cs:15-95`。

**修改时运行时生效**：多个 NetworkManager 引用同一 List，`Add/Remove` 会广播给所有引用者。

## GlobalObjectIdHash

- 每个 `NetworkObject` 在编辑器创建/导入时生成 `uint GlobalObjectIdHash`
- 这是 Server 和 Client 识别"同一个 prefab"的 key
- **重要**：重新导入同一 prefab 保留 hash；**复制**或**新建** prefab 时 hash 变化 — 所有联机双方要用**相同工程**或保证 prefab 文件一致

## ForceSamePrefabs

`NetworkConfig.ForceSamePrefabs = true`（默认）强制连接时校验 Server/Client 的 `Prefabs` 列表一致。建议生产环境保持 true。若 Server / Client 有意用不同 asset（例如不同 LOD），设为 false 并自己承担 hash 一致性。

## NetworkPrefabOverride

- `None` — 普通注册；Server 与 Client 用同一 prefab
- `Prefab` — Server 请求 prefab A，Client 改用 prefab B（A/B 在同工程但组件不同）
- `Hash` — Client 工程里没有 Server 侧 prefab，只知道 hash；映射到本地另一 prefab

绝大多数项目只用 `None`。

## 嵌套规则

```csharp
public bool TrySetParent(NetworkObject parent, bool worldPositionStays = true);  // NetworkObject.cs:2196
public bool TrySetParent(GameObject   parent, bool worldPositionStays = true);   // :2155
public bool TrySetParent(Transform    parent, bool worldPositionStays = true);   // :2135
```

**规则**：
- 已 Spawn 的 NetworkObject 改父**必须**用 `TrySetParent`（Server 调用）；不能 `transform.parent = ...`
- **不能**把 NetworkObject B 的子孙里包含另一个 NetworkObject 作为 **prefab** 上的静态嵌套（运行时重新 Parent 到 NetworkObject 下 OK）
- `AutoObjectParentSync = true`（默认）让 parent 变化自动同步；设 false 需自己 RPC

## Scene 关联

- `Spawn(destroyWithScene: true)` — 对象绑定到当前 active scene；scene 卸载时自动 Despawn
- `Spawn(destroyWithScene: false)` — 对象独立于 scene；需显式 Despawn 或 NetworkManager.Shutdown()
- 场景切换见 [SCENE.md](./SCENE.md)

## ❌ 常见错误 vs ✅ 正确模式

### 1. Client 上 Spawn

```csharp
// ❌ WRONG — InvalidOperation / 消息被拒
if (IsClient) {
    Instantiate(prefab).GetComponent<NetworkObject>().Spawn();
}

// ✅ Client 发 ServerRpc，Server Spawn
[Rpc(SendTo.Server)]
void RequestSpawnServerRpc(Vector3 pos) {
    var go = Instantiate(prefab, pos, Quaternion.identity);
    go.GetComponent<NetworkObject>().Spawn();
}
```

### 2. 忘记注册 prefab

```csharp
// ❌ 运行时：Server 能 Spawn，但 client 打印
// [Netcode] Failed to create object locally. [globalObjectIdHash=XXX] 不在 prefab list
```
检查：prefab 是否在 `NetworkConfig.Prefabs` 或 `NetworkPrefabsList` 里；`PlayerPrefab` 是否也注册（某些版本需要额外注册）。

### 3. 把 NetworkObject 当 prefab 的嵌套子物体

```
Prefab A (NetworkObject)
 └─ Child (NetworkObject)   ← 禁止，Spawn 会失败或警告
```

✅ 正确：把 Child 拆成独立 prefab，运行时 Spawn 完用 `TrySetParent(A)`。

### 4. 忘记调 ToMesh/Refresh 之类，但这是 ProBuilder 的坑 — Netcode 不需要

（列这里提醒别被其他 Unity 模块的"后处理调用"误导；Spawn 本身就足够，不需要再调任何刷新方法。）

### 5. 重复 Spawn / 对象未在 Server 却 Despawn

```csharp
// ❌ WRONG — 先判 IsSpawned
if (no.IsSpawned == false) no.Despawn();

// ✅ CORRECT
if (no.IsSpawned) no.Despawn();
```

### 6. 直接 `Destroy(go)` 一个已 Spawn 的 NetworkObject

```csharp
// ❌ WRONG — Server 端其他客户端不同步，状态错乱
Destroy(go);

// ✅ CORRECT
networkObject.Despawn(destroy: true);   // 自动广播 despawn 并销毁
```

### 7. NetworkPrefab.Override = Prefab 但 SourcePrefabToOverride 未设

`NetworkPrefab.Validate()` 会返回 false，Netcode 丢弃该条目。填写完整字段或用 `Override=None`。

## Spawn/Despawn 模板

```csharp
using Unity.Netcode;
using UnityEngine;

public class EnemySpawner : NetworkBehaviour
{
    public NetworkObject enemyPrefab;  // 在 Inspector 拖入，必须已注册

    public void SpawnEnemy(Vector3 pos) {
        if (!IsServer) return;  // 严格权威端执行
        var enemy = NetworkManager.SpawnManager.InstantiateAndSpawn(
            enemyPrefab,
            ownerClientId: NetworkManager.ServerClientId,
            destroyWithScene: true,
            position: pos,
            rotation: Quaternion.identity);
        // enemy 已 Spawn，client 端稍后触发 OnNetworkSpawn
    }

    public void KillEnemy(NetworkObject enemy) {
        if (!IsServer) return;
        if (enemy.IsSpawned) enemy.Despawn(destroy: true);
    }
}
```
