### prefab_rename_gameobject
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `newName` | string | Yes | — | New node name; cannot contain / or collide with a sibling. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Write results also include `skill`, the operation that ran. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.
