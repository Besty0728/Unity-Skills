### camera_get_properties
Get Game Camera properties (supports name/entityId/instanceId/path).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the camera GameObject |
| entityId | string | No | null | Entity ID of the camera GameObject (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, name, fieldOfView, nearClipPlane, farClipPlane, orthographic, orthographicSize, depth, cullingMask, clearFlags, backgroundColor, rect }`
