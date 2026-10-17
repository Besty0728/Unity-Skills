### gameobject_rename
Rename a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | Current object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `newName` | string | Yes | New name |

*At least one identifier required

**Returns**: `{success, oldName, newName, entityId, instanceId, path}`, read back from the renamed object (`path` is the new path).
