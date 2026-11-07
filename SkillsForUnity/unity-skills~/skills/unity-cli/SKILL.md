---
name: unity-cli
description: Guide for using the experimental Unity CLI with bound UnitySkills projects
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may call it directly.

# Unity CLI (advisory)

**Advisory module — no REST skills.** All commands here run in YOUR shell on the user's machine, not through the REST server. That is the point: they work while the Unity Editor is **closed**.

## Gate — read this first

Before using anything below, check the binding config:

```
<projectRoot>/Library/UnitySkills/cli_config.json
```

- File missing, unreadable, or `enabled: false` → **Unity CLI is OFF for this project. Ignore this module entirely.** Do not suggest installing the CLI unprompted; the user opts in via `Window > UnitySkills → AI Config → Unity CLI Setup…`.
- `enabled: true` → use `cliPath` as the executable (it may not be on your PATH). Respect the per-feature switches in `features`:

```json
{
  "schemaVersion": 1,
  "enabled": true,
  "cliPath": "/Users/me/.unity/bin/unity",
  "cliVersion": "1.0.0-beta.17",
  "projectPath": "/path/to/Project",
  "editorVersion": "6000.0.32f1",
  "boundAt": "2026-07-26T09:00:00Z",
  "features": { "coldStart": true, "openArgs": true, "cliTest": true, "cliRun": false, "cliBuild": false }
}
```

Configs written by older plugin versions may lack the `cliRun` / `cliBuild` keys — for these two, **a missing key means OFF** (the first three keep their original semantics). Both also default to off on fresh binds; the user enables them per project in the panel.

The global registry (`~/.unity_skills/registry.json`) also carries `cliBound` / `cliPath` per running instance — use it for **liveness checks only, never as authorization**: the ONLY thing that authorizes CLI use for a project is that project's own `cli_config.json`. Do not cold-start any project whose own config you have not read, even if it appears in the registry. `projectPath` inside the config is a bind-time snapshot — the directory you actually found the config under is authoritative (helper `get_cli_config()` already rewrites it); never `open` the stored path if it differs from the real project root.

> Unity CLI is **experimental (beta)** and changes every release — verified against **`1.0.0-beta.17`** (no stable release yet; beta.7 was withdrawn the day it shipped). `cliVersion` in the config is only a **bind-time snapshot**. Before using a command whose semantics changed recently, read `<cliPath> version --format json` (beta9+; with `UNITY_FORMAT=json` set even `--version` prints a JSON object) and `<cliPath> <command> --help` — **`--help` is always the final authority** for the binary on disk, and since beta14 its footer lists every exit code. Search instead of guessing flags: `<cliPath> commands --grep <term>` (10–50 KB as json for one term; **never** bare `commands --json` — 1.4 MB). If a command errors unexpectedly, run `<cliPath> doctor --format json` first (`--tail <n>` for more log) and re-check `--help`. Never modify the server or config to work around a CLI quirk. Which commands you may run at all: **Allowed commands** near the end.

## 1. Cold start / lifecycle (`features.coldStart`)

The one capability REST can never provide: starting the editor when it is not running.

```bash
<cliPath> open "<projectPath>" --args "-unityskills-coldstart" --format json --non-interactive
```

**Always pass `--args "-unityskills-coldstart"`** when cold-starting: the UnitySkills plugin detects this marker at editor startup and force-starts the REST server for this session, even if the user's Auto-start preference is off. Without the marker you depend on the user's saved preference. The marker is consumed once per editor session — it never overrides a mid-session manual stop.

`open --format json` (beta12+) returns `data.launchedPid` + `data.editorPath` — keep the pid for the liveness check below. **beta14+: if an Editor already holds the project, `open` launches nothing, exits `0` and returns `data.alreadyOpen: true` + `data.pid`; the marker is NOT delivered to that Editor**, so a REST server the user stopped stays stopped (beta9–13 exit `6` instead).

**Preflight — read-only, no feature switch needed.** `open` / `test` / `run` / `build` all resolve the editor from the project's `ProjectVersion.txt`. Before the first CLI launch of a session run these checks; none of them modifies the project:

