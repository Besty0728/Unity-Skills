### component_invoke_method_batch
Accept independent asynchronous method jobs. This batch is non-atomic: accepted earlier jobs remain accepted even if a later item fails.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of invocation requests |

**Item properties**: `componentType`, `methodName` (required), `name`, `instanceId` (Unity < 6000.4), `path`, `componentIndex`, `isStatic`, `parameterTypes`, `genericTypeArguments`, `arguments`, `executionMode`, `timeoutSeconds`, `assembly`. Defaults and argument encoding match `component_invoke_method`; nested parameter/argument arrays are JSON strings.

**Returns**: `{success, totalItems, successCount, failCount, results}`. Each accepted item includes `jobId`, `kind`, `waitUrl` and `status: accepted`. Acceptance is not method completion: poll each job. No Undo, rollback or transactional guarantee is provided. High-risk Router gates apply to the batch.
