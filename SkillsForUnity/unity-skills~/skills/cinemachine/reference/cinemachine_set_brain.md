### cinemachine_set_brain
Configure CinemachineBrain properties: update method, default blend, debug display.
**Parameters:**
- `updateMethod` (string, optional): `FixedUpdate`/`LateUpdate`/`SmartUpdate`/`ManualUpdate`.
- `blendUpdateMethod` (string, optional): `FixedUpdate`/`LateUpdate`.
- `defaultBlendStyle` (string, optional): Blend style name (see `cinemachine_set_blend`).
- `defaultBlendTime` (float, optional): Default blend duration in seconds.
- `showDebugText` (bool, optional): Show Cinemachine debug text overlay.
- `showCameraFrustum` (bool, optional): Draw active camera frustum gizmo.
- `ignoreTimeScale` (bool, optional): Ignore `Time.timeScale` for blends.
