### script_delete
Delete a script file (moved to the workflow trash).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `scriptPath` | string | Yes | Script to delete |

**Returns**: `{success, status: "accepted", deleted, jobId, waitUrl, serverAvailability}`; the job completes after the domain reload (no compile diagnostics). NeverInSemi: runs only under Bypass or an Allowlist hit.
