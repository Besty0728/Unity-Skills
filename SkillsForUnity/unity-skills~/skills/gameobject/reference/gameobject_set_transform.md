### gameobject_set_transform
Set position, rotation and/or scale in world, local or RectTransform terms.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityId` | string | No* | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `posX/posY/posZ` | float | No | World position |
| `rotX/rotY/rotZ` | float | No | World rotation (euler) |
| `scaleX/scaleY/scaleZ` | float | No | Local scale |
| `localPosX/localPosY/localPosZ` | float | No | Local position (relative to parent; works for both 3D and UI) |
| `anchoredPosX/anchoredPosY` | float | No | RectTransform anchored position (UI only) |
| `anchorMinX/anchorMinY` | float | No | RectTransform anchor min (0-1, UI only) |
| `anchorMaxX/anchorMaxY` | float | No | RectTransform anchor max (0-1, UI only) |
| `pivotX/pivotY` | float | No | RectTransform pivot (0-1, UI only) |
| `sizeDeltaX/sizeDeltaY` | float | No | RectTransform size delta (UI only) |
| `width/height` | float | No | Rect size set through the current anchors (equals sizeDelta when anchors don't stretch; UI only) |

*At least one identifier required. RectTransform fields are ignored on regular Transforms.

Order: world position, then local position, then rotation, then scale; the same axis given in both `pos*` and `localPos*` ends at the local value. Omitted axes keep their current value.

**Returns**: `{success, name, entityId, instanceId, isUI, position, localPosition, rotation, scale}`, read back (`position` world, `rotation` world euler, `scale` local). UI objects add `anchoredPosition, anchorMin, anchorMax, pivot, sizeDelta, rect: {width, height}`.
