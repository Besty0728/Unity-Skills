# Unity CLI: headless builds (`features.cliBuild`)

`cliBuild` is **off by default**. When the user enables it, choose the build path from the installed CLI version, the Unity Editor version, the target platform and whether a Build Profile exists.

**Never build while the Editor is alive.** beta9–15: the CLI refuses a project that is open in an Editor. **Since beta16, `build --target` builds *through* the open Editor and switches its active build target** (`viaRunningEditor: true`); `--timeout` and Ctrl+C only stop the wait. Run the liveness triage in [gate-and-lifecycle.md](./gate-and-lifecycle.md) first. If the Editor is alive or you are unsure, do not call `build`; use REST.

Discover valid values instead of guessing: `<cliPath> build --list-targets` and `<cliPath> build --list-profiles` only list and exit. `--create-profile` writes an asset and is not used.

## Path 1: Unity 6 Build Profile (beta4+)

```bash
<cliPath> build "<projectPath>" --profile "Windows" --output-path ./Builds/win64
```

- `--profile` accepts a Build Profile asset path or the profile name shown in the Build Profile window. Requires Unity 6 (`6000.x`) and a configured Build Profile.

## Path 2: built-in desktop build (beta4+)

```bash
<cliPath> build "<projectPath>" --target StandaloneWindows64 --output-path ./Builds/win64
```

- `--target` is required (without `--profile`). For desktop targets, `--execute-method` is **optional** (beta4+) when `--output-path` is provided.

## Path 3: custom C# build method (all versions; required for mobile and WebGL)

```bash
<cliPath> build "<projectPath>" --target Android --execute-method Builder.PerformBuild --output-path ./Builds/android
```

- `--execute-method` takes priority when provided. Use it for Android, iOS, WebGL, or projects that already have a custom build pipeline. `--output-path` is forwarded to Unity as `-buildOutput`; the execute-method itself must read it.
- If the project has no static build method, tell the user instead of writing one into their project unasked.

## Common build flags

- The build log tails to stdout by default (`--no-tail` disables it). The full log lands at `<project>/Logs/build-<target>-<timestamp>.log` unless `--log-file` overrides it. beta6+ writes `<output>.provenance.json` beside the artifact (`--no-provenance` skips it). beta17 removed the short forms `-o` and `-l`; write `--output-path` and `--log-file`.
- `--timeout <seconds>` (env `UNITY_BUILD_TIMEOUT`, beta6+) aborts the build. Disabled by default; always set one. Exit `6` (beta6–13) or `14` `BATCH_TIMEOUT` (beta14+). json and ndjson output carry heartbeat frames (elapsed time, seconds since the log last grew): a stalled Editor shows as a growing gap, not silence.
- **Dirty-worktree guard**: `build` refuses to run with uncommitted changes. That protection is deliberate. Pass `--allow-dirty-build` only when the user explicitly says so.
- `--versioning-strategy <semantic|tag|custom|none>` (default `none`) stamps the version from git. `--build-version` applies only with `custom`.
- Android: `--android-export-type <apk|aab|android-studio-project>` and keystore flags exist (beta15+ also reads `UNITY_ANDROID_KEYSTORE_PASSWORD` and `UNITY_ANDROID_KEY_ALIAS_PASSWORD`). The Editor still receives the passwords on its command line, so let the user handle signing. Never ask the user to paste keystore passwords into your commands.
- Same lifecycle rules as `run`: bound project only. Once the Editor is up again, all normal operations go back through REST.
- **Exit codes**: `0` success; `6` build failed or Editor exited abnormally (plus `--timeout` on beta6–13); `14` timeout (beta14+); `7` services unreachable; `130` / `143` signals. A crash is `stopped with signal SIG…` (beta5+), not `BUILD_FAILED`.
