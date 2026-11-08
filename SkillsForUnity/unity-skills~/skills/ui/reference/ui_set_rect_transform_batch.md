### ui_set_rect_transform_batch
Set full RectTransform data for multiple UI elements. items: JSON array of {name, instanceId, path, entityId, anchorMinX/Y, anchorMaxX/Y, pivotX/Y, anchoredPosX/Y/Z, sizeDeltaX/Y, offsetMinX/Y, offsetMaxX/Y, localPosX/Y/Z, localRotX/Y/Z, localScaleX/Y/Z, width, height}.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | string | Yes | - | JSON array mirroring ui_set_rect_transform's parameters per item: name\|instanceId\|path\|entityId plus the same anchor/pivot/size/offset/local fields. |

**Returns**: `{success, totalItems, successCount, failCount, results: [{name, anchorMin, anchorMax, anchoredPosition3D, sizeDelta, ...}]}`
