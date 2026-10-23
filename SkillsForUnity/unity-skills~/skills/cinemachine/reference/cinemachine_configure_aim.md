### cinemachine_configure_aim
Configure the Aim stage component (RotationComposer, Composer, PanTilt, POV, etc.) in one call. Only fields matching the active component are applied; when none is, the call fails and `warnings` lists the fields that could not be written.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/entityId/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).

Composer / RotationComposer:
- `screenX` / `screenY` (float, optional): Target on-screen position.
- `deadZoneWidth` / `deadZoneHeight` (float, optional): Dead zone size.
- `softZoneWidth` / `softZoneHeight` (float, optional): Soft zone size.
- `horizontalDamping` / `verticalDamping` (float, optional): Composition damping.
- `lookaheadTime` / `lookaheadSmoothing` (float, optional): Target lookahead tuning.
- `centerOnActivate` (bool, optional): Re-center on activation.

PanTilt / POV:
- `referenceFrame` (string, optional): Reference frame for pan/tilt axes.
- `panValue` / `tiltValue` (float, optional): Explicit pan/tilt values.

Target offset:
- `targetOffsetX` / `targetOffsetY` / `targetOffsetZ` (float, optional): Offset from target pivot.
