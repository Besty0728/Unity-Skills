### terrain_set_height
Set the height at normalized coordinates.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `height` | float | Yes | - | Height value (0-1) |
| `name` | string | No | - | Terrain name |
| `entityId` | string | No | - | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | - | Terrain hierarchy path |

**Returns**: `{success, normalizedX, normalizedZ, height, pixelX, pixelZ}`
