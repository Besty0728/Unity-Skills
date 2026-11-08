# Unity CLI: automation contract

- **Structured output**: `--format <human|json|tsv|ndjson>` (env `UNITY_FORMAT`); `--json` is shorthand for `--format json`. When stdout is piped, the default silently becomes TSV. Always pass `--format json --non-interactive`. JSON envelope: `{success, command, data, errors, warnings}` (plus `notifications` when present, beta12+). Listings return `data.items` (beta15+).
- **stdout vs stderr**: a failed command still writes a complete envelope to **stdout** (`success: false`; `errors[0].code` is the stable token to branch on). stderr carries only human diagnostics and, under Claude Code, a `<claude-code-hint … />` line (beta14+). **Capture the streams separately and parse stdout only. Never use `2>&1`**: the hint breaks JSON parsing with `Extra data`.
- **Non-interactive**: `--non-interactive` turns prompts into hard errors instead of hanging your shell. Add `--quiet` and `--no-banner` for clean output.
- **Recommended environment** (advisory): `UNITY_NON_INTERACTIVE=1`, `UNITY_FORMAT=json` (`--format` wins), `UNITY_NO_PAGER=1`, `UNITY_NO_UPDATE_CHECK=1` (no "update available" notifications), `UNITY_NO_AGENT_HINTS=1` (no agent plugin or skill suggestions: the stderr hint, `AGENT_PLUGIN_AVAILABLE`, `AGENT_SKILLS_OUTDATED`; beta14+), `UNITY_NO_CRASH_REPORT=1` and `UNITY_NO_CLI_INVOKED_TELEMETRY=1` (beta9+; `DO_NOT_TRACK` also stops the ping since beta15). Always pass the bound project path explicitly. `UNITY_PROJECT_PATH` or the working directory can silently select another project's Editor.
- **Notifications and hints are relayed, never executed.** `notifications[]` entries (e.g. `CLI_UPDATE_AVAILABLE` with `remediation.command: "unity self-update"`), "Tip / suggest" lines and agent hints go to the user as text. `requiresUserApproval: true` means do not run `remediation.command` yourself. Never pass `--yes` / `-y`, `--accept-eula`, `--accept-upgrades` or `--force` on your own.
- **NDJSON streaming**: `--format ndjson` for real-time progress. The stream ends with exactly one terminal `{"type":"result","success":…}` frame (beta4+ for build, test and run; every command since beta15; failures too). The **last** frame is the final state. Listing rows are `"type":"row"` (beta15+). beta3 has no result frame; use the exit code.
- **Signal crashes (beta5+)**: `build`, `test` and `run` report an Editor crash as `stopped with signal SIGSEGV/SIGILL/SIGTRAP/SIGFPE/SIGBUS`. This is distinct from business failures (`TESTS_FAILED`, `BUILD_FAILED`) and CLI-side failures (argument, config, network, auth).
- **Project defaults (`ProjectSettings/UnityCliConfig.json`, beta10+)**: a repo may commit `build.*` and `test.*` defaults (target, output path, profile, timeout, mode, report format, coverage) that fill in flags you did not pass. Precedence: flag > environment variable > this file > the user's global config. `<cliPath> config resolve <key> [project]` (e.g. `test.timeout`) prints the value and its source. `cli.version` (beta15+) pins supported CLI versions; out of range stops commands in CI with exit `1` `CLI_VERSION_OUT_OF_RANGE`. This file is unrelated to `Library/UnitySkills/cli_config.json`, which is the only authorization. This file only supplies defaults.
- `--verbose` adds full error details with stack traces. Use it once, when reporting a CLI problem to the user.

## Exit codes

Baseline is beta6–13. Clauses tagged beta14+ or beta16+ apply to those binaries only.

| Exit code | Meaning |
|-----------|---------|
| `0` | Success. |
| `1` | Generic error. Read the envelope `errors`. |
| `2` | Usage or argument error (`INVALID_COMMAND_ARGS`: wrong command, missing flag, too many arguments). |
| `3` | Authentication or authorization failure. |
| `4` | `NEEDS_CONFIG`: a required tool, setting or link is missing. |
| `6` | The command failed for a reason no more specific code names: build failed, Editor exited abnormally or crashed, `test` without a verdict, `doctor --ci` definitive blocker, `projects verify` errors, `open` Editor died (`OPEN_EDITOR_EXITED*`) or stopped by a dialog (`OPEN_BLOCKED_BY_DIALOG*`, beta14+), or a refused already-open project. **beta6–13 also: `--timeout` expired.** |
| `7` | Unity services unreachable after CLI retries (beta16+: any offline, DNS or proxy failure), or `doctor --ci` reached no verdict. Retry a limited number of times. |
| `8` | Tests failed (`test`, beta6+). Never retry. beta3–5 report this as `6`. |
| `10`–`14` | beta14+: `10` `open --until-ready` not ready in time; `11` project, release or module not found; `12` no running Editor answered (not proof the Editor is closed); `13` the Editor has no such command; `14` timeout (`--timeout` of run, test or build, Editor or HTTP request). |
| `17` / `18` | beta16+: Editor busy (loading, compiling, Safe Mode) / blocked by a modal dialog. |
| `130` / `143` | Cancelled by SIGINT / terminated by SIGTERM (`141`: stdout reader closed). |

`6` and `8` are terminal. `7`, `10`, `12`, `14` and `17` may be transient (beta14+ `--help` marks retryable codes with `*`). **A code you do not know: stop and report it.** The footer of `<cliPath> --help` (beta14+) lists every code. `exitCodes` in `commands --json` also has them. Read the footer, not the 1.4 MB manifest.
