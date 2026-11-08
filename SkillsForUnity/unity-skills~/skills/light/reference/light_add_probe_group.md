### light_add_probe_group
Add a Light Probe Group to a GameObject, optionally with a grid of probes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name |
| `entityId` | string | No | null | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Instance ID |
| `path` | string | No | null | Hierarchy path |
| `gridX` | int | No | 0 | Probe count on the X axis |
| `gridY` | int | No | 0 | Probe count on the Y axis |
| `gridZ` | int | No | 0 | Probe count on the Z axis |
| `spacingX` | float | No | 2 | Metres between probes on X |
| `spacingY` | float | No | 1.5 | Metres between probes on Y |
| `spacingZ` | float | No | 2 | Metres between probes on Z |

A grid is built only when `gridX`, `gridY` and `gridZ` are all greater than 0; it replaces existing probes. Positions are local, centred on X and Z, rising from y = 0.

**Returns:** `{ success, gameObject, probeCount, existed, hasGrid }`
