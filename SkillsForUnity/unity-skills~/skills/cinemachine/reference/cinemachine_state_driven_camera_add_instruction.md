### cinemachine_state_driven_camera_add_instruction
Add a state mapping instruction to a State Driven Camera.
**Parameters:**
- `cameraName` (string, optional): Name of the State Driven Camera.
- `cameraInstanceId` (int, optional): State Driven Camera instance ID.
- `cameraPath` (string, optional): State Driven Camera hierarchy path.
- `cameraEntityId` (string, optional): State Driven Camera entity ID (preferred; the only id on Unity 6000.4+).
- `stateName` (string): Name of the animation state (e.g., "Run").
- `childCameraName` (string, optional): Name of the child VCam to activate for this state.
- `childInstanceId` (int, optional): Child VCam instance ID.
- `childPath` (string, optional): Child VCam hierarchy path.
- `childEntityId` (string, optional): Child VCam entity ID (preferred; the only id on Unity 6000.4+).
- `minDuration` (float, optional): Minimum duration in seconds (default 0).
- `activateAfter` (float, optional): Delay in seconds before activation (default 0).
