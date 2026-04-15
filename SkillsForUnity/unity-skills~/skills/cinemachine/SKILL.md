---
name: unity-cinemachine
description: "Cinemachine virtual camera control. Use when users want to create cinematic cameras, set follow/look targets, or configure camera behaviors. Triggers: cinemachine, virtual camera, vcam, follow, look at, dolly, freelook, 虚拟相机, 跟随, 轨道."
---

# Cinemachine Skills

Control Cinemachine Virtual Cameras and settings (Cinemachine 2.x / 3.x).

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `cinemachine_create` does not exist → use `cinemachine_create_vcam` for virtual cameras
- `cinemachine_set_target` does not exist → use `cinemachine_set_follow` and `cinemachine_set_lookat` separately
- `cinemachine_add_brain` does not exist → CinemachineBrain is auto-added to Main Camera
- Cinemachine 2.x uses `CinemachineVirtualCamera`; Cinemachine 3.x uses `CinemachineCamera` — skills handle this automatically

Additional compatibility notes:
- CM3 priority access should use `Priority.Value` as the lowest common API when writing compatibility code.
- Early CM3 previews before `3.0.0-pre.5` changed core camera APIs significantly and are outside the current support baseline.

**Routing**:
- For basic Game Camera operations → use `camera` module
- For Scene View camera → use `camera` module's `camera_set_transform`/`camera_look_at`
- For camera animation sequences → use `timeline` module with Cinemachine track

## Skills

### `cinemachine_create_vcam`
Create a new Virtual Camera.
**Parameters:**
- `name` (string): Name of the VCam GameObject.
- `folder` (string): Parent folder path (default: "Assets/Settings").

### `cinemachine_inspect_vcam`
Deeply inspect a VCam, returning fields and tooltips.
**Parameters:**
- `objectName` (string): Name of the VCam GameObject.

### `cinemachine_set_vcam_property`
Set any property on VCam or its pipeline components.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `componentType` (string): "Main" (VCam itself), "Lens", or Component name (e.g. "OrbitalFollow").
- `propertyName` (string): Field or property name.
- `value` (object): New value.
- `fov` (float, optional): Lens FOV shortcut. When supplied without `propertyName`, routes to `cinemachine_set_lens`.
- `nearClip` (float, optional): Lens near clip shortcut.
- `farClip` (float, optional): Lens far clip shortcut.
- `orthoSize` (float, optional): Lens orthographic size shortcut.

### `cinemachine_set_targets`
Set Follow and LookAt targets.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID (preferred for precision).
- `path` (string, optional): VCam hierarchy path.
- `followName` (string, optional): GameObject name to follow.
- `lookAtName` (string, optional): GameObject name to look at.

### `cinemachine_set_component`
Switch VCam pipeline component (Body/Aim/Noise).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `stage` (string): "Body", "Aim", or "Noise".
- `componentType` (string): Type name (e.g. "OrbitalFollow", "Composer") or "None" to remove.

### `cinemachine_add_component`
> **DEPRECATED** — Use `cinemachine_set_component` instead for proper pipeline control (Body/Aim/Noise stages).
Add a Cinemachine component (legacy, supports CM2 and CM3).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `componentType` (string): Type name (e.g., "OrbitalFollow").

### `cinemachine_set_lens`
Quickly configure Lens settings (FOV, Near, Far, OrthoSize).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `fov` (float, optional): Field of View.
- `nearClip` (float, optional): Near Clip Plane.
- `farClip` (float, optional): Far Clip Plane.
- `orthoSize` (float, optional): Orthographic Size.

### `cinemachine_list_components`
List all available Cinemachine component names.
**Parameters:**
- None.

### `cinemachine_impulse_generate`
Trigger an Impulse at location or via Source.
**Parameters:**
- `sourceParams` (string, optional): JSON string for parameters, e.g., `{"velocity": {"x": 0, "y": -1, "z": 0}}`.

### `cinemachine_get_brain_info`
Get info about the Active Camera and Blend.
**Parameters:**
- None.

