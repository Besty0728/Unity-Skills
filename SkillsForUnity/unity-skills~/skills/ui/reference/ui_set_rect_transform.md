### ui_set_rect_transform
Set full RectTransform data for a UI element, including anchors, pivot, anchoredPosition3D, sizeDelta, offsets, local transform, width and height.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `anchorMinX` | float | No | null | anchorMinX/Y and anchorMaxX/Y: 0-1 fractions of the parent rect. |
| `anchorMinY` | float | No | null | Anchor min y |
| `anchorMaxX` | float | No | null | Anchor max x |
| `anchorMaxY` | float | No | null | Anchor max y |
| `pivotX` | float | No | null | pivotX/Y: 0-1 fractions of this object's own rect (0.5 = centre). |
| `pivotY` | float | No | null | Pivot y |
| `anchoredPosX` | float | No | null | anchoredPosX/Y/Z: anchoredPosition3D, the pivot's offset from the anchor point; localPosX/Y/Z is applied after and wins on the same axis. |
| `anchoredPosY` | float | No | null | Anchored pos y |
| `anchoredPosZ` | float | No | null | Anchored pos z |
| `sizeDeltaX` | float | No | null | sizeDeltaX/Y: size relative to the distance between the anchors (equals the size when the anchors coincide). |
| `sizeDeltaY` | float | No | null | Size delta y |
| `offsetMinX` | float | No | null | offsetMinX/Y, offsetMaxX/Y: corner offsets from the anchors; applied last, so they win over sizeDelta/anchoredPosition/width/height on the same axis. |
| `offsetMinY` | float | No | null | Offset min y |
| `offsetMaxX` | float | No | null | Offset max x |
| `offsetMaxY` | float | No | null | Offset max y |
| `localPosX` | float | No | null | Local pos x |
| `localPosY` | float | No | null | Local pos y |
| `localPosZ` | float | No | null | Local pos z |
| `localRotX` | float | No | null | Local rot x |
| `localRotY` | float | No | null | Local rot y |
| `localRotZ` | float | No | null | Local rot z |
| `localScaleX` | float | No | null | Local scale x |
| `localScaleY` | float | No | null | Local scale y |
| `localScaleZ` | float | No | null | Local scale z |
| `width` | float | No | null | width/height: rect size for the current anchors, applied after sizeDeltaX/Y and before offsetMinX/Y/offsetMaxX/Y. |
| `height` | float | No | null | Height |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.

**Returns**: same shape as `ui_get_rect_transform`.
