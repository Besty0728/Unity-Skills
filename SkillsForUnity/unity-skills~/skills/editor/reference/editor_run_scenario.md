### editor_run_scenario
`editor_run_scenario` takes `steps` (JSON array string, 1–100 entries), `executionMode` (`play` default or explicit `edit`), and `timeoutSeconds` (1–600, default 60). It returns a persisted `run_scenario` job. At least one `assert` or `wait_condition` is required. DryRun validates structure and step skill metadata without entering Play Mode or invoking code; each actual step still goes through SkillRouter with its own permissions. Invocation steps currently accept only `component_invoke_method`; other lifecycle-changing skills and nested scenarios are rejected.

Step forms:

- `{"kind":"invoke","skill":"component_invoke_method","args":{...}}`: call once and await its job. Use the same executionMode in invocation args as the scenario.
- `{"kind":"read","skill":"component_get_properties","args":{...}}`: run a skill declared ReadOnly and retain its result.
- `{"kind":"assert","expected":42,"select":"returnValue","tolerance":0.01}`: compare the previous result; alternatively supply a ReadOnly `skill` and `args` to read immediately. `select` is an optional JSONPath relative to response data. Missing paths fail even when expected is null. Numeric tolerance applies recursively to matching arrays/objects.
- `{"kind":"wait_frames","frames":2}` or `{"kind":"wait_seconds","seconds":0.5}`: wait without blocking the Editor. Frames use the actual Play frame counter; seconds use wall-clock time. A paused Play Mode cannot advance a frame wait, and Edit Mode frame waits should be zero.
- `{"kind":"wait_condition","skill":"component_get_properties","args":{...},"select":"...","expected":true}`: repeat a ReadOnly observation until it matches or the scenario deadline expires.

A scenario started from Edit Mode enters Play Mode and exits afterward. A scenario started in Play Mode preserves that mode. It temporarily enables `Application.runInBackground`, restoring its original value on success, failure or cancellation. Domain reload during Play entry/exit resumes lifecycle cleanup; an already-dispatched invocation is interrupted without replay. Arbitrary calls are high risk and have no Undo or atomicity promise.

Poll the job until terminal. `resultData.passed` is true only after assertion success, no runtime Error/Exception/Assert logs and complete log coverage. `steps` includes `actual`, `expected`, `tolerance` and `passed`; `logWindow` and `compilation` retain evidence. Runtime errors, missing log coverage, pending compilation, exceptions, missing targets and timeouts fail verification. Cancellation or timeout does not guarantee arbitrary user code has stopped. Fix source outside the scenario, wait for compile+reload, then explicitly submit a new verification job; never silently replay a failed invocation.

For the AI client's finite compile → scenario → repair → rerun loop, use [Runtime verification and bounded repair](../../../references/protocol-runtime-verification.md). The shipped helper accepts a caller-supplied repair callback and enforces at most three repairs and a thirty-minute budget.
