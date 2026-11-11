# Unity Physics Skills

Editor-time physics queries (raycast, sphere and box casts, overlap), gravity, PhysicMaterial assets and the layer collision matrix. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Casting rays or shapes
- Testing overlaps
- Querying colliders
- Adjusting gravity or physics settings

## Rules
- Each query is a single-frame check on the editor thread. Simulation (integration, collision response) advances only in Play Mode; these skills never start a simulation.
- Raycast and cast results use world-space coordinates.
- `layerMask` on queries is a bit mask (bit n = layer n; for example 256 = layer 8 only), not a layer index. `-1` means all layers.
- `layer1` and `layer2` on the layer-collision skills are indices 0-31. A name from `layer1Name` / `layer2Name` overrides the index on the same side.
- Adding physics components belongs to the [component](../component/MODULE.md) module (`component_add` with `Rigidbody`, `BoxCollider`, etc.).

## Pitfalls
- `physics_add_rigidbody`, `physics_add_collider` and `physics_simulate` do not exist.
- `physics_raycast` returns `collider` (the collider name) and `objectName` (the GameObject name), not `hitObjectName`.
- A zero direction vector returns `error: "Direction vector cannot be zero"`. Radius and half-extent values must be non-negative.
- `TARGET_NOT_FOUND` on `physics_set_material` means the target has no `Collider` (add one with `component_add`) or the PhysicMaterial path did not load (check it with `asset_find`).
- `physics_create_material` refuses a read-only package (registry, git or built-in) or a path no package owns with `SEMANTIC_INVALID`, before anything is created.

## Skills

### physics_raycast
Cast a ray and return the first hit.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX, originY, originZ | float | Yes | - | Ray origin |
| dirX, dirY, dirZ | float | Yes | - | Direction (normalized by the skill) |
| maxDistance | float | No | 1000 | Max ray distance |
| layerMask | int | No | -1 | Layer bit mask |

**Returns**: `{hit, collider, colliderEntityId, colliderInstanceId, objectName, objectEntityId, objectInstanceId, path, point, normal, distance}`; `{hit: false}` on a miss.

### physics_raycast_all
Cast a ray and return all hits, including those behind the first.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX, originY, originZ | float | Yes | - | Ray origin |
| dirX, dirY, dirZ | float | Yes | - | Direction |
| maxDistance | float | No | 1000 | Max ray distance |
| layerMask | int | No | -1 | Layer bit mask |

**Returns**: `{count, hits: [{objectName, instanceId, path, point, normal, distance}]}`

### physics_spherecast
Cast a sphere along a direction and return the first hit.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX, originY, originZ | float | Yes | - | Origin |
| dirX, dirY, dirZ | float | Yes | - | Direction |
| radius | float | Yes | - | Sphere radius |
| maxDistance | float | No | 1000 | Max cast distance |
| layerMask | int | No | -1 | Layer bit mask |

**Returns**: `{hit, objectName, entityId, instanceId, point, distance}`

### physics_boxcast
Cast a box along a direction and return the first hit.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| originX, originY, originZ | float | Yes | - | Origin |
| dirX, dirY, dirZ | float | Yes | - | Direction |
| halfExtentX, halfExtentY, halfExtentZ | float | No | 0.5 | Box half extents |
| maxDistance | float | No | 1000 | Max cast distance |
| layerMask | int | No | -1 | Layer bit mask |

**Returns**: `{hit, objectName, entityId, instanceId, point, distance}`

### physics_check_overlap
Check for colliders inside a sphere.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x, y, z | float | Yes | - | Sphere center |
| radius | float | Yes | - | Sphere radius, non-negative |
| layerMask | int | No | -1 | Layer bit mask |

### physics_overlap_box
Check for colliders overlapping a box volume.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x, y, z | float | Yes | - | Box center |
| halfExtentX, halfExtentY, halfExtentZ | float | No | 0.5 | Box half extents |
| layerMask | int | No | -1 | Layer bit mask |

**Returns**: `{count, colliders: [{objectName, path, isTrigger}]}`

### physics_get_gravity
Get the global gravity setting.

**Parameters**: none.

### physics_set_gravity
Set the global gravity vector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x, y, z | float | Yes | - | Gravity vector, e.g. 0, -9.81, 0 |

### physics_create_material
Create a PhysicMaterial asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | "New PhysicMaterial" | Material name |
| savePath | string | No | "Assets" | Folder under `Assets/`, or inside an embedded or local package (`Packages/<id>/...`). Missing folders are created. |
| dynamicFriction | float | No | 0.6 | Dynamic friction |
| staticFriction | float | No | 0.6 | Static friction |
| bounciness | float | No | 0 | Bounciness |

**Returns**: `{success, path}`. `path` is `<savePath>/<name>.physicMaterial`; a name already taken gets a numeric suffix (`<name> 1.physicMaterial`).

### physics_set_material
Assign a PhysicMaterial to a collider.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| materialPath | string | Yes | - | Asset path of the PhysicMaterial |
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns**: `{success, gameObject, material, collider}`. `material` is the asset path read back from the collider; `collider` is the component type name (e.g. `BoxCollider`).

### physics_get_layer_collision
Check whether two layers collide.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layer1 | int | No | -1 | First layer index (0-31); required unless `layer1Name` is given |
| layer2 | int | No | -1 | Second layer index (0-31); required unless `layer2Name` is given |
| layer1Name | string | No | null | First layer name from Tags & Layers; overrides `layer1` when given |
| layer2Name | string | No | null | Second layer name from Tags & Layers; overrides `layer2` when given |

**Returns**: `{layer1, layer2, collisionEnabled}`

### physics_set_layer_collision
Set whether two layers collide.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layer1 | int | No | -1 | First layer index (0-31); required unless `layer1Name` is given |
| layer2 | int | No | -1 | Second layer index (0-31); required unless `layer2Name` is given |
| enableCollision | bool | No | true | Whether the two layers collide |
| layer1Name | string | No | null | First layer name from Tags & Layers; overrides `layer1` when given |
| layer2Name | string | No | null | Second layer name from Tags & Layers; overrides `layer2` when given |

**Returns**: `{success, layer1, layer2, collisionEnabled}`

## Example

Raycast downward from (0, 5, 0):

```
curl -s "http://localhost:<port>/skill/physics_raycast?expectProject=<project>" -d '{"originX":0,"originY":5,"originZ":0,"dirX":0,"dirY":-1,"dirZ":0,"maxDistance":10}'
```
