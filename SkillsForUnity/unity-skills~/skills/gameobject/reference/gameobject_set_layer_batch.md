### gameobject_set_layer_batch
Set the layer of several GameObjects (there is no single-object variant).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `layer` (layer name), `recursive` (bool, default false: also sets every child, inactive ones included).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, entityId, success, layer, childrenUpdated?}]}`. `layer` is read back from the object (`LayerMask.LayerToName`); `childrenUpdated` (only present when `recursive:true`) counts the descendants also updated. The layer must already exist in Tags & Layers; an unknown one fails that item (`Layer not found`) and, all-or-nothing, reverts the rest.

```python
unity_skills.call_skill("gameobject_set_layer_batch", items=[
    {"name": "Enemy1", "layer": "Water"},
    {"name": "Enemy2", "layer": "Water"}
])
```
