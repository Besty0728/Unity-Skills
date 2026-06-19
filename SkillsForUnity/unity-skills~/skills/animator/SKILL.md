---
name: unity-animator
description: Edit Unity Animator Controllers and control runtime parameters — manage states, transitions, layers, and parameters (float/int/bool/trigger). Use when setting up or wiring an Animator, adjusting animation state machines, or driving animation parameters at runtime, even if the user just mentions "动画" or "状态机". 编辑 Unity Animator Controller 并控制运行时参数(状态、过渡、层、参数 float/int/bool/trigger);当用户要搭建或连接 Animator、调整动画状态机、或在运行时驱动动画参数时使用。
---

# Unity Animator Skills

Control Unity's Mecanim system — create Animator Controllers, add layers' states / transitions / parameters, assign controllers to GameObjects, set parameters at runtime, and play states.

## Operating Mode

- **Approval**：查询类 skill（`animator_get_parameters` / `animator_get_info` / `animator_list_states`，源码标 `SkillMode.SemiAuto`）直接执行；其余变更类（create_controller / add_parameter / set_parameter / play / assign_controller / add_state / add_transition，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：所有 skill 直接执行；Auto 走 AI 自我评估，Bypass 全放行。
- 本模块**不含** Delete / PlayMode / Reload / 高危 skill，无 Bypass-only 拦截项。
- `animator_set_parameter` / `animator_play` 作用于场景中已挂 Animator 的 GameObject；如果当前不在 Play mode，状态机只在 Editor 预览模式推进，效果与 runtime 不完全等价。

**DO NOT** (common hallucinations):
- `animator_create_clip` / `animator_add_clip` do not exist → AnimationClips are created via Unity Editor or asset import
- `animator_set_speed` does not exist → use `component_set_property` on Animator component with propertyName="speed"

**Routing**:
- For Timeline animation → use `timeline` module
- For component properties on Animator → use `component` module
- For animation import settings → use `importer` module

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
| `paramType` | string | Yes | - | float/int/bool/trigger |
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
Set a parameter value at runtime (supports `name`/`instanceId`/`path`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | GameObject hierarchy path |
| `paramName` | string | Yes | Parameter name |
| `paramType` | string | Yes | float/int/bool/trigger |
| `floatValue` | float | No* | Float value |
| `intValue` | int | No* | Integer value |
| `boolValue` | bool | No* | Boolean value |

*At least one identifier required. Use the appropriate value for paramType (trigger doesn't need a value).

### animator_play
Play a specific animation state (supports `name`/`instanceId`/`path`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | GameObject hierarchy path |
| `stateName` | string | Yes | - | Animation state name |
| `layer` | int | No | 0 | Animator layer |
| `normalizedTime` | float | No | 0 | Start time (0-1) |

*At least one identifier required.

### animator_get_info
Get Animator component information (supports `name`/`instanceId`/`path`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name |
| `instanceId` | int | No | 0 | GameObject instance ID |
| `path` | string | No | null | GameObject hierarchy path |

**Returns**: `{gameObject, instanceId, hasController, controllerPath, speed, applyRootMotion, updateMode, cullingMode, layerCount, parameterCount}`

### animator_assign_controller
Assign a controller to a GameObject (supports `name`/`instanceId`/`path`).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
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
| `clipPath` | string | No | null | Animation clip asset path to assign |
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
    paramName="Speed", paramType="float", defaultFloat=0
)
unity_skills.call_skill("animator_add_parameter",
    controllerPath="Assets/Animations/PlayerController.controller",
    paramName="IsGrounded", paramType="bool", defaultBool=True
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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
