### material_set_gi_flags
Set material global illumination flags.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | - | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | GameObject hierarchy path or material asset path |
| `flags` | string | No | `RealtimeEmissive` | GI flags: `None` / `RealtimeEmissive` / `BakedEmissive` / `EmissiveIsBlack` / `AnyEmissive` |
