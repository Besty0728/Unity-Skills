### prefab_get_serialized_properties
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |
| `includeChildren` | bool | No | true | Include nested serialized fields. |
| `limit` | int | No | 200 | Maximum returned records; positive. |
| `includeHidden` | bool | No | false | Also list fields the Inspector hides (`[HideInInspector]` members, Unity's `m_ObjectHideFlags` / `m_PrefabInstance` bookkeeping); they count toward `limit`. |

**Returns**: `{success, prefabPath, gameObjectPath, componentType, componentIndex, properties}`. Inspector paths and serialized values use the existing component serialized-property utility; an `ObjectReference` property also has `referenceState` (`none`, `missing` or `assigned`), which tells a never-assigned reference from one whose target is gone when `value` reads `"null"`.
