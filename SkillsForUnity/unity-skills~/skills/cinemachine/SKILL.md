---
name: unity-cinemachine
description: Set up Cinemachine Virtual Cameras — VCam/FreeLook/ClearShot/StateDriven cameras and the Body/Aim/Noise pipeline. Use when creating or tuning Cinemachine cameras, configuring follow/look-at or noise, or building cinematic camera behavior, even if the user just says "虚拟相机" or "运镜". 配置 Cinemachine 虚拟相机(VCam/FreeLook/ClearShot/StateDriven 及 Body/Aim/Noise 管线);当用户要创建或调校 Cinemachine 相机、设置跟随/注视或噪声、或构建运镜效果时使用。
---

# Cinemachine Skills

Control Cinemachine Virtual Cameras and brain settings. Works with Cinemachine **2.x and 3.x** through a runtime reflection adapter (`CinemachineAdapter` / `CinemachineSkills`).

## Operating Mode

- **Approval**：查询类 skill（`cinemachine_inspect_vcam` / `cinemachine_list_components` / `cinemachine_get_brain_info`，源码标 `SkillMode.SemiAuto`）直接执行；其余配置/创建类（`cinemachine_create_vcam` / `cinemachine_set_targets` / `cinemachine_set_lens` / `cinemachine_configure_body` / `cinemachine_configure_aim` 等，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：未被禁列表拦截的 skill 直接执行。
- 本模块**含 Delete 类 skill**：`cinemachine_set_component`（替换/移除 pipeline component）、`cinemachine_target_group_remove_member`、`cinemachine_remove_extension` 标记为 `SkillOperation.Delete`，被 `IsForbiddenInSemi` 静态拦截 —— 仅 **Bypass** 模式或加入 **Allowlist** 才能调用。
- **包依赖**：必须安装 `com.unity.cinemachine` 包（CM 2.x 或 3.x）。未安装时所有 skill 返回 `{ error = "Cinemachine 未安装..." }` 的 stub —— 调用方应先用 `package_*` 系列 skill 确认安装状态。
- **反射脆弱性**：CM2 ↔ CM3 之间 API 名变化大（`CinemachineVirtualCamera` → `CinemachineCamera`，`CinemachineComponentBase` 改名等）。本模块通过 `CinemachineAdapter` 反射桥接，遇 CM 早期预览版（< `3.0.0-pre.5`）可能因 API 漂移返回失败 —— 优先用 `cinemachine_inspect_vcam` 探测当前可用字段，再决定 `propertyName`。

**DO NOT** (common hallucinations):
- `cinemachine_create` does not exist → use `cinemachine_create_vcam` for virtual cameras
- `cinemachine_set_target` / `cinemachine_set_follow` / `cinemachine_set_lookat` do not exist → use `cinemachine_set_targets` (sets both Follow and LookAt in one call)
- `cinemachine_add_brain` does not exist → CinemachineBrain is auto-added to Main Camera on first VCam creation
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
- `vcamName` (string, optional): Name of the VCam GameObject.
- `instanceId` (int, optional): VCam instance ID.
- `path` (string, optional): VCam hierarchy path.

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
- `mixerName` (string, optional): Name of the Mixing Camera.
- `mixerInstanceId` (int, optional): Mixing Camera instance ID.
- `mixerPath` (string, optional): Mixing Camera hierarchy path.
- `mixerEntityId` (string, optional): Mixing Camera entity ID (Unity 6000.4+, preferred).
- `childName` (string, optional): Name of the child VCam.
- `childInstanceId` (int, optional): Child VCam instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (Unity 6000.4+, preferred).
- `weight` (float): Weight value, usually 0.0–1.0 (default 1).

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
- `cameraName` (string, optional): Name of the State Driven Camera.
- `cameraInstanceId` (int, optional): State Driven Camera instance ID.
- `cameraPath` (string, optional): State Driven Camera hierarchy path.
- `cameraEntityId` (string, optional): State Driven Camera entity ID (Unity 6000.4+, preferred).
- `stateName` (string): Name of the animation state (e.g., "Run").
- `childCameraName` (string, optional): Name of the child VCam to activate for this state.
- `childInstanceId` (int, optional): Child VCam instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (Unity 6000.4+, preferred).
- `minDuration` (float, optional): Minimum duration in seconds (default 0).
- `activateAfter` (float, optional): Delay in seconds before activation (default 0).

### `cinemachine_set_noise`
Configure Noise settings (Basic Multi Channel Perlin).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `amplitudeGain` (float): Noise Amplitude.
- `frequencyGain` (float): Noise Frequency.

