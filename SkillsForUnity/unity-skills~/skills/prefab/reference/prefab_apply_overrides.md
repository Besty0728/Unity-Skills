### prefab_apply_overrides
Apply all overrides from instance to source prefab asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Prefab instance name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path of the instance (or any object inside it); picks one of several same-name instances |

**Returns**: `{success, appliedTo}` (`appliedTo` is the prefab asset path)
