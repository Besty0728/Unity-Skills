### job_logs
Get structured logs for a UnitySkills job.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `limit` | int | No | 100 | Max log entries returned |

Also exposed as HTTP `GET /jobs/{id}/logs?limit=N` (server clamps `limit` to `[1, 500]`) and Python `client.get_job_logs(job_id, limit)`: the lightweight route (bypasses the skill router and the main-thread skill queue), preferred over this skill for repeated polling.

**Returns**: `{jobId, count, totalCount, logs: [{timestamp, level, stage, message, code}]}`.
