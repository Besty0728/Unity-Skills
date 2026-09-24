### component_set_serialized_property
Set an Inspector serialized property by `propertyPath`: nested fields, arrays/lists, object references, vectors, colours, enums and primitives.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `propertyPath` | string | Yes | SerializedProperty path, e.g. `m_Mass`, `items.Array.data[0]`; a bare name is retried as `m_<Name>`, `_<name>`, `m_<name>` |
| `value` | string | Cond. | Primitive/vector/color/enum value: integers (a LayerMask is the bit mask), `true`/`false`, enum name or display name (`A,B` for flags), vectors/colours as in `component_set_property` |
| `referenceName` | string | No | Scene object name for ObjectReference |
| `referenceInstanceId` | int | No | Scene object instance ID for ObjectReference |
| `referencePath` | string | No | Scene object path for ObjectReference |
| `assetPath` | string | No | Project asset path for ObjectReference |
| `objectType` | string | No | Expected type for references: a scene reference binds the GameObject unless this names a component; for `assetPath` an asset type |

*At least one identifier required. Provide `value` for scalar properties, or a scene/project reference for ObjectReference fields; no reference and no value (or `"null"`) clears an ObjectReference.

**Returns**: `{success, gameObject, component, propertyPath, valueSet}`; `valueSet` is re-read after applying, so `OnValidate` or native clamping shows. An unknown path fails listing available paths.
