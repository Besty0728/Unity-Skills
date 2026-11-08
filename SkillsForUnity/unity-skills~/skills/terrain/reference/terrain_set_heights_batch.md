### terrain_set_heights_batch
Set heights over a rectangular region in one call. Use it instead of many `terrain_set_height` calls.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `startX` | int | Yes | - | Start X pixel index |
| `startZ` | int | Yes | - | Start Z pixel index |
| `heights` | float[][] | Yes | - | 2D array indexed `[z][x]`, values 0-1 |
| `name` | string | No | - | Terrain name |
| `entityId` | string | No | - | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | - | Terrain hierarchy path |

**Returns**: `{success, startX, startZ, modifiedWidth, modifiedLength, totalPointsModified}`

The `heights` array is nested `[z][x]`; a 10x10 hill is ten rows of ten values, so build it in your own code and send the JSON.
