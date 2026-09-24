### gameobject_set_active
Enable or disable a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | null | Object name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `active` | bool | No | true | Enable state; pass `false` to disable |

*At least one identifier required

**Returns**: `{success, name, entityId, active}`
