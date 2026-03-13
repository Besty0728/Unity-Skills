---
name: unity-event
description: "UnityEvent management. Use when users want to inspect or modify UI events like Button.onClick. Triggers: event, onClick, listener, callback, UnityEvent, button click, 事件, 监听器, 按钮点击."
---

# Event Skills

Inspect and modify UnityEvents (e.g. Button.onClick).

## Skills

### `event_get_listeners`
Get persistent listeners of a UnityEvent.
**Parameters:**
- `objectName` (string): GameObject name.
- `componentName` (string): Component name.
- `eventName` (string): Event field name (e.g. "onClick").

### `event_add_listener`
Add a persistent listener to a UnityEvent (Editor time).
**Parameters:**
- `objectName`, `componentName`, `eventName`: Target event.
- `targetObjectName`, `targetComponentName`, `methodName`: Method to call.
- `mode` (string, optional): "RuntimeOnly", "EditorAndRuntime", "Off".
- `argType` (string, optional): "void", "int", "float", "string", "bool".
- `floatArg`, `intArg`, `stringArg`, `boolArg`: Argument value if needed.

### `event_remove_listener`
Remove a persistent listener by index.
**Parameters:**
- `objectName`, `componentName`, `eventName`: Target event.
- `index` (int): Listener index.

### `event_invoke`
Invoke a UnityEvent explicitly (Runtime only).
**Parameters:**
- `objectName`, `componentName`, `eventName`: Target event.

### `event_clear_listeners`
Remove all persistent listeners from a UnityEvent.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Component name |
| eventName | string | No | null | Event field name (e.g. "onClick") |

**Returns:** `{ success, removed }`

### `event_set_listener_state`
Set a listener's call state (Off, RuntimeOnly, EditorAndRuntime).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Component name |
| eventName | string | No | null | Event field name |
| index | int | No | 0 | Listener index |
| state | string | No | null | Call state: "Off", "RuntimeOnly", or "EditorAndRuntime" |

**Returns:** `{ success, index, state }`

### `event_list_events`
List all UnityEvent fields on a component.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Component name |

**Returns:** `{ success, component, count, events }`

### `event_add_listener_batch`
Add multiple listeners at once. items: JSON array of {targetObjectName, targetComponentName, methodName}.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Component name |
| eventName | string | No | null | Event field name |
| items | string | No | null | JSON array of {targetObjectName, targetComponentName, methodName} |

**Returns:** `{ success, added, total }`

### `event_copy_listeners`
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

### `event_get_listener_count`
Get the number of persistent listeners on a UnityEvent.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Component name |
| eventName | string | No | null | Event field name |

**Returns:** `{ success, count }`
