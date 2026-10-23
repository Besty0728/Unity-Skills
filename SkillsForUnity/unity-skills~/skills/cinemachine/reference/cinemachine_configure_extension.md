### cinemachine_configure_extension
Configure a Cinemachine extension (`CinemachineConfiner`, `CinemachineDeoccluder`/`Collider`, `CinemachineFollowZoom`, `CinemachineGroupFraming`, etc.). If `extensionName` is omitted, the first extension on the VCam is used; an `extensionName` the VCam does not carry and an unresolved `boundingShapeName` are rejected. Settings that could not be written are listed in `warnings`.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/entityId/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).
- `extensionName` (string, optional): Extension type name (e.g. `CinemachineConfiner`).

Confiner:
- `boundingShapeName` (string, optional): Name of the bounding shape GameObject.
- `damping` (float, optional): Damping applied when confining.
- `slowingDistance` (float, optional): Slow-down distance inside the bounding shape.

Deoccluder / Collider:
- `cameraRadius` (float, optional): Camera collision radius.
- `strategy` (string, optional): Deocclusion strategy name.
- `maximumEffort` (int, optional): Maximum raycast iterations.
- `smoothingTime` (float, optional): Smoothing time for occlusion changes.

FollowZoom:
- `width` (float, optional): Target width in world units.
- `fovMin` / `fovMax` (float, optional): FOV clamp range.

GroupFraming:
- `framingMode` (string, optional): Framing mode name.
- `framingSize` (float, optional): Desired framing size.
- `sizeAdjustment` (string, optional): Size adjustment strategy name.
