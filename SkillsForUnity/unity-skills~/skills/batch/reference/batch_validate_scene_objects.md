### batch_validate_scene_objects
Analyze the scene for missing scripts, missing references, duplicate names and empty objects. Report only: nothing to execute.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `issueLimit` | int | No | 100 | Max issues returned |

**Returns**: `{success, summary, scene, missingReferences}` (the scene validation report and the missing-reference scan); fix findings with the fixers, e.g. `batch_fix_missing_scripts`.
