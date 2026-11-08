### particle_stop
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `withChildren` | bool | No | false | Also every child ParticleSystem |
| `stopBehavior` | string | No | "StopEmittingAndClear" | `StopEmittingAndClear`: remove every particle, stopped at once; `StopEmitting`: live particles finish |
