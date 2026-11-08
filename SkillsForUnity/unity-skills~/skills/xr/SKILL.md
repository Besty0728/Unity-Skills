---
name: unity-xr
description: Set up XR Interaction Toolkit (XRI) for VR/AR
---

# Unity XR Interaction Toolkit Skills

XR Interaction Toolkit setup and configuration. Every `xr_*` skill is reflection-based and supports XRI 2.x on Unity 2022 and XRI 3.x on Unity 6 and later. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

> **Requires** `com.unity.xr.interaction.toolkit`. When it is missing, every `xr_*` skill returns the `NoXRI()` install instruction instead of running.

## When to use
- Building VR or AR interaction
- Setting up an XR rig
- Configuring grab, socket or ray interactors

## Rules
- Read this module before the first `xr_*` call in a session. Wrong property names can fail silently, because the bridge is reflection-based.
- Property names on XRI components must match XRI 2.x and 3.x exactly. A wrong field name on `xr_configure_interactable` or `xr_configure_haptics` is ignored without an error. Check detailed property edits in [api-reference](reference/api-reference.md).
- `xr_configure_interaction_layers` takes layer names or an integer mask, and rejects undefined names.
- Most locomotion skills assume an `XROrigin` already exists.
- Setup, collider rules, class names and version differences: [setup-and-colliders](reference/setup-and-colliders.md).
- For non-XR Canvas UI use the [ui](../ui/SKILL.md) module. For XR gameplay architecture load advisory modules such as `architecture`, `patterns`, `async` or `scriptdesign`.

## Pitfalls
- `XRHand`, `XRPlayer`, `XRTeleporter`, `GrabInteractor`, `VRController`, `XRLocomotion` and `XRManager` are not the runtime classes to use here.
- `interactable.OnGrab()` and `OnRelease()` are not the XRI event model. Use `selectEntered` and `selectExited`.
- `controller.vibrate()` is not the route here. Configure haptics with `xr_configure_haptics`.
- A physics Layer or Tag does not replace `InteractionLayerMask`.

## Skills

Skills that act on an existing GameObject find it by `name`, `entityId` (preferred; the only id on Unity 6000.4+), `instanceId` or `path`.

### Setup and diagnostics

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `xr_check_setup` | Validate the package, rig, managers and scene prerequisites | `verbose?` |
| `xr_setup_rig` | Create an XR Origin with camera and controllers | `name`, `cameraYOffset?` |
| `xr_setup_interaction_manager` | Add or find the interaction manager | none |
| `xr_setup_event_system` | Add the XR UI input stack | none |
| `xr_get_scene_report` | Report the scene's XR status | none |

### Interactors and interactables

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `xr_add_ray_interactor` | Remote pointing and ray interaction | `name`, `maxDistance?`, `lineType?` |
| `xr_add_direct_interactor` | Close-range hand grab | `name`, `radius?` |
| `xr_add_socket_interactor` | Snap to a slot | `name`, `showHoverMesh?`, `recycleDelay?` |
| `xr_add_grab_interactable` | Rigidbody, collider and grab configuration | `name`, `movementType?`, `throwOnDetach?` |
| `xr_add_simple_interactable` | Hover and select without grab | `name` |
| `xr_configure_interactable` | Fine-tune interactable behavior | target and changed fields only |
| `xr_list_interactors` | List the scene's interactors | none |
| `xr_list_interactables` | List the scene's interactables | none |

### Locomotion and XR UI

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `xr_setup_teleportation` | Add a teleport provider to the XR Origin | none |
| `xr_add_teleport_area` | Mark a surface as teleportable | `name`, `matchOrientation?` |
| `xr_add_teleport_anchor` | Create a fixed teleport destination | `name`, `x`, `y`, `z`, `rotY?`, `matchOrientation?` |
| `xr_setup_continuous_move` | Add stick locomotion | `moveSpeed?`, `enableStrafe?`, `enableFly?` |
| `xr_setup_turn_provider` | Add snap or smooth turn | `turnType`, `turnAmount?`, `turnSpeed?` |
| `xr_setup_ui_canvas` | Convert a Canvas for XR interaction | `name` |

### Feedback and filtering

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `xr_configure_haptics` | Set hover and select vibration | `name`, intensities, durations |
| `xr_add_interaction_event` | Wire an interaction callback to a target method | `name`, `eventType`, `targetName`, `targetMethod` |
| `xr_configure_interaction_layers` | Set the InteractionLayerMask | `name`, `layers`, `isInteractor` |

## Quick start

```
curl -s "http://localhost:<port>/skill/xr_check_setup?expectProject=<project>" -d '{}'
curl -s "http://localhost:<port>/skill/xr_setup_rig?expectProject=<project>" -d '{"name":"XR Origin","cameraYOffset":1.36}'
curl -s "http://localhost:<port>/skill/xr_setup_event_system?expectProject=<project>" -d '{}'
curl -s "http://localhost:<port>/skill/xr_add_ray_interactor?expectProject=<project>" -d '{"name":"Right Controller","maxDistance":30,"lineType":"StraightLine"}'
curl -s "http://localhost:<port>/skill/xr_add_direct_interactor?expectProject=<project>" -d '{"name":"Left Controller"}'
curl -s "http://localhost:<port>/skill/xr_setup_teleportation?expectProject=<project>" -d '{}'
curl -s "http://localhost:<port>/skill/xr_add_grab_interactable?expectProject=<project>" -d '{"name":"MyCube","movementType":"VelocityTracking"}'
```

## Reference
- [setup-and-colliders](reference/setup-and-colliders.md): rig and grab workflows, collider rules, class names, XRI 2.x and 3.x differences
- [api-reference](reference/api-reference.md): verified XRI property names, event and property tables, and detailed workflows

## Errors

Transport-level codes: [error codes](../../references/protocol-error-codes.md).
