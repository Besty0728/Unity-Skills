### event_copy_listeners
Copy listeners from one event to another.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| sourceObject | string | Yes | - | Source GameObject name |
| sourceComponent | string | Yes | - | Source component name |
| sourceEvent | string | Yes | - | Source event field name |
| targetObject | string | Yes | - | Target GameObject name |
| targetComponent | string | Yes | - | Target component name |
| targetEvent | string | Yes | - | Target event field name |

**Returns:** `{ success, copied }`