```bash
<cliPath> editors -i --format json                       # all versions: is the bound editorVersion installed?
<cliPath> doctor --ci --format json                      # beta6+: exit 6 = definitive blocker, 7 = retryable
<cliPath> projects verify "<projectPath>" --format json  # beta6+: exit 6 = errors, 0 = clean or warnings only
```

- `doctor --ci` (from the project root) checks licence, the project's editor, disk space and Unity services; branch on `errors[].code` (`LICENSE_NONE`, `EDITOR_NOT_INSTALLED`, `DISK_SPACE_LOW`, `NETWORK_UNREACHABLE`): `6` → stop and report, `7` → retry a few times, then tell the user. Plain `doctor` always exits `0`. `--skip-license` (beta15+) is for runners with their own licence — the user's call, not yours. beta6–8 on macOS may hang on a keychain prompt — wrap it in a shell timeout.
- `projects verify` needs no Editor, licence or network and only detects: `META_MISSING` / `META_ORPHAN` / `GUID_DUPLICATE` / `CONFLICT_MARKERS` / `MANIFEST_INVALID` exit `6` — report, never hand-repair; warnings (`PATH_UNVERIFIABLE`, `EDITOR_VERSION_DRIFT` via `--expect-editor <version>`) exit `0` unless `--strict`.

If the editor is not installed, **stop and tell the user** — never run `install` or pass `--allow-install`.

After launching, poll the UnitySkills REST server until ready (first import/compile can take minutes):

```python
from unity_skills import wait_for_health
health = wait_for_health(timeout=600)   # polls /health on ports 8090-8100
```

`open` exits `6` (`OPEN_EDITOR_EXITED`; `OPEN_EDITOR_EXITED_SIGNAL` beta10+) when the Editor dies inside the CLI's short watch window — report it at once; a later death is never observed, so a `wait_for_health` timeout remains the real failure signal. Skip `open --until-ready`: its readiness check is the Pipeline server, which UnitySkills projects may lack, so it just waits out `--timeout` (exit `10`); `/health` is the readiness signal.

**`wait_for_health` timed out — diagnose, do not relaunch.** One read-only call shows whether a dialog or Safe Mode holds the Editor:

```bash
<cliPath> status --format json --non-interactive --project-path "<projectPath>"
```

Branch on `errors[0].code` (envelope on stdout): `STATUS_BLOCKED_BY_DIALOG` (beta13+, `blockedBy` names it); beta14+ `STATUS_BLOCKED_BY_DIALOG_SAFE_MODE` (quotes the compile errors), `…_GRAPHICS_API`, `…_SOFTWARE_TERMS`, `STATUS_SAFE_MODE` (exit `6`). Relay the remedy (fix the compile errors, accept the terms or answer the upgrade dialog **in the Editor window**); never answer a dialog yourself, never `--accept-upgrades` / `terms accept`. `STATUS_NO_INSTANCES` (exit `12`) only means no Pipeline-backed Editor is visible. Limits: pre-Pipeline dialogs are matched by English title, and macOS needs the Screen Recording permission for the terminal or only "Project Upgrade Required" is seen.

**Liveness triage — prefer this over blind retry.** When REST is unreachable, determine whether an Editor is actually running before deciding to cold-start:

1. **Read the project's own `cli_config.json` first.** Only proceed if it exists and `enabled: true`.
2. **Check the UnitySkills registry**: read `~/.unity_skills/registry.json`, find the entry whose `path` equals the project root, then test its `pid` (or the `launchedPid` from `open`) with `ps -p <pid>` / Windows `tasklist`. Live pid → the editor is running but busy (Domain Reload / import) → keep the normal REST wait-and-retry; **do not** cold-start.
3. **Cross-check with the CLI process list**: `<cliPath> editors running --format json --non-interactive` (process table + Pipeline lockfiles, so it also sees Editors without the Pipeline package). A sandboxed agent shell may see nothing: an empty list is not proof.
4. **Check `Library/UnityLockfile`.** A held lockfile means an Editor is open on this project even if the registry entry is missing.
5. `<cliPath> status` is **supplementary, not authoritative**: it reads the Pipeline lockfile, so an editor without the Pipeline package (or a batch-mode one) shows nothing; an empty result / non-zero exit does **NOT** mean the editor is closed.

