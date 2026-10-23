### cinemachine_set_lens
Quickly configure Lens settings (FOV, Near, Far, OrthoSize) and the projection ModeOverride.
**Parameters:**
- `vcamName` (string): Name of the VCam.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `fov` (float, optional): Field of View.
- `nearClip` (float, optional): Near Clip Plane.
- `farClip` (float, optional): Far Clip Plane.
- `orthoSize` (float, optional): Orthographic Size.
- `mode` (string, optional): Writes the lens `ModeOverride`, i.e. the projection mode this VCam pushes to the Unity Camera when it goes live. Case-insensitive; valid values are `None` (leave the Camera's own projection alone), `Orthographic`, `Perspective` and `Physical`. Omitting it leaves the existing `ModeOverride` untouched.

At least one of `fov` / `nearClip` / `farClip` / `orthoSize` / `mode` must be supplied; passing none returns `No values provided to update.` An invalid `mode` rejects the whole call with `SEMANTIC_INVALID` + `validValues` before anything is written, so the other parameters are not applied either.
