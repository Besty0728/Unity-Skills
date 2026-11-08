### light_get_properties
Alias of `light_get_info`: same parameters, same response. It exists because the setter is `light_set_properties`, so the matching getter name resolves.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

**Returns:** identical to `light_get_info`.
