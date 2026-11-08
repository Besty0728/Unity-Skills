# Unity CLI: versions, Linux compatibility, Unity's own skill

## Linux compatibility

Starting with beta4, the Unity CLI requires **glibc 2.34+** on Linux. It no longer supports Ubuntu 20.04 and earlier, Debian 11, RHEL/CentOS 8 or Amazon Linux 2 (no change through beta17). On those systems the CLI may fail to start with a glibc-related error rather than a normal "command not found". Do **not** treat this as "CLI not installed" and prompt the user to reinstall. Surface the constraint instead: stay on beta3, upgrade the OS, or use another machine.

## Version branches

There is no global minimum version enforced by UnitySkills. Capabilities are branched by actual CLI behavior:

- **beta3**: old `build` semantics (`--execute-method` required), no NDJSON terminal result frame. **beta4+**: Build Profile support, built-in desktop build, NDJSON `type=result` frames. **beta5+**: named Editor signal diagnostics.
- **beta6+**: `test` exit `8` = failed, `6` = no verdict; `build --timeout`; `doctor --ci`; `projects verify`; `test --shard/--retries/--rerun-failed`; provenance manifest.
- **beta8+**: `close`, `vcs` and `plugin` groups; `upgrade` renamed `self-update`; `projects create` makes Unity Cloud projects by default.
- **beta9+**: `open` exits `6` on early Editor death (older: always `0`); `build` refuses an already-open project; `test --affected`; read-only `skill show`; `version --format json`; `doctor` no longer hangs on macOS keychain prompts.
- **beta10–13**: `commands`, `recompile`, `docs`, `watch`, `setup`, `open --wait`, `build run|--list-*`, `ProjectSettings/UnityCliConfig.json`; `status --until-ready`, `STATUS_BLOCKED_BY_DIALOG`; `open --format json`; json `notifications`; run and test exit `130` / `143`.
- **beta14+**: exit codes `10`–`14` (timeouts move from `6` to `14`); `--timeout` whole digits only; `open` on an open project exits `0` (`alreadyOpen`); Safe Mode and dialog `status` codes; agent hints on stderr; `terms`, `open --accept-upgrades|--batchmode|--until-ready` (not used).
- **beta15+**: `unity list`, `projects list|new` and other `list` subcommands removed; every ndjson stream ends with a result frame; rows are `"type":"row"`; listings are `data.items`; `cli.version` pin; installer auto-setup of agent skills.
- **beta16+**: exit `17` / `18`; **`build --target` borrows an open Editor**; `skill sync`. **beta17**: `-o` / `-l` short flags of `build` and `run` removed.

The safest way to branch is `<cliPath> version --format json` (beta9+) and `<cliPath> <command> --help` at the start of a session. Act on what the binary actually reports.

## Unity's own agent skill: reference, not authorization

`<cliPath> skill show` (read-only), a `unity-cli` skill the user installed into their agent client, and the installer's auto-setup (since beta15 the install scripts add Unity's skills to every agent client found; `UNITY_CLI_SKIP_SETUP=1` skips it) are Unity's guide for driving the CLI **and a running Editor through the Pipeline package**. It is a second reference only. This module's gate and allowlist still apply.

That guide routes agents to `unity command` / `script eval`, `pipeline install`, `mcp configure`, `setup`, `skill install|sync`, `self-update`, a hand-launched `-batchmode` Editor, `vcs`, `projects create`. **None of these are used here.** Editor running means UnitySkills REST. Editor closed means only the allowlist in [../SKILL.md](../SKILL.md). If both skills are loaded, REST and this module win.

The whole tree is about 720 KB (`SKILL.md` about 70 KB). Read single files only, e.g. `skill show --path references/build-run-test.md`.
