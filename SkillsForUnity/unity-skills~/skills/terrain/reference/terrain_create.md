### terrain_create
Create a Terrain GameObject with a TerrainData asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Terrain" | Terrain name; the TerrainData asset is `Assets/<name>_Data.asset` (made unique) |
| `width` | int | No | 500 | Terrain width (X) |
| `length` | int | No | 500 | Terrain length (Z) |
| `height` | int | No | 100 | Max terrain height (Y) |
| `heightmapResolution` | int | No | 513 | Heightmap resolution (power of 2 + 1) |
| `x`, `y`, `z` | float | No | 0 | Position |

**Returns**: `{success, name, instanceId, terrainDataPath, size, position}`

```
curl -s "http://localhost:<port>/skill/terrain_create?expectProject=<project>" -d '{"name":"MyTerrain","width":200,"length":200,"height":50}'
```
