### material_set_shader
Change the shader of a material.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `entityId` | string | No* | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | GameObject instance ID |
| `path` | string | No* | Material asset path |
| `shaderName` | string | Yes | Shader name |

Check shader property names in the Unity Inspector, or read them from `material_get_properties`; URP/HDRP property names differ from Standard (see the pipeline table in the entry).