If the PID, the CLI process list, or the lockfile indicates an Editor is already running, **do not** `open`, `run`, `test` or `build`. Only when none of the above show a live Editor for this project should you cold-start with `open`, then `wait_for_health`.

## 2. Launch with arguments (`features.openArgs`)

```bash
<cliPath> open "<projectPath>" --args "-unityskills-coldstart -openscene Assets/Scenes/Main.unity"
```

`--args` takes **one** string, which the CLI splits like a shell — put every editor argument inside a single pair of quotes. Unquoted `--args -openscene Assets/...` is two arguments and fails (`INVALID_COMMAND_ARGS`, too many arguments). `-projectPath`, `-useHub`, `-hubIPC` are reserved and rejected before launch (exit `6`). Useful to land in a known state (specific scene, custom `-executeMethod`). Only at launch time — for an already-running editor use REST `scene_load` instead.

## 3. Headless tests (`features.cliTest`)

```bash
<cliPath> test "<projectPath>" --mode EditMode --filter <pattern> --output test-results.xml --timeout 1800
```

- `--mode <EditMode|PlayMode>` — omit to run the editor's default test platform; cover both modes with two separate invocations.
- `--filter <pattern>` — only run tests whose names match.
- `--output <path>` — NUnit XML report (default `test-results.xml`). In CI, continue to use this file for test results.
- **CI-only parameters** — `test --help` may list report/coverage flags (beta4+: `--report-format nunit,junit`, `--junit-output`, `--coverage*`) and, beta6+, `--shard N/M`, `--retries N` (re-runs only the failed tests, `--timeout` per attempt, flaky tests named in `test-results.retries.json`, never-passing tests still exit `8`) and `--rerun-failed` (reads the failing set from `--output`, writes `test-results.rerun.xml`). beta9+ `--affected --since <ref>` runs only the tests a change can reach (an approximation, writes `test-results.affected.xml`) — run `--affected-compare` first to measure its false-negative rate before trusting it. The selectors are mutually exclusive. Use a parameter only if the **current** binary's `test --help` lists it; never guess names.
- `--timeout <seconds>` (env `UNITY_TEST_TIMEOUT`; whole digits only since beta14) — kills the Unity process after N seconds; disabled by default, always set one for unattended runs.
- Extra editor arguments pass through after `--`, e.g. `-- -nographics`.
- **Exit codes**: `0` all passed (also when `--retries` rescued a flaky test — the flakiness is still reported). beta6+: `8` tests failed (`TESTS_FAILED`); `6` no verdict — compile error, licence or Editor crash (`TEST_RUN_ERROR`), or the project is already open in an Editor (the CLI refuses it). `--timeout` expiry (`TEST_TIMED_OUT`) is `6` on beta6–13 and **`14` on beta14+**. beta3–5: `6` covers failed tests too — read the XML. Full table in §6. Retry no-verdict codes at most a few times; never retry `8`.

Routing rule:

- **Interactive iteration** (editor already running, quick feedback on a few tests) → REST `test_*` skills.
- **Full regression / CI-style run, or editor closed** → `unity test` (headless, NUnit XML output). Never run `unity test` against a project whose editor is open.

## 4. Batch runs (`features.cliRun`)

One-shot batch automation on the **bound project only**, while the editor is closed — the third lifecycle option between REST (editor open, interactive) and cold start (launch and keep serving):

```bash
<cliPath> run "<projectPath>" --timeout 1800 -- -executeMethod Your.Static.Method
```

