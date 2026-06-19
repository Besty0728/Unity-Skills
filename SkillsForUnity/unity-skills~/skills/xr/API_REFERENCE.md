---
name: unity-xr-api-reference
description: "Extended XR Interaction Toolkit (XRI) reference — detailed workflows, verified XRI property names, and event/property tables beyond the xr SKILL.md. XR Interaction Toolkit(XRI)扩展参考(详细工作流、已验证的 XRI 属性名、超出 xr SKILL.md 的事件/属性表)。"
type: reference
---

# XR API Reference

Load this file when you need the detailed workflows, verified XRI property names, or event/property tables. The main `SKILL.md` keeps only routing and critical guardrails.

## Workflow 1: XR Rig Setup

```python
import unity_skills as u

result = u.call_skill("xr_check_setup", verbose=True)

u.call_skill("xr_setup_rig", name="XR Origin", cameraYOffset=1.36)
u.call_skill("xr_setup_event_system")
u.call_skill("xr_add_ray_interactor", name="Right Controller", maxDistance=30)
u.call_skill("xr_add_direct_interactor", name="Left Controller", radius=0.1)
```

Checklist:
- `xr_check_setup` returns zero blocking issues
- one `XRInteractionManager`
- one `XR Origin`
- controllers have interactors
- EventSystem uses `XRUIInputModule`

## Workflow 2: Grab Interaction

```python
u.call_skill("xr_add_direct_interactor", name="Left Controller", radius=0.1)
u.call_skill("xr_add_grab_interactable", name="MyCube",
    movementType="VelocityTracking",
    throwOnDetach=True,
    useGravity=True)
```

```python
u.call_skill("xr_add_ray_interactor", name="Right Controller",
    maxDistance=30, lineType="StraightLine")
u.call_skill("xr_add_grab_interactable", name="DistantObject",
    movementType="Instantaneous")
```

```python
u.call_skill("xr_add_socket_interactor", name="ItemSlot",
    showHoverMesh=True, recycleDelay=1.0)
u.call_skill("xr_add_grab_interactable", name="KeyItem",
    movementType="VelocityTracking")
```

### Movement Type Guide

| Type | Behavior | Best for |
|------|----------|----------|
| `VelocityTracking` | Follows hand through physics velocity | General-purpose props |
| `Kinematic` | Uses kinematic movement | Handles/tools that should not snag |
| `Instantaneous` | Teleports to hand every frame | Remote or snappy grab |

### Attach Offset Example

```python
u.call_skill("xr_add_grab_interactable", name="Sword",
    movementType="VelocityTracking",
    attachTransformOffset="0,-0.3,0")
```

## Workflow 3: Teleportation

```python
u.call_skill("xr_setup_teleportation")
u.call_skill("xr_add_ray_interactor", name="Right Controller", lineType="ProjectileCurve")
u.call_skill("xr_add_teleport_area", name="Floor", matchOrientation="WorldSpaceUp")
```

### Area vs Anchor

| Feature | TeleportationArea | TeleportationAnchor |
|---------|-------------------|---------------------|
| Landing spot | Anywhere on surface | Fixed point |
| Use case | Floors, terrain | Seats, waypoints |
| Orientation | Configurable | Can inherit anchor facing |

### `matchOrientation`

| Value | Behavior |
|-------|----------|
| `WorldSpaceUp` | Stay upright, keep facing |
| `TargetUp` | Match surface up-axis |
| `TargetUpAndForward` | Match both up and facing |
| `None` | Do not rotate player |

## Workflow 4: Continuous Locomotion

```python
u.call_skill("xr_setup_continuous_move",
    moveSpeed=2.0, enableStrafe=True, enableFly=False)
u.call_skill("xr_setup_turn_provider",
    turnType="Snap", turnAmount=45)
```

```python
u.call_skill("xr_setup_continuous_move",
    moveSpeed=2.0, enableStrafe=True)
u.call_skill("xr_setup_turn_provider",
    turnType="Continuous", turnSpeed=90)
```

### Comfort Defaults

| Setting | Safer default |
|---------|---------------|
| Turn | Snap (`45` degrees) |
| Move speed | `1.5-2.0` |
| Fly mode | Off |
| Strafe | Optional, user-dependent |

## Workflow 5: XR UI

```python
u.call_skill("xr_setup_ui_canvas", name="MyCanvas")
u.call_skill("xr_setup_event_system")
u.call_skill("xr_add_ray_interactor", name="Right Controller")
```

### World Space Canvas Guidelines

| Property | Recommendation |
|----------|----------------|
| Scale | `0.001` per axis |
| Canvas size | about `400x300` pixels |
| Distance | `1.5-2.5m` from player |
| Height | `1.3-1.6m` |

## Workflow 6: Events and Haptics

