"""Host token-usage readers (contract 8): the UsageRecord of an attempt, built from the agent host's own logs.

Public API (all functions are pure apart from reading host logs; t0/t1 are ISO strings or datetimes):
    detect_host(env) / capture_host(env, cwd) / merge_session_ids(*id_lists)
    read_usage(host, t0, t1, enabled, env)      host log -> UsageRecord
    detect_declared(host, t0, t1, env)          host/hostVersion/model/effort detected from the same log
    manual_usage(values, t0, t1, host, model)   user-entered numbers; parse_manual_args() parses `key=value` items
    unavailable_usage(host_kind, reason, t0, t1)
    ledger_summary(record)
Log problems never raise: they come back as `unavailableReason` and `errors` inside the record.
"""
import os
import re
from datetime import timedelta

from . import claude, codex, common

PARSER_VERSION = common.PARSER_VERSION

__all__ = ["PARSER_VERSION", "detect_host", "capture_host", "merge_session_ids", "read_usage", "detect_declared",
           "manual_usage", "parse_manual_args", "unavailable_usage", "ledger_summary"]

_SESSION_ENV = {"claude-code": "CLAUDE_CODE_SESSION_ID", "codex": "CODEX_THREAD_ID"}
_MANUAL_KEYS = ("input", "output", "cacheRead", "cacheWrite", "reasoning")
_MANUAL_NUMBER = re.compile(r"[0-9]{1,15}")
_DECLARED_LOOKBACK = timedelta(minutes=30)


def detect_host(env=None):
    """Contract 8.1: Claude Code first, then Codex, else none."""
    env = os.environ if env is None else env
    if env.get("CLAUDE_CODE_SESSION_ID") or env.get("CLAUDECODE") == "1":
        return "claude-code"
    if env.get("CODEX_THREAD_ID") or env.get("CODEX_SANDBOX"):
        return "codex"
    return "none"


def capture_host(env=None, cwd=None):
    """{kind, sessionIds, cwd} observed by the current process; call at `next` and `submit`."""
    env = os.environ if env is None else env
    kind = detect_host(env)
    sid = env.get(_SESSION_ENV.get(kind, ""))
    if cwd is None:
        try:
            cwd = os.getcwd()
        except OSError:
            cwd = ""
    return {"kind": kind, "sessionIds": [sid] if common.is_safe_id(sid) else [], "cwd": cwd}


def merge_session_ids(*id_lists):
    merged = []
    for ids in id_lists:
        for sid in ids or ():
            if common.is_safe_id(sid) and sid not in merged:
                merged.append(sid)
    return merged


def _host_parts(host):
    host = host if isinstance(host, dict) else {}
    kind = host.get("kind") if host.get("kind") in ("claude-code", "codex", "none") else "none"
    ids = merge_session_ids(host.get("sessionIds") if isinstance(host.get("sessionIds"), (list, tuple)) else ())
    cwd = host.get("cwd") if isinstance(host.get("cwd"), str) and host.get("cwd") else None
    return kind, ids, cwd


def _end(t1, now):
    """The end of a window; an open end means now."""
    if t1 is not None:
        return common.parse_time(t1)
    return common.parse_time(now) if now is not None else common.now_utc()


def _bounds(t0, t1, now):
    return common.parse_time(t0), _end(t1, now)


def unavailable_usage(host_kind, reason, t0, t1, now=None):
    """Record for a read that was not attempted (`disabled_by_user`, `no_host_session`) or has nothing to report."""
    start, end = _bounds(t0, t1, now)
    return common.build_record(host_kind, "none", [], common.window_block(start, end), reason=reason, now=now)


