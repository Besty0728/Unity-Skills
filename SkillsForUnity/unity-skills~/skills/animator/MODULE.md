# Unity Animator Skills

Create Animator Controllers, add states, transitions and parameters, assign controllers to GameObjects, set parameters at runtime and play states. Parameters come from the schema (`GET /skills/schema?category=Animator&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Setting up or wiring an Animator
- Adjusting animation state machines
- Driving animation parameters at runtime



## Rules
- Query skills (`animator_get_parameters`, `animator_get_info`, `animator_list_states`) are read-only and run under Approval; the writers and `animator_play` need a grant there.
- `animator_set_parameter` and `animator_play` act on the Animator of a scene GameObject. Outside Play Mode the state machine advances only in the editor's preview, which does not match runtime exactly.
- Triggers reset automatically after they fire.
- Set parameters before playing states, and create the controller before adding parameters.
- States must exist in the controller before `animator_play` can use them.
- `animator_add_parameter` applies only the default that matches `paramType`.
- For Timeline animation, use [timeline](../timeline/MODULE.md); for Animator component properties, use [component](../component/MODULE.md); for animation import settings, use [importer](../importer/MODULE.md).

## Pitfalls
- `animator_create_clip` and `animator_add_clip` do not exist: AnimationClips come from the Unity Editor or asset import.
- `animator_set_speed` does not exist: set the `speed` property with `component_set_property` on the Animator.

## Example: setup, then drive at runtime

Add the state before playing it: a new controller has no states, so `animator_play` on an unadded state fails.

```bash
u="http://localhost:<port>/skill"; p="expectProject=<project>"; C=Assets/Animations/PlayerController.controller
curl -s "$u/animator_create_controller?$p" -d '{"name":"PlayerController","folder":"Assets/Animations"}'
curl -s "$u/animator_add_parameter?$p" -d "{\"controllerPath\":\"$C\",\"paramName\":\"Speed\",\"paramType\":\"float\"}"
curl -s "$u/animator_add_parameter?$p" -d "{\"controllerPath\":\"$C\",\"paramName\":\"Jump\",\"paramType\":\"trigger\"}"
curl -s "$u/animator_add_state?$p" -d "{\"controllerPath\":\"$C\",\"stateName\":\"Idle\"}"
curl -s "$u/animator_assign_controller?$p" -d "{\"name\":\"Player\",\"controllerPath\":\"$C\"}"
curl -s "$u/animator_set_parameter?$p" -d '{"name":"Player","paramName":"Speed","paramType":"float","floatValue":5.0}'
curl -s "$u/animator_set_parameter?$p" -d '{"name":"Player","paramName":"Jump","paramType":"trigger"}'
curl -s "$u/animator_play?$p" -d '{"name":"Player","stateName":"Idle"}'
```

## Skills Overview

| Skill | Description |
|-------|-------------|
| `animator_create_controller` | Create new Animator Controller |
| `animator_add_parameter` | Add parameter to controller |
| `animator_get_parameters` | List all parameters |
| `animator_set_parameter` | Set parameter value at runtime |
| `animator_play` | Play animation state |
| `animator_get_info` | Get Animator component info |
| `animator_assign_controller` | Assign controller to GameObject |
| `animator_list_states` | List states in controller |
| `animator_add_state` | Add a state to a controller layer |
| `animator_add_transition` | Add a transition between two states |

---

## Parameter Types

| Type | Description | Example Use |
|------|-------------|-------------|
| `float` | Decimal value | Speed, blend weights |
| `int` | Integer value | State index |
| `bool` | True/false | IsGrounded, IsRunning |
| `trigger` | One-shot signal | Jump, Attack |

---

## Skills

### animator_create_controller
Create a new Animator Controller.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Controller name |
| `folder` | string | No | "Assets/Animations" | Save folder |

**Returns**: `{success, name, path}`

### animator_add_parameter
Add a parameter to a controller.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `paramName` | string | Yes | - | Parameter name |
| `paramType` | string | No | float | float/int/bool/trigger |
| `defaultFloat` | float | No | 0 | Initial float value |
| `defaultInt` | int | No | 0 | Initial int value |
| `defaultBool` | bool | No | false | Initial bool value |

### animator_get_parameters
Get all parameters from a controller.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `controllerPath` | string | Yes | Controller asset path |

**Returns**: `{controller, parameters: [{name, type, defaultFloat, defaultInt, defaultBool}]}`

### animator_set_parameter
Set a parameter value at runtime (supports `name`/`entityId`/`instanceId`/`path`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | GameObject hierarchy path |
| `paramName` | string | Yes | Parameter name |
| `paramType` | string | No | float/int/bool/trigger (default: float) |
| `floatValue` | float | No* | Float value |
| `intValue` | int | No* | Integer value |
| `boolValue` | bool | No* | Boolean value |

*At least one identifier required. Use the appropriate value for paramType (trigger doesn't need a value).

### animator_play
Play a specific animation state (supports `name`/`entityId`/`instanceId`/`path`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |
| `stateName` | string | Yes | - | Animation state name |
| `layer` | int | No | 0 | Animator layer |
| `normalizedTime` | float | No | 0 | Start time (0-1) |

*At least one identifier required.

### animator_get_info
Get Animator component information (supports `name`/`entityId`/`instanceId`/`path`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name |
| `entityId` | string | No | null | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | GameObject instance ID |
| `path` | string | No | null | GameObject hierarchy path |

**Returns**: `{gameObject, instanceId, hasController, controllerPath, speed, applyRootMotion, updateMode, cullingMode, layerCount, parameterCount}`

### animator_assign_controller
Assign a controller to a GameObject (supports `name`/`entityId`/`instanceId`/`path`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | GameObject hierarchy path |
| `controllerPath` | string | Yes | Controller asset path |

*At least one identifier required.

### animator_list_states
List all states in a controller layer.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `layer` | int | No | 0 | Layer index |

**Returns**: `{controller, layer, layerName, stateCount, states: [{name, tag, speed, hasMotion}]}`

### animator_add_state
Add a state to an Animator Controller layer.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `stateName` | string | Yes | - | Name for the new state |
| `clipPath` | string | No | null | Animation clip asset path to assign; a path that is not an AnimationClip rejects the call before the state is added |
| `layer` | int | No | 0 | Layer index |

**Returns**: `{success, controller, stateName, layer}`

### animator_add_transition
Add a transition between two states in an Animator Controller.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `fromState` | string | Yes | - | Source state name |
| `toState` | string | Yes | - | Destination state name |
| `layer` | int | No | 0 | Layer index |
| `hasExitTime` | bool | No | true | Whether transition waits for exit time |
| `duration` | float | No | 0.25 | Transition duration in seconds |

**Returns**: `{success, from, to, layer, hasExitTime, duration}`

---
