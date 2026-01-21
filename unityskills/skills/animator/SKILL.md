---
name: unity-animator
description: Create and manage Animator Controllers and parameters in Unity Editor via REST API
---

# Unity Animator Skills

Control Unity's animation system - create controllers, manage parameters, and control playback.

## Capabilities

- Create Animator Controller assets
- Add/get/set animation parameters
- Control animation playback
- Assign controllers to GameObjects
- List animation states

## Skills Reference

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

## Parameter Types

| Type | Description | Example Use |
|------|-------------|-------------|
| `float` | Decimal value | Speed, blend weights |
| `int` | Integer value | State index |
| `bool` | True/false | IsGrounded, IsRunning |
| `trigger` | One-shot signal | Jump, Attack |

## Parameters

### animator_create_controller

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Controller name |
| `folder` | string | No | "Assets" | Save folder |

### animator_add_parameter

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `paramName` | string | Yes | - | Parameter name |
| `paramType` | string | Yes | - | float/int/bool/trigger |
| `defaultValue` | any | No | 0/false | Initial value |

### animator_get_parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `controllerPath` | string | Yes | Controller asset path |

### animator_set_parameter

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `paramName` | string | Yes | Parameter name |
| `paramType` | string | Yes | float/int/bool/trigger |
| `floatValue` | float | No* | Float value |
| `intValue` | int | No* | Integer value |
| `boolValue` | bool | No* | Boolean value |

*Use the appropriate value for paramType

### animator_play

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | GameObject name |
| `stateName` | string | Yes | - | Animation state name |
| `layer` | int | No | 0 | Animator layer |
| `normalizedTime` | float | No | 0 | Start time (0-1) |

### animator_assign_controller

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `controllerPath` | string | Yes | Controller asset path |

### animator_list_states

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `controllerPath` | string | Yes | - | Controller asset path |
| `layer` | int | No | 0 | Layer index |

## Example Usage

```python
import unity_skills

# Create an Animator Controller
unity_skills.call_skill("animator_create_controller",
    name="PlayerController",
    folder="Assets/Animations"
)

# Add parameters
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="Speed",
    paramType="float",
    defaultValue=0
)

unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="IsGrounded",
    paramType="bool",
    defaultValue=True
)

unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="Jump",
    paramType="trigger"
)

# Assign to character
unity_skills.call_skill("animator_assign_controller",
    name="Player",
    controllerPath="Assets/Animations/PlayerController.controller"
)

# Set parameter at runtime
unity_skills.call_skill("animator_set_parameter",
    name="Player",
    paramName="Speed",
    paramType="float",
    floatValue=5.0
)

# Trigger jump animation
unity_skills.call_skill("animator_set_parameter",
    name="Player",
    paramName="Jump",
    paramType="trigger"
)

# Play specific state
unity_skills.call_skill("animator_play",
    name="Player",
    stateName="Idle"
)

# Get all parameters
params = unity_skills.call_skill("animator_get_parameters",
    controllerPath="Assets/Animations/PlayerController.controller"
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "animator_create_controller",
  "result": {
    "success": true,
    "name": "PlayerController",
    "path": "Assets/Animations/PlayerController.controller"
  }
}
```

### animator_get_parameters Response

```json
{
  "result": {
    "success": true,
    "parameters": [
      {"name": "Speed", "type": "Float", "defaultFloat": 0},
      {"name": "IsGrounded", "type": "Bool", "defaultBool": true},
      {"name": "Jump", "type": "Trigger"}
    ]
  }
}
```

## Best Practices

1. Create controller before adding parameters
2. Use meaningful parameter names
3. Triggers reset automatically after firing
4. Set parameters before playing states
5. Use layers for independent animations (body + face)
6. States must exist in controller before playing
