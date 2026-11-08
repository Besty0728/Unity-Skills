### prefab_revert_overrides
Revert all overrides on a prefab instance back to prefab values.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Prefab instance name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path of the instance (or any object inside it); picks one of several same-name instances |

**Returns**: `{success, reverted}` (`reverted` is the prefab root's GameObject name)
