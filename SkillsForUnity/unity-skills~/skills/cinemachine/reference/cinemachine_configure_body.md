### cinemachine_configure_body
Configure the Body stage component (Follow, OrbitalFollow, ThirdPersonFollow, PositionComposer, FramingTransposer, etc.) in one call. Only fields matching the active component are applied; when none is, the call fails and `warnings` lists the fields that could not be written.
**Parameters:**
- `vcamName` (string, optional): VCam name. Provide one of name/entityId/instanceId/path.
- `instanceId` (int, optional): VCam Instance ID.
- `path` (string, optional): VCam hierarchy path.
- `entityId` (string, optional): VCam entity ID (preferred; the only id on Unity 6000.4+).

Follow / Transposer:
- `offsetX` / `offsetY` / `offsetZ` (float, optional): Follow offset vector.
- `bindingMode` (string, optional): Binding/target attachment mode (e.g. `LockToTarget`, `WorldSpace`).
- `dampingX` / `dampingY` / `dampingZ` (float, optional): Positional damping.

OrbitalFollow / OrbitalTransposer:
- `orbitStyle` (string, optional): `Sphere` or `ThreeRing`.
- `radius` (float, optional): Sphere orbit radius.
- `topHeight` / `topRadius` (float, optional): Top ring parameters.
- `midHeight` / `midRadius` (float, optional): Middle ring parameters.
- `bottomHeight` / `bottomRadius` (float, optional): Bottom ring parameters.

ThirdPersonFollow:
- `shoulderX` / `shoulderY` / `shoulderZ` (float, optional): Shoulder offset.
- `verticalArmLength` (float, optional): Vertical arm length.
- `cameraSide` (float, optional): 0 = left, 1 = right.

PositionComposer / FramingTransposer:
- `cameraDistance` (float, optional): Distance between camera and target.
- `screenX` / `screenY` (float, optional): On-screen target position (0–1).
- `deadZoneWidth` / `deadZoneHeight` (float, optional): Dead zone size (0–1).
