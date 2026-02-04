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
