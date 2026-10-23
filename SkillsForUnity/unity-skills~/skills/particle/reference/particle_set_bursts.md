### particle_set_bursts
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `burstsJson` | string | Yes | - | Complete list, at most 8: `[{time, count, cycleCount?, repeatInterval?, probability?}]`; `[]` clears |

`cycleCount` 0 repeats forever (Unity's encoding); `count` is a MinMaxCurve or a number.
