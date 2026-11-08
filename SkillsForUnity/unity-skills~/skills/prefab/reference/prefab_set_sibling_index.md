### prefab_set_sibling_index
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `index` | int | Yes | — | Sibling index; Unity clamps it to the available range. |
| `gameObjectPath` | string | Yes | — | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Write results also include `skill`, the operation that ran. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.
