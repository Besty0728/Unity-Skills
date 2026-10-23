### cinemachine_set_blend
Set default blend or per-camera-pair blend on the CinemachineBrain. Leave `fromCamera`/`toCamera` empty for default blend.
**Parameters:**
- `style` (string): Blend style — `Cut`/`EaseInOut`/`EaseIn`/`EaseOut`/`HardIn`/`HardOut`/`Linear` (default `EaseInOut`).
- `time` (float): Blend duration in seconds (default `2`).
- `fromCamera` (string, optional): Source VCam name for per-pair blend.
- `toCamera` (string, optional): Destination VCam name for per-pair blend.
