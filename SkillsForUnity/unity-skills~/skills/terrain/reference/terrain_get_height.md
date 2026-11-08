### terrain_get_height
Get the terrain height at a world position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `worldX` | float | Yes | - | World X coordinate |
| `worldZ` | float | Yes | - | World Z coordinate |
| `name` | string | No | - | Terrain name |
| `entityId` | string | No | - | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | - | Terrain hierarchy path |

**Returns**: `{success, worldX, worldZ, height, worldY}`
