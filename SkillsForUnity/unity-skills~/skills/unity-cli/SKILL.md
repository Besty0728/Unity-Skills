---
name: unity-cli
description: Advisory guidance for using the experimental Unity CLI (the official `unity` command-line tool) alongside UnitySkills — cold-start a bound project without Unity Hub, probe editor liveness, launch with arguments, and run headless tests. Only applies when the project has been bound in the UnitySkills panel (Library/UnitySkills/cli_config.json exists with enabled:true). 实验性 Unity CLI(官方 unity 命令行工具)与 UnitySkills 协同的指导文档——免 Unity Hub 冷启动已绑定项目、探测编辑器存活、传参启动、无头测试;仅当项目已在 UnitySkills 面板完成绑定(存在 Library/UnitySkills/cli_config.json 且 enabled:true)时适用。
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
  "cliVersion": "0.1.0-beta.7",
  "projectPath": "/path/to/Project",
  "editorVersion": "6000.0.32f1",
  "boundAt": "2026-07-26T09:00:00Z",
  "features": { "coldStart": true, "openArgs": true, "cliTest": true }
}
```

The global registry (`~/.unity_skills/registry.json`) also carries `cliBound` / `cliPath` per running instance — use it for **liveness checks only, never as authorization**: the ONLY thing that authorizes CLI use for a project is that project's own `cli_config.json`. Do not cold-start any project whose own config you have not read, even if it appears in the registry. Also note `projectPath` inside the config is a bind-time snapshot — the directory you actually found the config under is authoritative (helper `get_cli_config()` already rewrites it); never `open` the stored path if it differs from the real project root.

> Unity CLI is **experimental (beta)** and its command surface changes between releases. If a command errors unexpectedly, verify with `<cliPath> --help` before retrying. Never modify the server or config to work around a CLI quirk.

## 1. Cold start / lifecycle (`features.coldStart`)

The one capability REST can never provide: starting the editor when it is not running.

```bash
<cliPath> status --format json          # any editor instances running?
<cliPath> open "<projectPath>" --args -unityskills-coldstart
```

**Always pass `--args -unityskills-coldstart`** when cold-starting: the UnitySkills plugin detects this marker at editor startup and force-starts the REST server for this session, even if the user's Auto-start preference is off. Without the marker you depend on the user's saved preference. The marker is consumed once per editor session — it never overrides a mid-session manual stop.

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
<cliPath> test "<projectPath>" --filter <pattern> --output test-results.xml
```

Routing rule:

- **Interactive iteration** (editor already running, quick feedback on a few tests) → REST `test_*` skills.
- **Full regression / CI-style run, or editor closed** → `unity test` (headless, NUnit XML output). Do not run `unity test` against a project whose editor is open.

## DO NOT

- Do not use the CLI when `cli_config.json` is absent or `enabled:false` — the user has not opted in.
- Do not install the Unity CLI yourself; installation is a user decision made in the panel.
- Do not use `unity install` / `uninstall` / license commands unless the user explicitly asks — editor installs are large, slow, and system-changing.
- Do not run bare `unity mcp` — it starts a blocking stdio MCP server and waits for a client, hanging your shell.
- Do not parse the CLI's human-readable output (its display language follows `unity language`, e.g. Chinese table headers) — always pass `--format json --non-interactive` when you need to read results programmatically.
- Do not treat CLI availability as a substitute for the REST workflow: once `/health` responds, all normal operations go through REST skills.
