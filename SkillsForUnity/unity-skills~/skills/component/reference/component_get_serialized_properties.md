### component_get_serialized_properties
List a component's Inspector serialized properties via `SerializedObject`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `componentType` | string | Yes | - | Component type |
| `includeChildren` | bool | No | true | Include nested properties |
| `limit` | int | No | 200 | Max properties returned |

*At least one identifier required

**Returns**: `{success, gameObject, component, fullTypeName, properties}`.

The names here are not the names you write with: they are Unity's serialized backing fields, so a Rigidbody's kinematic flag reads `m_IsKinematic` and its mass `m_Mass`. Feed these paths only to `component_set_serialized_property` (as `propertyPath`); `component_set_property` wants `isKinematic` / `mass`.
