---
name: unity-animator
description: "Unity Animator Controller management. Use when users want to create animation controllers, add parameters, play animations, or control animation state machines. Triggers: animation, animator, controller, parameter, state machine, blend tree, animation clip, 动画, 动画控制器, 状态机."
---

# Unity Animator Skills

Control Unity's animation system - create controllers, manage parameters, and control playback.

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
| `folder` | string | No | "Assets" | Save folder |

**Returns**: `{success, name, path}`

### animator_add_parameter
Add a parameter to a controller.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `paramName` | string | Yes | - | Parameter name |
| `paramType` | string | Yes | - | float/int/bool/trigger |
| `defaultValue` | any | No | 0/false | Initial value |

### animator_get_parameters
Get all parameters from a controller.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `controllerPath` | string | Yes | Controller asset path |

**Returns**: `{success, parameters: [{name, type, defaultFloat/defaultBool/...}]}`

### animator_set_parameter
Set a parameter value at runtime.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `paramName` | string | Yes | Parameter name |
| `paramType` | string | Yes | float/int/bool/trigger |
| `floatValue` | float | No* | Float value |
| `intValue` | int | No* | Integer value |
| `boolValue` | bool | No* | Boolean value |

*Use the appropriate value for paramType (trigger doesn't need a value)

### animator_play
Play a specific animation state.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | GameObject name |
| `stateName` | string | Yes | - | Animation state name |
| `layer` | int | No | 0 | Animator layer |
| `normalizedTime` | float | No | 0 | Start time (0-1) |

### animator_get_info
Get Animator component information.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |

**Returns**: `{success, hasController, controllerName, parameters, currentState}`

### animator_assign_controller
Assign a controller to a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `controllerPath` | string | Yes | Controller asset path |

### animator_list_states
List all states in a controller layer.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `layer` | int | No | 0 | Layer index |

**Returns**: `{success, states: [{name, tag, speed}]}`

---

## Example: Complete Animation Setup

```python
import unity_skills

# 1. Create controller
unity_skills.call_skill("animator_create_controller",
    name="PlayerController",
    folder="Assets/Animations"
)

# 2. Add parameters
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="Speed", paramType="float", defaultValue=0
)
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="IsGrounded", paramType="bool", defaultValue=True
)
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="Jump", paramType="trigger"
)

# 3. Assign to character
unity_skills.call_skill("animator_assign_controller",
    name="Player",
    controllerPath="Assets/Animations/PlayerController.controller"
)

# 4. Control at runtime
unity_skills.call_skill("animator_set_parameter",
    name="Player", paramName="Speed", paramType="float", floatValue=5.0
)

# Trigger jump
unity_skills.call_skill("animator_set_parameter",
    name="Player", paramName="Jump", paramType="trigger"
)

# Play specific state
unity_skills.call_skill("animator_play", name="Player", stateName="Idle")
```

## Best Practices

1. Create controller before adding parameters
2. Use meaningful parameter names
3. Triggers reset automatically after firing
4. Set parameters before playing states
5. Use layers for independent animations (body + face)
6. States must exist in controller before playing
