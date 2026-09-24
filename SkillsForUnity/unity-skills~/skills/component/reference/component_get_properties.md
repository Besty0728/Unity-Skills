### component_get_properties
Get the C# properties and fields of a component with their current values.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `componentType` | string | Yes | - | Component type |
| `includePrivate` | bool | No | false | Include non-public members |

*At least one identifier required

**Returns**: `{gameObject, component, fullTypeName, properties: [{name, type, fullType, value, canWrite}], fields: [{name, type, fullType, value, isSerializable}]}`. Values use the same round-trippable format `component_set_property` accepts (vectors `"x,y,z"`, Quaternion as euler, objects by name); a Renderer's `material(s)` and a MeshFilter's `mesh` read the shared asset, so reading never instantiates a copy. These member names are the ones `component_set_property` writes.
