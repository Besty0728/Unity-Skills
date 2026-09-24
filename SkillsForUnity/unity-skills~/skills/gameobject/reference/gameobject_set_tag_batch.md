### gameobject_set_tag_batch
Set the tag of several GameObjects (there is no single-object variant).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `tag` (tag name).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, entityId, success, tag}]}`. The tag must already be registered: an unknown one fails that item with the list of valid tags (Unity itself would ignore it silently) and reverts the call.

```python
unity_skills.call_skill("gameobject_set_tag_batch", items=[
    {"name": "Enemy1", "tag": "Enemy"},
    {"name": "Enemy2", "tag": "Enemy"}
])
```
