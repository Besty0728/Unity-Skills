### workflow_list
List recorded tasks, newest first, as compact text rows. Each tracked write is one task with a short id `s<seq>`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| limit | int | No | 0 | Rows of tasks to return; `0` = 30 (rows) or all (objects); max 200 |
| agent | string | No | all | `all`, `self` (the calling agent), `unknown`, or an agent id such as `Codex` |
| before | string | No | null | An `s<seq>` or `c<n>` id: list only rows above it (pass the previous answer's `more`) |
| view | string | No | all | `all`, or `checkpoints` for one row per checkpoint with the steps it covers |
| format | string | No | rows | `rows`, or `objects` for the legacy object list (oldest first) |

**Returns (rows):** `{ success, count, agent, rows, more? }`: `agent` is the caller; `more` is present when older rows remain. Checkpoint rows (`c<n> time auto` or `c<n> time "label"`) sit among the steps; a `(undone)` step was reverted by a restore.

```
s344 14:36:10 light_set_properties Sun (2)
s343 14:35:58 light_create Sun (1)
s342 14:31:02 material_set_color Floor (1) @Codex
s341 14:28:11 gameobject_create_batch x12 (12)
```

A task row is `id time skill target (changes)`, plus `@agent` when another agent made it; `xN` counts an `items` batch. A caller the server could not identify yet (a bare `curl` or Python client) is `unknown` until the process lookup answers. Undo a row with `workflow_undo_task {"taskId":"s344"}`, newest first: undoing an older task restores its objects over any later edit to them.

**Returns (objects):** `{ success, count, history: [{ id, tag, description, time, changes, seq, agent, group }] }`. `group` is `g<seq>` for one agent's run of writes without a 30 s pause (an explicit `workflow_task_start`..`end` task is a run of its own) and `j<jobId>` for the chunks of one async batch job.
