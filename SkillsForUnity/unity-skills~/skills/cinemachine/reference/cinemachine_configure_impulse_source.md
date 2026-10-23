### cinemachine_configure_impulse_source
Configure `CinemachineImpulseSource` definition (shape, duration, gains). If no source is specified, the first `CinemachineImpulseSource` in the scene is used.
**Parameters:**
- `sourceName` (string, optional): Source name. Provide one of name/entityId/instanceId/path; omit all to pick the first source.
- `sourceInstanceId` (int, optional): Source Instance ID.
- `sourcePath` (string, optional): Source hierarchy path.
- `sourceEntityId` (string, optional): Source entity ID (preferred; the only id on Unity 6000.4+).
- `amplitudeGain` (float, optional): Amplitude gain multiplier.
- `frequencyGain` (float, optional): Frequency gain multiplier.
- `impactRadius` (float, optional): Impact radius in world units.
- `duration` (float, optional): Signal duration in seconds.
- `dissipationRate` (float, optional): Dissipation rate over distance.
