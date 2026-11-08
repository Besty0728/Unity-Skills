### prefab_delete_gameobject
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, skill, gameObjectPath, deleted}`. Deleting the root or inherited nodes is rejected.
