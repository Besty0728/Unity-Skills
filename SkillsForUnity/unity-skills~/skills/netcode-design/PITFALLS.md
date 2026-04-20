# Netcode - Pitfalls Checklist

AI 最常在 Netcode 代码里踩的坑。每条都可在源码里找到依据。写代码前/代码审查时扫一遍。

## 启动与生命周期

### ❌ P1. 场景里放多个 NetworkManager
- 症状：`NetworkManager.Singleton` 在 `Awake` 竞争后指向任意一个；行为不确定
- 源码：`NetworkManager.cs:881` 的 singleton 赋值逻辑
- ✅ 修：全项目仅保留一个 NetworkManager；若多个场景都有 NetworkManager，用 `DontDestroyOnLoad` + 启动场景唯一持有者

### ❌ P2. 在 Awake / Start 读 `IsOwner` / `IsSpawned`
- 症状：必为 false；后续网络行为错乱
- 源码：`NetworkObject.cs:1214, 1224` — 仅 OnNetworkSpawn 后才是真
- ✅ 修：所有网络状态判断放 `OnNetworkSpawn` 之后（含 Update，但需先 `if (!IsSpawned) return;`）

### ❌ P3. `NetworkManager.Shutdown()` 后立即读 NetworkVariable
- 症状：值错、NRE 或已释放
- ✅ 修：在 `OnNetworkDespawn` 里做最终读取与清理

### ❌ P4. `NetworkManager.Singleton` 还没创建就调用
- 症状：NRE
- ✅ 修：`if (NetworkManager.Singleton == null) return;`；或通过 `NetworkBehaviour.NetworkManager` 属性（`NetworkBehaviour.cs:455`）获取

## 所有权与权限

### ❌ P5. Client 上 `networkObject.Spawn()` / `Despawn()` / `ChangeOwnership()`
- 症状：InvalidOperation 或被忽略
- 源码：`NetworkObject.cs:1884, 1921, 1971`
- ✅ 修：走 Server 权威路径，通过 `[Rpc(SendTo.Server)]` 请求

### ❌ P6. Client 直接写默认权限 NetworkVariable
- 症状：Client 本地赋值被丢弃，UI 显示错了但 server 没变
- 源码：`NetworkVariablePermission.cs:25` 默认 `Server`
- ✅ 修：改 `WritePermission = Owner` 或发 ServerRpc

### ❌ P7. 在 ServerRpc 里用 `IsOwner` 判断发送方
- 症状：`IsOwner` 是 Server 视角的 owner，不是发送 client
- ✅ 修：`RpcParams p = default` 参数 → `p.Receive.SenderClientId == OwnerClientId`

## RPC

### ❌ P8. 老式 `[ServerRpc]` 方法名未以 `ServerRpc` 结尾
- 症状：ILPP 编译期报错 "ServerRpc methods must end with 'ServerRpc'"
- 源码：`Editor/CodeGen/` ILPP 校验
- ✅ 修：改名加后缀；或换成 `[Rpc(SendTo.Server)]`（无命名约束）

### ❌ P9. 认为 `SendTo.NotServer` 等于"所有 client"
- 症状：Host 的 client 侧没执行；误以为只有真 server 被跳过
- 源码：`Runtime/Messaging/RpcTargets/NotServerRpcTarget.cs`
- ✅ 修：想"所有 client 实例（含 Host 的 client 半）"用 `SendTo.ClientsAndHost`

### ❌ P10. RPC 参数用 `List<T>` / `class` / `string[]`（不支持 string 数组？支持但需小心）
- 症状：ILPP 报 "Parameter type not supported"
- 源码：`RpcParams.cs` + ILPP Rpc 生成器
- ✅ 修：只用 unmanaged / `INetworkSerializable` / `string`（单个） / 简单数组

### ❌ P11. RPC 方法返回 Task / async
- 症状：ILPP 失败；或运行时不按预期
- ✅ 修：RPC 必须 void；异步工作单独方法，完成后另一 RPC 回传结果

### ❌ P12. 高频位置同步走 `RpcDelivery.Reliable`
- 症状：带宽爆炸、延迟累积
- ✅ 修：位置走 `NetworkTransform`；需要自己发则 `RpcDelivery.Unreliable` + NetworkVariable UpdateTraits

## NetworkVariable / NetworkList

### ❌ P13. `NetworkVariable<string>` / `<List<T>>`
- 症状：ILPP 编译失败
- ✅ 修：string 用 `FixedString32Bytes`；集合用 `NetworkList<T>`

