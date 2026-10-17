"""Shared bootstrap and log builders for the usk_eval.usage tests (not a test module itself).

Importing it puts the packaged runner on sys.path.
"""
import json
import os
import sys
from pathlib import Path

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[3]
PACKAGE = REPO / "SkillsForUnity" / "Evaluation~"
USAGE_SOURCES = PACKAGE / "usk_eval" / "usage"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "usage"
sys.path.insert(0, str(PACKAGE))

T0 = "2026-10-17T03:00:00.000Z"
T1 = "2026-10-17T03:10:00.000Z"
NOW = "2026-10-17T04:00:00.000Z"
DAY = "2026-10-17T"
DAY_DIR = ("2026", "10", "17")

CLAUDE_SID = "c1a2b3c4-0000-4000-8000-000000000001"
CODEX_ROOT_THREAD = "01a10000-0000-7000-8000-00000000aaaa"
SENTINEL = "ZZ_SENTINEL_CONTENT"
SENTINEL_CWD = "/zz-sentinel-cwd/alpha"

ZERO_TOKENS = {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0, "reasoning": 0, "processed": 0}
CONTRACT_KEYS = ["host", "source", "sessionIds", "window", "model", "effort", "tokens", "raw", "messages", "subagents",
                 "parserVersion", "unavailableReason", "errors", "computedAt"]


def jsonl(items):
    dump = json.dumps
    lines = [item if isinstance(item, str) else dump(item, ensure_ascii=False, separators=(",", ":")) for item in items]
    return "\n".join(lines) + "\n"


def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    return path


def stamp(ts):
    return ts if "T" in ts else DAY + ts


def claude_line(mid, ts, out=0, inp=0, cache_read=0, cache_write=0, thinking=0, model="claude-opus-5-5", effort="max",
                version="2.1.296", requested=None):
    line = {"type": "assistant", "timestamp": stamp(ts), "isSidechain": False, "requestedModel": requested or model,
            "version": version,
            "message": {"id": mid, "model": model, "role": "assistant", "content": [{"type": "text", "text": SENTINEL}],
                        "usage": {"input_tokens": inp, "output_tokens": out, "cache_read_input_tokens": cache_read,
                                  "cache_creation_input_tokens": cache_write,
                                  "output_tokens_details": {"thinking_tokens": thinking}}}}
    if effort is not None:
        line["effort"] = effort
    return line


class ClaudeTree(object):
    """<root>/<project>/<sid>.jsonl and <root>/<project>/<sid>/subagents/agent-*.jsonl."""

    def __init__(self, root, project="-work-p"):
        self.root, self.project = str(root), project

    def main(self, sid, lines, project=None):
        return write_text(os.path.join(self.root, project or self.project, sid + ".jsonl"), jsonl(lines))

    def sub(self, sid, name, lines, project=None):
        return write_text(os.path.join(self.root, project or self.project, sid, "subagents", name), jsonl(lines))


def cx_meta(ident, cwd, session=None, source="user", parent=None, start=None, ts="02:30:00.000Z", version="0.161.0"):
    payload = {"id": ident, "session_id": session or ident, "timestamp": stamp(ts), "cwd": cwd, "cli_version": version,
               "base_instructions": {"text": SENTINEL}}
    if source is not None:
        payload["thread_source"] = source
    if parent:
        payload["parent_thread_id"] = parent
    if start is not None:
        payload["subagent_history_start_ordinal"] = start
    return {"timestamp": stamp(ts), "type": "session_meta", "payload": payload}


def cx_counters(i, cached, cache_write, out, reason):
    return {"input_tokens": i, "cached_input_tokens": cached, "cache_write_input_tokens": cache_write, "output_tokens": out,
            "reasoning_output_tokens": reason, "total_tokens": i + out}


def cx_count(ts, i, cached=0, cache_write=0, out=0, reason=0):
    usage = cx_counters(i, cached, cache_write, out, reason)
    return {"timestamp": stamp(ts), "type": "event_msg",
            "payload": {"type": "token_count", "info": {"total_token_usage": usage, "last_token_usage": usage}}}


def cx_context(ts, model, effort):
    payload = {"model": model, "effort": effort, "cwd": SENTINEL_CWD}
    return {"timestamp": stamp(ts), "type": "turn_context", "payload": payload}


def cx_message(ts, role="assistant"):
    return {"timestamp": stamp(ts), "type": "response_item",
            "payload": {"type": "message", "role": role, "content": [{"type": "output_text", "text": SENTINEL}]}}


class CodexTree(object):
    """<root>/YYYY/MM/DD/rollout-<local time>-<thread id>.jsonl; ordinals are assigned per rollout."""

    def __init__(self, root):
        self.root = str(root)

    def rollout(self, thread_id, lines, day=DAY_DIR, suffix=".jsonl", clock="11-00-00"):
        numbered = []
        for index, line in enumerate(lines):
            if isinstance(line, dict) and "ordinal" not in line:
                line = dict(line, ordinal=index)
            numbered.append(line)
        name = "rollout-%sT%s-%s%s" % ("-".join(day), clock, thread_id, suffix)
        return write_text(os.path.join(self.root, *day, name), jsonl(numbered))


def host_claude(*ids):
    return {"kind": "claude-code", "sessionIds": list(ids), "cwd": SENTINEL_CWD}


def host_codex(*ids, **kwargs):
    return {"kind": "codex", "sessionIds": list(ids), "cwd": kwargs.get("cwd", SENTINEL_CWD)}


# Producer:Betsy
