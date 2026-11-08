### prefab_list_components
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `includeProperties` | bool | No | false | Include serialized Inspector fields per component. |
| `includeHidden` | bool | No | false | With `includeProperties`: also list the fields the Inspector hides, as in `prefab_get_serialized_properties`. |

**Returns**: `{success, prefabPath, gameObjectPath, components}`. Entries report full componentType and its exact-type componentIndex, optional enabled/properties; missing scripts are reported as missingScript:true.
