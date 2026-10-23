### material_create
Create a new material (auto-detects render pipeline).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Material name |
| `shaderName` | string | No | auto-detect | Shader (auto-detects URP/HDRP/Standard); an unresolved name falls back to the pipeline default and the response adds `shaderRequested` + `warnings` |
| `savePath` | string | No | null | `Assets/...`, or a folder inside an embedded/local package (`Packages/<id>/...`); read-only packages are rejected |

Without `savePath` the material exists only in memory and the response carries a `warning`: it is lost on editor restart, and the way to persist one is to call `material_create` again with a `savePath` (there is no separate save skill).

Persist and reuse: save materials as assets (`savePath`) so they can be addressed by path and shared.
