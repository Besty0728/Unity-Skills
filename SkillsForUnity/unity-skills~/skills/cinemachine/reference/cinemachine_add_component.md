### cinemachine_add_component
> **DEPRECATED** — Use `cinemachine_set_component` instead for proper pipeline control (Body/Aim/Noise stages).
Add a Cinemachine component (legacy, supports CM2 and CM3).
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `componentType` (string): Type name (e.g., "OrbitalFollow").
