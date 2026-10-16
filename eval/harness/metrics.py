"""Per-attempt metrics from the subject's stream-json transcript, cross-checked with telemetry.

Tokens come from result.modelUsage (result.usage has been seen to drop a whole API call, see
doc-bench pitfall 15). HTTP requests are counted from the URLs in Bash commands (a heuristic:
loops undercount) and skill executions from the editor's own telemetry (exact, excludes the
evaluator's calls by X-Agent-Id). Cost is recorded only with its source; costBasis "unknown"
means the host priced an unrecognised model and the number must not be read as a real price.
"""
import datetime
import json
import os
import re

URL_RE = re.compile(r"(?:https?://)?(?:localhost|127\.0\.0\.1):(\d{2,5})(/[A-Za-z0-9_\-./]*)?(\?[^\s'\"\\)]*)?")
BATCH_SKILL_RE = re.compile(r"\\?[\"']skill\\?[\"']\s*:\s*\\?[\"']([a-z0-9_]+)")
CLIENT_RE = re.compile(r"unity_skills(?:\.py)?\b|call_skill")
ERRCODE_RE = re.compile(r"[\"']errorCode[\"']\s*:\s*[\"']([A-Z_]+)[\"']")
# Paths a subject has no business reading: the repo (harness, tasks, reports, earlier runs) and the
# user's Claude data, including an installed skill copy that is not the frozen docs under test.
SUSPICIOUS_PATHS = ("/CodeSpace/Unity-Skills", "/.claude/projects", "/.claude/skills",
                    "/.codex/", "/.agents/skills", "/eval/runs", "/temp/eval")


def load_events(path):
    events = []
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if line.startswith("{"):
                try:
                    events.append(json.loads(line))
                except ValueError:
                    pass
    return events


def _text(content):
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for part in content:
            if isinstance(part, dict):
                parts.append(part.get("text") or part.get("content") or "")
            else:
                parts.append(str(part))
        return "\n".join(p if isinstance(p, str) else json.dumps(p) for p in parts)
    return json.dumps(content)


def rest_kind(path, query, cmd, is_read_only=None):
    path = (path or "/").rstrip("/") or "/"
    query = query or ""
    dry = "mode=dryRun" in query or "mode=dryrun" in query
    if path in ("/", "/health"):
        return "health", None, []
    if path == "/skills/recommend":
        return "recommend", None, []
    if path == "/skills/schema":
        return "schema", None, []
    if path == "/skills/meta":
        return "meta", None, []
    if path == "/skills":
        return "skills_list", None, []
    if path.startswith("/jobs"):
        return "jobs", None, []
    if path.startswith("/compile"):
        return "compile_status", None, []
    if path == "/skills/batch":
        inner = BATCH_SKILL_RE.findall(cmd)
        if dry:
            return "dryrun", "batch", inner
        ro = bool(inner) and is_read_only is not None and all(is_read_only(s) for s in inner)
        return ("exec_read" if ro else "exec_write"), "batch", inner
    match = re.match(r"^/skill/([A-Za-z0-9_]+)$", path)
    if match:
        skill = match.group(1)
        if dry:
            return "dryrun", skill, [skill]
        if is_read_only is None:
            return "exec_unknown", skill, [skill]
        return ("exec_read" if is_read_only(skill) else "exec_write"), skill, [skill]
    return "other", path, []


