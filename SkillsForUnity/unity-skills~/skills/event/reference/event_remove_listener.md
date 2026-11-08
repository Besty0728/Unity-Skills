### event_remove_listener
Remove a persistent listener by index.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Component name |
| eventName | string | Yes | - | Event field name |
| index | int | No | 0 | Listener index |

**Returns:** `{ success, remainingCount }`
