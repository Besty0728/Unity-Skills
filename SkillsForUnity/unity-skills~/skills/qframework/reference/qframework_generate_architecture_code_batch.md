### qframework_generate_architecture_code_batch
Generate several architecture-layer code files in one request.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| items | string | Yes | - | JSON array of `{codeType, name, namespaceName, outputRoot, generateInterface}` |

**Returns:** `totalItems`, `successCount`, `failCount`, `results`

Refused under Approval and Auto (`MayTriggerReload`): Bypass or allowlist only.
