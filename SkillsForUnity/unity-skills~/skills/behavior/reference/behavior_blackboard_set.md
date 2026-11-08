### behavior_blackboard_set
Set one variable value.

| Parameter | Meaning |
|-----------|---------|
| `variable` | Variable name, case-sensitive. Required. |
| `value` | New value. Accepted shapes depend on the declared type (see below). |
| `name` / `entityId` / `instanceId` / `path` | GameObject locator — writes the **agent-level override**. |
| `graphAssetPath` | Writes the **graph asset default** and rebakes the runtime blackboard; if a rebake step fails the response carries `warning`. |

Provide either the GameObject locator or `graphAssetPath`; the GameObject wins if both are given.

**Returns**: `target` (`"agent"` or `"asset"`), `variable`, `type`, the normalized `value`, and a `note` explaining where the write landed.

Supported value types and accepted input shapes:

| Declared type | Accepted `value` |
|---------------|------------------|
| `int` / `float` / `double` / `bool` / `string` | JSON scalar, or a parseable string |
| enum | Member name (case-insensitive) |
| `Vector2` / `Vector3` / `Vector4` / `Quaternion` | `[1,2,3]`, `{"x":1,"y":2,"z":3}`, or `"1,2,3"` |
| `Color` | `{"r":1,"g":0,"b":0,"a":1}`, `[1,0,0,1]`, or `"#FF0000"` |
| `Vector2Int` / `Vector3Int` | Same as the float vectors, truncated |
| `GameObject` / `Component` | `Assets/...` prefab path, scene hierarchy path, or scene object name |
| Other `UnityEngine.Object` | `Assets/...` or `Packages/...` asset path |

Setting an unknown variable returns the available names in `availableVariables`.
