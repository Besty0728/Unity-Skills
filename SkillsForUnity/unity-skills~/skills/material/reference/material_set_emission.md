### material_set_emission
Set emission color with auto-enable keyword.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | - | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r`, `g`, `b` | float | No | 1 | Emission color (0-1) |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom) |
| `enableEmission` | bool | No | true | Auto-enable _EMISSION keyword |
