# Unity CLI: gate, version discipline, lifecycle

## Gate: read this first

Before using any command, check the binding config:

```
<projectRoot>/Library/UnitySkills/cli_config.json
```

- File missing, unreadable, or `enabled: false`: **Unity CLI is OFF for this project. Ignore the module entirely.** Do not suggest installing the CLI unprompted; the user opts in via `Window > UnitySkills → AI Config → Unity CLI Setup…`.
- `enabled: true`: use `cliPath` as the executable (it may not be on your PATH). Respect the per-feature switches in `features`:

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

Configs written by older plugin versions may lack the `cliRun` / `cliBuild` keys. For these two, **a missing key means OFF** (the first three keep their original semantics). Both also default to off on fresh binds; the user enables them per project in the panel.

The global registry (`~/.unity_skills/registry.json`) also carries `cliBound` / `cliPath` per running instance. Use it for **liveness checks only, never as authorization**: the ONLY thing that authorizes CLI use for a project is that project's own `cli_config.json`. Do not cold-start any project whose own config you have not read, even if it appears in the registry. `projectPath` inside the config is a bind-time snapshot; the directory you actually found the config under is authoritative (helper `get_cli_config()` already rewrites it). Never `open` the stored path if it differs from the real project root.

## Version discipline

Unity CLI is **experimental (beta)** and changes every release. This module is verified against **`1.0.0-beta.17`** (no stable release yet; beta.7 was withdrawn the day it shipped). `cliVersion` in the config is only a **bind-time snapshot**.

- Before using a command whose semantics changed recently, read `<cliPath> version --format json` (beta9+; with `UNITY_FORMAT=json` set, even `--version` prints a JSON object) and `<cliPath> <command> --help`. **`--help` is always the final authority** for the binary on disk. Since beta14 its footer lists every exit code.
- Search instead of guessing flags: `<cliPath> commands --grep <term>` (10–50 KB as JSON for one term). **Never** run bare `commands --json` (1.4 MB).
- If a command errors unexpectedly, run `<cliPath> doctor --format json` first (`--tail <n>` for more log) and re-check `--help`.
- Never modify the server or config to work around a CLI quirk.
- Which commands you may run at all: see [../SKILL.md](../SKILL.md).

## 1. Cold start and lifecycle (`features.coldStart`)

The one capability REST can never provide: starting the Editor when it is not running.

```bash
<cliPath> open "<projectPath>" --args "-unityskills-coldstart" --format json --non-interactive
```

**Always pass `--args "-unityskills-coldstart"`** when cold-starting. The UnitySkills plugin detects this marker at Editor startup and force-starts the REST server for this session, even if the user's Auto-start preference is off. Without the marker you depend on the user's saved preference. The marker is consumed once per Editor session and never overrides a mid-session manual stop.

`open --format json` (beta12+) returns `data.launchedPid` and `data.editorPath`. Keep the pid for the liveness check below.

**beta14+:** if an Editor already holds the project, `open` launches nothing, exits `0` and returns `data.alreadyOpen: true` plus `data.pid`. **The marker is NOT delivered to that Editor**, so a REST server the user stopped stays stopped. beta9–13 exit `6` instead.

### Preflight (read-only, no feature switch needed)

`open` / `test` / `run` / `build` all resolve the Editor from the project's `ProjectVersion.txt`. Before the first CLI launch of a session, run these checks. None of them modifies the project:

```bash
<cliPath> editors -i --format json                       # all versions: is the bound editorVersion installed?
<cliPath> doctor --ci --format json                      # beta6+: exit 6 = definitive blocker, 7 = retryable
<cliPath> projects verify "<projectPath>" --format json  # beta6+: exit 6 = errors, 0 = clean or warnings only
```

