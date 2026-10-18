# UnitySkills agent evaluation (P0)

Measures how well an agent configuration (host + model + reasoning effort) drives a Unity project
through UnitySkills: a bank of 14 tasks (6 basic, 8 composite), an independent verifier that reads
the editor state back, and a calibration across models. Lives outside `SkillsForUnity/` and is not
shipped with the package. P1 will move the same rules into the editor panel.

## v3 maintainer workflow

v3 moves the evaluation into the package: the runner is `SkillsForUnity/Evaluation~/run_eval.py` (contract:
[design/v3-contracts.md](design/v3-contracts.md)), users start it from the Evaluation tab or through their agent, and
the public tasks ship in `SkillsForUnity/Evaluation~/pack/`. This folder keeps what must not ship: the private
verifiers (`pack/private/<ID>.py`, published per release tag and as a zip), the reference solutions
(`pack/solutions/<ID>.py`) and the maintainer tools below. Everything under `runs/` stays local.

| Path | Content |
|---|---|
| `cli.py` | maintainer CLI: `selftest3`, `gate3`, `suite3` (v3); the v1 commands further down keep working |
| `selftest3.py`, `harness/solver_driver.py` | the gate: scripted solutions drive the real runner CLI |
| `suite3.py`, `harness/subject.py`, `harness/codex_proxy.py` | batch mode: headless Claude Code / Codex agents |
| `tools/` | `build_pack_manifest.py`, `check_eval_pack.py`, `lint_pack.py`, `build_private_zip.py` |
| `runs/selftest/gate.json`, `runs/suite3/<suite>/` | gate record; batch cohort, ledger and transcripts (local) |

Requirements: a Unity project whose `com.besty.unity-skills` is the version under test (for development a `file:`
dependency on this repository's `SkillsForUnity`), its editor open with the UnitySkills server running, operating mode
Bypass and skill surface Full; Python 3.9+ (Codex batch runs need 3.11+). Pass the project with `--project` or set
`UNITYSKILLS_EVAL_PROJECT`. One maintainer tool drives an editor at a time (`Library/UnitySkills/driver.lock`).

Tasks that build uGUI (`requires.packages` lists `com.unity.ugui`: DB5, T2, DC5, OC5) also need TextMesh Pro's Essential
Resources when the project has TextMesh Pro: always on Unity 6, where it ships inside `com.unity.ugui` 2.x, and on 2022
when `com.unity.textmeshpro` is installed. The UI skills create TMP text there; without a `TMP Settings.asset` in a
`Resources` folder TextMesh Pro opens its importer window instead, a batchmode or `-nographics` editor logs errors for
it, and every later Play Mode entry logs them again, so every runtime scenario fails with "Runtime error logs were
captured." even for tasks without UI. The runner reads the project files and reports `TMP_RESOURCES_MISSING` from
`check`, `start` and `next` before any task is built. Fix it once per project, in the editor with Window > TextMeshPro >
Import TMP Essential Resources, or with the editor closed:

```bash
Unity -batchmode -projectPath <project> -importPackage "<project>/Library/PackageCache/<TMP package>/Package Resources/TMP Essential Resources.unitypackage" -quit
```

`<TMP package>` is `com.unity.ugui@<fingerprint>` on Unity 6 and `com.unity.textmeshpro@<version>` on 2022; the problem's
fix text names the exact file.

T2 reads two facts from the project files and names them in its prompt: the UI input module that fits the Active
Input Handling (`StandaloneInputModule` for Input Manager (Old) or Both, `InputSystemUIInputModule` when only the Input
System package is active, which needs `com.unity.inputsystem`) and the label component of a button
(`TextMeshProUGUI` where the project has TextMesh Pro, `UnityEngine.UI.Text` otherwise, e.g. Unity 2022 without
`com.unity.textmeshpro`). A new Unity 6 URP project works as it is. The task reads them through
`usk_eval.verifylib` (`active_input_handler`, `has_text_mesh_pro`, `installed_packages`), the same code the TextMesh Pro
check above uses. `tools/lint_pack.py` rule 9 keeps that check complete: a task or solution that calls a `ui_*` skill
must list `com.unity.ugui` in `requires.packages`.

Re-imports are not writes. Unity imports a prefab again when a script it uses changes type (DC3 moves and renames one),
which the plugin's journal records as an `imported` file of the editor. The runner hashes the files under the run's
asset folder (`.meta` included) when the task is issued and, for the imported paths only, again at submit: an import by
the editor, or by the runner's own refresh, of a file that existed at issue with the same bytes is dropped from the
records the verifiers see and counted in `evidence.journal.unchangedReimports`. Created, changed, deleted and moved
files, a changed `.meta` and the agent's own REST imports still count; so does everything when Unity rewrites the
file itself.

### Gate

```bash
python3 -B eval/cli.py selftest3 --dry-run                 # pack, solutions and case list; no editor
python3 -B eval/cli.py selftest3 [--task S01 ...]          # reference x2, negatives, alternatives through the runner
python3 -B eval/cli.py gate3 [--task ...] [--unity-major 2022 --pipeline URP --plugin-version 3.0.0]
```

A task is gated per Unity major, render pipeline and plugin version, keyed by the hash of its gate inputs (runner,
task files, private verifier, solutions, `GATE_INPUTS` in `cli.py`); any change to those files needs a new run.
Basic tasks accept URP and the Built-in pipeline and are gated on 2022 and 6000 in both; standard and challenge tasks
are URP only. `selftest3` reads the private verifiers from `pack/private` (`--private-dir` to change it).
A gate vouches for the plugin build that ran it: the editor must load this checkout's `SkillsForUnity` (`/health`
`packageRoot`; `--allow-other-package` records anyway, marked `override`) and must have compiled every `.cs`/`.asmdef`
in it (none newer than `assembliesLoadedAtUtc`), otherwise no gate is recorded. `--task` may be repeated.

