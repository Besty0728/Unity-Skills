### job_cancel
Cancel a UnitySkills job, if its kind supports cancellation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |

**Returns**: `{success, jobId, status, resultSummary, warnings}`; an unknown `jobId` fails.
