---
name: unity-navmesh
description: "Navigation mesh operations. Use when users want to bake NavMesh or calculate paths for AI navigation. Triggers: navmesh, navigation, pathfinding, bake, AI, agent, obstacle, 导航网格, 寻路, 烘焙."
---

# NavMesh Skills

Baking and pathfinding.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `navmesh_create` does not exist → use `navmesh_bake` to generate NavMesh
- `navmesh_add_agent_component` / `navmesh_set_agent_speed` do not exist → use `navmesh_add_agent` + `navmesh_set_agent` (convenience wrappers), or `component_add`/`component_set_property` for full control
- NavMesh must be re-baked after scene geometry changes

**Routing**:
- For NavMeshAgent/NavMeshObstacle components → use `component` module
- For path calculation → `navmesh_calculate_path` (this module)

## Skills

### `navmesh_bake`
Bake the NavMesh (Synchronous). **Warning: Can be slow.**
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

**Returns:** `{ status: "PathComplete", distance: 12.5, corners: [...] }`

### `navmesh_add_agent`
Add NavMeshAgent component to an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |

**Returns:** `{ success, gameObject }`

### `navmesh_set_agent`
Set NavMeshAgent properties (speed, acceleration, radius, height, stoppingDistance).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
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
Add NavMeshObstacle component to an object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| carve | bool | No | true | Enable carving |

**Returns:** `{ success, gameObject, carving }`

### `navmesh_set_obstacle`
Set NavMeshObstacle properties (shape, size, carving).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| shape | string | No | null | Obstacle shape (e.g. Box, Capsule) |
| sizeX | float | No | null | Obstacle size X |
| sizeY | float | No | null | Obstacle size Y |
| sizeZ | float | No | null | Obstacle size Z |
| carving | bool | No | null | Enable carving |

**Returns:** `{ success, gameObject, shape, carving }`

### `navmesh_sample_position`
Find nearest point on NavMesh.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| x | float | Yes | - | Source position X |
| y | float | Yes | - | Source position Y |
| z | float | Yes | - | Source position Z |
| maxDistance | float | No | 10 | Maximum search distance |

**Returns:** `{ success, found, point: { x, y, z }, distance }`

### `navmesh_set_area_cost`
Set area traversal cost.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| areaIndex | int | Yes | - | NavMesh area index |
| cost | float | Yes | - | Traversal cost value |

**Returns:** `{ success, areaIndex, cost }`

### `navmesh_get_settings`
Get NavMesh build settings.

**Parameters:** None.

**Returns:** `{ success, agentRadius, agentHeight, agentSlope, agentClimb }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.