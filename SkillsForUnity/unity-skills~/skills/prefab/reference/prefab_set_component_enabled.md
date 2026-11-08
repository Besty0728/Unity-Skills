### prefab_set_component_enabled
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `enabled` | bool | Yes | — | Saved component enabled value; only components exposing m_Enabled are supported. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex, propertyPath, valueSet, valueRequested?, warnings?}` plus node info. valueSet is read after saving; internal references use root-relative path and, for components, a full type and index suffix.
