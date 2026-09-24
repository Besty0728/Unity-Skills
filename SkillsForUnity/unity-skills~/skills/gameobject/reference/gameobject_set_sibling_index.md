### gameobject_set_sibling_index
Set a GameObject's sibling index: its position among its parent's children, or among the scene's root objects when unparented. No batch variant; reorder one object at a time.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | null | Object name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `index` | int | No | 0 | Target sibling index (0 = first); clamped into the valid range |

*At least one identifier required

**Returns**: `{success, name, entityId, path, parent, previousIndex, index, clamped}`; `clamped:true` when `index` was out of range, `parent` is `"(scene root)"` for a root object.
