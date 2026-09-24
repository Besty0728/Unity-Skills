### job_list
List recent UnitySkills jobs.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 20 | Max jobs returned |

**Returns**: `{success, count, jobs: [{jobId, kind, status, progress, currentStage, startedAt, updatedAt, resultSummary, workflowId, reportId, canCancel}]}`.