### ❌ P14. NetworkVariable 在 OnNetworkSpawn 里 new
- 症状：ILPP 没注册，值不同步
- ✅ 修：字段声明时 `= new NetworkVariable<T>(...)`；OnNetworkSpawn 只订阅 / 写初值

### ❌ P15. OnValueChanged 订阅了但 OnNetworkDespawn 没解订
- 症状：Spawn-Despawn-Spawn 循环后事件触发多次；引用持有导致对象不被回收
- ✅ 修：镜像订阅与解订

## Spawn / Prefab

### ❌ P16. prefab 没挂 `NetworkObject` 组件就想 Spawn
- 症状：`NetworkPrefab.Validate()` 返回 false，被 Netcode 忽略；或 NRE
- 源码：`NetworkPrefab.cs:155-170`
- ✅ 修：prefab 根节点挂 NetworkObject

### ❌ P17. `PlayerPrefab` 没在 NetworkPrefabsList / NetworkConfig.Prefabs 里
- 症状：client 连接时报 prefab 不匹配，被踢或不 spawn 玩家
- ✅ 修：把 PlayerPrefab 同时注册进 prefabs list（2.x 强制）

### ❌ P18. Prefab 内嵌套 NetworkObject
- 症状：运行时警告；嵌套子 NetworkObject 行为异常
- ✅ 修：拆成独立 prefab，运行时 `TrySetParent`

### ❌ P19. Spawn 后 `transform.parent = x` 而非 TrySetParent
- 症状：客户端 parent 状态不同步
- 源码：`NetworkObject.cs:2135-2215`
- ✅ 修：`networkObject.TrySetParent(newParent)`（Server 调用）

### ❌ P20. `Destroy(go)` 一个已 Spawn 的 NetworkObject
- 症状：其他客户端看不到销毁，留下"幽灵"引用
- ✅ 修：`networkObject.Despawn(destroy: true)`

## 场景

### ❌ P21. Client 调 `NetworkSceneManager.LoadScene`
- 症状：返回 `NotServer` 错误
- 源码：`NetworkSceneManager.cs:1496`
- ✅ 修：Server 调用；Client 通过 ServerRpc 请求

### ❌ P22. 用 `UnityEngine.SceneManagement.SceneManager.LoadScene` 切场景
- 症状：只在本机切换，对方不跟随
- ✅ 修：`NetworkManager.SceneManager.LoadScene(name, mode)`

### ❌ P23. 一帧连续两次 LoadScene
- 症状：第二次返回 `SceneEventInProgress`，被拒
- ✅ 修：订阅 `OnLoadComplete` 后再发下一个

## Transport

### ❌ P24. Client 的 `Address` 填 "0.0.0.0"
- 症状：连接失败，报无效目标
- ✅ 修：填 server 的真实可达 IP

### ❌ P25. 同时配置 ConnectionData 与 RelayServerData
- 症状：行为不一致；连接可能失败
- ✅ 修：Relay 与直连二选一

### ❌ P26. 发布版忘关 DebugSimulator
- 症状：用户侧 100ms 延迟 + 丢包
- ✅ 修：`#if DEVELOPMENT_BUILD || UNITY_EDITOR` 圈起

## 其他

### ❌ P27. 误用幻觉特性 / 方法
- 不存在：`[ServerOnly]`, `[ClientOnly]`, `[NetworkRpc]`, `NetworkObject.Instantiate()`, `rpc.Invoke()`, `controller.Call()`
- ✅ 修：只用 `[Rpc]` / `[ServerRpc]` / `[ClientRpc]`；Spawn 路径走 `Instantiate` + `.Spawn()` 或 `InstantiateAndSpawn`

### ❌ P28. 忽略 StartHost/StartServer/StartClient 返回值
- 源码：`NetworkManager.cs:1309, 1371, 1426` 全部返回 bool
- ✅ 修：`if (!NetworkManager.Singleton.StartHost()) { ...handle error... }`

### ❌ P29. 在 `OnDestroy` 里解订 NetworkVariable
- 症状：对象已 Despawn 过，`OnValueChanged` 可能已为 null
- ✅ 修：解订统一在 `OnNetworkDespawn`

### ❌ P30. 在 ServerRpc 做长时间同步等待（阻塞 server）
- 症状：server tick 卡顿，所有 client 受影响
- ✅ 修：RPC 内启动协程/Task，用 `_ = DoAsync();`，完成后另发回包 RPC