```python
u.call_skill("xr_add_interaction_event",
    name="Lever",
    eventType="selectEntered",
    targetName="Door",
    targetMethod="Open")

u.call_skill("xr_configure_haptics", name="Right Controller",
    selectIntensity=0.7, selectDuration=0.15,
    hoverIntensity=0.1, hoverDuration=0.05)
```

### Event Types

| Event | Fires when |
|-------|------------|
| `selectEntered` | Grab/select begins |
| `selectExited` | Grab/select ends |
| `hoverEntered` | Hover begins |
| `hoverExited` | Hover ends |
| `firstSelectEntered` | First selector begins |
| `lastSelectExited` | Last selector ends |
| `activated` | Trigger/activate input pressed |
| `deactivated` | Trigger/activate input released |

## Component Dependency Map

```text
Scene Requirements:
  XRInteractionManager
  EventSystem + XRUIInputModule

XR Origin:
  XROrigin
  ├── Camera (TrackedPoseDriver)
  ├── Left Controller (TrackedPoseDriver + interactor)
  └── Right Controller (TrackedPoseDriver + interactor)

Grabbable Object:
  XRGrabInteractable
  ├── Rigidbody
  └── Collider (non-trigger)

Teleport Target:
  TeleportationArea/Anchor
  └── Collider

XR UI Canvas:
  Canvas (WorldSpace)
  └── TrackedDeviceGraphicRaycaster
```

## Interaction Layer Configuration

```python
u.call_skill("xr_configure_interaction_layers",
    name="TeleportRay", layers="Teleport", isInteractor=True)
u.call_skill("xr_configure_interaction_layers",
    name="Floor", layers="Teleport", isInteractor=False)
```

## `XRRayInteractor` Verified Properties

### Core

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `maxRaycastDistance` | float | 30 | Max ray length |
| `raycastMask` | LayerMask | Everything | Physics layers |
| `raycastTriggerInteraction` | QueryTriggerInteraction | Ignore | Trigger hit behavior |
| `hitClosestOnly` | bool | true | Only closest hit |

### Line

| Property | Type | Values |
|----------|------|--------|
| `lineType` | enum | `StraightLine`, `ProjectileCurve`, `BezierCurve` |
| `velocity` | float | projectile velocity |
| `acceleration` | float | projectile gravity |
| `additionalGroundHeight` | float | extra ground offset |
| `additionalFlightTime` | float | extra flight time |
| `endPointDistance` | float | bezier end distance |
| `endPointHeight` | float | bezier end drop |
| `controlPointDistance` | float | bezier control distance |
| `controlPointHeight` | float | bezier control height |
| `sampleFrequency` | int | curve sampling count |

### Detection and UI

| Property | Type | Description |
|----------|------|-------------|
| `hitDetectionType` | enum | `Raycast`, `SphereCast`, `ConeCast` |
| `sphereCastRadius` | float | SphereCast radius |
| `coneCastAngle` | float | ConeCast angle |
| `enableUIInteraction` | bool | UI support |
| `useForceGrab` | bool | Pull object to hand |
| `anchorControl` | bool | Push/pull/rotate selected object |
| `translateSpeed` | float | Anchor translation speed |
| `rotateSpeed` | float | Anchor rotation speed |

## `XRInteractorLineVisual`

| Property | Type | Default |
|----------|------|---------|
| `lineWidth` | float | `0.02` |
| `overrideInteractorLineLength` | bool | `true` |
| `lineLength` | float | `10` |
| `validColorGradient` | Gradient | white |
| `invalidColorGradient` | Gradient | red |
| `blockedColorGradient` | Gradient | yellow |
| `stopLineAtFirstRaycastHit` | bool | `true` |
| `stopLineAtSelection` | bool | `true` |
| `treatSelectionAsValidState` | bool | `true` |
| `smoothMovement` | bool | `false` |
| `reticle` | GameObject | `null` |

## `XRGrabInteractable` Verified Properties

### Movement and Smoothing

| Property | Type | Description |
|----------|------|-------------|
| `movementType` | enum | `Instantaneous`, `Kinematic`, `VelocityTracking` |
| `trackPosition` | bool | Follow position |
| `trackRotation` | bool | Follow rotation |
| `trackScale` | bool | Two-hand scale |
| `throwOnDetach` | bool | Apply release velocity |
| `forceGravityOnDetach` | bool | Re-enable gravity |
| `smoothPosition` | bool | Position smoothing |
| `smoothPositionAmount` | float | Position smoothing factor |
| `tightenPosition` | float | Position tightening |
| `smoothRotation` | bool | Rotation smoothing |
| `smoothRotationAmount` | float | Rotation smoothing factor |
| `tightenRotation` | float | Rotation tightening |

### Throw / Attach / Selection

