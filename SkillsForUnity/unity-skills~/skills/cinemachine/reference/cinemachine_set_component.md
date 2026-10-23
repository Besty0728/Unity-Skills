### cinemachine_set_component
Switch VCam pipeline component (Body/Aim/Noise).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `stage` (string): "Body", "Aim", or "Noise".
- `componentType` (string): Type name (e.g. "OrbitalFollow", "Composer") or "None" to remove.
