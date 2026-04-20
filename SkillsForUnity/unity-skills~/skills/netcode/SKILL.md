---
name: unity-netcode
description: "Netcode for GameObjects (NGO) 多人联机自动化。创建 NetworkManager、注册 NetworkPrefabsList、挂载 NetworkObject/NetworkTransform、生成 NetworkBehaviour 模板、Host/Server/Client 运行时控制。Triggers: netcode, NGO, multiplayer, 多人, 联机, NetworkManager, NetworkObject, NetworkBehaviour, NetworkTransform, NetworkVariable, ServerRpc, ClientRpc, Spawn, Despawn, host, client, server, relay, transport, 主机, 客户端, 服务器. Requires com.unity.netcode.gameobjects (2.x)."
---

# Unity Netcode for GameObjects Skills

用于 Netcode for GameObjects（NGO）多人联机搭建与运维。所有 skill 在 NGO 2.x 上经源码校对；未装包时每个 skill 返回 `NoNetcode()` 错误。

> **Requires**: `com.unity.netcode.gameobjects` (2.x)，Unity 6000.0+。
> **强烈建议**：第一次调用 `netcode_*` 前先加载 [netcode-design](../netcode-design/SKILL.md) 模块 — NGO 生命周期和权限规则极严格，仅靠 skill 不能阻止业务代码写错。

## Guardrails

**Mode**: Full-Auto required

**DO NOT**（常见幻觉）：
- `netcode_spawn_object` / `netcode_spawn_player` — 不存在。Spawn 必须发生在运行时代码（NetworkBehaviour）里，走 `.Spawn()` 或 `NetworkManager.SpawnManager.InstantiateAndSpawn`；skill 层不直接代理 Spawn（Spawn 需要 NetworkManager 已启动）。
- `netcode_register_scene` — 不存在。场景注册走 Build Settings + `EnableSceneManagement`；本模块只提供 `netcode_configure_scene_management` 读写配置。
- `netcode_set_tick_rate` / `netcode_set_protocol` 单独 skill 不存在 — 统一走 `netcode_configure_manager`。
- 不要假设 `netcode_start_host` 在 Edit Mode 生效 — 所有 Runtime 控制 skill 要求 PlayMode。
- 不要假设 `netcode_add_to_prefabs_list` 会自动替你加 `NetworkObject` 组件 — 先用 `netcode_add_network_object`。

**Routing**：
- 纯普通 GameObject 层级创建 → `gameobject`
- 给 GameObject 加 NetworkObject/NetworkTransform 等联网组件 → 本模块
- 玩家 prefab 的一般属性（Rigidbody/Collider/Animator） → `component`
- 场景切换（运行时 LoadScene） → 在生成的 NetworkBehaviour 脚本里调 `NetworkManager.SceneManager.LoadScene`；本模块不代执行
- 代码设计决策（RPC 方向、NetworkVariable 权限） → [netcode-design](../netcode-design/SKILL.md) advisory

## Object Targeting

`netcode_add_*` / `netcode_configure_*` skill 常用参数：
- `name` — 场景对象名
- `instanceId` — Unity InstanceID（精确）
- `path` — 层级路径 `Parent/Child`

优先 `instanceId`（避免同名歧义）。

## Skills

### Setup & Validation
| Skill | 用途 | 关键参数 |
|-------|------|----------|
| `netcode_check_setup` | 验证包、NetworkManager、Transport、PlayerPrefab、PrefabsList 一致性 | `verbose?` |
| `netcode_create_manager` | 创建 NetworkManager + UnityTransport | `name?` |
| `netcode_configure_manager` | 批量改 NetworkConfig（TickRate、ConnectionApproval、EnableSceneManagement、NetworkTopology...） | `name?`, 15+ 可选字段 |
| `netcode_get_manager_info` | 读 NetworkConfig + 运行时状态 | `name?` |
| `netcode_remove_manager` | 删除 NetworkManager（必须已 Shutdown） | `name?` |

### Transport
| Skill | 用途 | 关键参数 |
|-------|------|----------|
| `netcode_set_transport_address` | 直连：设 Address/Port/ServerListenAddress | `address`, `port`, `serverListenAddress?` |
| `netcode_set_relay_server_data` | Relay 模式（与直连互斥） | `address`, `port`, `allocationIdBase64`, `keyBase64`, `connectionDataBase64`, `hostConnectionDataBase64?`, `isSecure?` |
| `netcode_set_debug_simulator` | 模拟延迟/抖动/丢包（仅开发） | `packetDelay`, `packetJitter`, `dropRate` |
| `netcode_get_transport_info` | 读当前 Transport 信息 | `name?` |

### NetworkObject
| Skill | 用途 | 关键参数 |
|-------|------|----------|
| `netcode_add_network_object` | 给 GameObject 加 NetworkObject | `name/instanceId/path` + NetworkObject 字段 |
| `netcode_configure_network_object` | 改现有 NetworkObject 字段 | 同上 |
| `netcode_remove_network_object` | 移除 NetworkObject（需未 Spawn） | 同上 |
| `netcode_list_network_objects` | 列出场景全部 NetworkObject（含运行时状态） | `includeInactive?` |
| `netcode_get_network_object_info` | 查单个 NetworkObject 详情 | 同上 |

