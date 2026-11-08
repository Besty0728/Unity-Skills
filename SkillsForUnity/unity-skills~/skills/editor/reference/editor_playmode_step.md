### editor_playmode_step
Advance Play Mode forward by `frames` (1–100, default 1) using `EditorApplication.Step`; Unity automatically enters paused state as part of stepping. Requires Play Mode to already be active — call `editor_play` or `editor_play_capture` first, otherwise this returns a structured error (`error` + `hint` + `suggestedSkills: ["editor_play", "editor_play_capture"]`). Only one step job may be in flight at a time; a second call while one is still running returns an error naming the active `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `frames` | int | No | 1 | Frames to advance, clamped to 1–100 |

**Runs as an async job, not synchronously**: `EditorApplication.Step()` only lands on a later Editor tick, so the skill issues one `Step()` call at a time, confirms it landed by watching `Time.frameCount` advance, and only then issues the next — back-to-back `Step()` calls without that confirmation are not reliable. The call itself returns immediately: `{success, status: "accepted", jobId, framesRequested}`.

Poll `job_status` with the returned `jobId` until `status="completed"`; its `details` then contains `{framesRequested, framesCompleted, frameCount, isPaused}`. **Do not use `job_wait`** — like `playmode`/`play_capture`, this job's progress depends on `EditorApplication.update` ticks, and `job_wait`'s blocking loop runs on the same main thread those ticks come from, so it cannot observe progress; it will just spend its full timeout doing nothing. If Play Mode exits or Unity fails to advance a frame within 10s, the job fails with `failed_exited_play_mode` / `failed_step_timeout`.

**Example**:

```bash
# Enter Play Mode with a generous observation window
curl -s "http://localhost:<port>/skill/editor_play_capture?expectProject=<project>" -d '{"durationSeconds":30}'
# Poll job_status with the returned jobId until currentStage leaves entering_play_mode / domain_reload_recovery (it is then observing)
curl -s "http://localhost:<port>/skill/job_status?expectProject=<project>" -d '{"jobId":"<capture jobId>"}'

# Read the Player position before stepping
curl -s "http://localhost:<port>/skill/editor_playmode_inspect?expectProject=<project>" -d '{"name":"Player"}'

# Step three frames, then poll job_status (not job_wait: it would block the ticks the step needs) until status is completed or failed
curl -s "http://localhost:<port>/skill/editor_playmode_step?expectProject=<project>" -d '{"frames":3}'
curl -s "http://localhost:<port>/skill/job_status?expectProject=<project>" -d '{"jobId":"<step jobId>"}'

# Read the Player position again: it must differ from the first read; details.frameCount and details.isPaused are in the final job_status
curl -s "http://localhost:<port>/skill/editor_playmode_inspect?expectProject=<project>" -d '{"name":"Player"}'
```
