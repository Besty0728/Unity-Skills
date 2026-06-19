---
name: unity-event
description: Wire UnityEvent persistent listeners at editor time — add, remove, and configure serialized event callbacks on components. Use when hooking up UnityEvents in the Inspector, wiring button or trigger callbacks, or scripting persistent listener setup, even if the user just says "事件绑定" or "按钮点击". 在编辑器期连接 UnityEvent 持久化监听器(在组件上添加、移除、配置序列化的事件回调);当用户要在 Inspector 里挂接 UnityEvent、连接按钮或触发器回调、或脚本化设置持久监听时使用。
---

# Event Skills

Inspect and modify persistent listeners on UnityEvents (e.g. `Button.onClick`, `Toggle.onValueChanged`) — the same listeners you see in the Inspector's event drop slots.

## Operating Mode

- **Approval**：查询类 skill（`event_get_listeners` / `event_list_events` / `event_get_listener_count`，源码标 `SkillMode.SemiAuto`）直接执行；其余变更/调用类（`event_add_listener` / `event_set_listener` / `event_set_listener_state` / `event_invoke` / `event_add_listener_batch` / `event_copy_listeners`，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：未被禁列表拦截的 skill 直接执行。
- 本模块**含 Delete 类 skill**：`event_remove_listener`、`event_clear_listeners` 标记为 `SkillOperation.Delete`，被 `IsForbiddenInSemi` 静态拦截 —— 仅 **Bypass** 模式或加入 **Allowlist** 才能调用。
- `event_invoke` 只在 Play mode / runtime 下有效；编辑器空跑时仅触发 EditorAndRuntime 监听。`event_add_listener` 等写入的是 persistent listener（序列化到 prefab/scene），即可在编辑器时配置。

**DO NOT** (common hallucinations):
- `event_create` / `event_trigger` do not exist → UnityEvents are declared in component source code; this module only wires listeners
- `event_subscribe` does not exist → use `event_add_listener`
- `event_remove` does not exist → use `event_remove_listener`
- `event_add_listener` requires exact component type and method name on the target

**Routing**:
- For XR interaction events → use `xr` module's `xr_add_interaction_event`
- For C# event code → write via `script` module

## Skills

### `event_get_listeners`
Get persistent listeners of a UnityEvent.
**Parameters:**
- `name` / `instanceId` / `path`: Target GameObject locator.
- `componentName` (string): Component name.
- `eventName` (string): Event field name (e.g. "onClick").

### `event_add_listener`
Add a persistent listener to a UnityEvent (Editor time).
**Parameters:**
- `name` / `instanceId` / `path`, `componentName`, `eventName`: Target event.
- `targetObjectName`, `targetComponentName`, `methodName`: Method to call.
- `mode` (string, optional): "RuntimeOnly", "EditorAndRuntime", "Off".
- `argType` (string, optional): "void", "int", "float", "string", "bool".
- `floatArg`, `intArg`, `stringArg`, `boolArg`: Argument value if needed.

### `event_remove_listener`
Remove a persistent listener by index.
**Parameters:**
- `name` / `instanceId` / `path`, `componentName`, `eventName`: Target event.
- `index` (int): Listener index.

### `event_invoke`
Invoke a UnityEvent explicitly (Runtime only).
**Parameters:**
- `name` / `instanceId` / `path`, `componentName`, `eventName`: Target event.

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

### `event_set_listener`
Replace a persistent listener at a specific index.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | GameObject name |
| instanceId | int | No | 0 | GameObject instance ID |
| path | string | No | null | GameObject hierarchy path |
| componentName | string | No | null | Source component name |
| eventName | string | No | null | Event field name |
| index | int | No | 0 | Listener index to replace |
| targetName | string | No | null | Target GameObject name |
| targetInstanceId | int | No | 0 | Target GameObject instance ID |
| targetPath | string | No | null | Target hierarchy path |
| targetComponentName | string | No | null | Target component name, or `GameObject` |
| methodName | string | No | null | Public method or `set_PropertyName` |
| mode | string | No | RuntimeOnly | Off, RuntimeOnly, or EditorAndRuntime |
| argType | string | No | void | void, int, float, string, bool, object |
| floatArg | float | No | 0 | Static float argument |
| intArg | int | No | 0 | Static int argument |
| stringArg | string | No | null | Static string argument |
| boolArg | bool | No | false | Static bool argument |
| objectReferenceName | string | No | null | Scene object argument name |
| objectReferenceInstanceId | int | No | 0 | Scene object argument instance ID |
| objectReferencePath | string | No | null | Scene object argument path |
| objectAssetPath | string | No | null | Project asset argument path |
| objectType | string | No | null | Object/component type for object argument |

**Returns:** `{ success, index, target, targetType, method, state, argType }`

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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
