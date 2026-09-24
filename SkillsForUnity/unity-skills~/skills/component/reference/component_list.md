### component_list
List every component on a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `includeProperties` | bool | No | false | Add a few key properties per component |

*At least one identifier required

**Returns**: `{gameObject, entityId, instanceId, path, componentCount, components: [{type, fullType, enabled?, keyProperties?}]}`. `enabled` appears only on Behaviour, Renderer and Collider types; `keyProperties` only with `includeProperties=true` (Transform position/rotation/scale, Camera fieldOfView/orthographic).
