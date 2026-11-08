### light_set_enabled
Enable or disable one light.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | Light object name |
| `entityId` | string | No* | - | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | - | Instance ID |
| `path` | string | No* | - | Hierarchy path |
| `enabled` | bool | No | `true` | Enable state |

**Returns:** `{success, name, enabled}`
