### prefab_set_property
Set a property on a component inside a Prefab asset file (without instantiating it). Supports basic types, vectors, colors, enums, and asset references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Path to the prefab asset |
| `componentType` | string | Yes | - | Component type name |
| `propertyName` | string | Yes | - | Serialized property name |
| `value` | string | Cond. | null | Value for basic types (int/float/bool/string/enum/vector/color/quaternion) |
| `assetReferencePath` | string | Cond. | null | Asset path for Object reference fields (Material, Texture, AudioClip, ScriptableObject, etc.) |
| `gameObjectName` | string | No | null | Child object name inside prefab (defaults to root) |

> Provide either `value` (basic types) or `assetReferencePath` (asset references).

`value` covers Integer, Float, Boolean, String, Enum, Color, Vector2/3/4, Vector2Int/3Int, Quaternion, Rect, Bounds and LayerMask. A quaternion (`localRotation`) accepts 3 components as euler degrees (`"0,90,0"`) or 4 as raw x,y,z,w. A bool is `true`/`false`/`1`/`0`/`yes`/`no`/`on`/`off` (anything else is rejected). An enum takes a name, display name, `A,B` for flags, or a number with the same rules as `component_set_serialized_property` (`0`..`n-1` = member index, warned when the member's value differs; undeclared bits rejected). Any other serialized type answers `SEMANTIC_INVALID` naming the type — that is the signal to use `assetReferencePath`, not to reformat `value`.

**Returns:** `{ success, prefabPath, gameObject, component, property, valueSet, valueRequested?, warnings? }`: `component` / `property` are the resolved type name and serialized path; `valueSet` is read back from the asset after saving (serialized form: an enum reads as its index, a quaternion as x,y,z,w; for `assetReferencePath` the referenced asset's path); `valueRequested` appears only when the stored value differs from what was written (clamped by the component, or a reference of the wrong type not stored).

```bash
# Float property on the prefab root
curl -s "http://localhost:<port>/skill/prefab_set_property?expectProject=<project>" -d '{"prefabPath":"Assets/Prefabs/Enemy.prefab","componentType":"EnemyStats","propertyName":"maxHealth","value":"100"}'

# Asset reference on a component
curl -s "http://localhost:<port>/skill/prefab_set_property?expectProject=<project>" -d '{"prefabPath":"Assets/Prefabs/Enemy.prefab","componentType":"AudioSource","propertyName":"m_audioClip","assetReferencePath":"Assets/Audio/hit.wav"}'

# Child object inside the prefab
curl -s "http://localhost:<port>/skill/prefab_set_property?expectProject=<project>" -d '{"prefabPath":"Assets/Prefabs/Player.prefab","componentType":"MeshRenderer","propertyName":"m_Materials.Array.data[0]","assetReferencePath":"Assets/Materials/PlayerSkin.mat","gameObjectName":"Body"}'
```
