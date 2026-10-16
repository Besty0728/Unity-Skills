# Future scope-verifier identity resolution

This directory contains an evaluator-only proposal. Do not apply it to the
frozen source used by `p0-codex-isolated-v1`. It does not rewrite or fully
automate the adjudication of T2 attempt `091aeec4ce3f`.

## Confirmed defect and boundary

T2 D12 failed on two pathless `gameobject_destroyed` records produced by a
transactional rollback. Independent evidence in
`eval/reports/adjudication/t2-terra-091aeec4ce3f.{md,json}` establishes that
both destroyed objects were newly created within R. The uniform
`scope-event-review` found no other pathless destruction in the reviewed
cohort.

The current verifier treats every pathless scene change as outside scope. A
blanket exception for destruction would be unsafe: a pathless record could
represent deletion of a fixture object or an object in another scene.

`scope-identity-resolution.patch` therefore maintains an ordered identity-to-
location table only from two strong journal observations:

- `gameobject_created` with `entityId` or `instanceId` and `path`;
- `gameobject_reparented` with the same identity and `toPath`.

A later pathless destruction is allowed only when that identity's latest
observed location is inside R and its known scene, if present, is the run
scene. The entry is removed after destruction to prevent Unity instance-ID
reuse from inheriting stale scope. Unknown identities remain violations.

This also handles transactional rollback objects when the journal observed
their creation before rollback. The offline cases specifically require:

- in-scope create, rollback, then pathless destruction: pass;
- unknown identity destruction: fail;
- outside-created identity destruction: fail;
- in-scope identity moved outside, then destroyed: fail;
- outside-created identity adopted into R, then destroyed: pass.

## Deliberate limitation for `091aeec4ce3f`

The saved journal does not contain enough machine-readable evidence to clear
both records with this conservative rule. Button `-979220` has a later
property record with its in-scope path, after destruction; EventSystem
`-979254` has no identity-bound path anywhere in the journal. Its path exists
only in the saved API transcript.

The transcript is sound evidence for the completed human adjudication, but a
general verifier must not parse arbitrary shell output as authoritative scope
data. Doing so would allow echoed or transformed JSON to manufacture identity
evidence. This proposal consequently leaves the historical incident to the
separate adjudication and continues to fail closed for the equivalent unknown
future event.

A product-side follow-up should make destruction records self-contained, or
at least ensure `gameobject_created` and `gameobject_reparented` records always
carry `entityId` plus their path before transactional rollback removes the
object. That observability change is intentionally outside this evaluator-only
proposal.

## Integration after calibration

1. Finish and archive the 168-attempt frozen cohort. Preserve raw reports,
   streams, journals, ledger rows, freeze manifests, and the version 1 gate.
2. Confirm the reviewed frozen hashes before applying the patch:
   `eval/harness/journal.py` must be
   `c9fd802f14e293a00cb2d134ff9e0cc04757d6004717f6ea89586abb8a13f7a6`.
3. Apply from the repository root with
   `git apply eval/design/scope-verifier-fix/scope-identity-resolution.patch`.
4. Run `python3 -m unittest discover eval/tests`. Then rerun the complete
   source-bound selftest, because changing a shared harness module invalidates
   every task's `_gate_hash`, not only T2's.
5. The patch advances the base `Task.case_version` to `"2"`. Record the new
   harness and per-task gate hashes in a new freeze manifest and use a new
   suite/cohort identity such as `p0-codex-isolated-v2`. Do not append version 2
   attempts to `p0-codex-isolated-v1`; its cohort manifest must remain bound to
   the frozen version 1 source.

No Unity or model invocation is needed to validate the evaluator logic. The
candidate can be applied to a temporary source copy and exercised with the
offline unit suite.