### `cinemachine_create_target_group`
Create a CinemachineTargetGroup.
**Parameters:**
- `name` (string): Name of the new TargetGroup GameObject.

### `cinemachine_target_group_add_member`
Add or update a member in a TargetGroup.
**Parameters:**
- `groupName` (string): Name of the TargetGroup.
- `targetName` (string): Name of the member GameObject.
- `weight` (float): Member weight (default 1).
- `radius` (float): Member radius (default 1).

### `cinemachine_target_group_remove_member`
Remove a member from a TargetGroup.
**Parameters:**
- `groupName` (string): Name of the TargetGroup.
- `targetName` (string): Name of the member GameObject.

### `cinemachine_set_spline`
Assign a SplineContainer to a VCam's SplineDolly component (Body stage).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `splineName` (string): Name of the GameObject with SplineContainer.

### `cinemachine_add_extension`
Add a CinemachineExtension to a VCam.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `extensionName` (string): Type name of the extension (e.g., "CinemachineStoryboard", "CinemachineImpulseListener").

### `cinemachine_remove_extension`
Remove a CinemachineExtension from a VCam.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `extensionName` (string): Type name of the extension.

### `cinemachine_set_active`
Force activation of a VCam (SOLO) by setting highest priority.
**Parameters:**
- `vcamName` (string): Name of the VCam to activate.

### `cinemachine_create_mixing_camera`
Create a Cinemachine Mixing Camera.
**Parameters:**
- `name` (string): Name of the new GameObject.

### `cinemachine_mixing_camera_set_weight`
Set the weight of a child camera within a Mixing Camera.
**Parameters:**
- `mixerName` (string): Name of the Mixing Camera.
- `childName` (string): Name of the child VCam.
- `weight` (float): Weight value (usually 0.0 to 1.0).

### `cinemachine_create_clear_shot`
Create a Cinemachine Clear Shot Camera.
**Parameters:**
- `name` (string): Name of the new GameObject.

### `cinemachine_create_state_driven_camera`
Create a Cinemachine State Driven Camera.
**Parameters:**
- `name` (string): Name of the new GameObject.
- `targetAnimatorName` (string, optional): Name of the GameObject with the Animator to bind.

### `cinemachine_state_driven_camera_add_instruction`
Add a state mapping instruction to a State Driven Camera.
**Parameters:**
- `cameraName` (string): Name of the State Driven Camera.
- `stateName` (string): Name of the animation state (e.g., "Run").
- `childCameraName` (string): Name of the child VCam to activate for this state.
- `minDuration` (float, optional): Minimum duration in seconds.
- `activateAfter` (float, optional): Delay in seconds before activation.

### `cinemachine_set_noise`
Configure Noise settings (Basic Multi Channel Perlin).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `amplitudeGain` (float): Noise Amplitude.
- `frequencyGain` (float): Noise Frequency.

---

## Canonical Signatures

以下附录以 `SkillsForUnity/Editor/Skills/*Skills.cs` 的真实 `[UnitySkill]` 签名为准，供审计和自动化解析使用。

### cinemachine_create_vcam
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Canonical signature parameter |
| `folder` | string | No | "Assets/Settings" | Canonical signature parameter |

### cinemachine_inspect_vcam
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |

### cinemachine_set_vcam_property
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `componentType` | string | No | null | Canonical signature parameter |
| `propertyName` | string | No | null | Canonical signature parameter |
| `value` | any | No | null | Canonical signature parameter |
| `fov` | float | No | null | Canonical signature parameter |
| `nearClip` | float | No | null | Canonical signature parameter |
| `farClip` | float | No | null | Canonical signature parameter |
| `orthoSize` | float | No | null | Canonical signature parameter |

### cinemachine_set_targets
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `followName` | string | No | null | Canonical signature parameter |
| `lookAtName` | string | No | null | Canonical signature parameter |

### cinemachine_add_component
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `componentType` | string | No | null | Canonical signature parameter |

