### batch_retry_failed
Re-run only the failed items of a previous batch execution report.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `reportId` | string | Yes | - | Prior batch report ID to resume from |
| `runAsync` | bool | No | true | Whether to run asynchronously (returns `jobId`) |
| `chunkSize` | int | No | 100 | Chunk size per retry batch (clamped to 1-200) |

**Returns**: `{jobId, retryCount, originalReportId, ...}`; `retryCount: 0` when nothing failed. A report without enough operation context fails ("Re-run the original preview first"). Subject to the same surface-profile check as `batch_execute`.
