"""How closely headless suite3 agents follow the root SKILL.md protocol (maintainer tool for docs work, not a gate input).

    python3 -B eval/tools/compliance.py --group haiku-low=v3-1106-base-haiku-low- [--group ...] \
        [--schema schema-full.json] [--json out.json] [--md out.md]

A group is every suite whose name starts with the prefix; each task counts once, its latest attempt (a rerun replaces
an interrupted try). Skill executions come from the attempt's own editor telemetry (exact); request shape (instance
pinning, /health, discovery, polling) from the Bash commands in the agent's stream, after expanding simple shell
variables within each command; calls built in Python code stay invisible, so pinning is a share of the requests seen.
--schema is a saved `GET /skills/schema?wire=v2`; without it reads, writes and risky skills cannot be told apart.
"""
import argparse
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from harness import metrics as M  # noqa: E402

SUITES = os.path.join(os.path.dirname(__file__), "..", "runs", "suite3")
PROJECTS = [os.path.expanduser("~/UnityProject/" + name)
            for name in ("UnitySkillsEval2022", "UnitySkillsEval2022b", "skill")]
RUNNER_AGENT = "UnitySkillsEval"
ASSIGN_RE = re.compile(r"(?:^|[\s;&|(])(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(\"[^\"]*\"|'[^']*'|[^\s;&|]*)")
VAR_RE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)")
PIN_RE = re.compile(r"expectInstance=|expectProject=|X-Expect-Instance", re.IGNORECASE)
DOC_RE = re.compile(r"/docs/((?:skills|references)/[A-Za-z0-9_./-]+\.md|SKILL(?:_GUIDE)?\.md)")
DOC_VIEW_RE = re.compile(r"\b(?:cat|sed|head|tail|less|grep|awk)\b")
HALLUCINATION = ("SKILL_NOT_FOUND", "UNKNOWN_PARAM", "MISSING_PARAM", "INVALID_JSON")
RELOAD_503 = "Server reloading"
DRY_MODES = ("dryRun", "batch_step_dryRun")
EXEC_MODES = ("execute", "batch_step")
SCRIPT_WRITES = ("script_create", "script_append", "script_replace")


def expand(cmd):
    """$NAME / ${NAME} replaced by the value an earlier NAME=... in the same command gave it; unknown names stay."""
    env, out, pos = {}, [], 0
    for match in ASSIGN_RE.finditer(cmd):
        value = match.group(2)
        if value[:1] in "\"'":
            value = value[1:-1]
        env[match.group(1)] = VAR_RE.sub(lambda m: env.get(m.group(1) or m.group(2), m.group(0)), value)
    return VAR_RE.sub(lambda m: env.get(m.group(1) or m.group(2), m.group(0)), cmd)


def http_calls(cmd):
    """(path, query, pinned) for every localhost request a command names, after variable expansion."""
    text = expand(cmd)
    header_pin = "x-expect-instance" in text.lower()
    calls = []
    for _port, path, query in M.URL_RE.findall(text):
        if path and path != "/":
            calls.append((path, query or "", header_pin or bool(PIN_RE.search(query or ""))))
    return calls


