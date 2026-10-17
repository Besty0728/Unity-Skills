"""Skill-call telemetry of an attempt window, read across rotated files (contract 2.8 S6, 7.2)."""
import json
import os

from . import store
from .rest import RUNNER_AGENT

FILES = ("UnitySkillsTelemetry.jsonl", "UnitySkillsTelemetry.1.jsonl", "UnitySkillsTelemetry.2.jsonl",
         "UnitySkillsTelemetry.3.jsonl")


def read_window(library_dir, t0_iso, t1_iso, exclude_agent=RUNNER_AGENT):
    """(raw lines, files read): t0 < ts <= t1, not the runner's own calls, deduplicated by whole line."""
    start, end = store.parse_utc(t0_iso), store.parse_utc(t1_iso)
    seen, kept, files = set(), [], []
    for name in FILES:
        try:
            with open(os.path.join(library_dir, name), "rb") as handle:
                raw = handle.read()
        except OSError:
            continue
        files.append(name)
        for line in raw.split(b"\n"):
            line = line.strip()
            if not line or line in seen:
                continue
            seen.add(line)
            try:
                rec = json.loads(line.decode("utf-8-sig"))
                ts = store.parse_utc(rec["ts"])
            except (ValueError, KeyError, TypeError, AttributeError, UnicodeDecodeError):
                continue
            if not isinstance(rec, dict) or not (start < ts <= end) or rec.get("agent") == exclude_agent:
                continue
            kept.append((ts, line.decode("utf-8-sig"), rec))
    kept.sort(key=lambda item: item[0])
    return [(text, rec) for _ts, text, rec in kept], files


def summarize(records, files):
    by_skill, errors, agents, modes = {}, {}, {}, {}
    for rec in records:
        skill, agent, mode = rec.get("skill"), rec.get("agent"), rec.get("mode")
        by_skill[skill] = by_skill.get(skill, 0) + 1
        agents[str(agent)] = agents.get(str(agent), 0) + 1
        modes[str(mode)] = modes.get(str(mode), 0) + 1
        if rec.get("ok") is False:
            code = rec.get("errorCode") or "?"
            errors[code] = errors.get(code, 0) + 1
    return {"available": bool(files), "files": list(files), "skillCalls": len(records),
            "bySkill": {str(k): v for k, v in sorted(by_skill.items(), key=lambda kv: str(kv[0]))},
            "errors": dict(sorted(errors.items())), "agents": dict(sorted(agents.items())),
            "modes": dict(sorted(modes.items()))}


def freeze(library_dir, t0_iso, t1_iso, out_path):
    """Copy the window's lines to attempts/<aid>/telemetry.jsonl and return evidence.telemetry."""
    lines, files = read_window(library_dir, t0_iso, t1_iso)
    store.atomic_write_bytes(out_path, "".join(text + "\n" for text, _rec in lines).encode("utf-8"))
    return summarize([rec for _text, rec in lines], files)


def load_summary(path, files):
    return summarize(store.read_jsonl(path), files)

# Producer:Betsy
