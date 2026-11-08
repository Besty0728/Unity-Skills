### prefab_add_component
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `componentType` | string | Yes | — | Concrete component type, simple or namespace-qualified. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, componentType, componentIndex}` plus node info. RequireComponent dependencies are added by Unity; DisallowMultipleComponent conflicts are rejected.