- Everything after `--` is forwarded to the Unity Editor. The CLI adds `-batchmode` and `-quit` itself: **never pass `-batchmode`, `-quit`, `-projectPath`, `-useHub` or `-hubIPC`** — they are reserved and fail before launch (exit `6`); with `--log-file`, `-logFile` is reserved too. `-executeMethod <static method>` (asset re-import, batch fixes, custom pipelines) is the typical payload.
- Streams the editor log to stdout and **returns the editor's exit code** — non-zero means the batch run failed. An Editor crash is reported as `stopped with signal SIG…` (beta5+, see §6) — an Editor crash, not a business-logic failure.
- `--timeout <seconds>` (env `UNITY_RUN_TIMEOUT`) — disabled by default; always set one, a hung batch editor otherwise blocks your shell forever. Expiry exits `6` (beta6–13) / `14` `BATCH_TIMEOUT` (beta14+).
- Routing: editor already running → use REST skills, never `run` (Unity refuses a second instance on the same project — same `Library/UnityLockfile` rule as cold start). Editor closed + persistent session needed → cold start. Editor closed + one-shot task → `run`.
- `run --command <name>` drives Unity Pipeline package commands — not part of this workflow.

## 5. Headless builds (`features.cliBuild`)

`cliBuild` is **off by default**. When the user enables it, choose the build path based on the installed CLI version, Unity Editor version, target platform, and whether a Build Profile exists.

**Never build while the Editor is alive.** beta9–15 the CLI refuses a project that is open in an Editor; **since beta16 `build --target` instead builds *through* the open Editor and switches its active build target** (`viaRunningEditor: true`), and `--timeout` / Ctrl+C only stop the wait. Run the §1 liveness triage first; Editor alive or unsure → do not call `build`, use REST. Discover valid values instead of guessing: `<cliPath> build --list-targets` and `<cliPath> build --list-profiles` only list and exit (`--create-profile` writes an asset — not used).

### Path 1 — Unity 6 Build Profile (beta4+)

```bash
<cliPath> build "<projectPath>" --profile "Windows" --output-path ./Builds/win64
```

- `--profile` accepts a Build Profile asset path or the profile name shown in the Build Profile window. Requires Unity 6 (`6000.x`) and a configured Build Profile.

### Path 2 — Built-in desktop build (beta4+)

```bash
<cliPath> build "<projectPath>" --target StandaloneWindows64 --output-path ./Builds/win64
```

- `--target` is required (without `--profile`); for desktop targets `--execute-method` is **optional** (beta4+) when `--output-path` is provided.

### Path 3 — Custom C# build method (all versions, required for mobile/WebGL)

```bash
<cliPath> build "<projectPath>" --target Android --execute-method Builder.PerformBuild --output-path ./Builds/android
```

- `--execute-method` takes priority when provided; use it for Android, iOS, WebGL, or projects that already have a custom build pipeline. `--output-path` is forwarded to Unity as `-buildOutput`; the execute-method itself must read it.
- If the project does not already contain a static build method, tell the user instead of writing one into their project unasked.

### Common build flags

- The build log tails to stdout by default (`--no-tail` to disable); the full log lands at `<project>/Logs/build-<target>-<timestamp>.log` unless `--log-file` overrides it. beta6+ writes `<output>.provenance.json` beside the artifact (`--no-provenance` to skip). beta17 removed the short forms `-o` / `-l` — write `--output-path` / `--log-file`.
- `--timeout <seconds>` (env `UNITY_BUILD_TIMEOUT`, beta6+) — aborts the build; disabled by default, always set one. Exit `6` (beta6–13) / `14` `BATCH_TIMEOUT` (beta14+). json/ndjson carry heartbeat frames (elapsed, seconds since the log last grew): a stalled Editor is a growing gap, not silence.
- **Dirty-worktree guard**: `build` refuses to run with uncommitted changes. That protection is deliberate — pass `--allow-dirty-build` only when the user explicitly says so.
- `--versioning-strategy <semantic|tag|custom|none>` (default `none`) stamps the version from git; `--build-version` applies only with `custom`.
- Android: `--android-export-type <apk|aab|android-studio-project>` and keystore flags exist (beta15+ also reads `UNITY_ANDROID_KEYSTORE_PASSWORD` / `UNITY_ANDROID_KEY_ALIAS_PASSWORD`), but the Editor still receives the passwords on its command line — let the user handle signing; never ask them to paste keystore passwords into your commands.
- Same lifecycle rules as `run`: bound project only; once the editor is up again all normal operations go back through REST.
- **Exit codes**: `0` success; `6` build failed or Editor exited abnormally (+ `--timeout` on beta6–13); `14` timeout (beta14+); `7` services unreachable; `130` / `143` signals. A crash is `stopped with signal SIG…` (beta5+) — not `BUILD_FAILED`.

