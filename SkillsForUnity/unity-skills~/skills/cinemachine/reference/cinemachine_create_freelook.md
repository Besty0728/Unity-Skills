### cinemachine_create_freelook
Create a FreeLook camera. CM2 uses `CinemachineFreeLook`; CM3 builds `CinemachineCamera` + `OrbitalFollow(ThreeRing)` + `RotationComposer`.
**Parameters:**
- `name` (string): Name of the new GameObject.
- `followName` (string, optional): GameObject to follow.
- `lookAtName` (string, optional): GameObject to look at.
