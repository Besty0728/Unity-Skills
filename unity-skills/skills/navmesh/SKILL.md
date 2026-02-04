# NavMesh Skills

Baking and pathfinding.

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
