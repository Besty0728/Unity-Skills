### terrain_paint_texture
Paint a terrain texture layer at a position. The terrain layers must already exist.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `layerIndex` | int | Yes | - | Terrain layer index (0-based; list layers with `terrain_get_info`). Out-of-range values, including negative ones, are rejected before anything is painted. |
| `strength` | float | No | 1.0 | Paint strength |
| `brushSize` | int | No | 10 | Brush size in pixels |
| `name` | string | No | null | Terrain name |
| `entityId` | string | No | null | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | null | Terrain hierarchy path |

**Returns**: `{success, layerIndex, layerName, centerX, centerZ, brushSize, strength}`
