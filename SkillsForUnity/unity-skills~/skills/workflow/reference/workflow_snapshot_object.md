### workflow_snapshot_object
Manually snapshot an object's state before modification. Requires an active task (call workflow_task_start first).
**Call this BEFORE `component_set_property`, `gameobject_set_transform`, etc.**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the Game Object |
| instanceId | int | No | 0 | Instance ID of the object (preferred) |

**Returns:** `{ success, objectName, type }`

**Storage.** History is an append-only log under `Library/UnitySkills/workflow/`: `log.jsonl` plus sealed segments `log.NNNNNN.jsonl`, each sealed at 4 MB or 5,000 lines. A record states one task's current state, so replaying the log in order rebuilds the history; restore records are logged alongside. Snapshot payloads and asset / `.meta` bytes are content-addressed blobs under `workflow/blobs/`, written before the record that names them. History from before 3.0 is moved to `Library/UnitySkills/workflow_legacy/` and deleted automatically 14 days later.

**Snapshot types.** Snapshots are tiered by `SnapshotType`:

| Type | Trigger | What is stored | Undo behavior |
|------|---------|----------------|---------------|
| **Created** | New asset/folder | path + GUID only | Delete the created asset/folder |
| **Moved** | `asset_move` | old + new path only | Move back to the old path |
| **Deleted** | `asset_delete` etc. | file + `.meta` moved into the store | Full restore, **including `.cs` scripts** (old implementation could not restore `.cs`) |
| **Modified** | material / SO / scene / uss / uxml / shadergraph … | content-addressed backup + lightweight `originalJson` | Restore the backed-up bytes |
| **Setting** | editor / project settings | handled via `WorkflowSettingRestorerRegistry` | Registry restores the previous value |

Undo/redo return per-snapshot detail (`TaskUndoResult`: `total` / `succeeded` / `failed` / `details` / `error`). Operations run in reverse order; on the first failure, failed and unprocessed snapshots stay on their source stack so they can be retried.

**Auto-clean.** `WorkflowAutoCleanConfig` (per-project settings `UnitySkills.Workflow.*`) has an `Enabled` toggle and two limits. `CapacityMB` (default 512) caps the log segments plus blobs together; `MaxAgeDays` (default 30) removes sealed segments whose newest record is older than that. Eviction removes whole segments, oldest first; `0` disables a limit. A blob is kept while any retained segment names it or it was written within the last 10 minutes. `workflow_delete_task` removes the task from history, but its disk space is freed only when its segment is evicted. Cleanup runs deferred, never inside a write: after the history loads, when a segment seals or the log grows past its size check, and on the History tab's Clean Now.

**Settings.** Setting-class skills used to be one-way; they now register a restorer and can be rolled back: `console_set_pause_on_error` / `console_set_collapse` / `console_set_clear_on_play`, `debug_set_defines`, `graphics_set_quality_level` / `graphics_set_default_render_pipeline` / `graphics_set_quality_render_pipeline` / `graphics_add_always_included_shader` / `graphics_remove_always_included_shader` / `graphics_set_shader_stripping`, `physics_set_gravity` / `physics_set_layer_collision`, `project_add_tag`. `scene_save` over an existing scene is restorable: the old file is kept as a Modified snapshot.
