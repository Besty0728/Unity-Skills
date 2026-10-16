# UnitySkills agent evaluation (P0)

Measures how well an agent configuration (host + model + reasoning effort) drives a Unity project
through UnitySkills: a bank of 14 tasks (6 basic, 8 composite), an independent verifier that reads
the editor state back, and a calibration across models. Lives outside `SkillsForUnity/` and is not
shipped with the package. P1 will move the same rules into the editor panel.

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
