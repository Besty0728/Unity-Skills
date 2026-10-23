### cinemachine_mixing_camera_set_weight
Set the weight of a child camera within a Mixing Camera.
**Parameters:**
- `mixerName` (string, optional): Name of the Mixing Camera.
- `mixerInstanceId` (int, optional): Mixing Camera instance ID.
- `mixerPath` (string, optional): Mixing Camera hierarchy path.
- `mixerEntityId` (string, optional): Mixing Camera entity ID (preferred; the only id on Unity 6000.4+).
- `childName` (string, optional): Name of the child VCam.
- `childInstanceId` (int, optional): Child VCam instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (preferred; the only id on Unity 6000.4+).
- `weight` (float): Weight value, usually 0.0–1.0 (default 1).
