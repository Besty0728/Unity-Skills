### script_get_compile_feedback
Get the compile diagnostics related to one script, once Unity has finished compiling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `limit` | int | No | 20 | Max diagnostics |

**Returns**: `{scriptPath, isCompiling, hasErrors, errorCount, errors: [{type, message, file, line}], nextAction}`; console errors are matched by file or class name. With `isCompiling: true`, call again after compilation, or GET the write's `waitUrl`, which returns the same block in `resultData.compilation`.
