# Netcode - RPC Rules

所有规则来自 `Runtime/Messaging/RpcAttributes.cs`、`Runtime/Messaging/RpcTargets/RpcTarget.cs`、`Runtime/Messaging/RpcParams.cs`。

## 三套 RPC 模型（按优先级）

### 1. 通用 RPC（推荐，2.x 新增）— `[Rpc(SendTo.X)]`

```csharp
[Rpc(SendTo.Server)]                             // 等价于 [ServerRpc]
void AttackServerRpc(int targetId) { ... }

[Rpc(SendTo.NotServer)]                          // 等价于 [ClientRpc]
void ShowDamageRpc(int damage) { ... }

[Rpc(SendTo.Owner)]                              // 只给 owner 客户端执行
void GrantLootRpc(int itemId) { ... }

[Rpc(SendTo.SpecifiedInParams)]                  // 运行时指定目标
void TellClientRpc(int msg, RpcParams p = default) { ... }
```

**无方法名约束**（不必以 `Rpc` / `ServerRpc` 结尾）。

### 2. 老式 ServerRpc / ClientRpc（1.x 起遗留，仍支持）

```csharp
[ServerRpc]
void AttackServerRpc(int targetId) { ... }       // ⚠ 方法名必须以 ServerRpc 结尾

[ClientRpc]
void ShowDamageClientRpc(int damage) { ... }     // ⚠ 方法名必须以 ClientRpc 结尾
```

`[ServerRpc]` 继承 `RpcAttribute`，构造函数调用 `base(SendTo.Server)`（`RpcAttributes.cs:160`）。
`[ClientRpc]` 同样继承，默认 `SendTo.NotServer`（`RpcAttributes.cs:176`）。

### 3. 自定义 Messages（底层）— `CustomMessageManager` / `MessagingSystem`

非常少用，只在需要手控 payload 序列化时考虑。本文档不展开。

## SendTo 枚举完整列表（11 个值）

来源 `Runtime/Messaging/RpcTargets/RpcTarget.cs:9-80`。

| Value | 目标范围 |
|-------|---------|
| `Owner` | 只送给当前 NetworkObject 的 owner |
| `NotOwner` | 所有非 owner 的可见观察者 |
| `Server` | 只送给 Server（包括 Host 的 Server 侧） |
| `NotServer` | 所有非 Server，含 Host 的 client 侧（但**不含**纯 Server 的 Host）  |
| `Me` | 本地执行（不走网络） |
| `NotMe` | 除本机外的所有观察者 |
| `Everyone` | 所有观察者（含 Server 自己） |
| `ClientsAndHost` | 所有 client 实例（含 Host 的 client 侧） |
| `Authority` | 权威端（ClientServer 下 = Server；DistributedAuthority 下 = object authority） |
| `NotAuthority` | 所有非权威端 |
| `SpecifiedInParams` | 强制运行时通过 `RpcParams` 指定 |

> **`NotServer` vs `ClientsAndHost` 差异**：`NotServer` 不会给 Host 的 Server-side，但是给 Host 的 Client-side 执行一次；`ClientsAndHost` 同等效果——两者在大多数场景可以互换，但代码语义应选更贴近意图的那个。

## RpcDelivery

```
RpcDelivery.Reliable   (default)   可靠有序，包大也不丢
RpcDelivery.Unreliable             不保证送达，适合高频率状态（位置等已有 NetworkTransform 做）
```

源码：`RpcAttributes.cs:8-19, 88`。

## InvokePermission（控制谁能发起 RPC）

```csharp
public enum RpcInvokePermission {
    Everyone = 0,   // 任何客户端都能发
    Server,         // 只有 Server 能发（很少用）
    Owner,          // 只有 owner 能发
}
```

源码：`RpcAttributes.cs:24-40`。

### `RequireOwnership`（deprecated）迁移表

| 老写法 | 新写法 |
|--------|--------|
| `[ServerRpc]` (默认 require=true) | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]` |
| `[ServerRpc(RequireOwnership = false)]` | `[Rpc(SendTo.Server)]`（默认 Everyone） |

源码注释：`RpcAttributes.cs:140-154`。

## RpcParams（运行时目标 / 发送方信息）

```csharp
// 从接收端取发送者 ID
[Rpc(SendTo.Server)]
void MyServerRpc(int payload, RpcParams rpcParams = default) {
    ulong sender = rpcParams.Receive.SenderClientId;
    // ...
}

// 发送到指定 client
void SendTo(ulong targetClient) {
    MyTargetRpc(42, RpcTarget.Single(targetClient, RpcTargetUse.Temp));
}

