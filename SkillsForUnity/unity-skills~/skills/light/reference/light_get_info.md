### light_get_info
Get detailed light information.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

**Returns:** `{name, entityId, instanceId, path, lightType, color, intensity, range, spotAngle, shadows, enabled, cullingMask, bounceIntensity}`
