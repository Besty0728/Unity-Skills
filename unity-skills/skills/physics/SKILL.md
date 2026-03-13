---
name: unity-physics
description: "Unity physics operations. Use when users want to perform raycasts, overlap checks, or configure gravity. Triggers: physics, raycast, overlap, gravity, collision, layer mask, 物理, 射线检测, 重力, 碰撞."
---

# Physics Skills

Raycasts, overlap checks, and gravity settings.

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
