"""Helpers shared by the host-log readers (contract 8).

Parsing is whitelist-only: a reader copies numbers, ids and short labels out of a log line and nothing else.
Failures are reported as (file name, line number, exception type name); message text never leaves this module.
"""
import json
import os
import re
from datetime import datetime, timezone

from .. import store

PARSER_VERSION = 1
MAX_ERRORS = 20
HOSTS = ("claude-code", "codex", "other", "none")
TOKEN_FIELDS = ("input", "output", "cacheRead", "cacheWrite", "reasoning", "processed")

UTC = timezone.utc
_SAFE_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")
_ERROR_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]{0,63}")
_MAX_LABEL = 120


def home_dir(env):
    """Home of the current user; on Windows USERPROFILE wins because Git Bash exports HOME as a POSIX path."""
    for key in (("USERPROFILE", "HOME") if os.name == "nt" else ("HOME", "USERPROFILE")):
        if env.get(key):
            return env[key]
    return os.path.expanduser("~")


def is_safe_id(value):
    """Host ids end up in file names, so only plain tokens are accepted (no separators, no glob characters)."""
    return isinstance(value, str) and _SAFE_ID.fullmatch(value) is not None


def label(value):
    """A short printable string such as a model name or an effort level; anything else is dropped."""
    if isinstance(value, str) and 0 < len(value) <= _MAX_LABEL and value.isprintable() and value == value.strip():
        return value
    return None


def as_count(value):
    """Token counters are non-negative integers; missing, fractional, boolean or negative values count as 0."""
    return value if isinstance(value, int) and not isinstance(value, bool) and value >= 0 else 0


def parse_time(value):
    """Aware UTC datetime from a datetime or a timestamp string (strings go through store.parse_utc, contract 0)."""
    if isinstance(value, datetime):
        parsed = value
    elif isinstance(value, str):
        parsed = store.parse_utc(value)
    else:
        raise ValueError("timestamp")
    return parsed if parsed.tzinfo is not None else parsed.replace(tzinfo=UTC)


def fmt_utc(value):
    """Contract time format (store.iso): YYYY-MM-DDTHH:MM:SS.mmmZ."""
    return store.iso(parse_time(value))


def now_utc():
    return parse_time(store.now_iso())


class ErrorLog(object):
    """At most MAX_ERRORS entries of {file, line, error}; file is a bare file name, error an exception type name."""

    def __init__(self):
        self._items = []

    def add(self, path, line, error):
        if len(self._items) < MAX_ERRORS:
            name = error if isinstance(error, str) and _ERROR_NAME.fullmatch(error) else "Error"
            self._items.append({"file": os.path.basename(path) if path else "", "line": int(line), "error": name})

    def items(self):
        return [dict(item) for item in self._items]


def iter_json(path, errors, interest=None, counter=None, max_lines=None):
    """Yield (line number, dict) for the JSON object lines of a JSONL file.

    `interest` is a compiled regex used as a cheap necessary-condition prefilter, so huge lines of other types
    (tool results, compaction summaries) are never parsed. `counter` collects matched/parsed line counts.
    """
    try:
        handle = open(path, "r", encoding="utf-8-sig", errors="replace", newline="\n")
    except OSError as exc:
        errors.add(path, 0, type(exc).__name__)
        return
    lineno = 0
    try:
        with handle:
            for raw in handle:
                lineno += 1
                if max_lines is not None and lineno > max_lines:
                    return
                if raw.isspace() or (interest is not None and interest.search(raw) is None):
                    continue
                if counter is not None:
                    counter["matched"] += 1
                try:
                    obj = json.loads(raw)
                except (ValueError, RecursionError) as exc:
                    errors.add(path, lineno, type(exc).__name__)
                    continue
                if isinstance(obj, dict):
                    if counter is not None:
                        counter["parsed"] += 1
                    yield lineno, obj
    except OSError as exc:
        errors.add(path, lineno + 1, type(exc).__name__)


def tokens(input_, output, cache_read, cache_write, reasoning, processed=None):
    """Token block; `reasoning` is already part of `output` and never enters `processed`."""
    if processed is None:
        processed = input_ + output + cache_read + cache_write
    return {"input": input_, "output": output, "cacheRead": cache_read, "cacheWrite": cache_write,
            "reasoning": reasoning, "processed": processed}


def zero_tokens():
    return tokens(0, 0, 0, 0, 0, 0)


def add_tokens(left, right):
    return {key: left[key] + right[key] for key in TOKEN_FIELDS}


def values_block(values, source):
    values = sorted(set(values))
    return {"values": values, "source": source if values else None}


def window_block(start, end):
    return {"from": fmt_utc(start), "to": fmt_utc(end)}


def build_record(host, source, session_ids, window, model=None, effort=None, tokens_block=None, raw=None,
                 messages=None, subagents=None, reason=None, errors=None, now=None):
    """UsageRecord (contract 4.7) with every field present, in contract order."""
    return {
        "host": host if host in HOSTS else "other",
        "source": source,
        "sessionIds": list(session_ids),
        "window": window,
        "model": model if model is not None else values_block([], None),
        "effort": effort if effort is not None else values_block([], None),
        "tokens": tokens_block,
        "raw": raw,
        "messages": messages,
        "subagents": subagents,
        "parserVersion": PARSER_VERSION,
        "unavailableReason": reason,
        "errors": errors.items() if isinstance(errors, ErrorLog) else list(errors or []),
        "computedAt": store.now_iso() if now is None else fmt_utc(now),
    }


# Producer:Betsy