def parse(stream_path, eval_port, is_read_only=None, host=None):
    events = load_events(stream_path)
    if host == "codex" or any(e.get("type") in ("thread.started", "turn.started", "turn.completed",
                                               "turn.failed", "item.completed") for e in events):
        row = _parse_codex(events, eval_port, is_read_only)
        identity_path = os.path.join(os.path.dirname(stream_path), "subject.identity.json")
        if os.path.isfile(identity_path):
            try:
                with open(identity_path, "r", encoding="utf-8") as handle:
                    identity = json.load(handle)
                if not isinstance(identity, dict):
                    raise ValueError("identity metadata must be an object")
            except (OSError, ValueError):
                identity = {}
                row["evidenceProblems"].append("invalid_provider_identity_metadata")
            row["providerIdentity"] = identity
            observer_errors = identity.get("errors") or []
            row["apiRetries"] = max(row["apiRetries"], sum(
                str(error).startswith("upstream HTTP ") for error in observer_errors))
            row["evidenceProblems"].extend("provider_metadata: " + str(error) for error in observer_errors
                                           if str(error).startswith(("metadata ", "compressed ")))
            if identity.get("source") == "response.model":
                observed = set(identity.get("models") or []) | set(row["assistantModels"])
                row["observedModels"] = sorted(observed)
                row["identitySource"] = "response.model"
                row["init"]["model"] = next(iter(observed)) if len(observed) == 1 else None
            provider_usage = _provider_usage(identity.get("observations") or [])
            row["providerUsage"] = provider_usage
            if provider_usage:
                provider_tokens = _codex_tokens(provider_usage["total"])
                row["tokens"]["byModel"] = {model: _codex_tokens(counters)
                                              for model, counters in provider_usage["byModel"].items()}
                if row["tokens"]["source"]:
                    for key in ("input", "output", "cacheRead", "cacheCreation"):
                        if provider_tokens[key] != row["tokens"][key]:
                            row["evidenceProblems"].append("provider_usage_mismatch:" + key)
                else:
                    row["tokens"] = provider_tokens
                    row["tokens"]["source"] = "response.usage"
                    row["tokens"]["byModel"] = {model: _codex_tokens(counters)
                                                  for model, counters in provider_usage["byModel"].items()}
        if row["identitySource"] != "response.model" or not row["observedModels"]:
            row["evidenceProblems"].append("provider_model_identity_unavailable")
        if row["tokens"]["source"] is None:
            row["evidenceProblems"].append("token_usage_unavailable")
        return row
    row = {"init": {}, "apiRetries": 0, "assistantMessages": 0, "assistantModels": [],
           "toolCounts": {}, "http": [], "clientCalls": 0, "errorCodes": {}, "toolErrors": 0,
           "result": None, "modelUsage": None}
    seen_ids = set()
    message_usage = {"input": 0, "output": 0, "cacheRead": 0, "cacheCreation": 0}
    uses = {}
    models = set()
    for event in events:
        etype = event.get("type")
        if etype == "system" and event.get("subtype") == "init":
            row["init"] = {k: event.get(k) for k in ("model", "cwd", "tools", "skills", "mcp_servers",
                                                     "permissionMode", "claude_code_version", "plugins")}
        elif etype == "system" and event.get("subtype") == "api_retry":
            row["apiRetries"] += 1
        elif etype == "assistant":
            message = event.get("message") or {}
            row["assistantMessages"] += 1
            if message.get("model"):
                models.add(message["model"])
            mid = message.get("id")
            usage = message.get("usage") or {}
            if mid and mid not in seen_ids:
                seen_ids.add(mid)
                message_usage["input"] += usage.get("input_tokens") or 0
                message_usage["output"] += usage.get("output_tokens") or 0
                message_usage["cacheRead"] += usage.get("cache_read_input_tokens") or 0
                message_usage["cacheCreation"] += usage.get("cache_creation_input_tokens") or 0
            for block in message.get("content") or []:
                if block.get("type") != "tool_use":
                    continue
                name = block.get("name")
                row["toolCounts"][name] = row["toolCounts"].get(name, 0) + 1
                uses[block.get("id")] = block
                tool_input = block.get("input") or {}
                if name == "Bash":
                    _absorb_command(row, tool_input.get("command") or "", eval_port, is_read_only)
                touched = " ".join(str(v) for v in tool_input.values() if isinstance(v, str))
                for marker in SUSPICIOUS_PATHS:
                    if marker in touched:
                        row.setdefault("suspiciousAccess", []).append({"tool": name, "marker": marker,
                                                                       "input": touched[:300]})
        elif etype == "user":
            for block in (event.get("message") or {}).get("content") or []:
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    if block.get("is_error"):
                        row["toolErrors"] += 1
                    for code in ERRCODE_RE.findall(_text(block.get("content"))):
                        row["errorCodes"][code] = row["errorCodes"].get(code, 0) + 1
        elif etype == "result":
            row["result"] = {k: event.get(k) for k in ("subtype", "is_error", "num_turns", "duration_ms",
                                                       "duration_api_ms", "total_cost_usd", "stop_reason",
                                                       "terminal_reason", "api_error_status",
                                                       "permission_denials")}
            row["result"]["text"] = (event.get("result") or "")[:4000]
            row["modelUsage"] = event.get("modelUsage")
            row["resultUsage"] = event.get("usage")
    row["assistantModels"] = sorted(models)
    row.setdefault("suspiciousAccess", [])
    row["messageUsage"] = message_usage
    row["tokens"] = _tokens(row.get("modelUsage"), row.get("resultUsage"))
    row["evidenceProblems"] = []
    if row["tokens"]["source"] != "modelUsage":
        row["evidenceProblems"].append("canonical_model_usage_unavailable")
        row["tokens"]["degraded"] = row["tokens"]["source"] == "result.usage"
    row["httpByKind"] = {}
    for call in row["http"]:
        row["httpByKind"][call["kind"]] = row["httpByKind"].get(call["kind"], 0) + 1
    row["httpTotal"] = len(row["http"])
    row["foreignPorts"] = sorted({c["port"] for c in row["http"] if c["port"] != eval_port})
    row["batchSteps"] = sum(len(c["skills"]) for c in row["http"] if c["sub"] == "batch")
    return row


