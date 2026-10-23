### cinemachine_set_priority
Set explicit priority value for a Virtual Camera. Higher priority wins activation.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/entityId/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `priority` (int): Priority value (default 10).
