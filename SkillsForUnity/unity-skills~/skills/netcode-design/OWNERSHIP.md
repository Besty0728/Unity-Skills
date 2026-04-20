# Netcode - Ownership & Authority

所有规则来自 `Runtime/Core/NetworkBehaviour.cs:455-547`、`Runtime/Core/NetworkObject.cs:1172-2215`、`Runtime/Configuration/NetworkConfig.cs:169`。

## 角色关系

| 属性 | 含义 | 源码位置 |
|------|------|---------|
| `IsServer` | 当前进程是 Server 或 Host | `NetworkBehaviour.cs:505` |
| `IsClient` | 当前进程是 Client 或 Host | `NetworkBehaviour.cs:530` |
| `IsHost` | **等价于** `IsServer && IsClient` | `NetworkBehaviour.cs:536` |
| `IsOwner` | 当前 NetworkObject 的 `OwnerClientId == LocalClientId` 且已 Spawn | `NetworkObject.cs:1214` |
| `IsLocalPlayer` | 此 NetworkBehaviour 所在 NetworkObject 是本机 PlayerObject | `NetworkBehaviour.cs:495` |
| `OwnerClientId` | 当前所有者的 ClientId | `NetworkObject.cs:1177` |
| `LocalClientId` | 本机在网络中的 ID（Server 为 0） | `NetworkManager.cs:588` |

**重要不变式**：
- Host = Server + Client 同一进程。IsHost 为 true 时 IsServer **和** IsClient 都为 true。
- 只有 Server / Host 有权在 NetworkObject 上调用 `Spawn` / `Despawn` / `ChangeOwnership` / `RemoveOwnership`。
- ServerOnly（非 Host）模式下 `IsClient == false`；纯 Client 模式下 `IsServer == false`。

## 权限矩阵

| 操作 | Server | Host | Client (非 owner) | Client (owner) | 备注 |
|------|:------:|:----:|:-----------------:|:--------------:|------|
| `networkObject.Spawn()` | ✅ | ✅ | ❌ | ❌ | 其他调用者抛异常或被忽略 |
| `networkObject.Despawn()` | ✅ | ✅ | ❌ | ❌ | 同上 |
| `ChangeOwnership(id)` | ✅ | ✅ | ❌ | ❌ | `NetworkObject.cs:1971` |
| `RemoveOwnership()` | ✅ | ✅ | ❌ | ❌ | `NetworkObject.cs:1954` |
| 写 `NetworkVariable` (Server 写权限) | ✅ | ✅ | ❌ | ❌ | 默认权限 |
| 写 `NetworkVariable` (Owner 写权限) | ❌ | ✅* | ❌ | ✅ | *Host 当 Owner 时才能写 |
| 发起 `[Rpc(SendTo.Server)]` | — | ✅ | ✅ | ✅ | Server 自己也能发给自己 |
| 发起 `[Rpc(SendTo.X)]` (InvokePermission=Owner) | — | ✅* | ❌ | ✅ | *Host=owner 时可 |
| 读 `NetworkVariable` | ✅ | ✅ | ✅ | ✅ | 默认 `Everyone` 读权限 |
| `NetworkSceneManager.LoadScene` | ✅ | ✅ | ❌ | ❌ | `NetworkSceneManager.cs:1496` |
| `transform` 直接赋值并期望同步 | ❌ | ❌ | ❌ | ❌ | 必须通过 `NetworkTransform` 或权威 RPC |

## Distributed Authority 模式

`NetworkConfig.NetworkTopology = NetworkTopologyTypes.DistributedAuthority` 开启后，权限模型改变：

- 没有专门的 Server 角色；每个 NetworkObject 有自己的 **Authority**（默认 = Owner）
- 使用 `SendTo.Authority` / `SendTo.NotAuthority` 代替 `SendTo.Server` / `SendTo.NotServer`
- Owner 可直接写"自己所拥有对象"的 NetworkVariable、Spawn 新对象
- `NetworkObject.SetOwnershipStatus` + `OwnershipStatus` flags 控制谁能申请所有权

