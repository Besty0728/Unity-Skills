---
name: unity-physics
description: Run Unity physics queries and configure physics at editor time — raycasts, overlap checks, gravity, and physics settings. Use when casting rays, testing overlaps, querying colliders, or adjusting gravity/physics settings, even if the user just says "射线检测" or "物理设置". 执行 Unity 物理查询并在编辑器期配置物理(raycast、overlap 检测、重力、物理设置);当用户要发射射线、检测重叠、查询碰撞体、或调整重力/物理设置时使用。
---

# Physics Skills

Editor-time physics queries (raycast / overlap), gravity, PhysicMaterial assets, and layer collision matrix.

## Operating Mode

- **Approval**：查询类 skill（`physics_raycast` / `physics_check_overlap` / `physics_get_gravity` / `physics_get_layer_collision` 等，源码标 `SkillMode.SemiAuto`）直接执行；变更类（`physics_set_gravity` / `physics_create_material` / `physics_set_material` / `physics_set_layer_collision`，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：所有 skill 直接执行；Auto 走 AI 自我评估，Bypass 全放行。
- 本模块**不含** Delete / PlayMode / Reload / 高危 skill，无 Bypass-only 拦截项。
- 注意：所有 skill 都在编辑器线程同步运行。真实物理模拟（积分、碰撞响应）仅在 Play mode 下推进；本模块只做单帧 query + 资产/全局设置写入，不会启动模拟。

**DO NOT** (common hallucinations):
- `physics_add_rigidbody` / `physics_add_collider` do not exist → use `component_add` with componentType "Rigidbody"/"BoxCollider"/etc.
- `physics_simulate` does not exist → physics simulation runs during Play mode; this module does not step the simulation
- Raycast results use world-space coordinates

**Routing**:
- For adding physics components → use `component` module
- For physics material → use `physics_create_material` (this module)
- For layer collision matrix → `physics_set_layer_collision` (this module)

## Skills

### `physics_raycast`
Cast a ray and get hit info.
**Parameters:**
- `originX`, `originY`, `originZ` (float): Origin point.
- `dirX`, `dirY`, `dirZ` (float): Direction vector.
- `maxDistance` (float, optional): Max distance (default 1000).
- `layerMask` (int, optional): Layer mask (default -1).

**Returns:** `{ hit: true, collider: "Cube", distance: 5.2, ... }`

### `physics_check_overlap`
Check for colliders in a sphere.
**Parameters:**
- `x`, `y`, `z` (float): Center point.
- `radius` (float): Sphere radius.
- `layerMask` (int, optional): Layer mask.

### `physics_get_gravity`
Get global gravity setting.
**Parameters:** None.

### `physics_set_gravity`
Set global gravity setting.
**Parameters:**
- `x`, `y`, `z` (float): Gravity vector (e.g. 0, -9.81, 0).

### `physics_raycast_all`
Cast a ray and return ALL hits (penetrating).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX | float | Yes | - | Ray origin X |
| originY | float | Yes | - | Ray origin Y |
| originZ | float | Yes | - | Ray origin Z |
| dirX | float | Yes | - | Direction X |
| dirY | float | Yes | - | Direction Y |
| dirZ | float | Yes | - | Direction Z |
| maxDistance | float | No | 1000 | Max ray distance |
| layerMask | int | No | -1 | Layer mask filter |

**Returns:** `{ count, hits: [{ objectName, instanceId, path, point, normal, distance }] }`

### `physics_spherecast`
Cast a sphere along a direction and get hit info.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX | float | Yes | - | Origin X |
| originY | float | Yes | - | Origin Y |
| originZ | float | Yes | - | Origin Z |
| dirX | float | Yes | - | Direction X |
| dirY | float | Yes | - | Direction Y |
| dirZ | float | Yes | - | Direction Z |
| radius | float | Yes | - | Sphere radius |
| maxDistance | float | No | 1000 | Max cast distance |
| layerMask | int | No | -1 | Layer mask filter |

**Returns:** `{ hit, objectName, instanceId, point, distance }`

### `physics_boxcast`
Cast a box along a direction and get hit info.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX | float | Yes | - | Origin X |
| originY | float | Yes | - | Origin Y |
| originZ | float | Yes | - | Origin Z |
| dirX | float | Yes | - | Direction X |
| dirY | float | Yes | - | Direction Y |
| dirZ | float | Yes | - | Direction Z |
| halfExtentX | float | No | 0.5 | Box half extent X |
| halfExtentY | float | No | 0.5 | Box half extent Y |
| halfExtentZ | float | No | 0.5 | Box half extent Z |
| maxDistance | float | No | 1000 | Max cast distance |
| layerMask | int | No | -1 | Layer mask filter |

**Returns:** `{ hit, objectName, instanceId, point, distance }`

### `physics_overlap_box`
Check for colliders overlapping a box volume.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | float | Yes | - | Box center X |
| y | float | Yes | - | Box center Y |
| z | float | Yes | - | Box center Z |
| halfExtentX | float | No | 0.5 | Box half extent X |
| halfExtentY | float | No | 0.5 | Box half extent Y |
| halfExtentZ | float | No | 0.5 | Box half extent Z |
| layerMask | int | No | -1 | Layer mask filter |

**Returns:** `{ count, colliders: [{ objectName, path, isTrigger }] }`

### `physics_create_material`
Create a PhysicMaterial asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | "New PhysicMaterial" | Material name |
| savePath | string | No | "Assets" | Save directory path |
| dynamicFriction | float | No | 0.6 | Dynamic friction value |
| staticFriction | float | No | 0.6 | Static friction value |
| bounciness | float | No | 0 | Bounciness value |

**Returns:** `{ success, path }`

### `physics_set_material`
Set PhysicMaterial on a collider (supports name/instanceId/path).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| materialPath | string | Yes | - | Asset path of the PhysicMaterial |
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns:** `{ success, gameObject, material }`

### `physics_get_layer_collision`
Get whether two layers collide.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layer1 | int | Yes | - | First layer index |
| layer2 | int | Yes | - | Second layer index |

**Returns:** `{ layer1, layer2, collisionEnabled }`

### `physics_set_layer_collision`
Set whether two layers collide.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layer1 | int | Yes | - | First layer index |
| layer2 | int | Yes | - | Second layer index |
| enableCollision | bool | No | true | Whether to enable collision |

**Returns:** `{ success, layer1, layer2, collisionEnabled }`

---
## Minimal Example

```python
import unity_skills

# Raycast from position downward, check for hits
result = unity_skills.call_skill("physics_raycast",
    originX=0, originY=5, originZ=0,
    dirX=0, dirY=-1, dirZ=0,
    maxDistance=10
)
if result.get("hit"):
    print(f"Hit: {result['hitObjectName']} at distance {result['distance']}")
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.