- `doctor --ci` (from the project root) checks licence, the project's Editor, disk space and Unity services. Branch on `errors[].code` (`LICENSE_NONE`, `EDITOR_NOT_INSTALLED`, `DISK_SPACE_LOW`, `NETWORK_UNREACHABLE`): `6` means stop and report; `7` means retry a few times, then tell the user. Plain `doctor` always exits `0`. `--skip-license` (beta15+) is for runners with their own licence; that is the user's call, not yours. beta6–8 on macOS may hang on a keychain prompt, so wrap it in a shell timeout.
- `projects verify` needs no Editor, licence or network. It only detects `META_MISSING` / `META_ORPHAN` / `GUID_DUPLICATE` / `CONFLICT_MARKERS` / `MANIFEST_INVALID` (exit `6`): report them, never hand-repair. Warnings (`PATH_UNVERIFIABLE`, `EDITOR_VERSION_DRIFT` via `--expect-editor <version>`) exit `0` unless `--strict`.

If the Editor is not installed, **stop and tell the user**. Never run `install` or pass `--allow-install`.

After launching, poll the UnitySkills REST server until ready (first import or compile can take minutes):

```python
from unity_skills import wait_for_health
health = wait_for_health(timeout=600)   # polls /health on ports 8090-8100
```

An Editor that never got focus after launch can sit idle without starting the server. On macOS, if nothing answers for 45 seconds while an Editor process has the project open, `wait_for_health` brings that window to the front once (the terminal may ask once for permission to control System Events). Pass `project_root` when the current directory is not the project, and `wake_after=0` to turn this off.

`open` exits `6` (`OPEN_EDITOR_EXITED`; `OPEN_EDITOR_EXITED_SIGNAL` beta10+) when the Editor dies inside the CLI's short watch window. Report it at once. A later death is never observed, so a `wait_for_health` timeout remains the real failure signal. Skip `open --until-ready`: its readiness check is the Pipeline server, which UnitySkills projects may lack, so it just waits out `--timeout` (exit `10`). `/health` is the readiness signal.

### `wait_for_health` timed out: diagnose, do not relaunch

One read-only call shows whether a dialog or Safe Mode holds the Editor:

```bash
<cliPath> status --format json --non-interactive --project-path "<projectPath>"
```

Branch on `errors[0].code` (envelope on stdout):
- `STATUS_BLOCKED_BY_DIALOG` (beta13+; `blockedBy` names it).
- beta14+: `STATUS_BLOCKED_BY_DIALOG_SAFE_MODE` (quotes the compile errors), `…_GRAPHICS_API`, `…_SOFTWARE_TERMS`, `STATUS_SAFE_MODE` (exit `6`).
- `STATUS_NO_INSTANCES` (exit `12`) only means no Pipeline-backed Editor is visible.

Relay the remedy: fix the compile errors, or accept the terms or answer the upgrade dialog **in the Editor window**. Never answer a dialog yourself, and never use `--accept-upgrades` or `terms accept`. Limits: pre-Pipeline dialogs are matched by English title, and macOS needs the Screen Recording permission for the terminal, or only "Project Upgrade Required" is seen.

### Liveness triage: prefer this over blind retry

When REST is unreachable, determine whether an Editor is actually running before deciding to cold-start:

1. **Read the project's own `cli_config.json` first.** Only proceed if it exists and `enabled: true`.
2. **Check the UnitySkills registry**: read `~/.unity_skills/registry.json`, find the entry whose `path` equals the project root, then test its `pid` (or the `launchedPid` from `open`) with `ps -p <pid>` (Windows: `tasklist`). A live pid means the Editor is running but busy (Domain Reload or import): keep the normal REST wait-and-retry, and **do not** cold-start.
3. **Cross-check with the CLI process list**: `<cliPath> editors running --format json --non-interactive` (process table plus Pipeline lockfiles, so it also sees Editors without the Pipeline package). A sandboxed agent shell may see nothing. An empty list is not proof.
4. **Check `Library/UnityLockfile`.** A held lockfile means an Editor is open on this project even if the registry entry is missing.
5. `<cliPath> status` is **supplementary, not authoritative**. It reads the Pipeline lockfile, so an Editor without the Pipeline package (or a batch-mode one) shows nothing. An empty result or non-zero exit does **NOT** mean the Editor is closed.

If the pid, the CLI process list or the lockfile shows a running Editor, **do not** `open`, `run`, `test` or `build`. Only when none of the above shows a live Editor for this project should you cold-start with `open`, then `wait_for_health`.
