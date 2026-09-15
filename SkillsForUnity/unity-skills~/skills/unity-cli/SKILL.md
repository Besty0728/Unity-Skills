---
name: unity-cli
description: Guide for using the experimental Unity CLI with bound UnitySkills projects
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

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
  "cliPath": "/Users/me/.local/bin/unity",
  "cliVersion": "1.0.0-beta.5",
  "projectPath": "/path/to/Project",
  "editorVersion": "6000.0.32f1",
  "boundAt": "2026-07-26T09:00:00Z",
  "features": { "coldStart": true, "openArgs": true, "cliTest": true, "cliRun": false, "cliBuild": false }
}
```

Configs written by older plugin versions may lack the `cliRun` / `cliBuild` keys — for these two, **a missing key means OFF** (the first three keys keep their original semantics). Both also default to off on fresh binds; the user enables them per project in the panel.

The global registry (`~/.unity_skills/registry.json`) also carries `cliBound` / `cliPath` per running instance — use it for **liveness checks only, never as authorization**: the ONLY thing that authorizes CLI use for a project is that project's own `cli_config.json`. Do not cold-start any project whose own config you have not read, even if it appears in the registry. Also note `projectPath` inside the config is a bind-time snapshot — the directory you actually found the config under is authoritative (helper `get_cli_config()` already rewrites it); never `open` the stored path if it differs from the real project root.

> Unity CLI is **experimental (beta)** and its command surface changes between releases — this document was verified against **`1.0.0-beta.9`** (beta.7 was withdrawn the same day it shipped). `cliVersion` in the config is only a **bind-time snapshot**; the installed binary moves ahead of it. Before using a command whose semantics changed recently, read `<cliPath> --version` and `<cliPath> <command> --help` — **`--help` is always the final authority** for the binary on disk. beta9+ `<cliPath> skill show --list` / `--path <file>` prints Unity's own agent skill read-only (offline, ~44k tokens in total — read one file at a time) — a second reference, never an authorization; this module's gates and DO NOT list still apply. If a command errors unexpectedly, run `<cliPath> doctor --format json` first (environment snapshot; `--tail <n>` for more log) and re-check `--help` before retrying. Never modify the server or config to work around a CLI quirk.

## 1. Cold start / lifecycle (`features.coldStart`)

The one capability REST can never provide: starting the editor when it is not running.

```bash
<cliPath> status --format json          # any editor instances running?
<cliPath> open "<projectPath>" --args -unityskills-coldstart
```

**Always pass `--args -unityskills-coldstart`** when cold-starting: the UnitySkills plugin detects this marker at editor startup and force-starts the REST server for this session, even if the user's Auto-start preference is off. Without the marker you depend on the user's saved preference. The marker is consumed once per editor session — it never overrides a mid-session manual stop.

**Preflight — read-only, no feature switch needed.** `open` / `test` / `run` / `build` all resolve the editor from the project's `ProjectVersion.txt`. Before the first CLI launch of a session run these checks; none of them modifies the project:

```bash
<cliPath> editors -i --format json                       # all versions: is the bound editorVersion installed?
<cliPath> doctor --ci --format json                      # beta6+: exit 6 = definitive blocker, 7 = retryable
<cliPath> projects verify "<projectPath>" --format json  # beta6+: exit 6 = errors, 0 = clean or warnings only
```

- `doctor --ci` (from the project root) checks licence, the project's editor, disk space and Unity services; branch on `errors[].code` (`LICENSE_NONE`, `EDITOR_NOT_INSTALLED`, `DISK_SPACE_LOW`, `NETWORK_UNREACHABLE`): `6` → stop and report, `7` → retry a few times, then tell the user. Plain `doctor` always exits `0`. beta6–8 on macOS may hang on a keychain prompt — wrap it in a shell timeout.
- `projects verify` needs no Editor, licence or network and only detects: `META_MISSING` / `META_ORPHAN` / `GUID_DUPLICATE` / `CONFLICT_MARKERS` / `MANIFEST_INVALID` exit `6` — report, never hand-repair; warnings (`PATH_UNVERIFIABLE`, `EDITOR_VERSION_DRIFT` via `--expect-editor <version>`) exit `0` unless `--strict`.

If the editor is not installed, **stop and tell the user** — never run `install` or pass `--allow-install` (see DO NOT).

After launching, poll the UnitySkills REST server until ready (first import/compile can take minutes):

```python
from unity_skills import wait_for_health
health = wait_for_health(timeout=600)   # polls /health on ports 8090-8100
```

beta9+: `open` exits `6` (`OPEN_EDITOR_EXITED`) when the Editor dies inside the CLI's short watch window — report it at once instead of waiting on `wait_for_health`. Only that window is covered: an Editor that quits later is never observed, and older binaries exit `0` regardless, so a `wait_for_health` timeout remains the real failure signal.

**Liveness triage — prefer this over blind retry.** When REST is unreachable, determine whether an Editor is actually running before deciding to cold-start:

1. **Read the project's own `cli_config.json` first.** Only proceed if it exists and `enabled: true`.
2. **Check the UnitySkills registry**: read `~/.unity_skills/registry.json`, find the entry whose `path` equals the project root, then test its `pid` (`ps -p <pid>` / Windows `tasklist`). Live pid → the editor is running but busy (Domain Reload / import) → keep the normal REST wait-and-retry; **do not** cold-start.
3. **Cross-check with the CLI process list**: `<cliPath> editors running --format json --non-interactive` lists the Editor instances the CLI can see. This is useful confirmation, but it only sees instances the CLI's backend recognizes.
4. **Check `Library/UnityLockfile`.** A held lockfile means an Editor instance is open on this project even if the registry entry is missing.
5. `<cliPath> status` is **supplementary, not authoritative**: it only reflects Unity Pipeline connection state. An empty table / non-zero exit does **NOT** mean the editor is closed — verified in practice: a running editor without the Pipeline package shows nothing.

If the PID, the CLI process list, or the lockfile indicates an Editor is already running, **do not** `open`, `run`, or `build` — Unity refuses a second instance on the same project. Only when none of the above show a live Editor for this project should you cold-start with `open`, then `wait_for_health`.

## 2. Launch with arguments (`features.openArgs`)

```bash
<cliPath> open "<projectPath>" --args -openscene "Assets/Scenes/Main.unity"
```

Anything after `--args` is passed to the Unity Editor as standard command-line arguments. Useful to land in a known state (specific scene, custom `-executeMethod`). Only at launch time — for an already-running editor use REST `scene_load` instead.

## 3. Headless tests (`features.cliTest`)

```bash
<cliPath> test "<projectPath>" --mode EditMode --filter <pattern> --output test-results.xml --timeout 1800
```

- `--mode <EditMode|PlayMode>` — omit to run the editor's default test platform; cover both modes with two separate invocations.
- `--filter <pattern>` — only run tests whose names match.
- `--output <path>` — NUnit XML report (default `test-results.xml`). In CI, continue to use this file for test results.
- **CI-only parameters** — `test --help` may list report/coverage flags (beta4+: `--report-format nunit,junit`, `--junit-output`, `--coverage*`) and, beta6+, `--shard N/M`, `--retries N` (re-runs only the failed tests, `--timeout` per attempt, flaky tests named in `test-results.retries.json`, never-passing tests still exit `8`) and `--rerun-failed` (reads the failing set from `--output`, writes `test-results.rerun.xml`). beta9+ `--affected --since <ref>` runs only the tests a change can reach (an approximation, writes `test-results.affected.xml`) — run `--affected-compare` first to measure its false-negative rate before trusting it. The selectors are mutually exclusive. Use a parameter only if the **current** binary's `test --help` lists it; never guess names.
- `--timeout <seconds>` (env `UNITY_TEST_TIMEOUT`) — kills the Unity process after N seconds; disabled by default, always set one for unattended runs.
- Extra editor arguments pass through after `--`, e.g. `-- -nographics`.
- **Exit codes**: `0` all passed (also when `--retries` rescued a flaky test — the flakiness is still reported). **beta6+**: `8` tests failed (`errors[0].code` `TESTS_FAILED`); `6` no verdict — compile error, licence or Editor crash (`TEST_RUN_ERROR`), `--timeout` expiry (`TEST_TIMED_OUT`). **beta3–5**: `6` covers both — read the XML to tell them apart. `7` Unity services unreachable after CLI retries; `130` SIGINT; `143` SIGTERM; any other non-zero = the command itself failed — check stderr, the JSON envelope and `errors[].code`, not just the XML. Retry `6`/`7` if at all; never retry `8`.

Routing rule:

- **Interactive iteration** (editor already running, quick feedback on a few tests) → REST `test_*` skills.
- **Full regression / CI-style run, or editor closed** → `unity test` (headless, NUnit XML output). Do not run `unity test` against a project whose editor is open.

## 4. Batch runs (`features.cliRun`)

One-shot batch automation on the **bound project only**, while the editor is closed — the third lifecycle option between REST (editor open, interactive) and cold start (launch and keep serving):

```bash
<cliPath> run "<projectPath>" --timeout 1800 -- -executeMethod Your.Static.Method -quit
```

- Everything after `--` is forwarded to the Unity Editor as standard command-line arguments; `-executeMethod <static method>` plus `-quit` is the typical shape (asset re-import, batch fixes, custom pipelines).
- Streams the editor log to stdout and **returns the editor's exit code** — non-zero means the batch run failed. beta5+ reports an Editor crash as `stopped with signal SIG…` (see §6) — an Editor crash, not a business-logic failure.
- `--timeout <seconds>` (env `UNITY_RUN_TIMEOUT`) — disabled by default; always set one, a hung batch editor otherwise blocks your shell forever.
- Routing: editor already running → use REST skills, never `run` (Unity refuses a second instance on the same project — same `Library/UnityLockfile` rule as cold start). Editor closed + persistent session needed → cold start. Editor closed + one-shot task → `run`.
- `run --command <name>` drives Unity Pipeline package commands — not part of this workflow (see DO NOT).

## 5. Headless builds (`features.cliBuild`)

`cliBuild` is **off by default**. When the user enables it, choose the build path based on the installed CLI version, Unity Editor version, target platform, and whether a Build Profile exists:

### Path 1 — Unity 6 Build Profile (beta4+)

```bash
<cliPath> build "<projectPath>" --profile "Windows" --output-path ./Builds/win64
```

- `--profile` accepts a Build Profile asset path or the profile name shown in the Build Profile window.
- Requires Unity 6 (`6000.x`) and a configured Build Profile.

### Path 2 — Built-in desktop build (beta4+)

```bash
<cliPath> build "<projectPath>" --target StandaloneWindows64 --output-path ./Builds/win64
```

- `--target` is still required.
- For desktop targets, `--execute-method` is **optional** starting with beta4; the CLI can drive the built-in desktop build when `--output-path` is provided.

### Path 3 — Custom C# build method (all versions, required for mobile/WebGL)

```bash
<cliPath> build "<projectPath>" --target Android --execute-method Builder.PerformBuild --output-path ./Builds/android
```

- `--execute-method` remains compatible and **takes priority** when provided; use it for Android, iOS, WebGL, or projects that already have a custom build pipeline.
- `--output-path` is forwarded to Unity as `-buildOutput`; the execute-method itself is responsible for reading it.
- If the project does not already contain a static build method, tell the user instead of writing one into their project unasked.

### Common build flags

- The build log tails to stdout by default (`--no-tail` to disable); the full log lands at `<project>/Logs/build-<target>-<timestamp>.log` unless `--log-file` overrides it. beta6+ also writes `<output>.provenance.json` beside the artifact — what was built, failed builds too (`--no-provenance` to skip).
- `--timeout <seconds>` (env `UNITY_BUILD_TIMEOUT`, beta6+) — aborts the build and exits `6`; disabled by default, always set one for unattended builds. beta6+ json/ndjson also carry heartbeat frames (elapsed, and seconds since the log last grew — even with `--quiet`): a stalled Editor is a growing gap, not silence.
- **Dirty-worktree guard**: `build` refuses to run with uncommitted changes. That protection is deliberate — pass `--allow-dirty-build` only when the user explicitly says so.
- `--versioning-strategy <semantic|tag|custom|none>` (default `none`) stamps the build version from git tags/history; `--build-version` applies only with `custom`.
- Android: `--android-export-type <apk|aab|android-studio-project>` and keystore/signing flags exist, but secrets on the command line leak into shell history and CI logs — let the user handle signing; never ask them to paste keystore passwords into your shell commands.
- Same lifecycle rules as `run`: bound project only, editor must be closed, and once the editor is up again all normal operations go back through REST.
- **Exit codes / signals**: `0` success; `6` build failed, `--timeout` expired (beta6+), or the Editor exited abnormally; `7` Unity services unreachable after retries; `130` SIGINT; `143` SIGTERM. beta5+ reports an Editor crash as `stopped with signal SIG…` (see §6) — treat that separately from `BUILD_FAILED`.

## 6. Automation contract (all commands)

- **Structured output**: `--format <human|json|tsv|ndjson>` (env `UNITY_FORMAT`); `--json` is shorthand for `--format json`. When stdout is piped the default silently becomes TSV — one more reason to always pass `--format json` explicitly. JSON responses use a standard envelope `{success, command, data, errors, warnings}`; `ndjson` streams progress events for long-running commands.
- **Non-interactive**: `--non-interactive` (env `UNITY_NON_INTERACTIVE`) turns prompts into hard errors instead of hanging your shell; combine with `--quiet` (env `UNITY_QUIET`) and `--no-banner` (env `UNITY_NO_BANNER`) for clean machine output.
- **Recommended environment variables for scripted calls** (advisory, equivalent to the flags above):
  - `UNITY_NON_INTERACTIVE=1` — disable interactive prompts.
  - `UNITY_FORMAT=json` — default output format; command-line `--format` wins if both are set.
  - `UNITY_NO_PAGER=1` — never page long output (beta4+ uses a pager in interactive terminals).
  - `UNITY_NO_CRASH_REPORT=1` — opt out of anonymous crash reporting via Sentry (beta3+).
  - `UNITY_NO_CLI_INVOKED_TELEMETRY=1` — opt out of the per-run usage ping (beta9+; separate from the analytics opt-in).
- **Errors and exit codes**: data goes to stdout, errors/diagnostics to stderr (JSON-mode errors too). Always read the JSON envelope, `errors[].code`, and stderr together — do not rely on a single exit code.

| Exit code | Meaning |
|-----------|---------|
| `0` | Success. |
| `1` | Generic error — read stderr / JSON `errors`. |
| `2` | Usage or argument error (wrong command, missing required flag). |
| `3` | Authentication or authorization failure. |
| `4` | Missing required configuration. |
| `6` | Primary operation failed or reached no verdict — build failed, `--timeout` expired (beta6+), Editor exited abnormally or crashed, `test` without a verdict (beta6+; beta3–5 also put failed tests here), `doctor --ci` definitive blocker, `projects verify` errors, `open` Editor died in the watch window (beta9+, `OPEN_EDITOR_EXITED`). |
| `7` | Unity services unreachable after CLI retries, or `doctor --ci` reached no verdict (beta6+); retry a limited number of times. `6` is terminal, `7` may be transient. |
| `8` | Tests failed (`test`, beta6+, `TESTS_FAILED`); never retry. beta3–5 report this as `6`. |
| `130` | Cancelled by user (SIGINT / Ctrl+C). |
| `143` | Terminated by SIGTERM. |

- **NDJSON streaming**: for real-time progress use `--format ndjson`. Since beta4 the stream ends with a terminal `type=result` frame — use the **last** `{"type":"result","success":...}` frame as the final state, never an earlier progress frame or a stderr line; beta3 emits no result frame, fall back to the exit code plus stderr.
- **Signal crashes (beta5+)**: `build`/`test`/`run` report an Editor crash as `stopped with signal SIGSEGV/SIGILL/SIGTRAP/SIGFPE/SIGBUS`. Distinguish these from business failures (`TESTS_FAILED` on beta6+ / `TEST_FAILED` before, `BUILD_FAILED`) and from CLI-side failures (argument, config, network, auth).
- `--verbose` adds full error details with stack traces — useful once, when reporting a CLI problem to the user.

## DO NOT

- Do not use the CLI when `cli_config.json` is absent or `enabled:false` — the user has not opted in. Operate **only on the bound project** (the directory you found the config under); never `open` / `test` / `run` / `build` any other project.
- Do not install the Unity CLI yourself; installation is a user decision made in the panel.
- **Never pass `--allow-install`** (accepted by `test` / `run` / `build`), and do not use `unity install` / `install-modules` / `uninstall` / `editors upgrade` / `hub` / license commands unless the user explicitly asks — installing, upgrading or removing editors is a large, slow, system-changing operation that belongs to the user alone.
- Do not run bare `unity mcp` — it starts a blocking stdio MCP server and waits for a client, hanging your shell.
- Do not use `unity command` / `unity pipeline` / `unity run --command` — the Unity Pipeline package route duplicates what UnitySkills REST already provides and is not part of this workflow.
- Do not use `unity projects exec` or `unity projects clean` — these operate across multiple projects and violate the "only touch the bound project" boundary.
- Do not use `unity projects create|new|clone|link|unlink|upgrade`, `unity templates`, `unity cloud`, `unity collab`, or any `unity vcs` verb other than the read-only `status` / `diff` / `summarize` / `affected` / `conflicts` / `explain` / `providers` — they create, re-home or rewrite projects and repositories (local, remote, or Unity Cloud — `projects create` is cloud-enabled by default on beta8+), outside the bound-project boundary.
- Do not use `unity close` / `projects close` (beta8+) unless the user explicitly asks — it quits the Editor **without saving**; the graceful path needs `com.unity.pipeline` (`pipeline_missing` otherwise → hand the close back to the user) and `--force` (SIGTERM→SIGKILL) is never yours to pass. Save through REST first.
- Do not use `unity editors prune` or `unity editors verify` — these modify or validate the global Editor installation set and are outside the project lifecycle.
- Do not use `unity command --detach`, `unity eval --detach`, or `unity job` — detached commands and job queues are a different control channel from the bound-project workflow.
- Do not use `unity skill install` or `unity skill refresh` — they write into an AI client's config directory, and the official skill can bypass UnitySkills' own advisory and feature gates; skill installation is a user decision in the panel. Reading it with `unity skill show` (beta9+) is fine — see Gate.
- Do not use `unity shell --protocol ndjson` — it is the beta3 warm-process machine protocol, equivalent to the official MCP route, and is not part of this workflow.
- Do not let the CLI update itself (`unity self-update`, alias `upgrade`; `self-install` / `self-uninstall`; Homebrew, winget, etc.) or manage its bundled tools (`unity plugin install|remove|upgrade`, beta8+) — updating and installing are user decisions.
- Do not parse the CLI's human-readable output (its display language follows `unity language`, e.g. Chinese table headers) — always pass `--format json --non-interactive` when you need to read results programmatically.
- Do not treat CLI availability as a substitute for the REST workflow: once `/health` responds, all normal operations go through REST skills.

## 7. Linux compatibility note

Starting with beta4, the Unity CLI requires **glibc 2.34+** on Linux and no longer supports Ubuntu 20.04 and earlier, Debian 11, RHEL/CentOS 8, or Amazon Linux 2. On those systems the CLI may fail to start with a glibc-related error rather than a normal "command not found".

Do **not** treat this as "CLI not installed" and prompt the user to reinstall. Instead, surface the compatibility constraint: the installed binary cannot run on this OS/glibc version; options are to stay on beta3 capabilities, upgrade the OS, or use a different machine. There is no global minimum version enforced by UnitySkills — capabilities are branched by actual CLI behavior:

- **beta3** — old `build` semantics (`--execute-method` required), no NDJSON terminal result frame.
- **beta4+** — Build Profile support, built-in desktop build, NDJSON terminal `type=result` frames.
- **beta5+** — named Editor signal diagnostics (`SIGSEGV`, `SIGILL`, `SIGTRAP`, `SIGFPE`, `SIGBUS`).
- **beta6+** — `test` exit `8` = failed / `6` = no verdict; `build --timeout`; `doctor --ci`; `projects verify`; `test --shard/--retries/--rerun-failed`; provenance manifest `<output>.provenance.json`.
- **beta8+** — `unity close` and the `vcs` / `plugin` command groups (see DO NOT); `unity upgrade` renamed `self-update` (alias kept); `projects create` makes Unity Cloud projects by default.
- **beta9+** — `open` exits `6`/`OPEN_EDITOR_EXITED` on early Editor death (older: always `0`); `build` refuses an already-open project; `test --affected`; read-only `skill show`; `version --format json`; `UNITY_NO_CLI_INVOKED_TELEMETRY=1`; `doctor` no longer hangs on macOS keychain prompts; `projects verify` no longer flags native plug-in folders (`.bundle/.framework/.androidlib/.plugin`).

The safest way to branch is to run `<cliPath> --version` (beta9+: `version --format json`) and `<cliPath> <command> --help` at the start of a session and act on what the binary actually reports.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
