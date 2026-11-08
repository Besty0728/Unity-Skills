# XR Setup, Colliders and Versions

## Class names

Anchor on these XRI class names:

`XRInteractionManager`, `XROrigin`, `XRRayInteractor`, `XRDirectInteractor`, `XRSocketInteractor`, `XRGrabInteractable`, `XRSimpleInteractable`, `TeleportationProvider`, `TeleportationArea`, `TeleportationAnchor`, `ContinuousMoveProvider`, `SnapTurnProvider`, `ContinuousTurnProvider`, `TrackedDeviceGraphicRaycaster`, `XRUIInputModule`, `TrackedPoseDriver`.

Not runtime classes here: `XRHand`, `XRPlayer`, `XRTeleporter`, `GrabInteractor`, `VRController`, `XRLocomotion`, `XRManager`.

## Workflows

### Rig setup

1. Run `xr_check_setup`.
2. Create the rig with `xr_setup_rig`.
3. Make sure the XR UI input exists with `xr_setup_event_system`.
4. Add at least one interactor per controller.

### Grab setup

- Direct hand grab: `xr_add_direct_interactor` and `xr_add_grab_interactable`.
- Distance grab: `xr_add_ray_interactor` and `xr_add_grab_interactable`.
- Socket placement: `xr_add_socket_interactor` and a grabbable object.

`movementType` defaults matter:
- `VelocityTracking`: the best general-purpose physical grab.
- `Kinematic`: for handles and tools that should not snag.
- `Instantaneous`: the best for precise remote grab.

### Locomotion setup

- Teleport: `xr_setup_teleportation`, `xr_add_ray_interactor`, then `xr_add_teleport_area` or `xr_add_teleport_anchor`.
- Continuous locomotion: `xr_setup_continuous_move`.
- Turn: `xr_setup_turn_provider`. For comfort, use a snap turn and a moderate move speed (about 2.0 m/s).

### XR UI

1. Convert the Canvas with `xr_setup_ui_canvas`.
2. Make sure `xr_setup_event_system` has run.
3. Add a ray interactor to the controller that should click UI.

## Collider configuration

| Component | Collider required | `isTrigger` | Recommended collider | Reason |
|----------|-------------------|-------------|----------------------|--------|
| `XRDirectInteractor` | Yes | **True** | SphereCollider (`0.1-0.25`) | Overlap detection |
| `XRRayInteractor` | No | - | None | Uses raycasts |
| `XRSocketInteractor` | Yes | **True** | SphereCollider (`0.1-0.3`) | Snap zone |
| `XRGrabInteractable` | Yes | **False** | BoxCollider or convex MeshCollider | Physics and ray target |
| `XRSimpleInteractable` | Yes | **False** | BoxCollider | Selection detection |
| `TeleportationArea` | Yes | **False** | MeshCollider or BoxCollider | Surface raycast target |
| `TeleportationAnchor` | Yes | **False** | Thin BoxCollider | Point raycast target |

1. `XRGrabInteractable` needs a `Rigidbody`.
2. A grabbable collider must not be a trigger.
3. A direct interactor's collider must be a trigger.
4. Dynamic mesh colliders must be convex.
5. Socket colliders should be trigger-only overlap zones.

## XRI versions

| Topic | XRI 2.x | XRI 3.x |
|------|---------|---------|
| Namespace style | root namespace | split sub-namespaces |
| Rig type | `XROrigin` | `XROrigin` |
| Locomotion core | `LocomotionSystem` | `LocomotionMediator` |
| Controller type | `ActionBasedController` | `ActionBasedController` |
| Bridge behavior | The reflection helper falls back automatically | The reflection helper tries 3.x first |

## Notes

1. A scene should normally have exactly one `XRInteractionManager`.
2. Most locomotion skills assume an `XROrigin` already exists.
3. Installing or reconfiguring a package can trigger a domain reload. Wait before you retry XR calls.
4. Use `InteractionLayerMask` to filter interactors and interactables.
5. For custom XR scripts, prefer XRI lifecycle hooks and events over `Update()` polling.
