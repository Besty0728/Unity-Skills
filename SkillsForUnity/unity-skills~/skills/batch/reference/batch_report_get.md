### batch_report_get
Get a batch execution report by `reportId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `reportId` | string | Yes | - | Batch report identifier |

**Returns**: `{success, reportId, kind, status, summary, createdAt, workflowId, jobId, rollbackAvailable, query, operation, totals, failureGroups, items}`; failed items can be rerun with `batch_retry_failed(reportId)`.
