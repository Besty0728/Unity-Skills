### particle_set_bursts
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `burstsJson` | string | Yes | - | Complete list, at most 8: `[{time, count, cycleCount?, repeatInterval?, probability?}]`; `[]` clears |

`cycleCount` 0 repeats forever (Unity's encoding); `count` is a MinMaxCurve or a number.
