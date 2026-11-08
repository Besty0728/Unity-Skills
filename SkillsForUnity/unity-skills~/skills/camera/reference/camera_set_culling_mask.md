### camera_set_culling_mask
Set Game Camera culling mask by layer names (comma-separated).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layerNames | string | Yes | - | Comma-separated layer names |
| name | string | No | null | Name of the camera GameObject |
| entityId | string | No | null | Entity ID of the camera GameObject (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, cullingMask, layers }` — `layers` lists the layer names the resulting mask includes.
