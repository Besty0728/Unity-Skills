### prefab_instantiate
Instantiate a prefab into the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Prefab asset path |
| `name` | string | No | prefab name | Instance name |
| `x`, `y`, `z` | float | No | 0 | Local position (relative to parent if set) |
| `parentEntityId` | string | No | null | Parent entity ID (Unity 6000.4+, preferred) |
| `parentName` | string | No | null | Parent object name |
| `parentInstanceId` | int | No | 0 | Parent instance ID |
| `parentPath` | string | No | null | Parent hierarchy path |

**Returns**: `{success, name, entityId, instanceId, path}`