def stream_metrics(path):
    row = {"skillCalls": 0, "pinned": 0, "health": 0, "healthBundled": None, "recommend": 0, "schema": 0,
           "polls": 0, "doubleQuery": 0, "errors": {}, "docs": []}
    docs = set()
    for event in M.load_events(path):
        etype = event.get("type")
        if etype == "assistant":
            for block in (event.get("message") or {}).get("content") or []:
                if block.get("type") != "tool_use":
                    continue
                tool_input = block.get("input") or {}
                if block.get("name") == "Read":
                    docs.update(DOC_RE.findall(tool_input.get("file_path") or ""))
                if block.get("name") != "Bash":
                    continue
                cmd = tool_input.get("command") or ""
                if DOC_VIEW_RE.search(cmd):
                    docs.update(DOC_RE.findall(cmd))
                calls = http_calls(cmd)
                paths = [c[0].rstrip("/") for c in calls]
                requests = [p for p in paths if p != "/health"]
                if row["healthBundled"] is None and requests:
                    row["healthBundled"] = len(requests) < len(paths)
                for (call_path, query, pinned), short in zip(calls, paths):
                    row["doubleQuery"] += 1 if query.count("?") > 1 else 0
                    if short == "/health":
                        row["health"] += 1
                    elif short == "/skills/recommend":
                        row["recommend"] += 1
                    elif short == "/skills/schema":
                        row["schema"] += 1
                    elif short.startswith("/jobs") or short.startswith("/compile"):
                        row["polls"] += 1
                    elif short.startswith("/skill/") or short == "/skills/batch":
                        row["skillCalls"] += 1
                        row["pinned"] += 1 if pinned else 0
        elif etype == "user":
            content = (event.get("message") or {}).get("content")
            for block in content if isinstance(content, list) else []:
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    text = M._text(block.get("content"))
                    codes = M.ERRCODE_RE.findall(text)
                    # The 503 a domain reload answers is expected after a compile, not a rule violation.
                    reloads = min(text.count(RELOAD_503), codes.count("COMPILING"))
                    for code in codes:
                        if code == "COMPILING" and reloads:
                            reloads -= 1
                            code = "COMPILING_RELOAD"
                        row["errors"][code] = row["errors"].get(code, 0) + 1
    row["docs"] = sorted(docs)
    return row


def load_schema(path):
    if not path:
        return {}
    with open(path, "r", encoding="utf-8-sig") as handle:
        return {s["name"]: s for s in json.load(handle).get("skills") or []}


def is_read(schema, skill):
    return "readOnly" in ((schema.get(skill) or {}).get("flags") or [])


def is_risky(schema, skill, rule="current"):
    """Skills the root SKILL.md wants dry-run first. current: deletes and reloads other than script writes;
    p3: deletes and every reload; legacy: p3 plus riskLevel high and mayEnterPlayMode."""
    spec = schema.get(skill) or {}
    flags = spec.get("flags") or []
    if "Delete" in (spec.get("operation") or []):
        return True
    if "mayTriggerReload" in flags and (rule != "current" or skill not in SCRIPT_WRITES):
        return True
    return rule == "legacy" and (spec.get("riskLevel") == "high" or "mayEnterPlayMode" in flags)


def telemetry_metrics(rows, schema):
    out = {"reads": 0, "writes": 0, "batchWrites": 0, "dryRuns": 0, "failed": 0, "failedDryRun": 0,
           "riskyWithoutDryRun": 0, "riskyWithoutDryRunP3": 0, "riskyWithoutDryRunLegacy": 0, "jobPolls": 0,
           "failedSkills": {}, "riskySkills": {}, "dryRunSkills": {}}
    dried = set()
    for row in rows:
        if row.get("agent") == RUNNER_AGENT:
            continue
        skill, mode = row.get("skill"), row.get("mode")
        if not row.get("ok"):
            out["failedDryRun" if mode in DRY_MODES else "failed"] += 1
            key = "%s:%s" % (mode, skill)
            out["failedSkills"][key] = out["failedSkills"].get(key, 0) + 1
        if mode in DRY_MODES:
            out["dryRuns"] += 1
            out["dryRunSkills"][skill] = out["dryRunSkills"].get(skill, 0) + 1
            dried.add(skill)
            continue
        if mode not in EXEC_MODES:
            continue
        if skill in ("job_status", "job_wait", "test_get_result"):
            out["jobPolls"] += 1
        if schema and is_read(schema, skill):
            out["reads"] += 1
            continue
        out["writes"] += 1
        out["batchWrites"] += 1 if mode == "batch_step" else 0
        if not schema or skill in dried:
            continue
        if is_risky(schema, skill, rule="legacy"):
            out["riskyWithoutDryRunLegacy"] += 1
        if is_risky(schema, skill, rule="p3"):
            out["riskyWithoutDryRunP3"] += 1
        if is_risky(schema, skill):
            out["riskyWithoutDryRun"] += 1
            out["riskySkills"][skill] = out["riskySkills"].get(skill, 0) + 1
    return out


def attempt_telemetry(session_id, attempt_id, projects):
    for project in projects:
        path = os.path.join(project, "Library", "UnitySkills", "eval", "sessions", session_id, "attempts",
                            attempt_id, "telemetry.jsonl")
        if os.path.isfile(path):
            with open(path, "r", encoding="utf-8", errors="replace") as handle:
                return [json.loads(line) for line in handle if line.strip().startswith("{")]
    return None


