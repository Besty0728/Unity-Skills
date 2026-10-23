### particle_analyze
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No | null | One system (or `name` / `path` / `instanceId`) |
| `rootEntityId` | string | No | null | Every system under this object (also `rootName` / `rootPath` / `rootInstanceId`); not with a single system |
| `includeInactive` | bool | No | true | Include inactive objects |
| `limit` | int | No | 100 | Findings returned, 1-500, most severe first |
| `thresholdsJson` | string | No | null | Overrides, e.g. `{"maxParticlesPerSystem": 5000}` |

Omit every locator to analyze the open scenes. Static checks only (no frame times, GPU cost or overdraw); nothing is changed.

**Returns**: `{scope, systemsAnalyzed, totalMaxParticles, findingCount, counts: {error, warning, info}, findings: [{code, severity, system, field, message, evidence?, fix?: {skill, reason, args?}}], truncated, thresholds}`
