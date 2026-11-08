### ui_get_rect_transform
Get full RectTransform data for a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.

**Returns**: `{success, name, instanceId, path, anchorMin, anchorMax, pivot, anchoredPosition3D, sizeDelta, offsetMin, offsetMax, localPosition, localEulerAngles, localScale, rect}`
