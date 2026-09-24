### component_add_batch
Add components to several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType` (required per item).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, component}]}`; an already-present single-instance type gives `{target, success: true, warning: "Component already exists", component}`. All-or-nothing: an unknown object or type reverts every add.

```python
# BAD: 6 API calls (3 x component_add, 3 x component_set_property)
# GOOD: 2 API calls
unity_skills.call_skill("component_add_batch", items=[
    {"name": "Box1", "componentType": "Rigidbody"},
    {"name": "Box2", "componentType": "Rigidbody"},
    {"name": "Box3", "componentType": "Rigidbody"}
])
unity_skills.call_skill("component_set_property_batch", items=[
    {"name": "Box1", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Box2", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Box3", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0}
])
```
