### editor_playmode_step
Advance Play Mode forward by `frames` (1–100, default 1) using `EditorApplication.Step`; Unity automatically enters paused state as part of stepping. Requires Play Mode to already be active — call `editor_play` or `editor_play_capture` first, otherwise this returns a structured error (`error` + `hint` + `suggestedSkills: ["editor_play", "editor_play_capture"]`). Only one step job may be in flight at a time; a second call while one is still running returns an error naming the active `jobId`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `frames` | int | No | 1 | Frames to advance, clamped to 1–100 |

**Runs as an async job, not synchronously**: `EditorApplication.Step()` only lands on a later Editor tick, so the skill issues one `Step()` call at a time, confirms it landed by watching `Time.frameCount` advance, and only then issues the next — back-to-back `Step()` calls without that confirmation are not reliable. The call itself returns immediately: `{success, status: "accepted", jobId, framesRequested}`.

Poll `job_status` with the returned `jobId` until `status="completed"`; its `details` then contains `{framesRequested, framesCompleted, frameCount, isPaused}`. **Do not use `job_wait`** — like `playmode`/`play_capture`, this job's progress depends on `EditorApplication.update` ticks, and `job_wait`'s blocking loop runs on the same main thread those ticks come from, so it cannot observe progress; it will just spend its full timeout doing nothing. If Play Mode exits or Unity fails to advance a frame within 10s, the job fails with `failed_exited_play_mode` / `failed_step_timeout`.

**Example**:

```python
import unity_skills

# Step through Play Mode frame-by-frame and assert state changed
import time

# Enter Play Mode with a generous observation window so there's time left to step
capture = unity_skills.call_skill("editor_play_capture", durationSeconds=30)
while True:
    capture_status = unity_skills.call_skill("job_status", jobId=capture["jobId"])
    if capture_status["currentStage"] not in ("entering_play_mode", "domain_reload_recovery"):
        break  # Play Mode has been entered; capture_status["currentStage"] == "observing"
    time.sleep(0.2)

before = unity_skills.call_skill("editor_playmode_inspect", name="Player")["transform"]["position"]

step = unity_skills.call_skill("editor_playmode_step", frames=3)
while True:
    step_status = unity_skills.call_skill("job_status", jobId=step["jobId"])  # not job_wait, see above
    if step_status["status"] in ("completed", "failed"):
        break
    time.sleep(0.1)

after = unity_skills.call_skill("editor_playmode_inspect", name="Player")["transform"]["position"]
assert after != before, "Player did not move after 3 frames"
print(step_status["details"]["frameCount"], step_status["details"]["isPaused"])
```
