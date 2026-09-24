### gameobject_get_info
Get detailed information about one GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityId` | string | No* | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

*At least one identifier required

**Returns**: `{name, entityId, instanceId, path, tag, layer, isActive, position, rotation, scale, parent, parentPath, childCount, children: [{name, entityId, instanceId, path}], components}`. `position` world, `rotation` world euler, `scale` local; `isActive` is the object's own active flag; `parent` / `parentPath` are null at scene root; `components` lists type names.
