### terrain_flatten
Flatten a region to a target height, for example a plateau.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `targetHeight` | float | No | 0.5 | Target height (0-1) |
| `radius` | float | No | 0.1 | Flatten radius (0-1) |
| `strength` | float | No | 1.0 | Flatten strength (0-1) |
| `name` | string | No | null | Terrain name |
| `entityId` | string | No | null | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | null | Terrain hierarchy path |

**Returns**: `{success, centerX, centerZ, targetHeight, radius, strength}`

```
curl -s "http://localhost:<port>/skill/terrain_flatten?expectProject=<project>" -d '{"normalizedX":0.5,"normalizedZ":0.5,"targetHeight":0.6,"radius":0.15,"strength":1.0}'
```
