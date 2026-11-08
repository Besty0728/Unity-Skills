### prefab_set_transform
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `gameObjectPath` | string | No | null | Root-relative path; null or empty targets the root. Ambiguous sibling paths are rejected. |
| `posX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `posY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `posZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `rotZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `scaleZ` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchoredPosX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchoredPosY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMinX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMinY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMaxX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `anchorMaxY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `pivotX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `pivotY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `sizeDeltaX` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |
| `sizeDeltaY` | float? | No | null | Local transform/layout axis. Omitted axes are kept; layout fields require RectTransform. |

**Returns**: `{success, prefabPath, gameObjectPath, gameObject, parentPath, activeSelf, layer, tag, siblingIndex, childCount, localPosition, localEulerAngles, localScale, prefabAssetType}`. Write results also include `skill`, the operation that ran. RectTransform nodes also return anchoredPosition, anchorMin, anchorMax, pivot and sizeDelta. Values are read back from the saved asset.
