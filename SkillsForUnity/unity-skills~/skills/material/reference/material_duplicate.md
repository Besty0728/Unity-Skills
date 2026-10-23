### material_duplicate
Duplicate a material asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourcePath` | string | Yes | Source material path |
| `newName` | string | Yes | Name for the duplicated material |
| `savePath` | string | No | Optional folder/path override for the duplicated material; `Assets/...` or an embedded/local package folder, read-only packages rejected. Omitted: saved next to the source material |
