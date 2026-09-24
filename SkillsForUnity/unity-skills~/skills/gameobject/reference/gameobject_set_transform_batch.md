### gameobject_set_transform_batch
Set transforms for several objects in one call, with the spaces and order of `gameobject_set_transform`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties** (identifier + any subset of transform fields):
- Identifier: `entityId` / `name` / `instanceId` / `path` (at least one required)
- World: `posX`, `posY`, `posZ`, `rotX`, `rotY`, `rotZ`; local scale: `scaleX`, `scaleY`, `scaleZ`
- Local: `localPosX`, `localPosY`, `localPosZ`
- RectTransform (UI only): `anchoredPosX`, `anchoredPosY`, `anchorMinX`, `anchorMinY`, `anchorMaxX`, `anchorMaxY`, `pivotX`, `pivotY`, `sizeDeltaX`, `sizeDeltaY`, `width`, `height`

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, entityId, instanceId, isUI, position, pos, localPosition, rotation, scale}]}`, read back per item; `pos` is the legacy alias of `position` (world); UI items add the RectTransform values. All-or-nothing: one missing object reverts every item.

```python
unity_skills.call_skill("gameobject_set_transform_batch", items=[
    {"name": "Cube1", "posX": 0, "posY": 1},
    {"instanceId": 12345, "posX": 2, "posY": 1},
    {"path": "Env/Cube3", "posX": 4, "posY": 1}
])
```
