### gameobject_duplicate
Duplicate a GameObject as a sibling named `<name>_Copy` (rename it afterwards if needed).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityId` | string | No* | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

*At least one identifier required

**Returns**: `{success, originalName, copyName, copyEntityId, copyInstanceId, copyPath, copyParentPath}`, read back from the copy (`copyParentPath` null at scene root).
