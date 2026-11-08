### event_list_events
List all UnityEvent fields on a component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Component name |

**Returns:** `{ success, component, count, events }`
