"""Attempt classification (contract 2.9): first matching row wins, flags never change the status."""
import json

MODEL_OUTCOMES = ("pass", "partial", "fail")
FINAL_STATUSES = ("pass", "partial", "fail", "invalid", "contaminated", "cancelled")


def journal_reason(journal_state):
    """`journal_incomplete: ...` when the frozen journal does not prove the window, else None."""
    if journal_state.get("seqReset"):
        return "journal_incomplete: seq_reset"
    if journal_state.get("complete") is False:
        text = "journal_incomplete: gaps=%s" % json.dumps(journal_state.get("gaps") or [], separators=(",", ":"))
        if journal_state.get("conflicts"):
            text += " conflicts=%d" % journal_state["conflicts"]
        return text
    return None


def drift_reasons(drift):
    """environment_drift for settings, editor_restarted for a new editor process (contract 2.8)."""
    reasons = []
    settings = {k: v for k, v in (drift or {}).items() if k != "pid"}
    if settings:
        reasons.append("environment_drift: %s" % json.dumps(settings, ensure_ascii=False, sort_keys=True,
                                                            separators=(",", ":")))
    if "pid" in (drift or {}):
        reasons.append("editor_restarted")
    return reasons


def classify(card=None, cancelled=None, invalid=(), contaminated=(), flags=()):
    """(status, reasons). card is a finished Scorecard; cancelled is the abort reason."""
    flags = list(flags)
    if cancelled is not None:
        return "cancelled", ["aborted: %s" % cancelled] + flags
    if invalid:
        return "invalid", list(invalid) + flags
    if contaminated:
        return "contaminated", list(contaminated) + flags
    if card is None:
        return "invalid", ["verifier_error: NoScorecard: verification produced no scorecard"] + flags
    if card.strict:
        return "pass", flags
    failed = "failed: %s" % ",".join(card.failed())
    if card.score > 0:
        return "partial", [failed] + flags
    return "fail", [failed] + flags


def after_teardown(status, reasons, teardown):
    """A failed teardown invalidates a scored outcome (v1 runner.py:39-46); other statuses keep theirs."""
    if teardown and not teardown.get("ok") and status in MODEL_OUTCOMES:
        return "invalid", ["teardown_failed: %s" % (teardown.get("error") or "unknown")] + list(reasons)
    return status, list(reasons)


def flag_reasons(over_budget=False, forced_play_mode_exit=False, inflight_jobs=0, isolation=None,
                 verified_late=False):
    flags = []
    if over_budget:
        flags.append("over_budget")
    if forced_play_mode_exit:
        flags.append("forced_play_mode_exit")
    if inflight_jobs:
        flags.append("inflight_jobs: %d" % inflight_jobs)
    if isolation == "unverified":
        flags.append("isolation_unverified")
    if verified_late:
        flags.append("verified_late")
    return flags

# Producer:Betsy
