### material_assign
Assign material to object's renderer.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | GameObject hierarchy path |
| `materialPath` | string | Yes | Material asset to assign (e.g. `Assets/Materials/X.mat`) |
