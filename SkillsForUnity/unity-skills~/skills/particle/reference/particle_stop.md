### particle_stop
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `withChildren` | bool | No | false | Also every child ParticleSystem |
| `stopBehavior` | string | No | "StopEmittingAndClear" | `StopEmittingAndClear`: remove every particle, stopped at once; `StopEmitting`: live particles finish |