def latest_attempts(prefix, suites_root=SUITES):
    """task -> (attempt record, launch record, suite dir), the newest attempt over every suite of the group."""
    chosen = {}
    for suite_dir in sorted(glob.glob(os.path.join(suites_root, prefix + "*"))):
        ledger = os.path.join(suite_dir, "ledger.jsonl")
        if not os.path.isfile(ledger):
            continue
        launches = {}
        with open(ledger, "r", encoding="utf-8") as handle:
            for line in handle:
                rec = json.loads(line)
                if rec.get("kind") == "launch":
                    launches[rec["launchId"]] = rec
                elif rec.get("kind") == "attempt":
                    previous = chosen.get(rec["task"])
                    if previous is None or rec["recordedAt"] > previous[0]["recordedAt"]:
                        chosen[rec["task"]] = (rec, launches.get(rec.get("launchId")), suite_dir)
    return chosen


def score_group(prefix, schema, projects=PROJECTS, suites_root=SUITES):
    tasks = []
    for task, (attempt, launch, suite_dir) in sorted(latest_attempts(prefix, suites_root).items()):
        stream = os.path.join(suite_dir, launch["dir"], "subject.stream.jsonl") if launch else None
        row = {"task": task, "status": attempt.get("status"),
               "processed": (attempt.get("usage") or {}).get("processed") or 0,
               "wallSeconds": (launch or {}).get("wallSeconds") or 0}
        row.update(stream_metrics(stream) if stream and os.path.isfile(stream) else {})
        tele = attempt_telemetry(attempt.get("sessionId") or "", attempt.get("attemptId") or "", projects)
        row["telemetry"] = telemetry_metrics(tele, schema) if tele is not None else None
        tasks.append(row)
    return {"prefix": prefix, "tasks": tasks, "summary": summarize(tasks)}


def summarize(tasks):
    n = len(tasks) or 1
    total = lambda key: sum(t.get(key) or 0 for t in tasks)  # noqa: E731
    tele = [t["telemetry"] for t in tasks if t.get("telemetry")]
    tsum = lambda key: sum(t[key] for t in tele)  # noqa: E731
    errors = {}
    for t in tasks:
        for code, count in (t.get("errors") or {}).items():
            errors[code] = errors.get(code, 0) + count
    bundled = [t["healthBundled"] for t in tasks if t.get("healthBundled") is not None]
    writes = tsum("writes")
    seen_calls = total("skillCalls")
    dry_runs = _merge(t.get("dryRunSkills") or {} for t in tele)
    return {
        "tasks": len(tasks),
        "status": {s: sum(1 for t in tasks if t["status"] == s) for s in sorted({t["status"] for t in tasks})},
        "processedMean": total("processed") / n,
        "wallMeanSeconds": total("wallSeconds") / n,
        "pinnedRate": (total("pinned") / seen_calls) if seen_calls else None,
        "requestsPerTask": seen_calls / n,
        "healthBundledRate": (sum(bundled) / len(bundled)) if bundled else None,
        "healthPerTask": total("health") / n,
        "discoveryPerTask": (total("recommend") + total("schema")) / n,
        "docReadTasks": sum(1 for t in tasks if t.get("docs")),
        "docs": sorted({d for t in tasks for d in t.get("docs") or []}),
        "hallucinationPerTask": sum(errors.get(code, 0) for code in HALLUCINATION) / n,
        "semanticInvalidPerTask": errors.get("SEMANTIC_INVALID", 0) / n,
        "compilingPerTask": errors.get("COMPILING", 0) / n,
        "reload503PerTask": errors.get("COMPILING_RELOAD", 0) / n,
        "pollsPerTask": (total("polls") + tsum("jobPolls")) / n,
        "readsPerWrite": (tsum("reads") / writes) if writes else None,
        "batchShare": (tsum("batchWrites") / writes) if writes else None,
        "riskyWithoutDryRun": tsum("riskyWithoutDryRun"),
        "riskyWithoutDryRunP3": sum(t.get("riskyWithoutDryRunP3", 0) for t in tele),
        "riskyWithoutDryRunLegacy": sum(t.get("riskyWithoutDryRunLegacy", 0) for t in tele),
        "doubleQueryCalls": total("doubleQuery"),
        "sceneSaveDryRuns": dry_runs.get("scene_save", 0),
        "scriptWriteDryRuns": sum(dry_runs.get(s, 0) for s in SCRIPT_WRITES),
        "failedPerTask": tsum("failed") / n,
        "failedDryRunPerTask": tsum("failedDryRun") / n,
        "failedSkills": _merge(t["failedSkills"] for t in tele),
        "riskySkills": _merge(t["riskySkills"] for t in tele),
        "errors": dict(sorted(errors.items(), key=lambda kv: -kv[1])),
    }