| Property | Type | Description |
|----------|------|-------------|
| `throwSmoothingDuration` | float | Throw averaging window |
| `throwVelocityScale` | float | Throw velocity multiplier |
| `throwAngularVelocityScale` | float | Angular multiplier |
| `attachTransform` | Transform | Custom grip point |
| `secondaryAttachTransform` | Transform | Two-hand grip point |
| `useDynamicAttach` | bool | Grab at contact point |
| `attachEaseInTime` | float | Grab ease-in |
| `snapToColliderVolume` | bool | Snap to collider surface |
| `reinitializeEveryGrab` | bool | Reset attach every grab |
| `selectMode` | enum | `Single`, `Multiple` |
| `startingSingleGrabTransformers` | List | Single-hand transformers |
| `startingMultipleGrabTransformers` | List | Multi-hand transformers |
| `addDefaultGrabTransformers` | bool | Auto-add default transformer |

## `XRSocketInteractor`

| Property | Type | Default |
|----------|------|---------|
| `showInteractableHoverMeshes` | bool | `true` |
| `interactableHoverMeshMaterial` | Material | `null` |
| `interactableCantHoverMeshMaterial` | Material | `null` |
| `socketActive` | bool | `true` |
| `recycleDelayTime` | float | `1` |
| `socketSnappingRadius` | float | `0.1` |
| `socketScaleMode` | enum | `None` |
| `targetBoundsSize` | Vector3 | `(1,1,1)` |
| `startingSelectedInteractable` | XRBaseInteractable | `null` |

## `XRDirectInteractor`

| Property | Type | Default |
|----------|------|---------|
| `improveRaycastBySortingOrder` | bool | `true` |
| `keepSelectedTargetValid` | bool | `true` |

Direct interactor collider rule: same GameObject must have a trigger collider, typically a `SphereCollider`.

## Event System Reference

### Interactable Events

| Property | Args type | Meaning |
|----------|-----------|---------|
| `firstHoverEntered` | `HoverEnterEventArgs` | first hover begins |
| `hoverEntered` | `HoverEnterEventArgs` | any hover begins |
| `hoverExited` | `HoverExitEventArgs` | any hover ends |
| `lastHoverExited` | `HoverExitEventArgs` | last hover ends |
| `firstSelectEntered` | `SelectEnterEventArgs` | first selection begins |
| `selectEntered` | `SelectEnterEventArgs` | any selection begins |
| `selectExited` | `SelectExitEventArgs` | any selection ends |
| `lastSelectExited` | `SelectExitEventArgs` | last selection ends |
| `activated` | `ActivateEventArgs` | trigger pressed while selected |
| `deactivated` | `DeactivateEventArgs` | trigger released while selected |

### Event Args

```csharp
args.interactorObject
args.interactableObject
args.isCanceled
```

### Script Pattern

```csharp
protected override void OnEnable()
{
    base.OnEnable();
    selectEntered.AddListener(OnGrab);
    selectExited.AddListener(OnRelease);
}

protected override void OnDisable()
{
    selectEntered.RemoveListener(OnGrab);
    selectExited.RemoveListener(OnRelease);
    base.OnDisable();
}
```

### Update Phases

| Phase | Typical use |
|-------|-------------|
| `Dynamic` | primary gameplay updates |
| `Fixed` | physics-dependent work |
| `Late` | post-process / visuals |
| `OnBeforeRender` | last-moment VR sync |

## `TrackedPoseDriver`

| Property | Type | Values |
|----------|------|--------|
| `trackingType` | enum | `RotationAndPosition`, `RotationOnly`, `PositionOnly` |
| `updateType` | enum | `UpdateAndBeforeRender`, `Update`, `BeforeRender` |
| `positionInput` | InputActionProperty | position action |
| `rotationInput` | InputActionProperty | rotation action |

Typical bindings:
- Camera: `<XRHMD>/centerEyePosition`, `<XRHMD>/centerEyeRotation`
- Left controller: `<XRController>{LeftHand}/devicePosition`, `deviceRotation`
- Right controller: `<XRController>{RightHand}/devicePosition`, `deviceRotation`

## Default Input Action Map

### Interaction

| Action | Type | Typical binding | Use |
|--------|------|-----------------|-----|
| Select | Button | Grip | grab/select |
| Select Value | Axis | Grip | analog grip |
| Activate | Button | Trigger | activate |
| Activate Value | Axis | Trigger | analog trigger |
| UI Press | Button | Trigger | UI click |
| Rotate Anchor | Vector2 | thumbstick | rotate held object |
| Translate Anchor | Vector2 | thumbstick | push/pull held object |

### Locomotion

| Action | Type | Typical binding | Use |
|--------|------|-----------------|-----|
| Move | Vector2 | left thumbstick | continuous move |
| Turn | Vector2 | right thumbstick | smooth turn |
| Snap Turn | Vector2 | right thumbstick | snap turn |
| Teleport Mode Activate | Vector2 | thumbstick | activate teleport |
| Teleport Mode Cancel | Button | grip button | cancel teleport |
| Grab Move | Button | grip button | hand locomotion |
