### event_add_listener_batch
Add multiple listeners at once. items: JSON array of {targetObjectName, targetComponentName, methodName, mode, argType, floatArg, intArg, stringArg, boolArg, targetEntityId, targetInstanceId, targetPath, objectArgName, objectArgEntityId, objectArgInstanceId, objectArgPath, objectArgAssetPath, objectArgType}, each as in `event_add_listener` (an item needs one target locator, not necessarily the name).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| entityId | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | Yes | - | Component name |
| eventName | string | Yes | - | Event field name |
| items | string | Yes | - | JSON array of the item fields listed above |

**Returns:** `{ success, added, failed, total, results }`; `results` has one `{ target, method, success, error? }` per item.
