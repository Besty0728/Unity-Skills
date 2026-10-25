### workflow_snapshot_created
Record a newly created object for undo tracking. Requires an active task (call workflow_task_start first).
**Note:** `component_add` and `gameobject_create` automatically record created objects, so you typically don't need to call this manually.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

**Auto-tracked operations.** The following are recorded automatically while a session or task is active:

- `gameobject_create` / `gameobject_create_batch`
- `gameobject_duplicate` / `gameobject_duplicate_batch`
- `component_add` / `component_add_batch`
- `ui_create_*` (canvas, button, text, image, etc.)
- `light_create`
- `prefab_instantiate` / `prefab_instantiate_batch`
- `material_create` / `material_duplicate`
- `terrain_create`
- `cinemachine_create_vcam`

For **modification operations**, the system auto-snapshots target objects before changes when possible.