### NetworkPrefabsList
| Skill | 用途 | 关键参数 |
|-------|------|----------|
| `netcode_create_prefabs_list` | 创建 NetworkPrefabsList 资产 | `path`, `assignToManager?` |
| `netcode_add_to_prefabs_list` | 添加 prefab（可选 override：None/Prefab/Hash） | `listPath`, `prefabPath`, `overrideMode?`, ... |
| `netcode_remove_from_prefabs_list` | 移除 prefab | `listPath`, `prefabPath` |
| `netcode_list_network_prefabs` | 列出所有条目含 hash | `listPath` |
| `netcode_set_player_prefab` | 设 NetworkConfig.PlayerPrefab | `prefabPath`, `name?` |

### Components
| Skill | 用途 | 关键参数 |
|-------|------|----------|
| `netcode_add_network_transform` | 挂 NetworkTransform + 轴向同步开关 | target + 15 个可选字段 |
| `netcode_configure_network_transform` | 改现有 NT 字段与阈值 | 含 PositionThreshold 等 |
| `netcode_add_network_rigidbody` | 挂 NetworkRigidbody / NetworkRigidbody2D | `useRigidbody2D?`, `useRigidBodyForMotion?` |
| `netcode_add_network_animator` | 挂 NetworkAnimator（需 Animator） | target |
| `netcode_add_network_behaviour_script` | 生成脚本模板（OnNetworkSpawn/Despawn + 可选 RPC/NetworkVariable/Ownership） | `className`, `path`, `includeRpc?`, `includeNetworkVariable?`, `includeOwnershipCallbacks?` |
| `netcode_list_network_behaviours` | 列场景中 NetworkBehaviour 子类实例 | `includeInactive?` |

### Scene & Spawning Query
| Skill | 用途 |
|-------|------|
| `netcode_configure_scene_management` | 设 EnableSceneManagement / LoadSceneTimeOut / ClientSynchronizationMode |
| `netcode_get_spawn_manager_info` | 运行时列 SpawnedObjects |
| `netcode_get_scene_manager_info` | 运行时读场景加载状态 |

### Runtime Control（需 PlayMode）
| Skill | 用途 |
|-------|------|
| `netcode_start_host` | 启动 Host |
| `netcode_start_server` | 启动 Server |
| `netcode_start_client` | 启动 Client |
| `netcode_shutdown` | 关闭（可选 discardMessageQueue） |
| `netcode_get_status` | 读 IsHost/Server/Client、LocalClientId、ConnectedClients、NetworkTime |

## Quick Start

```python
import unity_skills as u

# 1. 检查现状
u.call_skill("netcode_check_setup")

# 2. 创建 NetworkManager + UnityTransport
u.call_skill("netcode_create_manager", name="NetworkManager")

# 3. 配置
u.call_skill("netcode_configure_manager",
    tickRate=30,
    connectionApproval=False,
    enableSceneManagement=True,
    networkTopology="ClientServer")

# 4. Transport
u.call_skill("netcode_set_transport_address",
    address="127.0.0.1", port=7777, serverListenAddress="0.0.0.0")

# 5. 准备 Player
u.call_skill("netcode_add_network_object", path="Assets/Prefabs/Player.prefab")  # 注意：add_network_object 当前对 prefab 的支持取决于 GameObjectFinder — 推荐先实例化到场景中再加
u.call_skill("netcode_create_prefabs_list", path="Assets/NetworkPrefabs.asset")
u.call_skill("netcode_add_to_prefabs_list",
    listPath="Assets/NetworkPrefabs.asset",
    prefabPath="Assets/Prefabs/Player.prefab")
u.call_skill("netcode_set_player_prefab", prefabPath="Assets/Prefabs/Player.prefab")

# 6. 生成 NetworkBehaviour 脚本模板
u.call_skill("netcode_add_network_behaviour_script",
    className="PlayerController",
    path="Assets/Scripts/PlayerController.cs",
    includeRpc=True, includeNetworkVariable=True)

# 7. PlayMode 下启动
u.call_skill("editor_play")   # 进入 PlayMode
u.call_skill("netcode_start_host")
u.call_skill("netcode_get_status")
u.call_skill("netcode_shutdown")
u.call_skill("editor_stop")
```

## Critical Rules（必读）

1. **Spawn/Despawn 不由 skill 提供**。它们必须在 NetworkBehaviour 代码里调用（Server 权威）；skill 负责 prefab 注册和 Manager 启动。
2. **PlayerPrefab 必须放入 NetworkPrefabsList**（2.x 运行时强校验）。用 `netcode_add_to_prefabs_list` 注册。
3. **Runtime 控制 skill（start_*/shutdown）要求 PlayMode**。Edit Mode 调用返回错误。
4. **Address 与 ServerListenAddress 语义不同**：Client 的 Address 是目标 server IP；Server 的 ServerListenAddress 是监听地址（通常 `0.0.0.0`）。
5. **`useRigidbody2D`** 切换 NetworkRigidbody 与 NetworkRigidbody2D；Physics2D.AutoSyncTransforms 等 Unity 物理设置在别处。

## Version Scope

针对 NGO **2.x**（验证版本 2.11.0）。1.x（使用老式 Prefabs 列表、不同 RPC 模型）不在本模块保证范围内。

## Exact Signatures

精确参数名、默认值与返回字段请查询 `GET /skills/schema` 或 `unity_skills.get_skill_schema()`。本文档是路由与最佳实践指南，不是签名权威来源。