def _parse_codex(events, eval_port, is_read_only):
    """Normalize exec JSONL without mistaking a requested model for provider identity."""
    row = {"host": "codex", "init": {"model": None}, "identitySource": "unavailable",
           "apiRetries": 0, "assistantMessages": 0, "assistantModels": [], "observedModels": [],
           "toolCounts": {}, "http": [], "clientCalls": 0, "errorCodes": {}, "toolErrors": 0,
           "result": None, "modelUsage": None, "suspiciousAccess": [], "hostErrors": []}
    row["evidenceProblems"] = []
    usage = {}
    counted = set()
    models = set()
    messages = []
    for event in events:
        kind = event.get("type")
        if kind == "thread.started":
            row["init"]["thread_id"] = event.get("thread_id")
        if kind in ("turn.completed", "turn.failed"):
            if kind == "turn.completed":
                for key, value in (event.get("usage") or {}).items():
                    if isinstance(value, (int, float)):
                        usage[key] = usage.get(key, 0) + value
                row["result"] = {"subtype": "success", "is_error": False,
                                 "total_cost_usd": None, "costBasis": "unavailable"}
            else:
                error = event.get("error") or {}
                message = error.get("message", str(error)) if isinstance(error, dict) else str(error)
                row["hostErrors"].append(message)
                row["result"] = {"subtype": "host_error", "is_error": True,
                                 "api_error_status": message or "turn.failed", "total_cost_usd": None,
                                 "costBasis": "unavailable"}
        elif kind == "error":
            message = event.get("message") or _text(event.get("error"))
            row["hostErrors"].append(message)
            if re.search(r"reconnect|retry", message, re.I):
                row["apiRetries"] += 1
        elif kind in ("item.started", "item.completed"):
            item = event.get("item") or {}
            itype = item.get("type")
            iid = item.get("id")
            if itype == "agent_message" and kind == "item.completed":
                row["assistantMessages"] += 1
                messages.append(item.get("text") or "")
                if item.get("model"):
                    models.add(item["model"])
            elif itype in ("command_execution", "file_change", "mcp_tool_call", "web_search"):
                if iid is None or iid not in counted:
                    counted.add(iid)
                    row["toolCounts"][itype] = row["toolCounts"].get(itype, 0) + 1
                    command = item.get("command") or ""
                    if itype == "command_execution":
                        _absorb_command(row, command, eval_port, is_read_only)
                    touched = command if itype == "command_execution" else json.dumps(item)
                    for marker in SUSPICIOUS_PATHS:
                        if marker in touched:
                            row["suspiciousAccess"].append({"tool": itype, "marker": marker,
                                                            "input": touched[:300]})
                if kind == "item.completed":
                    if item.get("status") == "failed" or item.get("exit_code") not in (None, 0):
                        row["toolErrors"] += 1
                    output = item.get("aggregated_output") or item.get("result") or ""
                    for code in ERRCODE_RE.findall(_text(output)):
                        row["errorCodes"][code] = row["errorCodes"].get(code, 0) + 1
    row["assistantModels"] = sorted(models)
    if models:
        row["observedModels"] = sorted(models)
        row["identitySource"] = "item.model"
        if len(models) == 1:
            row["init"]["model"] = next(iter(models))
    if row["result"]:
        row["result"]["text"] = "\n".join(messages)[-4000:]
    row["resultUsage"] = usage or None
    row["tokens"] = _codex_tokens(usage)
    row["httpByKind"] = {}
    for call in row["http"]:
        row["httpByKind"][call["kind"]] = row["httpByKind"].get(call["kind"], 0) + 1
    row["httpTotal"] = len(row["http"])
    row["foreignPorts"] = sorted({c["port"] for c in row["http"] if c["port"] != eval_port})
    row["batchSteps"] = sum(len(c["skills"]) for c in row["http"] if c["sub"] == "batch")
    return row


