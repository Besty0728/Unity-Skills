### prefab_unpack
Unpack a prefab instance (break prefab connection).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | Prefab instance name |
| `entityId` | string | No* | - | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | - | Instance ID |
| `path` | string | No* | - | Object path |
| `completely` | bool | No | false | Unpack all nested prefabs |

*At least one identifier required.

**Returns**: `{success, unpacked}` (`unpacked` is the instance's GameObject name)
