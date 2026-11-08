### prefab_remove_component
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `componentIndex` | int | No | 0 | Zero-based index among components matching componentType. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, removed, remainingCount}` plus node info. Transform and required components cannot be removed.
