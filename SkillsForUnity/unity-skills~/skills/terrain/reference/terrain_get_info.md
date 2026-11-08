### terrain_get_info
Get terrain size, resolution and layers.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | - | Terrain name |
| `entityId` | string | No | - | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Instance ID |
| `path` | string | No | - | Terrain hierarchy path |

With none of these, the first terrain in the scene is used.

**Returns**: `{success, name, instanceId, position, size, heightmapResolution, alphamapResolution, detailResolution, terrainLayerCount, layers}`
