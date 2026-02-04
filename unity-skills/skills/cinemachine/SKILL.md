# Cinemachine Skills

Control Cinemachine Virtual Cameras and settings.

## Skills

### `cinemachine_create_vcam`
Create a new Virtual Camera.
**Parameters:**
- `name` (string): Name of the VCam GameObject.

### `cinemachine_inspect_vcam`
Deeply inspect a VCam, returning fields and tooltips.
**Parameters:**
- `objectName` (string): Name of the VCam GameObject.

### `cinemachine_set_vcam_property`
Set any property on VCam or its pipeline components.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `componentType` (string): "Main" (VCam itself), "Body", "Aim", "Noise", or "Lens".
- `propertyName` (string): Field or property name (e.g. "m_Lens.FieldOfView", "m_Priority").
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
- `componentType` (string): Type name (e.g. "CinemachineTransposer", "DoNothing").
