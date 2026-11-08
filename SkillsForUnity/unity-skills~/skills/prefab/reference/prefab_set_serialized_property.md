### prefab_set_serialized_property
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `propertyPath` | string | Yes | — | Inspector serialized path; also tries m_Name, _name and m_name aliases. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |
| `value` | string | No | null | Serialized value text; empty strings are supported. Object references require a reference source or explicit null. |
| `assetPath` | string | No | null | Project asset reference; cannot be combined with an internal reference or clearReference. |
| `referenceGameObjectPath` | string | No | null | Root-relative reference within this prefab. Empty string references the root; null means omitted. |
| `referenceComponentType` | string | No | null | Omit to reference a GameObject; otherwise select its component type. |
| `referenceComponentIndex` | int | No | 0 | Zero-based matching component index on the reference node. |
| `clearReference` | bool | No | false | Explicitly clear an ObjectReference field. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex, propertyPath, valueSet, valueRequested?, warnings?}` plus node info. valueSet is read after saving; internal references use root-relative path and, for components, a full type and index suffix.
