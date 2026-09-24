### component_set_property_batch
Set properties on several components in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType`, `propertyName`, `value`, `referencePath`, `referenceName`, `assetPath` (same rules as `component_set_property`). `value` may be any JSON: number, bool, string, or a vector/colour object. Items may target the same object to set several properties in one call.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, property, valueSet, valueRequested?, readBack?, valueType, component}]}`, read back per item. All-or-nothing: one rejected item reverts every write.

```python
unity_skills.call_skill("component_set_property_batch", items=[
    {"name": "Enemy1", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Enemy2", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0}
])
```
