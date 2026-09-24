### gameobject_create_batch
Create several GameObjects in one call, parents before children.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `primitiveType`, `x`, `y`, `z`, `rotX`, `rotY`, `rotZ`, `scaleX`, `scaleY`, `scaleZ`, `space`, `parentEntityId`, `parentName`, `parentInstanceId`, `parentPath`. Same meaning and defaults as `gameobject_create`, per item.

`parentName` / `parentPath` may name an item created earlier in the same call; it is checked before the scene and the latest such item wins, so one call builds a hierarchy top-down. An item cannot parent to a later one.

**Returns**: `{success, totalItems, successCount, failCount, warnings?, results: [{success, name, entityId, instanceId, path, parent, parentPath, position, localPosition, rotation, scale}]}`. All-or-nothing: a bad `primitiveType` / `space` or an unresolved parent in any item creates nothing (`rolledBack:true`, other items `reverted:true`).

```python
# 2 calls instead of 6: create, parent and place; then tag
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "Room", "primitiveType": "Empty"},
    {"name": "Floor", "primitiveType": "Plane", "parentName": "Room"},
    {"name": "Wall1", "primitiveType": "Cube", "x": -5, "scaleY": 3, "parentName": "Room"},
    {"name": "Wall2", "primitiveType": "Cube", "x": 5, "scaleY": 3, "parentName": "Room"}
])
unity_skills.call_skill("gameobject_set_tag_batch", items=[
    {"name": "Wall1", "tag": "Wall"},
    {"name": "Wall2", "tag": "Wall"}
])
```
