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

## Schedule

- Current S4 pilot: 14 tasks × 4 Codex configurations × 1 (rep 0; 56 attempts). S5 calibration adds
  reps 1–2 (168 total, n = 3 per cell). The Claude cohort is deferred; see the scope update below.
- Order: rep-major; tasks in bank order; models rotated per task (Latin rows). All models in a
  rep share the task seed (`tasks.run_seed`), so they face identical fixtures.
- Cost/time estimate printed before each batch (`suite` prints it first).

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

## Post-hoc changes

2026-10-11, before registered pilot/calibration data: retained all three Claude configurations;
added Codex gpt-6-astra/high, gpt-6.1-sol/high, gpt-5.6-terra/high, gpt-6-luna/max per user decision.
The complete target is 294 attempts (14 × 7 × 3), with original host cohorts kept identifiable.
No model ranking is implied by the user's model-role correspondence.

Codex runs use `codex exec`, a fresh temporary CODEX_HOME with only transport/auth configuration,
and frozen protocol text supplied as prompt context. Global instructions, memories, plugins,
MCP tools and host skill discovery are disabled. A per-attempt loopback forwarding observer
records actual provider response.model and token metadata; it never treats the requested model
as actual identity. Missing identity or canonical token evidence invalidates the attempt.
Codex costs are unknown, not zero; no USD cap is claimed. CLI wall time remains bounded.

Pilot and calibration now share one suite per host, avoiding duplicate rep 0. Host, model and
effort identify a slot. Gate records are tied to code hashes, and suite manifests pin code,
docs and environment on resume. Duplicate valid slots and mixed cohorts are rejected in reports.
Cleanup failure invalidates a scored attempt and stops the suite until recovery.

Inherited one-off Claude smoke results remain historical diagnostics, outside the registered
pilot/calibration cohorts. Changes above do not reclassify or erase historical attempts.

### Scope update, 2026-10-11

The user confirmed the Claude channel is currently unavailable. This run completes only the
four Codex configurations (168 attempts, including the 56-attempt pilot). Claude's three
configurations, previous diagnostics and suite definitions are retained for a later resumption;
no Claude capability results are inferred from channel errors. The combined 294-attempt target
is deferred, and release notes must distinguish completed Codex evidence from pending Claude.

### Isolation incident and restart authorization, 2026-10-11

The first Codex cohort `p0-codex-calibration` is quarantined as host-isolation diagnostics:
Codex 0.161.0 injected the user skill catalog despite skip_host_skill_discovery. Eight attempts
completed (seven pass, one contaminated); S04/Terra was interrupted and its fixture cleaned.
All original reports and ledger rows remain unchanged. They are excluded from capability
comparisons. The user explicitly approved a new, isolated 168-attempt cohort after the fix.
New suite identity: `p0-codex-isolated-v1`. No valid attempt is selectively retried: this is a
whole-cohort restart for documented infrastructure failure, with the original evidence retained.
The new cohort must pass offline prompt isolation checks before any model attempt.

### Completed calibration and approved post-hoc adjudication, 2026-10-12

The isolated168-slot cohort completed with162 raw passes and6 raw partials; no invalid,
contaminated or replacement attempts. The user separately approved two evidence-based T2
corrections (D3 first-empty-label selection and D12 pathless rollback deletion), yielding
164 derived passes and4 partials. All12 T2 samples and all168 scope journals were reviewed
uniformly. Original reports, ledger and frozen source are unchanged. The separate generator
validates original evidence hashes and changes copied report objects only. CaseVersion1
remains frozen in `results/case-freeze-v1.json`; proposed future fixes require new identities.
