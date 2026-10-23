### cinemachine_set_vcam_property
Set any property on VCam or its pipeline components.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `componentType` (string): "Main" (VCam itself), "Lens", or Component name (e.g. "OrbitalFollow").
- `propertyName` (string): Field or property name.
- `value` (object): New value.
- `fov` (float, optional): Lens FOV shortcut. When supplied without `propertyName`, routes to `cinemachine_set_lens`.
- `nearClip` (float, optional): Lens near clip shortcut.
- `farClip` (float, optional): Lens far clip shortcut.
- `orthoSize` (float, optional): Lens orthographic size shortcut.
