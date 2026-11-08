---
name: unity-navmesh
description: Bake and query Unity NavMesh
---

# Unity NavMesh Skills

Bake or clear NavMesh data, calculate paths, sample positions, set area costs and configure NavMeshAgent and NavMeshObstacle components. Parameters come from the schema (`GET /skills/schema?category=NavMesh&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Setting up navigation for a scene
- Baking or clearing a NavMesh
- Testing agent paths or nearest reachable points
- Tuning agent or obstacle settings and area traversal costs

## Rules
- `navmesh_bake` runs synchronously and blocks the Editor; on large scenes warn the user first.
- Re-bake after scene geometry changes; the NavMesh does not follow them.
- `navmesh_clear` is a Delete operation: it is refused in Approval and Auto, and runs only in Bypass or when allowlisted.
- Query skills (`navmesh_calculate_path`, `navmesh_sample_position`, `navmesh_get_settings`) are read-only and run in every mode; the write skills need a grant under Approval.

## Pitfalls
- `navmesh_calculate_path` returns `status: "NoPath"` when `NavMesh.CalculatePath` returns false; `PathPartial` is not `valid`.
- `navmesh_set_area_cost` takes `areaIndex` 0-31 and `cost` >= 0.

## Skills

### `navmesh_bake`
Bake the NavMesh (synchronous). **Blocks the Editor; can be slow on large scenes.**
**Parameters:** None.

### `navmesh_clear`
Clear the NavMesh data.
**Parameters:** None.

### `navmesh_calculate_path`
Calculate a path between two points.
**Parameters:**
- `startX`, `startY`, `startZ` (float): Start position.
- `endX`, `endY`, `endZ` (float): End position.
- `areaMask` (int, optional): NavMesh area mask.

**Returns:** `{ status, valid, distance, cornerCount, corners }`

- `status`: `PathComplete`, `PathPartial`, `PathInvalid`, or `NoPath`.
- `valid`: `true` only when `status == "PathComplete"`.
- `distance`: total length of the corners (0 for invalid paths).
- `cornerCount`: number of entries in `corners`.
- `corners`: array of `{x, y, z}` waypoints (empty when there is no path).

### `navmesh_add_agent`
Add a NavMeshAgent component to an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns:** `{ success, gameObject }`

### `navmesh_set_agent`
Set NavMeshAgent properties (speed, acceleration, radius, height, stoppingDistance).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| speed | float | No | null | Agent movement speed |
| acceleration | float | No | null | Agent acceleration |
| angularSpeed | float | No | null | Agent angular speed |
| radius | float | No | null | Agent radius |
| height | float | No | null | Agent height |
| stoppingDistance | float | No | null | Distance to stop before target |

**Returns:** `{ success, gameObject, speed, radius }`

### `navmesh_add_obstacle`
Add a NavMeshObstacle component to an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| carve | bool | No | true | Enable carving |

**Returns:** `{ success, gameObject, carving }`

### `navmesh_set_obstacle`
Set NavMeshObstacle properties (shape, size, carving).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| shape | string | No | null | Obstacle shape (e.g. Box, Capsule); an unknown value is rejected before any write |
| sizeX | float | No | null | Obstacle size X |
| sizeY | float | No | null | Obstacle size Y |
| sizeZ | float | No | null | Obstacle size Z |
| carving | bool | No | null | Enable carving |

**Returns:** `{ success, gameObject, shape, carving }`

### `navmesh_sample_position`
Find the nearest point on the NavMesh.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | float | Yes | - | Source position X |
| y | float | Yes | - | Source position Y |
| z | float | Yes | - | Source position Z |
| maxDistance | float | No | 10 | Maximum search distance |

**Returns:** `{ success, found, point: { x, y, z }, distance }`

### `navmesh_set_area_cost`
Set an area's traversal cost.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| areaIndex | int | Yes | - | NavMesh area index, 0-31 |
| cost | float | Yes | - | Traversal cost, >= 0 |

**Returns:** `{ success, areaIndex, cost }`

### `navmesh_get_settings`
Get NavMesh build settings.

**Parameters:** None.

**Returns:** `{ success, agentRadius, agentHeight, agentSlope, agentClimb }`

## Routing
- NavMeshAgent or NavMeshObstacle properties beyond these skills → `component_add` / `component_set_property`.
- Path queries stay in this module (`navmesh_calculate_path`).

**DO NOT** (common hallucinations):
- `navmesh_create` does not exist: use `navmesh_bake`.
- `navmesh_add_agent_component` and `navmesh_set_agent_speed` do not exist: use `navmesh_add_agent` then `navmesh_set_agent`.
