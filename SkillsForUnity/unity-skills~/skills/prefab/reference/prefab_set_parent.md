### prefab_set_parent
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `parentPath` | string | No | null | Root-relative parent; omitted/empty means prefab root. |
| `worldPositionStays` | bool | No | false | Whether reparent preserves preview world coordinates; default preserves local transform. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Write results also include `skill`, the operation that ran. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.
