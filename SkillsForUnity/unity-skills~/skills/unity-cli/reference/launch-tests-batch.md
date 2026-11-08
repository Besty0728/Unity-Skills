# Unity CLI: launch arguments, headless tests, batch runs

## 2. Launch with arguments (`features.openArgs`)

```bash
<cliPath> open "<projectPath>" --args "-unityskills-coldstart -openscene Assets/Scenes/Main.unity"
```

`--args` takes **one** string, which the CLI splits like a shell. Put every Editor argument inside a single pair of quotes. Unquoted `--args -openscene Assets/...` is two arguments and fails (`INVALID_COMMAND_ARGS`, too many arguments). `-projectPath`, `-useHub` and `-hubIPC` are reserved and rejected before launch (exit `6`). Useful to land in a known state (a specific scene, a custom `-executeMethod`). Only at launch time. For an already-running Editor, use REST `scene_load` instead.

## 3. Headless tests (`features.cliTest`)

```bash
<cliPath> test "<projectPath>" --mode EditMode --filter <pattern> --output test-results.xml --timeout 1800
```

- `--mode <EditMode|PlayMode>`: omit it to run the Editor's default test platform. Cover both modes with two separate invocations.
- `--filter <pattern>`: only run tests whose names match.
- `--output <path>`: NUnit XML report (default `test-results.xml`). In CI, keep using this file for test results.
- **CI-only parameters**: `test --help` may list report and coverage flags (beta4+: `--report-format nunit,junit`, `--junit-output`, `--coverage*`), and, beta6+, `--shard N/M`, `--retries N` (re-runs only the failed tests, `--timeout` per attempt, flaky tests named in `test-results.retries.json`; never-passing tests still exit `8`) and `--rerun-failed` (reads the failing set from `--output`, writes `test-results.rerun.xml`). beta9+ `--affected --since <ref>` runs only the tests a change can reach (an approximation; writes `test-results.affected.xml`). Run `--affected-compare` first to measure its false-negative rate before trusting it. The selectors are mutually exclusive. Use a parameter only if the **current** binary's `test --help` lists it. Never guess names.
- `--timeout <seconds>` (env `UNITY_TEST_TIMEOUT`; whole digits only since beta14) kills the Unity process after N seconds. Disabled by default. Always set one for unattended runs.
- Extra Editor arguments pass through after `--`, e.g. `-- -nographics`.
- **Exit codes**: `0` all passed (also when `--retries` rescued a flaky test; the flakiness is still reported). beta6+: `8` tests failed (`TESTS_FAILED`); `6` no verdict (compile error, licence or Editor crash: `TEST_RUN_ERROR`), or the project is already open in an Editor, which the CLI refuses. `--timeout` expiry (`TEST_TIMED_OUT`) is `6` on beta6–13 and **`14` on beta14+**. beta3–5: `6` covers failed tests too, so read the XML. Full table in [automation-contract.md](./automation-contract.md). Retry no-verdict codes at most a few times. Never retry `8`.

Routing rule:
- **Interactive iteration** (Editor already running, quick feedback on a few tests): REST `test_*` skills.
- **Full regression or CI-style run, or Editor closed**: `unity test` (headless, NUnit XML output). Never run `unity test` against a project whose Editor is open.

## 4. Batch runs (`features.cliRun`)

One-shot batch automation on the **bound project only**, while the Editor is closed. This is the third lifecycle option, between REST (Editor open, interactive) and cold start (launch and keep serving):

```bash
<cliPath> run "<projectPath>" --timeout 1800 -- -executeMethod Your.Static.Method
```

- Everything after `--` is forwarded to the Unity Editor. The CLI adds `-batchmode` and `-quit` itself. **Never pass `-batchmode`, `-quit`, `-projectPath`, `-useHub` or `-hubIPC`**: they are reserved and fail before launch (exit `6`). With `--log-file`, `-logFile` is reserved too. `-executeMethod <static method>` (asset re-import, batch fixes, custom pipelines) is the typical payload.
- Streams the Editor log to stdout and **returns the Editor's exit code**. Non-zero means the batch run failed. An Editor crash is reported as `stopped with signal SIG…` (beta5+, see [automation-contract.md](./automation-contract.md)). That is an Editor crash, not a business-logic failure.
- `--timeout <seconds>` (env `UNITY_RUN_TIMEOUT`) is disabled by default. Always set one, because a hung batch Editor otherwise blocks your shell forever. Expiry exits `6` (beta6–13) or `14` `BATCH_TIMEOUT` (beta14+).
- Routing: Editor already running means use REST skills, never `run`, because Unity refuses a second instance on the same project (same `Library/UnityLockfile` rule as cold start). Editor closed and a persistent session needed means cold start. Editor closed and a one-shot task means `run`.
- `run --command <name>` drives Unity Pipeline package commands. It is not part of this workflow.