def _provider_usage(observations):
    """Sum each provider response once, keeping token attribution tied to its returned model."""
    by_model, total, seen = {}, {}, set()
    for observation in observations:
        if not isinstance(observation, dict):
            continue
        if observation.get("event") not in ("response.completed", "response.json", "response.failed", "response.incomplete"):
            continue
        usage = observation.get("usage") or {}
        model = observation.get("model")
        if not model or not isinstance(usage, dict) or "input_tokens" not in usage or "output_tokens" not in usage:
            continue
        rid = observation.get("responseId")
        if rid and rid in seen:
            continue
        if rid:
            seen.add(rid)
        details = usage.get("input_tokens_details") or {}
        counters = {"input_tokens": usage["input_tokens"], "output_tokens": usage["output_tokens"],
                    "cached_input_tokens": details.get("cached_tokens") or 0,
                    "cache_write_input_tokens": details.get("cache_write_tokens") or 0,
                    "reasoning_output_tokens": (usage.get("output_tokens_details") or {}).get("reasoning_tokens") or 0}
        dest = by_model.setdefault(model, {})
        for key, value in counters.items():
            if isinstance(value, (int, float)):
                dest[key] = dest.get(key, 0) + value
                total[key] = total.get(key, 0) + value
    return {"total": total, "byModel": by_model} if by_model else None


def _codex_tokens(usage):
    # Codex input_tokens includes cached input; preserve that total without counting it twice.
    total_input = usage.get("input_tokens") or 0
    cached = usage.get("cached_input_tokens") or 0
    written = usage.get("cache_write_input_tokens") or 0
    output = usage.get("output_tokens") or 0
    return {"source": "turn.completed.usage" if usage else None, "byModel": {},
            "input": max(0, total_input - cached - written), "output": output,
            "cacheRead": cached, "cacheCreation": written,
            "processed": total_input + output, "uncached": max(0, total_input - cached) + output,
            "reasoningOutput": usage.get("reasoning_output_tokens"),
            "costUSD": None, "costBasis": "unavailable", "raw": usage}


