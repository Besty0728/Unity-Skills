### component_get_serialized_properties
List a component's Inspector serialized properties via `SerializedObject`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `entityId` | string | No* | null | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `componentType` | string | Yes | - | Component type |
| `includeChildren` | bool | No | true | Include nested properties |
| `limit` | int | No | 200 | Max properties returned |
| `includeHidden` | bool | No | false | Also list fields the Inspector hides |

*At least one identifier required

**Returns**: `{success, gameObject, component, fullTypeName, properties}`; each property is `{propertyPath, name, displayName, propertyType, type, isArray, editable, value}`, and an `ObjectReference` property adds `referenceState`.

`referenceState` tells what a `"null"` value means: `none` (never assigned, or cleared), `missing` (assigned, but the target was destroyed or its asset deleted) or `assigned` (then `value` is `<entityId>:<type>:<name>`). Other property types carry no `referenceState`.

By default only what the Inspector shows is listed, so a field like `HingeJoint.m_ConnectedBody` is absent. `includeHidden: true` lists every serialized field, `[HideInInspector]` members and Unity's own bookkeeping (`m_ObjectHideFlags`, `m_PrefabInstance`, `m_GameObject`...) included; they count toward `limit`, and a reference or string is one entry, without its file/path ids or characters.

The names here are not the names you write with: they are Unity's serialized backing fields, so a Rigidbody's kinematic flag reads `m_IsKinematic` and its mass `m_Mass`. Feed these paths only to `component_set_serialized_property` (as `propertyPath`); `component_set_property` wants `isKinematic` / `mass`.
