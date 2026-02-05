# Cinemachine Skills

Control Cinemachine Virtual Cameras and settings (Cinemachine 3.x).

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
- `componentType` (string): "Main" (VCam itself), "Lens", or Component name (e.g. "OrbitalFollow").
- `propertyName` (string): Field or property name.
- `value` (object): New value.

### `cinemachine_set_targets`
Set Follow and LookAt targets.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `followName` (string, optional): GameObject name to follow.
- `lookAtName` (string, optional): GameObject name to look at.

### `cinemachine_set_component`
Switch VCam pipeline component (Body/Aim/Noise).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `stage` (string): "Body", "Aim", or "Noise".
- `componentType` (string): Type name (e.g. "OrbitalFollow", "Composer") or "None" to remove.

### `cinemachine_add_component`
(Deprecated: Use `cinemachine_set_component` for better pipeline control)
Add a Cinemachine component.

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

### `cinemachine_set_noise`
Configure Noise settings (Basic Multi Channel Perlin).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `amplitudeGain` (float): Noise Amplitude.
- `frequencyGain` (float): Noise Frequency.
