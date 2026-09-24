### job_progress
Get fine-grained progress events for a job by incremental polling: pass the previous `totalCount` as the next `offset` to fetch only new events.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `offset` | int | No | 0 | Skip first N events (use previous `totalCount` for incremental polling) |

Also exposed as HTTP `GET /jobs/{id}/progress` and Python `client.get_job_progress(job_id, offset)`; all three share one response shape.

**Returns**: `{jobId, status, totalCount, offset, events: [{timestamp (ms), progress, stage, description}], terminal}`.

**An empty `events[]` is not a malfunction.** Per-item progress exists only for batch-executor jobs (`rename` / `set_property` / `replace_material` / `set_render_layer` / `cleanup_temp_objects` / `fix_missing_scripts` / `standardize_naming`), which emit one event every `progressGranularity` items because only they own a countable item list. Other kinds emit at most coarse lifecycle events (queued, stage change, terminal), and `test_discovery` emits **none at all**: `totalCount: 0` there means "no progress stream", not "stuck". For those, watch `status` via `GET /jobs/{id}`.
