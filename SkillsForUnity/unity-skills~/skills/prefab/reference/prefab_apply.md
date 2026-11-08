### prefab_apply
Apply instance changes back to the prefab asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Prefab instance name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Object path |

*At least one identifier required.

**Returns**: `{success, appliedTo}` (`appliedTo` is the prefab asset path)
