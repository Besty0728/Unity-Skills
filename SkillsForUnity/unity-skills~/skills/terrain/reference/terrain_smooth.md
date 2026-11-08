### terrain_smooth
Smooth heights around a point to reduce sharp edges.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `normalizedX` | float | Yes | - | X position (0-1) |
| `normalizedZ` | float | Yes | - | Z position (0-1) |
| `radius` | float | No | 0.1 | Smoothing radius (0-1) |
| `iterations` | int | No | 1 | Number of smoothing passes |
| `name` | string | No | null | Terrain name |
| `entityId` | string | No | null | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | null | Terrain hierarchy path |

**Returns**: `{success, centerX, centerZ, radius, iterations, affectedArea}`

```
curl -s "http://localhost:<port>/skill/terrain_smooth?expectProject=<project>" -d '{"normalizedX":0.5,"normalizedZ":0.5,"radius":0.2,"iterations":3}'
```
