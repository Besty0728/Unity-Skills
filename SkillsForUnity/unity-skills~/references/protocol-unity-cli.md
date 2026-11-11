# Protocol: Unity CLI Cold Start

> Unity CLI cold start is opt-in (v2.3+). Most sessions do not need it. This protocol was verified against Unity CLI `1.0.0-beta.17`.

If the REST server is unreachable at session start, check `<projectRoot>/Library/UnitySkills/cli_config.json` (helper: `unity_skills.get_cli_config()`). **Only if** it exists with `enabled: true` has the user bound the experimental Unity CLI in the panel — then read `skills/unity-cli/MODULE.md` and you may:

1. Triage liveness via the UnitySkills registry (`~/.unity_skills/registry.json` → is the project entry's `pid` alive? alive → it's a Domain Reload window, keep waiting; not alive → continue checking).
2. Cross-check with `<cliPath> editors running --format json --non-interactive` (a sandboxed shell may see nothing — an empty list is not proof).
3. Check `Library/UnityLockfile` if the registry is inconclusive.
4. Only if none of the above show a live Editor for this project: run the read-only preflight (`<cliPath> editors -i --format json`; beta6+ also `doctor --ci --format json` — exit `6` stop, `7` retryable — and `projects verify "<projectPath>" --format json` — exit `6` = project broken, detection only), then cold-start with `<cliPath> open "<projectPath>" --args "-unityskills-coldstart" --format json --non-interactive` (the marker makes the plugin auto-start the REST server regardless of the Auto-start preference; `--args` is ONE quoted string), then `unity_skills.wait_for_health()` until REST is ready.
5. If `wait_for_health` times out, diagnose before relaunching: `<cliPath> status --format json --non-interactive --project-path "<projectPath>"` and relay `errors[0].code` (`STATUS_BLOCKED_BY_DIALOG*`, `STATUS_SAFE_MODE`) to the user — never answer dialogs or accept terms/upgrades yourself.

`unity status` alone is **NOT authoritative** — it misses editors without the Unity Pipeline package. Since beta14, `open` on an already-open project exits `0` with `data.alreadyOpen: true` and does not deliver the marker. Never `build` while the Editor is alive: since beta16 `build --target` borrows the open Editor and switches its build target.

Use the project path from the directory where you found `cli_config.json`, not the stale `projectPath` field stored inside it. If the file is absent or `enabled: false`, Unity CLI is off for this project — ignore it completely and never suggest installing it unprompted. Only the commands on the allowlist in `SKILL.md` are used; Unity's own `unity skill show` guide is reference, not authorization.

When scripting CLI calls, set these environment variables (advisory) and capture stdout and stderr separately (failures are a JSON envelope on stdout; never `2>&1`):

- `UNITY_NON_INTERACTIVE=1` — disable interactive prompts.
- `UNITY_FORMAT=json` — default output format.
- `UNITY_NO_PAGER=1` — disable the interactive pager on long output.
- `UNITY_NO_UPDATE_CHECK=1`, `UNITY_NO_AGENT_HINTS=1` — no update notifications / agent plugin suggestions (relay such hints to the user, never execute them).
- `UNITY_NO_CRASH_REPORT=1`, `UNITY_NO_CLI_INVOKED_TELEMETRY=1` — opt out of crash reporting and the per-run usage ping.

For build/test/run exit codes (`test`: `8` failed, `6` no verdict; beta14+ timeouts exit `14`; beta16+ `17`/`18` for a busy/blocked Editor; unknown code → stop and report, `<cliPath> --help` lists them), signal diagnostics (`SIGSEGV/SIGILL/SIGTRAP/SIGFPE/SIGBUS`), and NDJSON terminal-frame handling, see `skills/unity-cli/MODULE.md`. The binary's own `--help` is always the final authority.
