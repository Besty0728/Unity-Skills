### prefab_create_gameobject
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `name` | string | Yes | — | New child name; cannot contain / or collide with a sibling. |
| `parentPath` | string | No | null | Root-relative parent; omitted/empty means prefab root. |
| `rectTransform` | bool | No | false | Create a RectTransform node instead of a Transform node. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Write results also include `skill`, the operation that ran. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.
