"""Claude Code session log reader (contract 8.2).

Only `assistant` lines are looked at, and only: timestamp, effort, requestedModel, version, message.id,
message.model and the numeric fields of message.usage.
"""
import fnmatch
import os
import re
from collections import namedtuple

from . import common

SOURCE = "claude-session-log"
MODEL_SOURCE = "provider-returned"
EFFORT_SOURCE = "host-reported"
RAW_KEYS = ("input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens",
            "thinking_tokens")

# Claude Code writes a local zero-usage placeholder (model "<synthetic>") for failed API calls; it is no response.
_PLACEHOLDER_MODEL = "<synthetic>"
_ASSISTANT = re.compile(r'"assistant"')

Row = namedtuple("Row", "id ts counts model effort sub file")


def projects_root(env):
    override = env.get("USK_EVAL_CLAUDE_ROOT")
    if override:
        return override
    config = env.get("CLAUDE_CONFIG_DIR")
    if config:
        return os.path.join(config, "projects")
    return os.path.join(common.home_dir(env), ".claude", "projects")


def session_files(root, session_ids):
    """(main session files, subagent files) for the given ids, each sorted by path."""
    mains, subs = set(), set()
    try:
        projects = sorted(entry.path for entry in os.scandir(root) if entry.is_dir())
    except OSError:
        return [], []
    for sid in session_ids:
        if not common.is_safe_id(sid):
            continue
        for project in projects:
            main = os.path.join(project, sid + ".jsonl")
            if os.path.isfile(main):
                mains.add(main)
            subdir = os.path.join(project, sid, "subagents")
            try:
                names = os.listdir(subdir)
            except OSError:
                continue
            for name in names:
                path = os.path.join(subdir, name)
                if fnmatch.fnmatchcase(name, "agent-*.jsonl") and os.path.isfile(path):
                    subs.add(path)
    return sorted(mains), sorted(subs)


def _row(obj, path, lineno, errors, sub):
    message = obj.get("message")
    if obj.get("type") != "assistant" or not isinstance(message, dict):
        return None
    mid = message.get("id")
    if not isinstance(mid, str) or not mid:
        return None
    model = common.label(message.get("model"))
    if model == _PLACEHOLDER_MODEL:
        return None
    try:
        ts = common.parse_time(obj.get("timestamp"))
    except (ValueError, TypeError, OverflowError) as exc:
        errors.add(path, lineno, type(exc).__name__)
        return None
    usage = message.get("usage")
    usage = usage if isinstance(usage, dict) else {}
    details = usage.get("output_tokens_details")
    thinking = common.as_count(details.get("thinking_tokens")) if isinstance(details, dict) else 0
    counts = (common.as_count(usage.get("input_tokens")), common.as_count(usage.get("output_tokens")),
              common.as_count(usage.get("cache_read_input_tokens")),
              common.as_count(usage.get("cache_creation_input_tokens")), thinking)
    return Row(mid, ts, counts, model, common.label(obj.get("effort")), sub, path)


def read(session_ids, t0, t1, env):
    """Outcome for the (t0, t1] window; `reason` is a contract unavailableReason or None."""
    errors = common.ErrorLog()
    mains, subs = session_files(projects_root(env), session_ids)
    if not mains:
        return {"reason": "session_log_not_found", "errors": errors}
    rows = {}
    counter = {"matched": 0, "parsed": 0}
    for path, sub in [(p, False) for p in mains] + [(p, True) for p in subs]:
        for lineno, obj in common.iter_json(path, errors, _ASSISTANT, counter):
            row = _row(obj, path, lineno, errors, sub)
            if row is not None:
                # One API response is split over several lines; the last line of an id carries its final usage (8.2 #4).
                rows[row.id] = row
    if counter["matched"] and not counter["parsed"]:
        return {"reason": "parse_error", "errors": errors}
    kept = [row for row in rows.values() if t0 < row.ts <= t1]
    total, sub_total, sub_files = [0] * 5, [0] * 5, set()
    for row in kept:
        for index, value in enumerate(row.counts):
            total[index] += value
            if row.sub:
                sub_total[index] += value
        if row.sub:
            sub_files.add(row.file)
    return {
        "reason": None if kept else "no_messages_in_window",
        "tokens": common.tokens(*total),
        "raw": dict(zip(RAW_KEYS, total)),
        "messages": len(kept),
        "subagents": {"files": len(sub_files), "tokens": common.tokens(*sub_total)},
        "models": [row.model for row in kept if row.model],
        "efforts": [row.effort for row in kept if row.effort],
        "errors": errors,
    }


def detect(session_ids, end, env):
    """Latest version / requested model / effort seen in the main session log up to `end` (declared detection, 8.1)."""
    errors = common.ErrorLog()
    mains, _subs = session_files(projects_root(env), session_ids)
    found = {}
    for path in mains:
        for _lineno, obj in common.iter_json(path, errors, _ASSISTANT):
            message = obj.get("message")
            if obj.get("type") != "assistant" or not isinstance(message, dict):
                continue
            model = common.label(message.get("model"))
            if model == _PLACEHOLDER_MODEL:
                continue
            try:
                if common.parse_time(obj.get("timestamp")) > end:
                    continue
            except (ValueError, TypeError, OverflowError):
                continue
            for key, value in (("hostVersion", obj.get("version")),
                               ("model", common.label(obj.get("requestedModel")) or model),
                               ("effort", obj.get("effort"))):
                value = common.label(value)
                if value:
                    found[key] = value
    return found


# Producer:Betsy
