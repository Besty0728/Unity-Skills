# P0 evaluation protocol (registered before model data)

Spec: `Unity-Skills-Agent评测与档位推荐计划.md` v0.3. This file freezes how P0 runs are executed,
classified and analysed. Any change after S4 data exists is marked "post-hoc" with a date.

## Environment

- Calibration project `UnitySkillsEval2022` (Unity 2022.3.62f2, URP 14.0.12, template
  `com.unity.template.urp-blank`), plugin `com.besty.unity-skills` from git `#v2.9.0` (58d3339d).
- Global settings recorded, never changed by the harness: mode `bypass`, tier `maximum`,
  surface `full`, dryRunPolicy `off`. Drift during an attempt ⇒ `invalid` (one rerun).
- Subjects: `claude -p` 2.1.287 with `--model claude-opus-5-5[1M] | claude-sonnet-5-5[1M] |
  claude-fable-5-1[1M] --effort high`, flags from the doc-bench method (`--safe-mode`,
  `--strict-mcp-config`, `--setting-sources project`, `--no-session-persistence`,
  `--tools Bash,Read,Write,Edit`), per-run cap `--max-budget-usd` (basic 15, composite 40).
- Docs: frozen copy of the installed package's `unity-skills~` (tree hash recorded per run), at a
  random neutral `/tmp` path; warm mode (root SKILL.md appended to the system prompt).
- Subject cwd: a fresh empty `/tmp` directory. Environment: inherited minus session-linked and
  reasoning-budget variables (`harness/subject.py` DROP_ENV); inherited keys recorded.
- One attempt per editor at a time (file lock). Each attempt runs in its own scene and folder
  `Assets/__UnitySkillsEval/<runId>/`, deleted afterwards.

## Attempt status (first match wins)

1. `unsupported` — environment lacks a requirement (not URP, mode not bypass, editor busy).
2. `awaiting_user` — an open scene has unsaved changes.
3. `invalid` — model mismatch (init.model ≠ requested or assistant messages on another model;
   aborts the suite), no result event, host API error, ≥3 api_retry events, verifier exception or
   unobservable evidence, incomplete change journal, settings drift.
4. `contaminated` — requests to another editor's port, subject touched repo / `.claude` paths,
   evaluation files changed during the attempt, non-REST editor changes.
5. `pass` (all assertions), `partial` (score > 0), `fail` (score 0). Timeouts and budget
   exhaustion are scored as they stand and flagged.
6. `product_defect` — review label for failures matching `known-defects.md` signatures.

Only `invalid` attempts are rerun, once per slot, linked by `rerunOf` with the reason. No other
reruns; nothing is removed from `runs/ledger.jsonl`.

## Selftest gate

Before any model run of a task: reference solution ×2 strict pass with identical fixture
fingerprint, every negative fails exactly its declared assertions, every alternative passes
(`python3 eval/cli.py selftest`). `run`/`suite` refuse tasks whose gate is not green.

## Analysis (S5, descriptive; n = 3 per cell is not a significance test)

- Main table: per task × model strict passes k/3, mean completion C (0–100), median processed
  tokens (modelUsage), median wall, median HTTP requests to the eval port, status counts.
- Discrimination per task: D = max pairwise difference in strict passes, ΔC = max pairwise
  difference in mean completion. D ≥ 2 or ΔC ≥ 25 ⇒ discriminating; D = 1 or 10 ≤ ΔC < 25 ⇒ weak;
  otherwise flat.
- Ceiling: every configured valid attempt strict-passes (12 per task in the current Codex cohort).
  Floor: zero strict passes and aggregate mean C < 30. Both require the complete rep grid and
  complete cohort evidence; otherwise unknown. Review task wording before freezing when flagged.
- Instability: a model's completion range within a cell ≥ 50.
- Budget calibration: wall budget = clamp(ceil(1.5 × P90 wall of strict-pass attempts), 0.5×,
  2× of the pilot timeout); the same rule for HTTP requests.
- Cost: compared on passing attempts only; totals over all attempts reported separately;
  processed tokens are primary, cache-read listed apart.
- The report is a calibration of task discrimination, not a model ranking.

## Freeze (caseVersion 1)

Prompt text, fixture recipe and public seeds, assertions/weights/tolerances/critical flags,
answer schema, budgets, docs tree hash, plugin commit and harness hash at the end of S5.

CaseVersion 1 remains frozen in `results/case-freeze-v1.json`; proposed future fixes require new identities.
