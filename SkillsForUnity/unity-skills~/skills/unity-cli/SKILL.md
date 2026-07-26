---
name: unity-cli
description: Advisory guidance for using the experimental Unity CLI (the official `unity` command-line tool) alongside UnitySkills — cold-start a bound project without Unity Hub, probe editor liveness, launch with arguments, run headless tests, run one-shot batch automation, and build headlessly. Only applies when the project has been bound in the UnitySkills panel (Library/UnitySkills/cli_config.json exists with enabled:true). 实验性 Unity CLI(官方 unity 命令行工具)与 UnitySkills 协同的指导文档——免 Unity Hub 冷启动已绑定项目、探测编辑器存活、传参启动、无头测试、批处理运行、无头构建;仅当项目已在 UnitySkills 面板完成绑定(存在 Library/UnitySkills/cli_config.json 且 enabled:true)时适用。
---

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
  "cliVersion": "1.0.0-beta.3",
  "projectPath": "/path/to/Project",
  "editorVersion": "6000.0.32f1",
  "boundAt": "2026-07-26T09:00:00Z",
  "features": { "coldStart": true, "openArgs": true, "cliTest": true, "cliRun": false, "cliBuild": false }
}
```

Configs written by older plugin versions may lack the `cliRun` / `cliBuild` keys — for these two, **a missing key means OFF** (the first three keys keep their original semantics). Both also default to off on fresh binds; the user enables them per project in the panel.

The global registry (`~/.unity_skills/registry.json`) also carries `cliBound` / `cliPath` per running instance — use it for **liveness checks only, never as authorization**: the ONLY thing that authorizes CLI use for a project is that project's own `cli_config.json`. Do not cold-start any project whose own config you have not read, even if it appears in the registry. Also note `projectPath` inside the config is a bind-time snapshot — the directory you actually found the config under is authoritative (helper `get_cli_config()` already rewrites it); never `open` the stored path if it differs from the real project root.

> Unity CLI is **experimental (beta)** and its command surface changes between releases — this document was verified against `1.0.0-beta.3`; the official docs may lag behind the binary, so `<cliPath> --help` is always authoritative. If a command errors unexpectedly, run `<cliPath> doctor --format json` first (environment snapshot: CLI version, paths, auth state, installed editors, recent log lines; `--tail <n>` for more log) and re-check `--help` before retrying. Never modify the server or config to work around a CLI quirk.

## 1. Cold start / lifecycle (`features.coldStart`)

The one capability REST can never provide: starting the editor when it is not running.

```bash
<cliPath> status --format json          # any editor instances running?
<cliPath> open "<projectPath>" --args -unityskills-coldstart
```

**Always pass `--args -unityskills-coldstart`** when cold-starting: the UnitySkills plugin detects this marker at editor startup and force-starts the REST server for this session, even if the user's Auto-start preference is off. Without the marker you depend on the user's saved preference. The marker is consumed once per editor session — it never overrides a mid-session manual stop.

**Preflight — is the right editor even installed?** `open` / `test` / `run` / `build` all resolve the editor from the project's `ProjectVersion.txt`. Before the first CLI launch of a session, confirm the bound `editorVersion` is actually installed:

```bash
<cliPath> editors -i --format json
```

If it is not installed, **stop and tell the user** — installing an editor is a large, system-changing operation that only the user decides on. Never run `install`, and never pass `--allow-install` (see DO NOT).

After launching, poll the UnitySkills REST server until ready (first import/compile can take minutes):

```python
from unity_skills import wait_for_health
health = wait_for_health(timeout=600)   # polls /health on ports 8090-8100
```

**Liveness triage — prefer this over blind retry.** When REST is unreachable:

1. **Check the UnitySkills registry first**: read `~/.unity_skills/registry.json`, find the entry whose `path` equals the project root, then test its `pid` (`ps -p <pid>` / Windows `tasklist`). Live pid → the editor is running but busy (Domain Reload / import) → keep the normal REST wait-and-retry; **do not** cold-start.
2. `<cliPath> status` is **supplementary, not authoritative**: it only lists editor instances visible to the CLI (requires the Unity Pipeline package in the project). An empty table / non-zero exit does **NOT** mean the editor is closed — verified in practice: a running editor without the Pipeline package shows nothing.
3. Only when the registry has no live-pid entry for this project → cold-start with `open`, then `wait_for_health`.
4. Never `open` a project whose editor is already running (live registry pid, or `Library/UnityLockfile` held) — Unity refuses a second instance on the same project.

## 2. Launch with arguments (`features.openArgs`)

```bash
<cliPath> open "<projectPath>" --args -openscene "Assets/Scenes/Main.unity"
```

Anything after `--args` is passed to the Unity Editor as standard command-line arguments. Useful to land in a known state (specific scene, custom `-executeMethod`). Only at launch time — for an already-running editor use REST `scene_open` instead.

## 3. Headless tests (`features.cliTest`)

```bash
<cliPath> test "<projectPath>" --mode EditMode --filter <pattern> --output test-results.xml --timeout 1800
```

- `--mode <EditMode|PlayMode>` — omit to run the editor's default test platform; cover both modes with two separate invocations.
- `--filter <pattern>` — only run tests whose names match.
- `--output <path>` — NUnit XML report (default `test-results.xml`).
- `--timeout <seconds>` (env `UNITY_TEST_TIMEOUT`) — kills the Unity process after N seconds; disabled by default, always set one for unattended runs.
- Extra editor arguments pass through after `--`, e.g. `-- -nographics`.
- **Exit codes**: `0` all passed; `6` tests ran but at least one failed (introduced in the official 0.1.0-beta.7 release notes); any other non-zero = the command itself failed — check stderr, not the XML.

Routing rule:

- **Interactive iteration** (editor already running, quick feedback on a few tests) → REST `test_*` skills.
- **Full regression / CI-style run, or editor closed** → `unity test` (headless, NUnit XML output). Do not run `unity test` against a project whose editor is open.

## 4. Batch runs (`features.cliRun`)

One-shot batch automation on the **bound project only**, while the editor is closed — the third lifecycle option between REST (editor open, interactive) and cold start (launch and keep serving):

```bash
<cliPath> run "<projectPath>" --timeout 1800 -- -executeMethod Your.Static.Method -quit
```

- Everything after `--` is forwarded to the Unity Editor as standard command-line arguments; `-executeMethod <static method>` plus `-quit` is the typical shape (asset re-import, batch fixes, custom pipelines).
- Streams the editor log to stdout and **returns the editor's exit code** — non-zero means the batch run failed.
- `--timeout <seconds>` (env `UNITY_RUN_TIMEOUT`) — disabled by default; always set one, a hung batch editor otherwise blocks your shell forever.
- Routing: editor already running → use REST skills, never `run` (Unity refuses a second instance on the same project — same `Library/UnityLockfile` rule as cold start). Editor closed + persistent session needed → cold start. Editor closed + one-shot task → `run`.
- Do not use `run --command <name>` — that drives Unity Pipeline package commands, which is not part of the UnitySkills workflow (see DO NOT).

## 5. Headless builds (`features.cliBuild`)

```bash
<cliPath> build "<projectPath>" --target StandaloneWindows64 --execute-method Builder.PerformBuild --output-path ./Builds/win64
```

- `--target` **and** `--execute-method` are both required — Unity has no built-in command-line build; the bound project must already contain a static build method. If it does not, tell the user instead of writing one into their project unasked.
- `--output-path` is forwarded to Unity as `-buildOutput`; the execute-method itself is responsible for reading it.
- The build log tails to stdout by default (`--no-tail` to disable); the full log lands at `<project>/Logs/build-<target>-<timestamp>.log` unless `--log-file` overrides it.
- **Dirty-worktree guard**: `build` refuses to run with uncommitted changes. That protection is deliberate — pass `--allow-dirty-build` only when the user explicitly says so.
- `--versioning-strategy <semantic|tag|custom|none>` (default `none`) stamps the build version from git tags/history; `--build-version` applies only with `custom`.
- Android: `--android-export-type <apk|aab|android-studio-project>` plus keystore/signing flags exist, but the CLI's own help warns that secrets passed as CLI arguments can leak into shell history and CI logs — let the user handle signing configuration themselves; never ask them to paste keystore passwords into your shell commands.
- Same lifecycle rules as `run`: bound project only, editor must be closed, and once the editor is up again all normal operations go back through REST.

## 6. Automation contract (all commands)

- **Structured output**: `--format <human|json|tsv|ndjson>` (env `UNITY_FORMAT`); `--json` is shorthand for `--format json`. When stdout is piped the default silently becomes TSV — one more reason to always pass `--format json` explicitly. JSON responses use a standard envelope `{success, command, data, errors, warnings}`; `ndjson` streams progress events for long-running commands.
- **Non-interactive**: `--non-interactive` (env `UNITY_NON_INTERACTIVE`) turns prompts into hard errors instead of hanging your shell; combine with `--quiet` (env `UNITY_QUIET`) and `--no-banner` (env `UNITY_NO_BANNER`) for clean machine output. Exporting the env vars once (`UNITY_FORMAT=json`, `UNITY_NON_INTERACTIVE=1`) covers a whole scripted session.
- **Errors and exit codes**: data goes to stdout, errors/diagnostics to stderr (JSON-mode errors too). `0` success, `1` generic error (read stderr), `130` cancelled by user, `6` = `test` finished with failing tests.
- `--verbose` adds full error details with stack traces — useful once, when reporting a CLI problem to the user.

## DO NOT

- Do not use the CLI when `cli_config.json` is absent or `enabled:false` — the user has not opted in. Operate **only on the bound project** (the directory you found the config under); never `open` / `test` / `run` / `build` any other project.
- Do not install the Unity CLI yourself; installation is a user decision made in the panel.
- **Never pass `--allow-install`** (accepted by `test` / `run` / `build`), and do not use `unity install` / `uninstall` / `hub` / license commands unless the user explicitly asks — installing or removing editors is a large, slow, system-changing operation that belongs to the user alone.
- Do not run bare `unity mcp` — it starts a blocking stdio MCP server and waits for a client, hanging your shell.
- Do not use `unity command` / `unity pipeline` / `unity run --command` — the Unity Pipeline package route duplicates what UnitySkills REST already provides and is not part of this workflow.
- Do not parse the CLI's human-readable output (its display language follows `unity language`, e.g. Chinese table headers) — always pass `--format json --non-interactive` when you need to read results programmatically.
- Do not treat CLI availability as a substitute for the REST workflow: once `/health` responds, all normal operations go through REST skills.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
