### component_set_serialized_property
Set an Inspector serialized property by `propertyPath`: nested fields, arrays/lists, object references, vectors, colours, enums and primitives.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `propertyPath` | string | Yes | SerializedProperty path, e.g. `m_Mass`, `items.Array.data[0]`; a bare name is retried as `m_<Name>`, `_<name>`, `m_<name>` |
| `value` | string | Cond. | Primitive/vector/color/enum value: integers (a LayerMask is the bit mask), bools `true`/`false`/`1`/`0`/`yes`/`no`/`on`/`off`, enum name or display name (`A,B` for flags) or number, vectors/colours as in `component_set_property` |
| `referenceName` | string | No | Scene object name for ObjectReference |
| `referenceInstanceId` | int | No | Scene object instance ID for ObjectReference |
| `referencePath` | string | No | Scene object path for ObjectReference |
| `assetPath` | string | No | Project asset path for ObjectReference |
| `objectType` | string | No | Expected type for references: a scene reference binds the GameObject unless this names a component; for `assetPath` an asset type |

*At least one identifier required. Provide `value` for scalar properties, or a scene/project reference for ObjectReference fields; no reference and no value (or `"null"`) clears an ObjectReference.

**Enum numbers**: `0`..`n-1` is the member index, the same number `valueSet` reports for an enum, so a value read back writes back unchanged; when that index's member has a different value a `warnings` entry names it (for `[Flags] {None, A=1, B=2, C=4, D=8}`, `"3"` is `C`, and `A|B` is `"A,B"`). A larger number is a member value, `-1` (Everything) or a raw bitmask of declared bits (warned); bits no member declares are rejected.

**Returns**: `{success, gameObject, component, propertyPath, valueSet, warnings?}`; `valueSet` is re-read after applying, so `OnValidate` or native clamping shows. A rejected value changes nothing. An unknown path fails listing available paths.
