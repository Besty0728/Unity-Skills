"""Codex rollout reader (contract 8.3).

Token usage comes from the cumulative `token_count` counters (difference between the window bounds).
Read fields: top-level type/timestamp (plus `ordinal`, see scan_rollout), payload.type/id/session_id/parent_thread_id/
thread_source/cli_version/model/effort/role, payload.cwd (compared, never stored), payload.info.total_token_usage.*.
"""
import os
import re
from datetime import timedelta

from . import common

SOURCE = "codex-rollout"
MODEL_SOURCE = "host-requested"
EFFORT_SOURCE = "host-reported"
COUNTERS = ("input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens",
            "reasoning_output_tokens", "total_tokens")
MAX_DAY_SPAN = 400

_ZERO = (0,) * len(COUNTERS)
_SESSION_META = re.compile(r'"session_meta"')
_INTEREST = re.compile(r'"(?:token_count|turn_context|assistant)"')


def sessions_root(env):
    override = env.get("USK_EVAL_CODEX_ROOT")
    if override:
        return override
    home_dir = env.get("CODEX_HOME")
    if home_dir:
        return os.path.join(home_dir, "sessions")
    return os.path.join(common.home_dir(env), ".codex", "sessions")


def cwd_key(value):
    if not isinstance(value, str) or not value:
        return None
    if value.startswith("\\\\?\\"):
        value = "\\\\" + value[8:] if value[4:].startswith("UNC\\") else value[4:]
    return os.path.normcase(os.path.realpath(value))


def _is_rollout(name):
    return name.startswith("rollout-") and name.endswith(".jsonl")


def _walk(root, accept):
    found = []
    for dirpath, _dirs, names in os.walk(root):
        found.extend(os.path.join(dirpath, name) for name in names if accept(name))
    return sorted(found)


def _day_dirs(root, t0, t1):
    """Existing YYYY/MM/DD directories covering the local dates of [t0 - 1 day, t1 + 1 day]."""
    try:
        first = (t0 - timedelta(days=1)).astimezone().date()
        last = (t1 + timedelta(days=1)).astimezone().date()
    except (OverflowError, OSError, ValueError):
        return []
    if (last - first).days > MAX_DAY_SPAN:
        return []
    days, day = [], first
    while day <= last:
        path = os.path.join(root, "%04d" % day.year, "%02d" % day.month, "%02d" % day.day)
        if os.path.isdir(path):
            days.append(path)
        day += timedelta(days=1)
    return days


def list_rollouts(root, t0, t1):
    days = _day_dirs(root, t0, t1)
    if not days:
        return _walk(root, _is_rollout)  # no date layout under the root: recursive scan
    found = []
    for day in days:
        try:
            names = os.listdir(day)
        except OSError:
            continue
        found.extend(os.path.join(day, name) for name in names if _is_rollout(name))
    return sorted(found)


def read_meta(path, errors):
    """Whitelisted fields of the first session_meta line (a fork's later copy of the parent's meta is ignored)."""
    lines = common.iter_json(path, errors, _SESSION_META, max_lines=5)
    try:
        for _lineno, obj in lines:
            payload = obj.get("payload")
            if obj.get("type") != "session_meta" or not isinstance(payload, dict):
                continue
            ident = payload.get("id") if common.is_safe_id(payload.get("id")) else None
            session = payload.get("session_id") if common.is_safe_id(payload.get("session_id")) else ident
            parent = payload.get("parent_thread_id") if common.is_safe_id(payload.get("parent_thread_id")) else None
            return {
                "id": ident, "session": session, "parent": parent,
                "subagent": payload.get("thread_source") == "subagent",
                "version": common.label(payload.get("cli_version")),
                "cwd": cwd_key(payload.get("cwd")),
                "history_start": common.as_count(payload.get("subagent_history_start_ordinal")),
            }
    finally:
        lines.close()
    return None


def _is_below(value, limit):
    return isinstance(value, int) and not isinstance(value, bool) and value < limit