def read_usage(host, t0, t1, enabled=True, env=None, now=None):
    """UsageRecord (contract 4.7) for the window (t0, t1] of the attempt.

    `host` is the attempt's state.host ({kind, sessionIds, cwd}); `enabled` is session.usageReading.
    """
    env = os.environ if env is None else env
    kind, ids, cwd = _host_parts(host)
    start, end = _bounds(t0, t1, now)
    if not enabled:
        return unavailable_usage(kind, "disabled_by_user", start, end, now=now)
    if kind == "none" or not (ids or (kind == "codex" and cwd)):
        return unavailable_usage(kind, "no_host_session", start, end, now=now)
    reader = claude if kind == "claude-code" else codex
    window = common.window_block(start, end)
    try:
        outcome = reader.read(*((ids, start, end, env) if reader is claude else (ids, cwd, start, end, env)))
    except Exception as exc:  # a log-format surprise must never break scoring (contract 8.5)
        failure = [{"file": "", "line": 0, "error": type(exc).__name__}]
        return common.build_record(kind, reader.SOURCE, ids, window, reason="parse_error", errors=failure, now=now)
    ids = outcome.get("sessionIds") or ids
    if "tokens" not in outcome:
        return common.build_record(kind, reader.SOURCE, ids, window, reason=outcome["reason"],
                                   errors=outcome["errors"], now=now)
    return common.build_record(
        kind, reader.SOURCE, ids, window,
        model=common.values_block(outcome["models"], reader.MODEL_SOURCE),
        effort=common.values_block(outcome["efforts"], reader.EFFORT_SOURCE),
        tokens_block=outcome["tokens"], raw=outcome["raw"], messages=outcome["messages"],
        subagents=outcome["subagents"], reason=outcome["reason"], errors=outcome["errors"], now=now)


def detect_declared(host=None, t0=None, t1=None, env=None, now=None):
    """Detected parts of `declared` (contract 4.4/8.1): each {value, source} with source detected or unknown.

    Without a window the latest log state is used (Codex: a rollout active within the last 30 minutes).
    """
    env = os.environ if env is None else env
    kind, ids, cwd = _host_parts(host if host is not None else capture_host(env))
    end = _end(t1, now)
    start = common.parse_time(t0) if t0 is not None else end - _DECLARED_LOOKBACK
    try:
        if kind == "claude-code" and ids:
            found = claude.detect(ids, end, env)
        elif kind == "codex" and (ids or cwd):
            found = codex.detect(ids, cwd, start, end, env)
        else:
            found = {}
    except Exception:  # detection is best effort; unknown values are acceptable
        found = {}
    found["host"] = kind if kind != "none" else None
    unknown = {"value": None, "source": "unknown"}
    return {key: {"value": found[key], "source": "detected"} if found.get(key) else dict(unknown)
            for key in ("host", "hostVersion", "model", "effort")}


def parse_manual_args(items):
    """['input=N', 'output=N', 'cacheRead=N', ..., 'model=NAME'] -> (values, model); ValueError on bad input."""
    values, model = {}, None
    for item in items:
        key, sep, raw = str(item).partition("=")
        if not sep:
            raise ValueError("expected key=value: %s" % key[:40])
        if key == "model":
            if model is not None:
                raise ValueError("duplicate key model")
            model = common.label(raw)
            if model is None:
                raise ValueError("model must be a short printable name")
        elif key in _MANUAL_KEYS:
            if key in values:
                raise ValueError("duplicate key %s" % key)
            if _MANUAL_NUMBER.fullmatch(raw) is None:
                raise ValueError("%s must be a non-negative integer" % key)
            values[key] = int(raw)
        else:
            raise ValueError("unknown key %s" % key[:40])
    for key in ("input", "output"):
        if key not in values:
            raise ValueError("missing required key %s" % key)
    return values, model


def manual_usage(values, t0, t1, host="other", model=None, now=None):
    """User-entered usage (source `user`); `values` needs input and output, the rest defaults to 0."""
    unknown = sorted(str(key) for key in set(values) - set(_MANUAL_KEYS))
    if unknown:
        raise ValueError("unknown key %s" % unknown[0][:40])
    for key in ("input", "output"):
        if key not in values:
            raise ValueError("missing required key %s" % key)
    counts = {key: values.get(key, 0) for key in _MANUAL_KEYS}
    for key, value in counts.items():
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            raise ValueError("%s must be a non-negative integer" % key)
    if model is not None and common.label(model) is None:
        raise ValueError("model must be a short printable name")
    start, end = _bounds(t0, t1, now)
    named = [model] if model else []
    return common.build_record(
        host, "user", [], common.window_block(start, end),
        model=common.values_block(named, "user"),
        tokens_block=common.tokens(counts["input"], counts["output"], counts["cacheRead"], counts["cacheWrite"],
                                   counts["reasoning"]),
        now=now)


def ledger_summary(record):
    """The `usage` field of a ledger line (contract 4.8)."""
    block = record.get("tokens")
    return {"source": record.get("source"), "processed": block["processed"] if isinstance(block, dict) else None}


# Producer:Betsy
