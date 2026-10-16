### component_invoke_method
Accept one exact method call as an asynchronous job. This is high risk, non-atomic and has no Undo guarantee. DryRun resolves the method and arguments without invoking it.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentType` | string | Yes | - | Component type, or ordinary C# type for static calls |
| `methodName` | string | Yes | - | Exact method name |
| `name` | string | No* | null | Instance target GameObject name |
| `instanceId` | int | No* | 0 | Instance target GameObject ID |
| `path` | string | No* | null | Instance target hierarchy path |
| `componentIndex` | int | No | 0 | Zero-based index among components of this type |
| `isStatic` | bool | No | false | Call a static method without a GameObject target |
| `parameterTypes` | json string | No | null | Exact CLR parameter type array; required when overloaded, including `&` for ref/out |
| `genericTypeArguments` | json string | No | null | Type array closing a generic method |
| `arguments` | json string | No | null | Positional argument array; omit trailing optional arguments and retain null slots for out parameters |
| `executionMode` | string | No | play | Must match current mode: play or explicit edit |
| `timeoutSeconds` | int | No | 30 | Deadline, 1–600 seconds |
| `assembly` | string | No | null | Optional assembly name |

*Instance calls require a target identifier; static calls do not. The skill never enters Play Mode itself.

**Returns**: `{success, status, jobId, kind, waitUrl}`. A completed job has `resultData: {returnValue, refOut, signature}`. Plain, Task, Task<T>, ValueTask, ValueTask<T> and IEnumerator results are supported; output is bounded and marks truncation/cycles. Coroutine yields support null, nested iterators/CustomYieldInstruction, AsyncOperation and Play Mode WaitForSeconds.

Use shared scalar/vector/colour conversion, JSON arrays for arrays/params, and `{referenceName}`, `{referencePath}`, `{referenceInstanceId}` or `{assetPath}` for Unity object parameters. Method exceptions fail the job. Timeout/cancellation cannot undo effects or forcibly stop arbitrary user Tasks. Synchronous code cannot be preempted. Started calls lost during domain reload report interrupted and are never replayed.

Every terminal outcome also records `acceptedUtc`, `executionStartedUtc`, `finishedUtc`, `acceptedFrame`, `startFrame`, `finishFrame`, `acceptedLogSeq`, `startLogSeq`, `finishLogSeq` and `logWindow`. Start fields are null if invocation never began. The log window spans acceptance through terminal observation; it associates logs by time/sequence, without claiming exclusive causation. Its errors do not by themselves fail a completed method call; use a scenario to assert runtime health. Coverage gaps and pagination remain explicit in the window.

Failures caused by exceptions include `exception: {type, message, stackTrace, truncated}` with bounded text. Return serialization reads public fields and auto-property backing fields without custom getter execution. More than 64 fields/properties, excessive depth/node count, or omitted unsafe getters set truncation markers; objects without any safely readable data carry `$unsupported` and `$reason`, rather than silently returning only a type name. An ambiguous method or type returns `details.candidates`; choose an exact signature/type and submit a new request, without overload guessing.
