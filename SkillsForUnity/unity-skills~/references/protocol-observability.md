# Protocol: Observability

Three read-only endpoints close the loop after a mutation — most useful across Domain Reload, when the server briefly drops out (see root `SKILL.md` Core Rules).

## `GET /compile/status` — did my script edit compile?

After `script_*` writes (or any change that recompiles) the editor runs a Domain Reload and the server briefly answers `503/504`. Once it responds again, this endpoint reports the last compilation without re-reading files: `{status, isCompiling, isUpdating, domainReloadPending, lastCompilation:{finishedAtUtc, durationMs, success, errorCount, warningCount, errors:[{file, line, column, message, assembly}], warnings:[...], truncated} | null}`. `errors` are capped at 200, `warnings` at 50. `lastCompilation` survives the reload (persisted via `SessionState` for the editor session), so the pass/fail verdict and exact error lines are still there after the server returns. Recommended write-then-verify loop: `script_*` write → wait out the transient unavailability → `GET /compile/status` for success + error lines.

## `GET /events` — long-poll event channel

Instead of hammering `/compile/status` in a loop, subscribe to a 500-entry in-memory ring buffer. Query: `since` (omit = wait only for events newer than the current max seq; `0` = replay the whole buffer), `timeout` (seconds, default 25, clamped 1–55), `types` (comma-separated filter). Response: `{status, events:[{seq, type, tsUtc, payload}], cursor, oldestSeq, dropped, timedOut}`; carry `cursor` into the next call's `since` to resume. Event types: `compilation_started` / `compilation_finished` (carries `firstErrors`, first 5) / `before_domain_reload` / `after_domain_reload` / `server_restored` / `playmode_changed` / `console_error` (throttled 20/s with `droppedSinceLast`) / `job_completed` / `job_failed`. `seq` is monotonic and never rewinds across reloads; a reload discards in-flight events, signalled by `dropped:true`. **Reconnect anchor:** the "compilation succeeded" event is lost when Domain Reload tears down the connection — after reconnecting, read `server_restored`, whose `payload` carries the `lastCompilation` summary, to recover the verdict you missed.

## `GET /analytics` — execution telemetry

Aggregates how skills have been performing. Query `?window=1h|24h|7d|all` (default `24h`). The same local data feeds `/skills/recommend`: at least 5 valid calls are required before a high failure rate applies a bounded 1–3 point penalty; slow skills are warned but not penalized. Client/permission errors are ignored, and telemetry disabled means recommendation order remains semantic-only.
