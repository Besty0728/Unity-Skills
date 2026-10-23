### cinemachine_configure_camera_manager
Configure ClearShot/StateDriven/Sequencer camera manager properties in one call. A parameter for a manager type the target lacks, an unresolved `animatorName` (or one without an Animator) and an unknown blend style are rejected before anything is written.
**Parameters:**
- `cameraName` (string, optional): Camera manager name. Provide one of name/entityId/instanceId/path.
- `cameraInstanceId` (int, optional): Camera manager Instance ID.
- `cameraPath` (string, optional): Camera manager hierarchy path.
- `cameraEntityId` (string, optional): Camera manager entity ID (preferred; the only id on Unity 6000.4+).
- `activateAfter` (float, optional): ClearShot — delay before activation.
- `minDuration` (float, optional): ClearShot — minimum duration on a shot.
- `randomizeChoice` (bool, optional): ClearShot — randomize shot selection.
- `animatorName` (string, optional): StateDriven — name of the GameObject carrying the Animator.
- `layerIndex` (int, optional): StateDriven — Animator layer index.
- `defaultBlendStyle` (string, optional): Default blend style (shared by ClearShot/StateDriven).
- `defaultBlendTime` (float, optional): Default blend duration in seconds.
- `loop` (bool, optional): Sequencer — loop playback.
