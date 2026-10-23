### particle_simulate
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `seconds` | float | Yes | - | Simulated time, rounded to whole steps |
| `restart` | bool | No | true | false continues from the current state |
| `withChildren` | bool | No | false | Also every child ParticleSystem |
| `fixedTimeStep` | bool | No | true | Steps of `Time.fixedDeltaTime`; false: variable steps |
| `runAsync` | bool | No | false | Run as a sliced job and return a `jobId` |

**Returns**: `{status, name, entityId, instanceId, path, steps, systems, undoable, warnings, time?, totalTime?, particleCount?, isPaused?, playback?, jobId?, waitUrl?}`: inline `status: "completed"` with the read-back state; `runAsync` `status: "accepted"` with `jobId` / `waitUrl`, the inline result arriving in the job's `resultData`.
