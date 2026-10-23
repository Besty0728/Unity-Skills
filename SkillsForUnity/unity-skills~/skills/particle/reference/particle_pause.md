### particle_pause
Pause a ParticleSystem, with its child systems when `withChildren=true`, at its current time keeping its particles, and return the state read back; `particle_play` resumes it. Runtime state only: not saved, not undoable; never enters Play Mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | null | Target by name |
| `instanceId` | int | No* | 0 | Target by instance id (older Unity) |
| `path` | string | No* | null | Target by hierarchy path |
| `withChildren` | bool | No | false | Also pause every child ParticleSystem (Unity's withChildren). |

*Exactly one locator (`entityId`, `name`, `path` or `instanceId`) identifies the target; an id that names nothing is an error, never a fallback to the name.

**Returns**: `{name, entityId, instanceId, path, action, withChildren, playback: {isPlaying, isPaused, isStopped, isEmitting, particleCount, time, totalTime}, systems: [{name, entityId, path, playback}], undoable, warnings}`. Never refused for a `stopAction`, collision or trigger setup.
