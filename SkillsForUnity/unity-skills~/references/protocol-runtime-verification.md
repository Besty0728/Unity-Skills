# Runtime verification and bounded repair

Use `component_get_methods` before invoking a component or static C# method. Discovery returns metadata without executing getters or methods. Select an explicit signature when overloads are ambiguous. Invocation defaults to Play Mode, runs as a job, and can have arbitrary side effects; cancellation stops waiting and subsequent steps, without undoing a started synchronous call.

Use `editor_run_scenario` for behavioral evidence: an invocation followed by an explicit assertion, or a condition that must become true. Steps pass through Router permission gates. Read the job's actual values, tolerances, compilation evidence and log window. A completed invocation alone does not prove the target behavior.

The shipped `scripts/runtime_verification.py` exports:

```python
from runtime_verification import run_verified_scenario

report = run_verified_scenario(
    client,
    steps=[
        {"kind": "invoke", "skill": "component_invoke_method", "args": invocation},
        {"kind": "assert", "select": "returnValue", "expected": 42},
    ],
    repair_callback=repair_related_files,
    max_repairs=3,
    timeout_seconds=1800,
    compilation_job_id=script_write_result["jobId"],
)
```

`repair_related_files(evidence, repair_number, remaining_seconds)` is supplied by the AI client or caller. It may edit scripts, Prefabs and scenes related to the target flow and return replacement steps, or return `None` to retain the current steps. It must honor the remaining time budget. There are no model calls in the plugin or helper. Package installation and Unity upgrades are outside this repair loop.

Each attempt exits Play when compilation requires it and requires evidence of a successful new compilation cycle and loaded assemblies before running the scenario. Supply `compilation_job_id` from the current script or asset mutation to use its exact compile proof; the job must be of kind `compile` and have been accepted within the previous sixty seconds. A repair callback may return `{"steps": replacement_steps, "compilationJobId": accepted_job_id}`. Otherwise the helper requests `debug_force_recompile`. A request that compiles no assemblies reports `unverified` and does not trigger code repairs. Accepted compilation jobs are inspected and awaited through instance-bound GET reads before requesting Editor state, so compilation and reload gaps do not replay a write. A job deferred until Play exits first captures Play ownership and exits through the normal permission gate. The helper preserves the original Play ownership. It uses dry-run previews and forwards their tokens; it never changes the operating mode or permission policy to bypass gates.

The default limit is three repairs and thirty minutes overall. Deadline checks use a monotonic clock. A blocking user callback or synchronous main-thread method cannot be forcibly killed by this helper. Transport uncertainty, cancellation and interrupted calls stop automatic reruns; retain the `jobId` to inspect progress. Reports distinguish `passed`, `failed`, `interrupted` and `unverified`, and retain each attempt's evidence. Cleanup failures are reported explicitly.

Inspect Console evidence with collapsed groups first. Expand the relevant group or UTC time window when the summary is insufficient. Coverage gaps and native history without timestamps prevent claims of complete recovery.
