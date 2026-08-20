# Protocol: Unity CLI Cold Start

> Unity CLI cold start is opt-in (v2.3+). Most sessions do not need it. This protocol was verified against Unity CLI `1.0.0-beta.5`.

If the REST server is unreachable at session start, check `<projectRoot>/Library/UnitySkills/cli_config.json` (helper: `unity_skills.get_cli_config()`). **Only if** it exists with `enabled: true` has the user bound the experimental Unity CLI in the panel — then read `skills/unity-cli/SKILL.md` and you may:

1. Triage liveness via the UnitySkills registry (`~/.unity_skills/registry.json` → is the project entry's `pid` alive? alive → it's a Domain Reload window, keep waiting; not alive → continue checking).
2. Cross-check with `<cliPath> editors running --format json --non-interactive`.
3. Check `Library/UnityLockfile` if the registry is inconclusive.
4. Only if none of the above show a live Editor for this project, cold-start with `<cliPath> open "<projectPath>" --args -unityskills-coldstart` (the marker makes the plugin auto-start the REST server regardless of the Auto-start preference), then `unity_skills.wait_for_health()` until REST is ready.

`unity status` alone is **NOT authoritative** — it misses editors without the Unity Pipeline package.

Use the project path from the directory where you found `cli_config.json`, not the stale `projectPath` field stored inside it. If the file is absent or `enabled: false`, Unity CLI is off for this project — ignore it completely and never suggest installing it unprompted.

When scripting CLI calls, set these environment variables (advisory):

- `UNITY_NON_INTERACTIVE=1` — disable interactive prompts.
- `UNITY_FORMAT=json` — default output format.
- `UNITY_NO_PAGER=1` — disable the interactive pager on long output.
- `UNITY_NO_CRASH_REPORT=1` — opt out of anonymous crash reporting.

For build/test/run exit codes, signal diagnostics (`SIGSEGV/SIGILL/SIGTRAP/SIGFPE/SIGBUS`), and NDJSON terminal-frame handling, see `skills/unity-cli/SKILL.md`. The binary's own `--help` is always the final authority.
