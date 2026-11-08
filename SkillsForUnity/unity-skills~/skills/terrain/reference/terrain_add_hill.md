### terrain_add_hill
Add a smooth hill. Recommended over raw height edits for natural shapes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `radius` | float | No | 0.2 | Hill radius (0-1, relative to terrain size) |
| `height` | float | No | 0.5 | Hill height (0-1) |
| `smoothness` | float | No | 1.0 | Smoothness factor (higher = smoother) |
| `name` | string | No | null | Terrain name |
| `entityId` | string | No | null | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | null | Terrain hierarchy path |

**Returns**: `{success, centerX, centerZ, radius, height, affectedArea}`

```
curl -s "http://localhost:<port>/skill/terrain_add_hill?expectProject=<project>" -d '{"normalizedX":0.5,"normalizedZ":0.5,"radius":0.3,"height":0.6,"smoothness":2.0}'
```