## 6. Automation contract (all commands)

- **Structured output**: `--format <human|json|tsv|ndjson>` (env `UNITY_FORMAT`); `--json` is shorthand for `--format json`. When stdout is piped the default silently becomes TSV — always pass `--format json --non-interactive`. JSON envelope: `{success, command, data, errors, warnings}` (+ `notifications` when present, beta12+); listings return `data.items` (beta15+).
- **stdout vs stderr**: a failed command still writes a complete envelope to **stdout** (`success: false`; `errors[0].code` is the stable token to branch on); stderr only carries human diagnostics and, under Claude Code, a `<claude-code-hint … />` line (beta14+). **Capture the streams separately and parse stdout only — never `2>&1`** (the hint breaks JSON parsing with `Extra data`).
- **Non-interactive**: `--non-interactive` turns prompts into hard errors instead of hanging your shell; add `--quiet` and `--no-banner` for clean output.
- **Recommended environment** (advisory): `UNITY_NON_INTERACTIVE=1`, `UNITY_FORMAT=json` (`--format` wins), `UNITY_NO_PAGER=1`; `UNITY_NO_UPDATE_CHECK=1` (no "update available" notifications) and `UNITY_NO_AGENT_HINTS=1` (no agent plugin/skill suggestions: the stderr hint, `AGENT_PLUGIN_AVAILABLE`, `AGENT_SKILLS_OUTDATED`; beta14+); `UNITY_NO_CRASH_REPORT=1` and `UNITY_NO_CLI_INVOKED_TELEMETRY=1` (beta9+; `DO_NOT_TRACK` also stops the ping since beta15). Always pass the bound project path explicitly — `UNITY_PROJECT_PATH` or the working directory can silently select another project's Editor.
- **Notifications and hints are relayed, never executed.** `notifications[]` entries (e.g. `CLI_UPDATE_AVAILABLE` with `remediation.command: "unity self-update"`), "Tip / suggest" lines and agent hints go to the user as text; `requiresUserApproval: true` means do not run `remediation.command` yourself. Never pass `--yes` / `-y`, `--accept-eula`, `--accept-upgrades` or `--force` on your own.
- **Exit codes** (beta6–13 baseline; clauses tagged beta14+/beta16+ apply to those binaries only):

| Exit code | Meaning |
|-----------|---------|
| `0` | Success. |
| `1` | Generic error — read the envelope `errors`. |
| `2` | Usage or argument error (`INVALID_COMMAND_ARGS`: wrong command, missing flag, too many arguments). |
| `3` | Authentication or authorization failure. |
| `4` | `NEEDS_CONFIG` — a required tool, setting or link is missing. |
| `6` | The command failed for a reason no more specific code names: build failed, Editor exited abnormally or crashed, `test` without a verdict, `doctor --ci` definitive blocker, `projects verify` errors, `open` Editor died (`OPEN_EDITOR_EXITED*`) or stopped by a dialog (`OPEN_BLOCKED_BY_DIALOG*`, beta14+), a refused already-open project. **beta6–13 also: `--timeout` expired.** |
| `7` | Unity services unreachable after CLI retries (beta16+: any offline / DNS / proxy failure), or `doctor --ci` reached no verdict; retry a limited number of times. |
| `8` | Tests failed (`test`, beta6+); never retry. beta3–5 report this as `6`. |
| `10`–`14` | beta14+: `10` `open --until-ready` not ready in time; `11` project / release / module not found; `12` no running Editor answered (not proof the Editor is closed); `13` the Editor has no such command; `14` timeout (`--timeout` of run / test / build, Editor or HTTP request). |
| `17` / `18` | beta16+: Editor busy (loading, compiling, Safe Mode) / blocked by a modal dialog. |
| `130` / `143` | Cancelled by SIGINT / terminated by SIGTERM (`141`: stdout reader closed). |

  `6` and `8` are terminal; `7`, `10`, `12`, `14`, `17` may be transient (beta14+ `--help` marks retryable codes with `*`). **A code you do not know → stop and report it**: the footer of `<cliPath> --help` (beta14+) lists every code (also `exitCodes` in `commands --json` — read the footer, not the 1.4 MB manifest).