### `cinemachine_set_priority`
Set explicit priority value for a Virtual Camera. Higher priority wins activation.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `priority` (int): Priority value (default 10).

### `cinemachine_set_blend`
Set default blend or per-camera-pair blend on the CinemachineBrain. Leave `fromCamera`/`toCamera` empty for default blend.
**Parameters:**
- `style` (string): Blend style — `Cut`/`EaseInOut`/`EaseIn`/`EaseOut`/`HardIn`/`HardOut`/`Linear` (default `EaseInOut`).
- `time` (float): Blend duration in seconds (default `2`).
- `fromCamera` (string, optional): Source VCam name for per-pair blend.
- `toCamera` (string, optional): Destination VCam name for per-pair blend.

### `cinemachine_set_brain`
Configure CinemachineBrain properties: update method, default blend, debug display.
**Parameters:**
- `updateMethod` (string, optional): `FixedUpdate`/`LateUpdate`/`SmartUpdate`/`ManualUpdate`.
- `blendUpdateMethod` (string, optional): `FixedUpdate`/`LateUpdate`.
- `defaultBlendStyle` (string, optional): Blend style name (see `cinemachine_set_blend`).
- `defaultBlendTime` (float, optional): Default blend duration in seconds.
- `showDebugText` (bool, optional): Show Cinemachine debug text overlay.
- `showCameraFrustum` (bool, optional): Draw active camera frustum gizmo.
- `ignoreTimeScale` (bool, optional): Ignore `Time.timeScale` for blends.

### `cinemachine_create_sequencer`
Create a Sequencer camera (CM3) or BlendList camera (CM2) that plays child cameras in sequence.
**Parameters:**
- `name` (string): Name of the new GameObject.
- `loop` (bool): Whether to loop the sequence (default `false`).

### `cinemachine_sequencer_add_instruction`
Add a child camera instruction to a Sequencer/BlendList camera.
**Parameters:**
- `sequencerName` (string, optional): Sequencer camera name.
- `sequencerInstanceId` (int, optional): Sequencer Instance ID.
- `sequencerPath` (string, optional): Sequencer hierarchy path.
- `sequencerEntityId` (string, optional): Sequencer entity ID (Unity 6000.4+, preferred).
- `childCameraName` (string, optional): Child VCam name.
- `childInstanceId` (int, optional): Child VCam Instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (Unity 6000.4+, preferred).
- `hold` (float): Duration to hold on this child in seconds (default `2`).
- `blendStyle` (string): Blend style to use entering this child (default `EaseInOut`).
- `blendTime` (float): Blend duration in seconds (default `2`).

Provide at least one identifier for each of sequencer and child camera.

### `cinemachine_create_freelook`
Create a FreeLook camera. CM2 uses `CinemachineFreeLook`; CM3 builds `CinemachineCamera` + `OrbitalFollow(ThreeRing)` + `RotationComposer`.
**Parameters:**
- `name` (string): Name of the new GameObject.
- `followName` (string, optional): GameObject to follow.
- `lookAtName` (string, optional): GameObject to look at.

### `cinemachine_configure_camera_manager`
Configure ClearShot/StateDriven/Sequencer camera manager properties in one call. Applies only the properties whose matching component exists on the target.
**Parameters:**
- `cameraName` (string, optional): Camera manager name. Provide one of name/instanceId/path.
- `cameraInstanceId` (int, optional): Camera manager Instance ID.
- `cameraPath` (string, optional): Camera manager hierarchy path.
- `activateAfter` (float, optional): ClearShot — delay before activation.
- `minDuration` (float, optional): ClearShot — minimum duration on a shot.
- `randomizeChoice` (bool, optional): ClearShot — randomize shot selection.
- `animatorName` (string, optional): StateDriven — name of the GameObject carrying the Animator.
- `layerIndex` (int, optional): StateDriven — Animator layer index.
- `defaultBlendStyle` (string, optional): Default blend style (shared by ClearShot/StateDriven).
- `defaultBlendTime` (float, optional): Default blend duration in seconds.
- `loop` (bool, optional): Sequencer — loop playback.

### `cinemachine_configure_body`
Configure the Body stage component (Follow, OrbitalFollow, ThirdPersonFollow, PositionComposer, FramingTransposer, etc.) in one call. Only fields matching the active component are applied.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.