### cinemachine_set_lens
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `fov` | float? | No | null | Canonical signature parameter |
| `nearClip` | float? | No | null | Canonical signature parameter |
| `farClip` | float? | No | null | Canonical signature parameter |
| `orthoSize` | float? | No | null | Canonical signature parameter |
| `mode` | string | No | null | Canonical signature parameter |

### cinemachine_list_components
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| - | - | - | - | No parameters |

### cinemachine_set_component
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `stage` | string | No | null | Canonical signature parameter |
| `componentType` | string | No | null | Canonical signature parameter |

### cinemachine_impulse_generate
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceParams` | string | Yes | - | Canonical signature parameter |

### cinemachine_get_brain_info
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| - | - | - | - | No parameters |

### cinemachine_set_active
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |

### cinemachine_set_noise
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `amplitudeGain` | float | No | 1f | Canonical signature parameter |
| `frequencyGain` | float | No | 1f | Canonical signature parameter |

### cinemachine_create_target_group
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Canonical signature parameter |

### cinemachine_target_group_add_member
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `groupName` | string | No | null | Canonical signature parameter |
| `groupInstanceId` | int | No | 0 | Canonical signature parameter |
| `groupPath` | string | No | null | Canonical signature parameter |
| `targetName` | string | No | null | Canonical signature parameter |
| `targetInstanceId` | int | No | 0 | Canonical signature parameter |
| `targetPath` | string | No | null | Canonical signature parameter |
| `weight` | float | No | 1f | Canonical signature parameter |
| `radius` | float | No | 1f | Canonical signature parameter |

### cinemachine_target_group_remove_member
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `groupName` | string | No | null | Canonical signature parameter |
| `groupInstanceId` | int | No | 0 | Canonical signature parameter |
| `groupPath` | string | No | null | Canonical signature parameter |
| `targetName` | string | No | null | Canonical signature parameter |
| `targetInstanceId` | int | No | 0 | Canonical signature parameter |
| `targetPath` | string | No | null | Canonical signature parameter |

### cinemachine_set_spline
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `vcamInstanceId` | int | No | 0 | Canonical signature parameter |
| `vcamPath` | string | No | null | Canonical signature parameter |
| `splineName` | string | No | null | Canonical signature parameter |
| `splineInstanceId` | int | No | 0 | Canonical signature parameter |
| `splinePath` | string | No | null | Canonical signature parameter |

### cinemachine_add_extension
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `extensionName` | string | No | null | Canonical signature parameter |

### cinemachine_remove_extension
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `vcamName` | string | No | null | Canonical signature parameter |
| `instanceId` | int | No | 0 | Canonical signature parameter |
| `path` | string | No | null | Canonical signature parameter |
| `extensionName` | string | No | null | Canonical signature parameter |

### cinemachine_create_mixing_camera
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Canonical signature parameter |

### cinemachine_mixing_camera_set_weight
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `mixerName` | string | No | null | Canonical signature parameter |
| `mixerInstanceId` | int | No | 0 | Canonical signature parameter |
| `mixerPath` | string | No | null | Canonical signature parameter |
| `childName` | string | No | null | Canonical signature parameter |
| `childInstanceId` | int | No | 0 | Canonical signature parameter |
| `childPath` | string | No | null | Canonical signature parameter |
| `weight` | float | No | 1f | Canonical signature parameter |

### cinemachine_create_clear_shot
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Canonical signature parameter |

### cinemachine_create_state_driven_camera
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Canonical signature parameter |
| `targetAnimatorName` | string | No | null | Canonical signature parameter |

### cinemachine_state_driven_camera_add_instruction
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `cameraName` | string | No | null | Canonical signature parameter |
| `cameraInstanceId` | int | No | 0 | Canonical signature parameter |
| `cameraPath` | string | No | null | Canonical signature parameter |
| `stateName` | string | No | null | Canonical signature parameter |
| `childCameraName` | string | No | null | Canonical signature parameter |
| `childInstanceId` | int | No | 0 | Canonical signature parameter |
| `childPath` | string | No | null | Canonical signature parameter |
| `minDuration` | float | No | 0 | Canonical signature parameter |
| `activateAfter` | float | No | 0 | Canonical signature parameter |
