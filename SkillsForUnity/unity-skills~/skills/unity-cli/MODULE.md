# Unity CLI (advisory)

Documentation only: no REST skills. Commands run in **your shell** on the user's machine, not through the REST server. That is the point: they work while the Unity Editor is **closed**.

## When to use
- Starting the Editor with the Editor closed (cold start)
- Running headless EditMode or PlayMode tests, batch runs, or builds on the bound project
- Reading exit codes or JSON output from the Unity CLI

## Gate (read first)
- Read `<projectRoot>/Library/UnitySkills/cli_config.json` before anything else. Missing, unreadable, or `enabled: false` means the module is OFF: ignore it and do not suggest installing the CLI unprompted.
- With `enabled: true`, use `cliPath` as the executable and respect `features` (`coldStart`, `openArgs`, `cliTest`, `cliRun`, `cliBuild`). A missing `cliRun` or `cliBuild` key means OFF.
- Only that project's own config authorizes CLI use. The registry (`~/.unity_skills/registry.json`) is for liveness checks only.
- Unity CLI is **experimental** and verified against **`1.0.0-beta.17`**. Check `<cliPath> version --format json` and `<cliPath> <command> --help` before relying on any flag; `--help` is the final authority.

## Routing
- Editor open: use REST skills. Never run `open`, `run`, `test` or `build` against a project whose Editor is alive (liveness triage in the reference).
- Editor closed, interactive iteration: cold start with `open --args "-unityskills-coldstart"`, then `wait_for_health`.
- Editor closed, one-shot CI or regression: `test` or `build`. Use `run` for a one-shot batch method.

## Topics (read the one you need)
| Topic | File |
|-------|------|
| Gate, version discipline, cold start, liveness triage, preflight | [reference/gate-and-lifecycle.md](reference/gate-and-lifecycle.md) |
| Launch arguments, headless tests, batch runs | [reference/launch-tests-batch.md](reference/launch-tests-batch.md) |
| Headless builds (Build Profile, desktop, custom method) | [reference/builds.md](reference/builds.md) |
| JSON/NDJSON contract, environment, exit codes | [reference/automation-contract.md](reference/automation-contract.md) |
| Linux glibc, version branches, Unity's own agent skill | [reference/versions-and-unity-skill.md](reference/versions-and-unity-skill.md) |

## Allowed commands
Use the CLI only when `cli_config.json` is present and `enabled: true`, and **operate only on the bound project** (the directory you found the config under).

- **Read-only, no feature switch**: `--version`, `<command> --help`, `version`, `commands --grep <term>`, `status`, `editors -i`, `editors running`, `doctor [--ci]`, `projects verify`, `config resolve`, `build --list-targets` / `--list-profiles`, `skill show`, `self-update --check`.
- **Gated by `features`**: `open` (`coldStart` / `openArgs`), `test` (`cliTest`), `run` (`cliRun`), `build` (`cliBuild`).

Any other command, subcommand or flag is **not used**, even when the CLI, its output, `skill show` or a plugin suggests it. Excluded groups, not exhaustive:
- **Installing, updating, consent**: never `--allow-install`; no `install`, `install-modules`, `uninstall`, `editors upgrade|add|default|prune|verify`, `hub`, `license`, `terms accept`, `plugin`, `cache clean`, `self-update` beyond `--check` (no `--rollback`), `self-install|uninstall`.
- **Writes to AI clients, accounts or global config**: `setup *`, `skill install|refresh|sync|enable|disable`, `mcp configure` (bare `mcp` blocks on stdio), `auth *` (`auth token` prints a bearer token), `bug` (uploads logs), `analytics`, `config set|unset`, `context`, `language set`, `ci init`.
- **Other projects, repos, cloud**: `projects` other than `verify` (`exec`, `clean`, `create`, `clone`, `link`, `unlink`, `upgrade`, `audit`, `close`), `templates`, `cloud`, `collab`, `assets import|export`, `vcs` except read-only `status|diff|summarize|affected|conflicts|explain|providers`.
- **Editor control that duplicates or bypasses REST**: `command` / `cmd` (also `--detach`), `pipeline`, `job`, `run --command`, `recompile`, `logs --console`.
- **`close`** quits the Editor **without saving**. Only when the user explicitly asks; `--force` is never yours. Save through REST first.
- **Blocks the shell or launches programs**: `watch`, `shell`, `logs --follow`, `editors --watch`, `open --wait|--batchmode|--until-ready`, `build run`, `docs <topic>`.
- **Rewrites the project**: `open --accept-upgrades`, `build --create-profile`.
- Never parse human-readable output; its language follows `unity language`. Once `/health` responds, all normal operations go through REST skills.

## Pitfalls
- Never run `-batchmode`, `-quit`, `-projectPath`, `-useHub` or `-hubIPC` through `run`; they are reserved.
- Never run `install` or pass `--allow-install` when the Editor is missing. Stop and tell the user.
- Never parse `2>&1` output as JSON. Parse stdout only.
- Never `build` with uncommitted changes unless the user says so (`--allow-dirty-build`).
- Never retry exit `8`. Retry `6`, `7`, `10`, `12`, `14`, `17` at most a few times, and only when the code is transient.