class Scan(object):
    """Per-rollout result: counters at the window bounds plus what the window contains."""

    def __init__(self):
        self.before = None
        self.after = None
        self.in_window = 0
        self.messages = 0
        self.reset = False
        self.token_events = 0
        self.token_valid = 0
        self.models, self.efforts = set(), set()
        self.model_before = self.effort_before = None
        self.last_model = self.last_effort = None


def scan_rollout(path, meta, t0, t1, errors):
    # A forked subagent rollout starts with a copy of the parent's history, stamped with the fork time. Lines with an
    # ordinal below subagent_history_start_ordinal belong to the parent and must not count again.
    skip_below = meta["history_start"] if meta["subagent"] else 0
    scan, prev = Scan(), None
    for lineno, obj in common.iter_json(path, errors, _INTEREST):
        kind, payload = obj.get("type"), obj.get("payload")
        if kind not in ("event_msg", "turn_context", "response_item") or not isinstance(payload, dict):
            continue
        if _is_below(obj.get("ordinal"), skip_below):
            continue
        try:
            ts = common.parse_time(obj.get("timestamp"))
        except (ValueError, TypeError, OverflowError) as exc:
            errors.add(path, lineno, type(exc).__name__)
            continue
        if ts > t1:
            continue
        if kind == "event_msg":
            info = payload.get("info")
            if payload.get("type") != "token_count" or not isinstance(info, dict):
                continue
            scan.token_events += 1
            usage = info.get("total_token_usage")
            if not isinstance(usage, dict):
                continue
            scan.token_valid += 1
            values = tuple(common.as_count(usage.get(key)) for key in COUNTERS)
            if ts > t0:
                scan.in_window += 1
                if prev is not None and any(new < old for new, old in zip(values, prev)):
                    scan.reset = True
            else:
                scan.before = values
            scan.after = prev = values
        elif kind == "turn_context":
            model, effort = common.label(payload.get("model")), common.label(payload.get("effort"))
            if model:
                scan.last_model = model
                if ts > t0:
                    scan.models.add(model)
                else:
                    scan.model_before = model
            if effort:
                scan.last_effort = effort
                if ts > t0:
                    scan.efforts.add(effort)
                else:
                    scan.effort_before = effort
        elif ts > t0 and payload.get("type") == "message" and payload.get("role") == "assistant":
            scan.messages += 1
    return scan


def _find_named(root, suffix):
    """(plain rollouts, compressed rollouts) whose file name ends with `-<thread id>.jsonl[.zst]`."""
    plain, packed = [], []
    for dirpath, _dirs, names in os.walk(root):
        for name in names:
            if name.startswith("rollout-"):
                if name.endswith(suffix):
                    plain.append(os.path.join(dirpath, name))
                elif name.endswith(suffix + ".zst"):
                    packed.append(os.path.join(dirpath, name))
    return sorted(plain), sorted(packed)


def _descendants(main_meta, pool):
    if not main_meta["subagent"]:
        return [item for item in pool if item[1]["session"] == main_meta["session"]]
    known, found, grew = {main_meta["id"]}, [], True
    while grew:
        grew = False
        for item in pool:
            if item not in found and item[1]["parent"] in known:
                found.append(item)
                known.add(item[1]["id"])
                grew = True
    return found