- **NDJSON streaming**: `--format ndjson` for real-time progress. The stream ends with exactly one terminal `{"type":"result","success":…}` frame (beta4+ for build/test/run, every command since beta15; failures too) — the **last** frame is the final state; listing rows are `"type":"row"` (beta15+). beta3 has no result frame: use the exit code.
- **Signal crashes (beta5+)**: `build`/`test`/`run` report an Editor crash as `stopped with signal SIGSEGV/SIGILL/SIGTRAP/SIGFPE/SIGBUS` — distinct from business failures (`TESTS_FAILED`, `BUILD_FAILED`) and CLI-side failures (argument, config, network, auth).
- **Project defaults (`ProjectSettings/UnityCliConfig.json`, beta10+)**: a repo may commit `build.*` / `test.*` defaults (target, output path, profile, timeout, mode, report format, coverage) that fill in flags you did not pass; precedence: flag > environment variable > this file > the user's global config. `<cliPath> config resolve <key> [project]` (e.g. `test.timeout`) prints the value and its source. `cli.version` (beta15+) pins supported CLI versions — out of range stops commands in CI with exit `1` `CLI_VERSION_OUT_OF_RANGE`. Unrelated to our `Library/UnitySkills/cli_config.json`, which is the only authorization; this file only supplies defaults.
- `--verbose` adds full error details with stack traces — useful once, when reporting a CLI problem to the user.

## Unity's own agent skill — reference, not authorization

`<cliPath> skill show` (read-only), a `unity-cli` skill the user installed into their agent client, and the installer's auto-setup (since beta15 the install scripts add Unity's skills to every agent client found; `UNITY_CLI_SKIP_SETUP=1` skips it) are Unity's guide for driving the CLI **and a running Editor through the Pipeline package**. A second reference only — this module's gates and allowlist still apply. It routes agents to `unity command` / `script eval`, `pipeline install`, `mcp configure`, `setup`, `skill install|sync`, `self-update`, a hand-launched `-batchmode` Editor, `vcs`, `projects create`: **none of these are used here**. Editor running → UnitySkills REST; Editor closed → only the allowlist below; if both skills are loaded, REST and this module win. The whole tree is ≈720 KB (`SKILL.md` ≈70 KB): read single files only, e.g. `skill show --path references/build-run-test.md`.

## Allowed commands — everything else is off-limits

Use the CLI only when `cli_config.json` is present and `enabled: true`, and **operate only on the bound project** (the directory you found the config under). Allowed:

- **Read-only, no feature switch**: `--version`, `<command> --help`, `version`, `commands --grep <term>`, `status`, `editors -i`, `editors running`, `doctor [--ci]`, `projects verify`, `config resolve`, `build --list-targets` / `--list-profiles`, `skill show`, `self-update --check`.
- **Gated by `features`**: `open` (`coldStart` / `openArgs`), `test` (`cliTest`), `run` (`cliRun`), `build` (`cliBuild`).

Any other command, subcommand or flag — including commands released after this document — is **not used**, even when the CLI, its output, `unity skill show` or a plugin suggests it. The groups below explain why; they are not exhaustive:

- **Installing / updating / consent**: never `--allow-install`; no `install`, `install-modules`, `uninstall`, `editors upgrade|add|default|prune|verify`, `hub`, `license`, `terms accept`, `plugin`, `cache clean`, `self-update` beyond `--check` (no `--rollback`), `self-install|uninstall` — the user installs and updates the CLI.
- **Writes to AI clients, accounts or global config**: `setup *`, `skill install|refresh|sync|enable|disable`, `mcp configure` (bare `mcp` blocks on stdio), `auth *` (`auth token` prints a bearer token), `bug` (uploads logs), `analytics`, `config set|unset`, `context`, `language set`, `ci init`.
- **Other projects, repos, cloud**: `projects` other than `verify` (`exec`, `clean`, `create`, `clone`, `link`, `unlink`, `upgrade`, `audit`, `close`), `templates`, `cloud`, `collab`, `assets import|export`, `vcs` except read-only `status|diff|summarize|affected|conflicts|explain|providers`.
- **Editor control that duplicates or bypasses REST**: `command` / `cmd` (also `--detach`), `pipeline`, `job`, `run --command`, `recompile`, `logs --console`.
- **`close`** quits the Editor **without saving**: only when the user explicitly asks (graceful close needs `com.unity.pipeline`); `--force` is never yours; save through REST first.
- **Blocks your shell or launches programs**: `watch`, `shell`, `logs --follow`, `editors --watch`, `open --wait|--batchmode|--until-ready`, `build run`, `docs <topic>` (opens a browser).
- **Rewrites the project**: `open --accept-upgrades` (upgrades and rewrites scripts without a prompt), `build --create-profile`.
- Never parse human-readable output (its language follows `unity language`). Once `/health` responds, all normal operations go through REST skills.

## 7. Linux compatibility and version branches

Starting with beta4, the Unity CLI requires **glibc 2.34+** on Linux and no longer supports Ubuntu 20.04 and earlier, Debian 11, RHEL/CentOS 8, or Amazon Linux 2 (no change through beta17). On those systems the CLI may fail to start with a glibc-related error rather than a normal "command not found". Do **not** treat this as "CLI not installed" and prompt the user to reinstall; surface the compatibility constraint (stay on beta3, upgrade the OS, or use another machine). There is no global minimum version enforced by UnitySkills — capabilities are branched by actual CLI behavior:

- **beta3** — old `build` semantics (`--execute-method` required), no NDJSON terminal result frame. **beta4+** — Build Profile support, built-in desktop build, NDJSON `type=result` frames. **beta5+** — named Editor signal diagnostics.
- **beta6+** — `test` exit `8` = failed / `6` = no verdict; `build --timeout`; `doctor --ci`; `projects verify`; `test --shard/--retries/--rerun-failed`; provenance manifest.
- **beta8+** — `close`, `vcs` / `plugin` groups; `upgrade` renamed `self-update`; `projects create` makes Unity Cloud projects by default.
- **beta9+** — `open` exits `6` on early Editor death (older: always `0`); `build` refuses an already-open project; `test --affected`; read-only `skill show`; `version --format json`; `doctor` no longer hangs on macOS keychain prompts.
- **beta10–13** — `commands`, `recompile`, `docs`, `watch`, `setup`, `open --wait`, `build run|--list-*`, `ProjectSettings/UnityCliConfig.json`; `status --until-ready`, `STATUS_BLOCKED_BY_DIALOG`; `open --format json`; json `notifications`; run/test exit `130`/`143`.
- **beta14+** — exit codes `10`–`14` (timeouts move from `6` to `14`); `--timeout` whole digits only; `open` on an open project exits `0` (`alreadyOpen`); Safe Mode / dialog `status` codes; agent hints on stderr; `terms`, `open --accept-upgrades|--batchmode|--until-ready` (not used).
- **beta15+** — `unity list`, `projects list|new` and other `list` subcommands removed; every ndjson stream ends with a result frame, rows are `"type":"row"`; listings are `data.items`; `cli.version` pin; installer auto-setup of agent skills.
- **beta16+** — exit `17` / `18`; **`build --target` borrows an open Editor**; `skill sync`. **beta17** — `-o` / `-l` short flags of `build` / `run` removed.

The safest way to branch is `<cliPath> version --format json` (beta9+) and `<cliPath> <command> --help` at the start of a session; act on what the binary actually reports.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
