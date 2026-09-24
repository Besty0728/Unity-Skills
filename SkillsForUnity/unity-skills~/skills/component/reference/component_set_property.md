### component_set_property
Set one property or field of a component through its C# member name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `propertyName` | string | Yes | C# property/field name (not an `m_` serialized path) |
| `value` | string | Cond. | New value (basic types, vectors, colours, enums) |
| `referencePath` | string | No | Scene object hierarchy path (for scene references) |
| `referenceName` | string | No | Scene object name (for scene references; first match) |
| `assetPath` | string | No | Project asset path (Material, Texture, AudioClip, ScriptableObject, Prefab, ...) |

*At least one identifier required. Send exactly one value source; the module entry has the precedence and encodings.

**Returns**: `{success, gameObject, component, property, valueSet, valueRequested?, readBack?, valueType, fullTypeName}`: `component` / `property` are the resolved type and member names, `valueSet` is read back after the write in a form you can send again, `valueRequested` appears only when the stored value differs (clamped, normalised, wrapped), `readBack: false` only for a write-only member (then `valueSet` echoes the request); `valueType` is the member's resolved type name.

```python
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="mass", value=2.5)
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="useGravity", value=False)
call_skill("component_set_property", name="Obj", componentType="Transform", propertyName="localPosition", value="1,2,3")
call_skill("component_set_property", name="Obj", componentType="Light", propertyName="color", value="1,0.5,0,1")
call_skill("component_set_property", name="Obj", componentType="Light", propertyName="color", value="#FF8000")
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="interpolation", value="Interpolate")
call_skill("component_set_property", name="Obj", componentType="MeshRenderer", propertyName="sharedMaterial", assetPath="Assets/Materials/Red.mat")
call_skill("component_set_property", name="Hand", componentType="FixedJoint", propertyName="connectedBody", referencePath="Root/Player")
```

`value="null"` clears an object reference. Bools accept `true`/`false`/`1`/`0`/`yes`/`no`/`on`/`off` (anything else is rejected, not read as false); an AnimationCurve takes a preset (`linear`, `easeIn`, `easeOut`, `easeInOut`, `constant`) or the JSON curve `{"keys":[{"time":0,"value":0},...]}` that `component_set_serialized_property` takes, and an unknown preset is rejected. A read-only property fails; check names with `component_get_properties` (a wrong one fails listing the writable members).
