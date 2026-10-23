### particle_play
`particle_pause` takes the same parameters.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `withChildren` | bool | No | false | Also every child ParticleSystem |

**Returns**: `{name, entityId, instanceId, path, action, withChildren, playback: {isPlaying, isPaused, isStopped, isEmitting, particleCount, time, totalTime}, systems: [{name, entityId, path, playback}], undoable, warnings}`