[Rpc(SendTo.SpecifiedInParams)]
void MyTargetRpc(int v, RpcParams p = default) { ... }
```

老式对应物 `ServerRpcParams` / `ClientRpcParams` 仍可用，但通用 `RpcParams` 更推荐。

## 参数序列化约束

RPC 参数类型 ILPP 编译期校验，必须是以下之一：

1. **unmanaged** 基础类型（int, float, bool, enum, struct of unmanaged ...）
2. Unity 内建序列化类型（`Vector3`, `Quaternion`, `Color`, ...）
3. `string`（内建支持）
4. `INetworkSerializable` 实现者
5. 上述任意的数组 `T[]` / `NativeArray<T>`

**禁止**：`class`（非 string）、`List<T>`、`Dictionary<K,V>`、`object`、`Task`、`Func`/`Action`、自定义引用类型。

## ❌ 常见错误 vs ✅ 正确模式

### 1. 幻觉特性

```csharp
// ❌ WRONG — 这些特性根本不存在
[ServerOnly] void X() { }
[ClientOnly] void X() { }
[NetworkRpc] void X() { }
[RPC(Client)] void X() { }
```

唯一存在的是 `[Rpc]` / `[ServerRpc]` / `[ClientRpc]`。

### 2. 老式 RPC 方法名没按约定

```csharp
// ❌ WRONG — ILPP 编译失败："ServerRpc methods must end with 'ServerRpc'"
[ServerRpc] void Attack(int id) { }

// ✅ CORRECT
[ServerRpc] void AttackServerRpc(int id) { }
// 或改用通用 RPC
[Rpc(SendTo.Server)] void Attack(int id) { }
```

### 3. RPC 返回 Task / async

```csharp
// ❌ WRONG — RPC 必须 void，async Task 会编译或运行时失败
[Rpc(SendTo.Server)]
async Task DoAsyncServerRpc() { await ...; }

// ✅ CORRECT — RPC 本身同步；异步工作放内部并用另一个 RPC 回传结果
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

### 4. 传 List / class / string[] 误用

```csharp
// ❌ WRONG — List<int> 不能做 RPC 参数
[Rpc(SendTo.Server)] void SetItemsServerRpc(List<int> items) { }

// ✅ CORRECT — 用数组
[Rpc(SendTo.Server)] void SetItemsServerRpc(int[] items) { }
```

### 5. 在 OnNetworkSpawn 发 RPC 给还没 Spawn 的对象

RPC 需要 NetworkObject 已 Spawn。`OnNetworkSpawn` 本身可以发 RPC，但要发给**其他**对象时需保证那些对象也已 Spawn。不确定时订阅 `NetworkManager.OnClientConnectedCallback`。

### 6. 忘记同步方法名和 `Rpc` 后缀的一致性（通用 RPC 不需要）

```csharp
// ✅ 通用 RPC — 无命名约束，但**推荐**带后缀 "Rpc" 让调用点明显：
[Rpc(SendTo.Server)] void FireRpc() { }  // 推荐
[Rpc(SendTo.Server)] void Fire() { }      // 合法但降低可读性
```

### 7. 高频状态同步选错 Delivery

```csharp
// ❌ WRONG — 每帧位置同步用默认 Reliable，带宽爆炸
[Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Reliable)]
void SendPositionRpc(Vector3 p) { }

// ✅ BETTER — 位置同步直接挂 NetworkTransform；必须自己同步时选 Unreliable
[Rpc(SendTo.NotOwner, Delivery = RpcDelivery.Unreliable)]
void SendPositionRpc(Vector3 p) { }
```

## 推荐 RPC 模板骨架

```csharp
using Unity.Netcode;

public class MyBehaviour : NetworkBehaviour
{
    // Client → Server：请求执行
    [Rpc(SendTo.Server)]
    void RequestFireServerRpc(Vector3 dir, RpcParams p = default)
    {
        if (p.Receive.SenderClientId != OwnerClientId) return;  // 权威校验
        // Server 权威执行
        SpawnBullet(dir);
        // 可选：广播结果
        PlayFireSoundRpc();
    }

    // Server → Clients：广播结果
    [Rpc(SendTo.ClientsAndHost)]
    void PlayFireSoundRpc() { /* 客户端播特效 */ }

    // Server → 指定 client（例如奖励 drop）
    [Rpc(SendTo.SpecifiedInParams)]
    void GrantLootRpc(int itemId, RpcParams p = default) { /* ... */ }

    void ServerGrantsLoot(ulong receiverId, int itemId) {
        GrantLootRpc(itemId, RpcTarget.Single(receiverId, RpcTargetUse.Temp));
    }
}
```
