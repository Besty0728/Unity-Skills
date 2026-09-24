### job_wait
Wait for a job to finish or until `timeoutMs` elapses. **Blocks the Unity main thread** while waiting, so `timeoutMs` is clamped server-side to `[0, 2000]`: a 10000/60000 request waits at most 2 s. Prefer `GET /jobs/{id}?wait=<s>` (up to 120 s), which waits on the HTTP thread and only probes the main thread every 0.5 s.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `jobId` | string | Yes | - | Job identifier |
| `timeoutMs` | int | No | 2000 | Wait timeout in milliseconds; clamped to `[0, 2000]` |
| `includeDetails` | bool | No | false | Inline the full result payload as `details` instead of `resultAvailable` / `resultHint` |

Job kinds that advance through Unity's own engine loop rather than this plugin's pump (`compile`, `package`, `test`, `playmode`, `play_capture`, `build_player`) cannot progress while this thread is blocked: the compiler and domain reload, PackageManager requests, TestRunner callbacks, the PlayMode state machine and BuildPipeline all need the main thread free. For those kinds `job_wait` **does not enter a wait loop**: it returns the current snapshot at once with `waitNotSupported: true` and a `hint`. Self-driven kinds (batch-executor jobs such as `rename` / `set_property` / `replace_material` / `set_render_layer` / `cleanup_temp_objects` / `fix_missing_scripts` / `standardize_naming`, and `test_smoke`) still block up to the clamped timeout, since each tick advances them.

For engine-driven jobs use `GET /jobs/{id}?wait=<s>`, poll `GET /jobs/{id}` every 200-500 ms, or long-poll `GET /events`. `GET /jobs/{id}` skips the main-thread skill queue (light lane, drained every frame, exempt from the frame budget) but is still answered by the main thread between frames, so it stalls while a long operation holds that thread; `GET /events` is a pure HTTP-thread long poll that never enters the main-thread queue.

**Returns**: the `job_status` fields plus `terminal` (bool) and `waitNotSupported` (bool); `hint` only when `waitNotSupported` is true. Like `job_status` it reports `resultAvailable` + `resultHint` and leaves `details` null unless `includeDetails=true`.