def _merge(counters):
    merged = {}
    for counter in counters:
        for key, count in counter.items():
            merged[key] = merged.get(key, 0) + count
    return dict(sorted(merged.items(), key=lambda kv: -kv[1]))


def _fmt(value, pct=False):
    if value is None:
        return "-"
    if pct:
        return "%d%%" % round(value * 100)
    return ("%.2f" % value) if isinstance(value, float) else str(value)


def markdown(groups):
    lines = ["| group | tasks | pass | partial | fail | invalid | processed/task | wall/task s |",
             "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for name, g in groups:
        s = g["summary"]
        st = s["status"]
        lines.append("| %s | %d | %d | %d | %d | %d | %.0fK | %.0f |" % (
            name, s["tasks"], st.get("pass", 0), st.get("partial", 0), st.get("fail", 0), st.get("invalid", 0),
            s["processedMean"] / 1000, s["wallMeanSeconds"]))
    lines += ["", "| group | pinned | health bundled | health/task | discovery/task | doc-read tasks | "
                  "hallucination/task | SEMANTIC_INVALID/task | COMPILING/task | reload 503/task | polls/task | "
                  "reads/write | batch share | risky w/o dryRun | (P3 rule) | (old rule) | failed calls/task | "
                  "failed dryRuns/task | requests/task | second-? calls | scene_save dryRuns | script dryRuns |",
              "|---|" + "---:|" * 21]
    for name, g in groups:
        s = g["summary"]
        lines.append("| %s | %s | %s | %s | %s | %d | %s | %s | %s | %s | %s | %s | %s | %d | %d | %d | %s | %s | %s "
                     "| %d | %d | %d |" % (
            name, _fmt(s["pinnedRate"], True), _fmt(s["healthBundledRate"], True), _fmt(s["healthPerTask"]),
            _fmt(s["discoveryPerTask"]), s["docReadTasks"], _fmt(s["hallucinationPerTask"]),
            _fmt(s["semanticInvalidPerTask"]), _fmt(s["compilingPerTask"]), _fmt(s["reload503PerTask"]),
            _fmt(s["pollsPerTask"]), _fmt(s["readsPerWrite"]), _fmt(s["batchShare"], True),
            s["riskyWithoutDryRun"], s["riskyWithoutDryRunP3"], s["riskyWithoutDryRunLegacy"],
            _fmt(s["failedPerTask"]), _fmt(s["failedDryRunPerTask"]), _fmt(s["requestsPerTask"]),
            s["doubleQueryCalls"], s["sceneSaveDryRuns"], s["scriptWriteDryRuns"]))
    return "\n".join(lines) + "\n"


def main(argv=None):
    p = argparse.ArgumentParser(description="Rule-adherence metrics for suite3 runs.")
    p.add_argument("--group", action="append", required=True, help="name=suite prefix (repeatable)")
    p.add_argument("--schema", help="saved GET /skills/schema?wire=v2 (reads, writes, risky skills)")
    p.add_argument("--project", action="append", help="eval project roots (default: the local eval projects)")
    p.add_argument("--json")
    p.add_argument("--md")
    args = p.parse_args(argv)
    schema = load_schema(args.schema)
    groups = []
    for spec in args.group:
        name, _, prefix = spec.partition("=")
        groups.append((name, score_group(prefix or name, schema, args.project or PROJECTS)))
    text = markdown(groups)
    if args.md:
        with open(args.md, "w", encoding="utf-8") as handle:
            handle.write(text)
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(dict(groups), handle, indent=1)
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
