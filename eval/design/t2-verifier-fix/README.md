# T2 D3 future verifier fix

This directory contains a proposal only. Do not apply it while the 168-attempt
`p0-codex-isolated-v1` calibration is running. The frozen `eval/tasks/t2.py`
remains unchanged.

## Finding

T2 D3 says that **a** `TextMeshProUGUI` below `StartButton` must read
`Start Game`. The version 1 helper returns the first TMP value it encounters,
including `""`, and stops before checking later siblings. Attempt
`c854bf101f47` therefore received a false D3 failure even though its later
`StartButtonLabel` read `Start Game`. The independent evidence is recorded in
`eval/reports/adjudication/t2-d3-review-snapshot.md` and its JSON companion.

The frozen T2 source reviewed here has SHA-256
`3da55ba096de117d25071266023ca908baeeb9d766e101f390af50e3a97ecceb`.

## Proposed change

Apply `t2-existential-tmp.patch` only after calibration and adjudication are
complete. It makes D3 existential by collecting the TMP text values below the
button and checking membership. The traversal covers all descendants, matching
the assertion's "under the button" wording and removing the current arbitrary
two-level cap. The diagnostic `actual` becomes the complete ordered list of TMP
values.

The patch adds two task-level selftest cases:

- `negative:wrong_text` changes the only label to `Launch` and must fail exactly
  D3. This proves that merely having a TMP component is insufficient.
- `alternative:later_matching_tmp` empties the original first TMP label and
  appends a second TMP label containing `Start Game`; it must strict-pass. This
  directly guards the observed false-negative shape.

Together with the two reference repeats, determinism case, existing seven
negatives, and existing serialized-listener alternative, T2's selftest matrix
grows from 11 to 13 cases.

## Integration and identity

1. Finish and archive the frozen 168-attempt calibration. Preserve every raw
   report, stream, ledger row, source-freeze manifest, and the version 1 gate.
   Apply any adjudication as a separate derived analysis; do not rewrite raw
   scores.
2. Confirm the source SHA-256 above still matches `eval/tasks/t2.py`, then apply
   the patch from the repository root with
   `git apply eval/design/t2-verifier-fix/t2-existential-tmp.patch`.
3. The patch sets `T2.case_version` to `"2"`. The source change also produces a
   new `_gate_hash("T2")`, invalidating the version 1 T2 gate automatically.
   Record the new task source hash in the next freeze manifest.
4. Run the offline test suite, then run
   `python3 eval/cli.py selftest --task T2 --repeat 2` against the designated
   evaluation editor. Require all 13 cases to be green, including the exact D3
   failure set for `wrong_text` and strict pass for `later_matching_tmp`.
5. Use a new suite/cohort identity, for example `p0-codex-isolated-v2`, for any
   model attempts made with the corrected verifier. Never resume or append them
   to `p0-codex-isolated-v1`: `_check_cohort` should reject the changed T2 source
   hash, and `caseVersion: "2"` gives reports an explicit case identity.

The proposal changes only `eval/tasks/t2.py`; it does not alter the prompt,
fixture, assertion weights, or answer schema.