Follow / Transposer:
- `offsetX` / `offsetY` / `offsetZ` (float, optional): Follow offset vector.
- `bindingMode` (string, optional): Binding/target attachment mode (e.g. `LockToTarget`, `WorldSpace`).
- `dampingX` / `dampingY` / `dampingZ` (float, optional): Positional damping.

OrbitalFollow / OrbitalTransposer:
- `orbitStyle` (string, optional): `Sphere` or `ThreeRing`.
- `radius` (float, optional): Sphere orbit radius.
- `topHeight` / `topRadius` (float, optional): Top ring parameters.
- `midHeight` / `midRadius` (float, optional): Middle ring parameters.
- `bottomHeight` / `bottomRadius` (float, optional): Bottom ring parameters.

ThirdPersonFollow:
- `shoulderX` / `shoulderY` / `shoulderZ` (float, optional): Shoulder offset.
- `verticalArmLength` (float, optional): Vertical arm length.
- `cameraSide` (float, optional): 0 = left, 1 = right.

PositionComposer / FramingTransposer:
- `cameraDistance` (float, optional): Distance between camera and target.
- `screenX` / `screenY` (float, optional): On-screen target position (0–1).
- `deadZoneWidth` / `deadZoneHeight` (float, optional): Dead zone size (0–1).

### `cinemachine_configure_aim`
Configure the Aim stage component (RotationComposer, Composer, PanTilt, POV, etc.) in one call. Only fields matching the active component are applied.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.

Composer / RotationComposer:
- `screenX` / `screenY` (float, optional): Target on-screen position.
- `deadZoneWidth` / `deadZoneHeight` (float, optional): Dead zone size.
- `softZoneWidth` / `softZoneHeight` (float, optional): Soft zone size.
- `horizontalDamping` / `verticalDamping` (float, optional): Composition damping.
- `lookaheadTime` / `lookaheadSmoothing` (float, optional): Target lookahead tuning.
- `centerOnActivate` (bool, optional): Re-center on activation.

PanTilt / POV:
- `referenceFrame` (string, optional): Reference frame for pan/tilt axes.
- `panValue` / `tiltValue` (float, optional): Explicit pan/tilt values.

Target offset:
- `targetOffsetX` / `targetOffsetY` / `targetOffsetZ` (float, optional): Offset from target pivot.

### `cinemachine_configure_extension`
Configure a Cinemachine extension (`CinemachineConfiner`, `CinemachineDeoccluder`/`Collider`, `CinemachineFollowZoom`, `CinemachineGroupFraming`, etc.). If `extensionName` is omitted, the first extension on the VCam is used.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `extensionName` (string, optional): Extension type name (e.g. `CinemachineConfiner`).

Confiner:
- `boundingShapeName` (string, optional): Name of the bounding shape GameObject.
- `damping` (float, optional): Damping applied when confining.
- `slowingDistance` (float, optional): Slow-down distance inside the bounding shape.

Deoccluder / Collider:
- `cameraRadius` (float, optional): Camera collision radius.
- `strategy` (string, optional): Deocclusion strategy name.
- `maximumEffort` (int, optional): Maximum raycast iterations.
- `smoothingTime` (float, optional): Smoothing time for occlusion changes.

FollowZoom:
- `width` (float, optional): Target width in world units.
- `fovMin` / `fovMax` (float, optional): FOV clamp range.

GroupFraming:
- `framingMode` (string, optional): Framing mode name.
- `framingSize` (float, optional): Desired framing size.
- `sizeAdjustment` (string, optional): Size adjustment strategy name.

### `cinemachine_configure_impulse_source`
Configure `CinemachineImpulseSource` definition (shape, duration, gains). If no source is specified, the first `CinemachineImpulseSource` in the scene is used.
**Parameters:**
- `sourceName` (string, optional): Source name. Provide one of name/instanceId/path; omit all to pick the first source.
- `sourceInstanceId` (int, optional): Source Instance ID.
- `sourcePath` (string, optional): Source hierarchy path.
- `amplitudeGain` (float, optional): Amplitude gain multiplier.
- `frequencyGain` (float, optional): Frequency gain multiplier.
- `impactRadius` (float, optional): Impact radius in world units.
- `duration` (float, optional): Signal duration in seconds.
- `dissipationRate` (float, optional): Dissipation rate over distance.

---
## Minimal Example

```python
import unity_skills

# Create a vcam that follows and looks at the player
unity_skills.call_skill("cinemachine_create_vcam", name="PlayerCam")
unity_skills.call_skill("cinemachine_set_targets", vcamName="PlayerCam", followName="Player", lookAtName="Player")
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