def resolve(thread_ids, cwd, t0, t1, env, errors, with_subagents=True):
    """(status, mains, subagents): rollouts are (path, meta, scan) triples; status is a contract reason or None."""
    root = sessions_root(env)
    paths = list_rollouts(root, t0, t1)
    metas = {}

    def meta_of(path):
        if path not in metas:
            metas[path] = read_meta(path, errors)
        return metas[path]

    mains = []
    if thread_ids:
        for tid in thread_ids:
            suffix = "-" + tid + ".jsonl"
            hits = [path for path in paths if os.path.basename(path).endswith(suffix)]
            if not hits:
                hits, packed = _find_named(root, suffix)
                if not hits and packed:
                    errors.add(packed[0], 0, "CompressedRollout")
            if len(hits) > 1:
                return "ambiguous_rollouts", [], []
            if hits and hits[0] not in [path for path, _meta, _scan in mains]:
                meta = meta_of(hits[0])
                if meta is None:
                    return "parse_error", [], []
                mains.append((hits[0], meta, scan_rollout(hits[0], meta, t0, t1, errors)))
        if not mains:
            return "rollout_not_found", [], []
    else:
        key = cwd_key(cwd)
        for path in paths:
            meta = meta_of(path)
            if meta is None or meta["subagent"] or key is None or meta["cwd"] != key:
                continue
            scan = scan_rollout(path, meta, t0, t1, errors)
            if scan.in_window:
                mains.append((path, meta, scan))
        if not mains:
            return "rollout_not_found", [], []
        if len(mains) > 1:
            return "ambiguous_rollouts", [], []

    subs = []
    if with_subagents:
        main_paths = set(path for path, _meta, _scan in mains)
        pool = []
        for path in paths:
            meta = None if path in main_paths else meta_of(path)
            if meta and meta["subagent"] and meta["session"]:
                pool.append((path, meta))
        wanted = {}
        for _path, meta, _scan in mains:
            for item in _descendants(meta, pool):
                wanted[item[0]] = item
        for path in sorted(wanted):
            meta = wanted[path][1]
            subs.append((path, meta, scan_rollout(path, meta, t0, t1, errors)))
    return None, mains, subs


def _map(delta):
    """Codex counts cached and cache-write tokens inside input_tokens (contract 8.3 #5)."""
    d_in, d_cached, d_write, d_out, d_reason, _d_total = delta
    return common.tokens(max(0, d_in - d_cached - d_write), d_out, d_cached, d_write, d_reason, d_in + d_out)


def read(thread_ids, cwd, t0, t1, env):
    """Outcome for the (t0, t1] window; `reason` is a contract unavailableReason or None."""
    errors = common.ErrorLog()
    status, mains, subs = resolve(thread_ids, cwd, t0, t1, env, errors)
    ids = [meta["id"] for _path, meta, _scan in mains if meta["id"]]
    if status:
        return {"reason": status, "sessionIds": ids, "errors": errors}
    scans = [(scan, False) for _p, _m, scan in mains] + [(scan, True) for _p, _m, scan in subs]
    if any(scan.token_events and not scan.token_valid for scan, _sub in scans):
        return {"reason": "parse_error", "sessionIds": ids, "errors": errors}
    total, sub_total = common.zero_tokens(), common.zero_tokens()
    raw, models, efforts, sub_files = [0] * len(COUNTERS), set(), set(), 0
    for scan, sub in scans:
        if not scan.in_window:
            continue
        delta = [new - old for new, old in zip(scan.after or _ZERO, scan.before or _ZERO)]
        if scan.reset or any(value < 0 for value in delta):
            return {"reason": "counter_reset", "sessionIds": ids, "errors": errors}
        mapped = _map(delta)
        total = common.add_tokens(total, mapped)
        if sub:
            sub_total = common.add_tokens(sub_total, mapped)
            sub_files += 1
        raw = [a + b for a, b in zip(raw, delta)]
        models |= scan.models or ({scan.model_before} if scan.model_before else set())
        efforts |= scan.efforts or ({scan.effort_before} if scan.effort_before else set())
    return {
        "reason": None if any(scan.in_window for scan, _sub in scans) else "no_messages_in_window",
        "sessionIds": ids,
        "tokens": total,
        "raw": dict(zip(COUNTERS, raw)),
        "messages": sum(scan.messages for scan, _sub in scans),
        "subagents": {"files": sub_files, "tokens": sub_total},
        "models": sorted(models),
        "efforts": sorted(efforts),
        "errors": errors,
    }


def detect(thread_ids, cwd, t0, t1, env):
    """CLI version and the latest model / effort of the main rollout up to t1 (declared detection, 8.1)."""
    status, mains, _subs = resolve(thread_ids, cwd, t0, t1, env, common.ErrorLog(), with_subagents=False)
    if status or not mains:
        return {}
    _path, meta, scan = mains[0]
    found = {"hostVersion": meta["version"], "model": scan.last_model, "effort": scan.last_effort}
    return {key: value for key, value in found.items() if value}


# Producer:Betsy