def _absorb_command(row, cmd, eval_port, is_read_only):
    if CLIENT_RE.search(cmd):
        row["clientCalls"] += 1
    for port, path, query in URL_RE.findall(cmd):
        kind, sub, skills = rest_kind(path, query, cmd, is_read_only)
        row["http"].append({"port": int(port), "path": path or "/", "kind": kind, "sub": sub, "skills": skills})


def _tokens(model_usage, result_usage):
    out = {"source": None, "byModel": {}, "input": 0, "output": 0, "cacheRead": 0, "cacheCreation": 0}
    if model_usage:
        out["source"] = "modelUsage"
        for model, entry in model_usage.items():
            entry = entry or {}
            out["byModel"][model] = {"input": entry.get("inputTokens") or 0,
                                     "output": entry.get("outputTokens") or 0,
                                     "cacheRead": entry.get("cacheReadInputTokens") or 0,
                                     "cacheCreation": entry.get("cacheCreationInputTokens") or 0,
                                     "costUSD": entry.get("costUSD"), "costBasis": entry.get("costBasis")}
            for key in ("input", "output", "cacheRead", "cacheCreation"):
                out[key] += out["byModel"][model][key]
    elif result_usage:
        out["source"] = "result.usage"
        out["input"] = result_usage.get("input_tokens") or 0
        out["output"] = result_usage.get("output_tokens") or 0
        out["cacheRead"] = result_usage.get("cache_read_input_tokens") or 0
        out["cacheCreation"] = result_usage.get("cache_creation_input_tokens") or 0
    out["processed"] = out["input"] + out["output"] + out["cacheRead"] + out["cacheCreation"]
    out["uncached"] = out["input"] + out["output"] + out["cacheCreation"]
    if result_usage and model_usage:
        billed = sum((result_usage.get(k) or 0) for k in ("input_tokens", "output_tokens",
                                                          "cache_read_input_tokens", "cache_creation_input_tokens"))
        out["resultUsageMismatch"] = out["processed"] - billed
    return out


def _base_model(name):
    return re.sub(r"\[[^\]]*\]$", "", (name or "").strip().lower())


def model_matches(requested, row):
    """init must report the requested id (case-insensitive) and every assistant message its base
    name: the S1 probe saw init "claude-opus-5-5[1m]" with messages "claude-opus-5-5"."""
    if row.get("host") == "codex":
        observed = row.get("observedModels") or []
        return row.get("identitySource") == "response.model" and bool(observed) and all(
            _base_model(m) == _base_model(requested) for m in observed)
    init_model = ((row.get("init") or {}).get("model") or "").lower()
    if init_model != (requested or "").lower():
        return False
    want = _base_model(requested)
    observed = row.get("assistantModels") or []
    return bool(observed) and all(_base_model(m) == want for m in observed)


def telemetry(project_path, started_at, ended_at, exclude_agent="evaluator"):
    """Skill executions the editor recorded in [started_at, ended_at] for callers other than the evaluator."""
    path = os.path.join(project_path, "Library", "UnitySkillsTelemetry.jsonl")
    counts, errors, agents, total = {}, {}, {}, 0
    if not os.path.exists(path):
        return {"available": False}
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            try:
                rec = json.loads(line)
                ts = datetime.datetime.strptime(rec["ts"][:23], "%Y-%m-%dT%H:%M:%S.%f").replace(
                    tzinfo=datetime.timezone.utc).timestamp()
            except (ValueError, KeyError):
                continue
            if ts < started_at or ts > ended_at or rec.get("agent") == exclude_agent:
                continue
            total += 1
            counts[rec.get("skill")] = counts.get(rec.get("skill"), 0) + 1
            agents[rec.get("agent")] = agents.get(rec.get("agent"), 0) + 1
            if not rec.get("ok"):
                errors[rec.get("errorCode") or "?"] = errors.get(rec.get("errorCode") or "?", 0) + 1
    return {"available": True, "executions": total, "bySkill": counts, "errors": errors, "agents": agents}

# Producer:Betsy
