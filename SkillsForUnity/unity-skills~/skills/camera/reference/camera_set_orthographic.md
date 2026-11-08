### camera_set_orthographic
Switch Game Camera between orthographic and perspective mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| orthographic | bool | Yes | - | True for orthographic, false for perspective |
| orthographicSize | float? | No | null | Orthographic size (only applies in orthographic mode) |
| name | string | No | null | Name of the camera GameObject |
| entityId | string | No | null | Entity ID of the camera GameObject (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, orthographic, orthographicSize }`