### Batch suite

```bash
python3 -B eval/cli.py suite3 --suite my-sonnet-v3 --models sonnet --dry-run      # every slot; runs nothing
python3 -B eval/cli.py suite3 --suite my-sonnet-v3 --models sonnet --plan pilot
python3 -B eval/cli.py suite3 --suite my-sonnet-v3 --models sonnet --plan calibration
python3 -B eval/cli.py suite3 --suite my-codex-v3 --host codex --models sol --effort high --mode suite
```

- A slot is one headless agent session (`claude -p` / `codex exec`, v1 isolation: neutral cwd, frozen docs, process
  group killed on timeout). The agent gets the start command of the frozen docs' entry script with `--launcher batch`
  and does what a user's agent does: start, the session prompt, next, the task through REST, the answer, submit.
  `--mode strict` (default) is one agent and one runner session per task; `--mode suite` one agent for the selection.
- Slot identity is suite, rep, task, host, model and effort. Without `--effort` the host's own default applies and is
  recorded as `default`, a slot identity of its own. `luna-max` is `gpt-6-luna` at effort `max`.
- The first run binds the suite to its cohort (`runs/suite3/<suite>/cohort.json`: pack id, version and manifest hash,
  plugin version, Unity version, pipeline, each task's gate inputs hash, configurations, frozen docs, host version);
  a later run with any difference is refused, so a new experiment needs a new `--suite`. Tasks need a green `gate3`
  entry for the editor's Unity major, pipeline and plugin version unless `--force` is given.
- Resume by rerunning the same command: finished slots are skipped, a session left open is finished first. A strict
  slot whose outcome is `invalid` gets one new session; never rename a suite to retry a model outcome.
- Results: the runner's own `result.json` and report per session, plus the suite ledger
  `runs/suite3/<suite>/ledger.jsonl` (`attempt` and `outcome` lines). The ledger overlays the v1 host checks on the
  runner's status: model identity, host errors, process cleanup and missing usage evidence make a slot `invalid`;
  requests to another editor or touching evaluation paths (runner state `Library/UnitySkills/eval`, runner code
  `Evaluation~`, this repository, host logs) make it `contaminated`.
- Token usage comes from the host itself: Claude's stream-json, Codex through the forwarding observer, counted over
  the attempt window and stored with `usage --record` (sources `stream`, `proxy`); without host messages in the
  window the runner's log-based usage stays.
- Sessions use `--private-dir pack/private` unless `--private-zip` or `--pack-dir` is given; the agents see a copy of
  that folder or zip under `/tmp/eve-p/`, kept for a later `submit --retry-verify` of the same suite.

### Private verifiers zip

Each release publishes the private verifiers twice: in the git tag (the runner downloads `eval/pack/private/<ID>.py`
from `v<plugin>`, `main` or `beta`) and as a release asset for machines that cannot reach GitHub:

```bash
python3 -B eval/tools/build_private_zip.py --out dist/unity-skills-eval-private-v3.0.0.zip
python3 -B SkillsForUnity/Evaluation~/run_eval.py start --project <P> --private-zip <zip>   # or the panel / agent
```

The zip holds exactly the manifest's `<ID>.py` files at its root, checked against the manifest hashes and read back
through the runner; equal files give a byte-identical zip.

### Reserved packs

Official comparisons use tasks that are never published. A reserved pack is a self-contained folder outside the
repository (or under the git-ignored `reserved/`): `manifest.json`, `tasks/<ID>/`, `private/<ID>.py` and
`solutions/<ID>.py`, built and checked with the same tools (`build_pack_manifest.py --pack-dir`,
`check_eval_pack.py --pack-dir`). Gate it and run it with `--pack-dir`:

```bash
python3 -B eval/cli.py selftest3 --pack-dir ~/usk-reserved/pack
python3 -B eval/cli.py suite3 --suite reserved-sonnet-v3 --models sonnet --pack-dir ~/usk-reserved/pack
```

Sessions on a `--pack-dir` pack are never official and their reports stay local; publish only summaries.

The sections below describe the frozen v1 harness.

## Completed calibration

The isolated Codex cohort completed **168/168** slots on 2026-10-12: raw **162 pass / 6 partial**,
separately adjudicated **164 pass / 4 partial**. Two documented T2 verifier false negatives were
corrected only in derived reports with user approval; raw scores and ledger remain unchanged.
All identity/isolation/cleanup/cohort checks passed, with no invalid attempts or replacements.

See [final report](reports/p0-calibration.md), [case freeze](reports/case-freeze-v1.json),
[integrity audit](reports/adjudication/final-evidence-audit.md), and
[release-note draft](reports/release-notes-draft.md). The original Claude cohort remains deferred;
a separate 42-slot Claude Code supplement has since completed with 41 pass and one partial,
unchanged after review. Current additional cohorts and their execution order are recorded in
the [extension report index](reports/extension/README.md). The 14-task bank
has 10 ceiling tasks after adjudication and does not establish a stable model ranking.
Future verifier patches are proposed separately; the current source is the frozen evaluated version.

## Requirements

- A Unity project open in the editor with UnitySkills installed and its server running (any
  project works; a fresh one from the **Universal 3D** template is recommended — the task bank
  requires URP). Operating mode `bypass`.
- Python 3.11+ (standard library only), Claude Code CLI and Codex CLI for model runs.

## Usage

See the [task catalog and testing guide](../docs/TESTING_GUIDE.md) for all 14 questions,
project setup, supported hosts, and output locations. Use a new suite name for a new
experiment; the completed cohorts below are historical evidence.

```bash
export UNITYSKILLS_EVAL_PROJECT=/absolute/path/to/YourEvalProject
export UNITYSKILLS_EVAL_MANUAL_FOCUS=1             # keep the test editor in the foreground manually
python3 eval/cli.py setup-check --record-baseline   # identity, environment, frozen docs, baseline
python3 eval/cli.py selftest                       # all task verifiers; no model calls
python3 eval/cli.py suite --host codex --models sol --effort high --suite my-sol-high-v1 --plan pilot
python3 eval/cli.py suite --host codex --models sol --effort high --suite my-sol-high-v1 --plan calibration
python3 eval/cli.py report --suite my-sol-high-v1 --models sol --output eval/reports/my-sol-high-v1.md
```

Run each step only after the preceding one succeeds. The pilot schedules 14 slots; optional
calibration fills the same cohort to three repetitions per task (42 slots). A pilot-only
report shows the remaining repetitions as missing. Do not bypass the selftest gate.

## Layout

| Path | Content |
|---|---|
| `harness/` | instance discovery, REST client, environment, fixture lifecycle, change journal, subject launcher, metrics, verifier library, classification, reports |
| `tasks/` | one module per task (`TASK`), registry in `tasks/__init__.py` |
| `design/task-specs.md` | design draft; current prompts and assertions live in `tasks/` |
| `protocol.md` | registered execution and analysis rules |
| `known-defects.md` | plugin defects that affect attribution |
| `probes.md` | capability probe results |
| `reports/` | aggregate reports (tracked) |
| `runs/` | local only: per-attempt reports, transcripts, ledger, frozen docs, selftest gate |
| `reserved/` | local only: reserved task variants |

Each attempt runs in its own scene and folder under `Assets/__UnitySkillsEval/<runId>/` and is
removed afterwards; the user's scenes are never saved. The harness never changes the operating
mode or token tier; it records them.

## Registered model configurations (2026-10-11)

The original Claude cohort is retained. The added Codex cohort is separate; combined results
measure agent configurations (host + model + reasoning effort), not models in isolation.

| Host | Alias | Requested model | Effort |
|---|---|---|---|
| Claude | opus | claude-opus-5-5[1M] | high |
| Claude | sonnet | claude-sonnet-5-5[1M] | high |
| Claude | fable | claude-fable-5-1[1M] | high |
| Codex | astra | gpt-6-astra | high |
| Codex | sol | gpt-6.1-sol | high |
| Codex | terra | gpt-5.6-terra | high |
| Codex | luna-max | gpt-6-luna | max |

The completed Codex cohort contains 168 attempts, including its 56-attempt pilot. The original
126-slot Claude plan remains deferred as historical scope; the completed 42-slot Claude Code
supplement is a separate cohort and does not fill that plan. `codex -p`
selects a configuration profile; `codex exec` is the noninteractive equivalent of `claude -p`.
The completed Codex suite is `p0-codex-isolated-v1`. The earlier `p0-codex-calibration`
cohort is quarantined as isolation diagnostics; do not resume it or include it in comparisons.
For a new experiment, choose a new suite name, then keep that name when filling reps 1–2.
Never rename a suite to selectively retry a
failed model outcome. Invalid attempts may be replaced once, with their reason retained.
A suite manifest rejects code, docs, environment or configuration drift on resume. Old selftest
gates without matching source hashes do not authorize model runs.

Codex dollar costs are unknown unless independently supplied: CLI token counts do not imply a
USD amount, and Codex does not enforce Claude's `--max-budget-usd`. The wall-clock timeout still
applies. The temporary forwarding observer records provider-returned model identity and usage;
request headers, credentials and response content are not saved in its metadata.
