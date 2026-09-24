### batch_report_list
List recent batch reports.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `limit` | int | No | 20 | Max reports returned |

**Returns**: `{success, count, reports}`, newest first, one summary per report (`reportId`, `kind`, ...); open one with `batch_report_get`.
