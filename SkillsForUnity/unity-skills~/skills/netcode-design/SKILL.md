---
name: unity-netcode-design
description: "Netcode for GameObjects 源码级设计规则。写任何 NetworkBehaviour / RPC / NetworkVariable / Spawn 代码前必读，避免幻觉 API 和生命周期顺序 bug。Triggers: netcode, NGO, multiplayer, NetworkManager, NetworkObject, NetworkBehaviour, ServerRpc, ClientRpc, NetworkVariable, Spawn, Despawn, host, client, transport, relay, 网络同步, 多人游戏, 服务器权威, 主机, 客户端, RPC, 网络游戏, netcode 设计."
---

# Netcode for GameObjects - Design Rules

Advisory 模块。所有规则直接引自 `com.unity.netcode.gameobjects` 2.x 源码，每条都可追溯到具体文件行号。

> **Mode**: Both (Semi-Auto + Full-Auto) — 纯文档，不提供 REST skill。

## 何时加载本模块

写或审查以下任意内容之前加载：
- NetworkBehaviour 脚本（OnNetworkSpawn / OnNetworkDespawn / RPC / NetworkVariable）
- NetworkManager 启动 / 停止 / 场景切换
- NetworkObject Spawn / Despawn / ChangeOwnership / TrySetParent
- UnityTransport 配置（直连 / Relay）
- 任何涉及 IsHost / IsServer / IsClient / IsOwner 权限判断的代码

## Critical Rule Summary（不读子文档也至少记住这些）

| # | 规则 | 源码锚点 |
|---|------|---------|
| 1 | `Spawn()` / `Despawn()` 只能在 **Server/Host** 调用；Client 调用必失败 | `Runtime/Core/NetworkObject.cs:1884, 1921` |
| 2 | `OnNetworkSpawn()` **早于** Unity `Start()`，**晚于** `Awake`/`OnEnable` | `Runtime/Core/NetworkBehaviour.cs:704` + 调用方 `InvokeBehaviourNetworkSpawn` |
| 3 | 老式 `[ServerRpc]` 方法名**必须**以 `ServerRpc` 结尾；`[ClientRpc]` 必须以 `ClientRpc` 结尾（ILPP 编译期强制） | `Editor/CodeGen/` ILPP 校验 |
| 4 | 新式 `[Rpc(SendTo.X)]` 无命名约束；`SendTo` 枚举 11 个值 | `Runtime/Messaging/RpcTargets/RpcTarget.cs:9-80` |
| 5 | `PlayerPrefab` 必须存在于 `NetworkPrefabsList` 或 `NetworkConfig.Prefabs`，否则 2.x 运行时拒绝 | `Runtime/Configuration/NetworkConfig.cs:40` + `NetworkPrefabsList.cs:14` |
| 6 | **嵌套 NetworkObject 禁止**（父/子都是 NetworkObject 不允许）；要改父必须 `TrySetParent` | `Runtime/Core/NetworkObject.cs:2135-2215` |
| 7 | `NetworkVariable<T>` 的 `T` 必须 `unmanaged` 或实现 `INetworkSerializable`；**不能**是 `string`、`List<>`、`class` | `Runtime/NetworkVariable/NetworkVariable.cs:12` + ILPP |
| 8 | `NetworkList<T>` 的 `T: unmanaged, IEquatable<T>`；不是 `NetworkVariable<List<T>>` | `Runtime/NetworkVariable/Collections/NetworkList.cs:14` |
| 9 | `NetworkSceneManager.LoadScene/UnloadScene` 只能 Server 调用 | `Runtime/SceneManagement/NetworkSceneManager.cs:1496, 1252` |
| 10 | `UnityTransport.SetRelayServerData` 与 `SetConnectionData` **互斥**，只能用其一 | `Runtime/Transports/UTP/UnityTransport.cs:776-897` |

## 子文档路由

| 子文档 | 何时看 |
|--------|-------|
| [LIFECYCLE.md](./LIFECYCLE.md) | 生命周期、回调顺序、`Awake/OnNetworkSpawn/Start` 差异 |
| [OWNERSHIP.md](./OWNERSHIP.md) | IsOwner/IsServer/IsHost 权限矩阵、ChangeOwnership、Distributed Authority |
| [RPC.md](./RPC.md) | RPC 特性选择、SendTo 语义、RpcInvokePermission、deprecated 路径 |
| [VARIABLES.md](./VARIABLES.md) | NetworkVariable/NetworkList 初始化与序列化约束 |
| [SPAWNING.md](./SPAWNING.md) | Prefab 注册 → Spawn → Despawn、GlobalObjectIdHash、SpawnAsPlayerObject |
| [SCENE.md](./SCENE.md) | NetworkSceneManager、EnableSceneManagement、预置对象同步 |
| [TRANSPORT.md](./TRANSPORT.md) | UnityTransport 直连 / Relay / DebugSimulator 配置 |
| [PITFALLS.md](./PITFALLS.md) | 20 条常见幻觉 checklist |

## 与其他模块的路由

- 代码生成（创建 NetworkBehaviour 脚本）→ 使用 `netcode` 功能模块的 `netcode_add_network_behaviour_script`
- 场景内批量加挂 NetworkObject/NetworkTransform → 使用 `netcode` 功能模块的 Components 类 skill
- 架构级决策（Server-authoritative vs Distributed Authority 选型） → 同时加载 [architecture](../architecture/SKILL.md)

## Version Scope

本文档针对 `com.unity.netcode.gameobjects` **2.x**（验证版本 2.11.0，Unity 6000.0+）。部分 API（`SendTo.Authority`、`RpcInvokePermission`、`[Rpc]` 通用特性）在 1.x 不存在。
