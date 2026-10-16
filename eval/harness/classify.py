"""Attempt status per plan section 8.4 and the append-only attempt ledger.

Statuses: pass / partial / fail are model outcomes; invalid (infra or verifier error),
contaminated (environment drift or non-REST writes during the attempt), product_defect,
unsupported and awaiting_user are not, and never enter capability statistics. Only invalid
attempts may be rerun, each rerun links to the attempt it replaces and states why; nothing is
ever deleted from the ledger (no selective reruns).
"""
import datetime
import json
import os
import secrets

MODEL_OUTCOMES = ("pass", "partial", "fail")
API_RETRY_FAULT = 3          # doc-bench registered threshold (round2 protocol)


def new_attempt_id():
    return secrets.token_hex(6)


def classify(scorecard, subject=None, metrics=None, drift=None, journal_complete=True,
             non_rest_changes=0, verifier_error=None, model_ok=True, forced=None, tampered=None):
    reasons = []
    if forced:
        return forced[0], [forced[1]]
    if verifier_error:
        return "invalid", ["verifier_error: %s" % verifier_error]
    if subject is not None:
        process_cleanup = subject.get("processCleanup") or {}
        if subject.get("timedOut") and not process_cleanup.get("ok", True):
            return "invalid", ["subject_process_cleanup_unconfirmed: %s" % "; ".join(
                process_cleanup.get("errors") or ["process group state unknown"])]
        finalization_errors = subject.get("finalizationErrors") or []
        if finalization_errors:
            return "invalid", ["subject_finalization_error: %s" % "; ".join(finalization_errors)]
        if not model_ok:
            return "invalid", ["model_mismatch: requested %s, saw init=%s messages=%s" % (
                subject.get("model"), (metrics or {}).get("init", {}).get("model"),
                (metrics or {}).get("assistantModels"))]
        evidence = (metrics or {}).get("evidenceProblems") or []
        if evidence:
            return "invalid", ["metric_evidence: %s" % "; ".join(evidence)]
        result = (metrics or {}).get("result") or {}
        if not result and not subject.get("timedOut"):
            return "invalid", ["host_error: no result event (rc=%s)" % subject.get("returncode")]
        if (metrics or {}).get("apiRetries", 0) >= API_RETRY_FAULT:
            return "invalid", ["api_retries=%d" % metrics["apiRetries"]]
        if result.get("is_error") and result.get("api_error_status"):
            return "invalid", ["host_api_error: %s" % result.get("api_error_status")]
    if metrics:
        if metrics.get("foreignPorts"):
            return "contaminated", ["requests to other instances on ports %s" % metrics["foreignPorts"]]
        if metrics.get("suspiciousAccess"):
            return "contaminated", ["subject touched evaluation/user paths: %s" % sorted(
                {a["marker"] for a in metrics["suspiciousAccess"]})]
    if tampered:
        return "contaminated", ["evaluation files changed during the attempt: %s" % tampered]
    if non_rest_changes:
        return "contaminated", ["%d non-REST editor changes during the attempt" % non_rest_changes]
    if drift:
        return "invalid", ["environment drift: %s" % json.dumps(drift, ensure_ascii=False)]
    if not journal_complete:
        return "invalid", ["change journal incomplete (dropped/truncated); side effects unverifiable"]
    if subject is not None:
        if subject.get("timedOut"):
            reasons.append("timed_out")
        result = (metrics or {}).get("result") or {}
        if result.get("subtype") and result.get("subtype") != "success":
            reasons.append("host_subtype=%s" % result.get("subtype"))
    if scorecard.strict:
        return "pass", reasons
    if scorecard.score > 0:
        return "partial", reasons + ["failed: %s" % ",".join(scorecard.failed())]
    return "fail", reasons + ["failed: %s" % ",".join(scorecard.failed())]


class Ledger(object):
    def __init__(self, path):
        self.path = path
        os.makedirs(os.path.dirname(path), exist_ok=True)

    def entries(self):
        if not os.path.exists(self.path):
            return []
        with open(self.path, "r", encoding="utf-8") as handle:
            return [json.loads(line) for line in handle if line.strip()]

    def append(self, entry):
        entry = dict(entry)
        entry.setdefault("recordedAt", datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"))
        with open(self.path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(entry, ensure_ascii=False) + "\n")
        return entry

    def cell(self, suite, task, model):
        return [e for e in self.entries() if e.get("suite") == suite and e.get("task") == task
                and e.get("model") == model and e.get("kind") == "model"]

    def valid_count(self, suite, task, model):
        return sum(1 for e in self.cell(suite, task, model) if e.get("status") in MODEL_OUTCOMES)

    def rerunnable(self, suite, task, model):
        """Invalid attempts not yet replaced by a rerun."""
        cell = self.cell(suite, task, model)
        replaced = {e.get("rerunOf") for e in cell if e.get("rerunOf")}
        return [e for e in cell if e.get("status") == "invalid" and e.get("attemptId") not in replaced]

# Producer:Betsy