> 普通游戏建议先用 ClientServer（默认），遇到 P2P / 去中心需求再切 Distributed Authority。

## 所有权状态与锁

`NetworkObject.Ownership` 是 `OwnershipStatus` flags（`NetworkObject.cs:1023`）：

- `None` — 默认，固定拥有者
- `Distributable` — 可被系统自动迁移
- `Transferable` — 可被主动转移
- `RequestRequired` — 转移需申请批准
- `SessionOwner` — 迁到"会话所有者"（房主）

`IsOwnershipLocked`（`NetworkObject.cs:491`）可锁定转移。

## 转移 / 放弃所有权

```csharp
// Server 调用
networkObject.ChangeOwnership(targetClientId);   // NetworkObject.cs:1971
networkObject.RemoveOwnership();                 // NetworkObject.cs:1954（归还给 Server）
```

触发远端 / 本机的 `OnGainedOwnership` / `OnLostOwnership`。

## ❌ 常见错误 vs ✅ 正确模式

### 1. Client 端尝试 Spawn

```csharp
// ❌ WRONG — 拒绝执行
if (Input.GetKeyDown(KeyCode.F)) {
    Instantiate(bulletPrefab).GetComponent<NetworkObject>().Spawn();
}

// ✅ CORRECT — Client 发 RPC 给 Server，由 Server Spawn
[Rpc(SendTo.Server)]
void FireBulletServerRpc(Vector3 pos, Vector3 dir) {
    var bullet = Instantiate(bulletPrefab, pos, Quaternion.LookRotation(dir));
    bullet.GetComponent<NetworkObject>().Spawn();
}
```

### 2. 在 ServerRpc 内用 `IsOwner` 判断发送者

```csharp
// ❌ WRONG — ServerRpc 执行在 Server 上，IsOwner 是 Server 对此对象的视角，不是发送方
[Rpc(SendTo.Server)]
void DoSomethingServerRpc() {
    if (IsOwner) { ... }  // 永远是 Server 的 owner 视角
}

// ✅ CORRECT — 从 RpcParams 取 SenderClientId，与 OwnerClientId 比较
[Rpc(SendTo.Server)]
void DoSomethingServerRpc(RpcParams rpcParams = default) {
    ulong sender = rpcParams.Receive.SenderClientId;
    if (sender == OwnerClientId) { /* 来自真正的 owner */ }
}
```

### 3. Client 直接写 NetworkVariable

```csharp
// ❌ WRONG — 默认 Server 写权限，Client 赋值会被拒绝或日志报错
public NetworkVariable<int> Score = new NetworkVariable<int>();

void OnClientClicksButton() {
    Score.Value++;  // Client 上无效
}

// ✅ CORRECT — Client 通过 RPC 请求，Server 写
[Rpc(SendTo.Server)]
void IncrementScoreServerRpc() {
    Score.Value++;
}

void OnClientClicksButton() {
    IncrementScoreServerRpc();
}

// ✅ ALT — 若业务允许，声明 Owner 写权限
public NetworkVariable<int> Score = new NetworkVariable<int>(
    0,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);
```

### 4. 用 MonoBehaviour 普通 `transform.position = ...` 期望同步

```csharp
// ❌ WRONG — 直接修改 transform 不会通过网络同步
void Update() {
    if (IsServer) transform.position += Vector3.forward * Time.deltaTime;
}

// ✅ CORRECT — 挂 NetworkTransform 组件，然后正常改 transform；NetworkTransform 自动同步
// （或自行维护 NetworkVariable<Vector3> + 插值）
```

### 5. 认为 Host 既是 Server 又不算 Client，所以 SendTo.NotServer 包含 Host

```csharp
// ❌ WRONG — SendTo.NotServer 不会送给 Host。想同时让 Host 上的 Client 执行要用 SendTo.ClientsAndHost
[Rpc(SendTo.NotServer)]
void AnnounceToClientsRpc() { ... }

// ✅ CORRECT — 想让"所有 client 端（包括 Host 的 client 侧）"都执行，用 ClientsAndHost
[Rpc(SendTo.ClientsAndHost)]
void AnnounceToClientsRpc() { ... }
```
