"""Session report (contract 4.9): report.json, and report.md rendering the same content."""
import os

from . import store
from .problems import notice

MODEL_OUTCOMES = ("pass", "partial", "fail")
STATUSES = ("pass", "partial", "fail", "invalid", "contaminated", "cancelled")
TOKEN_KEYS = ("processed", "input", "output", "cacheRead", "cacheWrite", "reasoning")
LIMITATIONS = (
    "Local runs are marked local-unverified: an agent on this machine can read the evaluation state, the package's "
    "build code, or download the verification files.",
    "Private verification files are published on GitHub and only deter casual discovery; official comparisons use "
    "reserved tasks that are never published.",
    "In suite mode the agent's context accumulates across tasks, so per-task token costs are not comparable with "
    "strict mode.",
)
GRADE_FLOORS = ((0.90, "excellent"), (0.70, "good"), (0.50, "pass"))
GRADE_WORDS = {
    "excellent": {"zh-CN": "优秀", "en": "Excellent", "ru": "Отлично"},
    "good": {"zh-CN": "良好", "en": "Good", "ru": "Хорошо"},
    "pass": {"zh-CN": "及格", "en": "Pass", "ru": "Удовлетворительно"},
    "needs_work": {"zh-CN": "待提升", "en": "Needs work", "ru": "Нужно улучшить"},
}
VERDICT_TEXT = {
    "zh-CN": {"sep": "", "list": "；", "none": "没有可评分的任务。", "head": "%s：完全通过 %d / %d 个已评分任务。",
              "items": "未完全通过：%s。", "more": "以及另外 %d 个", "excluded": "另有 %d 个任务无效、已污染或已取消，未计入。"},
    "en": {"sep": " ", "list": "; ", "none": "No gradable tasks.", "head": "%s: %d of %d scored tasks fully passed.",
           "items": "Not fully passed: %s.", "more": "and %d more", "excluded": "%d task(s) invalid, contaminated or "
                                                                              "cancelled, not counted."},
    "ru": {"sep": " ", "list": "; ", "none": "Нет оцениваемых заданий.", "head": "%s: полностью выполнены %d из %d "
                                                                              "оцениваемых заданий.",
           "items": "Не выполнены полностью: %s.", "more": "и ещё %d", "excluded": "Не учтено: %d (недействительных, "
                                                                                 "загрязнённых или отменённых)."},
}
MAX_LISTED_TASKS = 5


def report_paths(paths, sid):
    directory = paths.session_dir(sid)
    return {"json": os.path.join(directory, "report.json"), "md": os.path.join(directory, "report.md")}


def _results(rt, session):
    """(final result per task id or None, every result of the session)."""
    finals, everything = {}, []
    for task_id in session["order"]:
        finals[task_id] = None
        for aid in session["tasks"][task_id]["attempts"]:
            result = store.read_json(rt.paths.result_file(session["sessionId"], aid), missing_ok=True)
            if result:
                everything.append(result)
                finals[task_id] = result
    return finals, everything


def _usage_sum(results):
    sums, seen, sources = {k: 0 for k in TOKEN_KEYS}, False, set()
    for result in results:
        usage = result.get("usage") or {}
        if usage.get("source"):
            sources.add(usage["source"])
        tokens = usage.get("tokens")
        if isinstance(tokens, dict):
            seen = True
            for key in TOKEN_KEYS:
                value = tokens.get(key)
                sums[key] += value if isinstance(value, int) and not isinstance(value, bool) else 0
    out = {k: (sums[k] if seen else None) for k in TOKEN_KEYS}
    out["sources"] = sorted(sources)
    return out


def summarize(session, finals, everything):
    counts = {status: 0 for status in STATUSES}
    completions, wall = [], 0.0
    for result in finals.values():
        if not result:
            continue
        counts[result["status"]] = counts.get(result["status"], 0) + 1
        score = result.get("score")
        if result["status"] in MODEL_OUTCOMES and score:
            completions.append(float(score.get("completion") or 0.0))
        seconds = (result.get("timings") or {}).get("wall")
        if isinstance(seconds, (int, float)):
            wall += seconds
    outcomes = sum(counts[s] for s in MODEL_OUTCOMES)
    completed = sum(1 for t in session["order"] if session["tasks"][t]["state"] in ("done", "invalid", "cancelled"))
    summary = {"tasks": len(session["order"]), "completed": completed}
    summary.update(counts)
    summary.update({"strictPassRate": round(counts["pass"] / outcomes, 4) if outcomes else 0.0,
                    "meanCompletion": round(sum(completions) / len(completions), 2) if completions else 0.0,
                    "wallSeconds": round(wall, 1), "usage": _usage_sum(everything)})
    return summary


def grade_for(summary):
    """Grade key from strictPassRate; None when no task was scored (pass, partial or fail)."""
    if not sum(summary.get(s, 0) for s in MODEL_OUTCOMES):
        return None
    for floor, grade in GRADE_FLOORS:
        if summary["strictPassRate"] >= floor:
            return grade
    return "needs_work"


def build_verdict(summary, tasks, lang):
    """Template verdict in the session language (English when unknown); no generated text."""
    lang = lang if lang in VERDICT_TEXT else "en"
    text, grade = VERDICT_TEXT[lang], grade_for(summary)
    scored = sum(summary.get(s, 0) for s in MODEL_OUTCOMES)
    parts = [text["none"] if grade is None else text["head"] % (GRADE_WORDS[grade][lang], summary["pass"], scored)]
    unfinished = sorted((row for row in tasks if row["status"] in ("partial", "fail")), key=lambda r: r["taskId"])
    items = ["%s (%s: %s)" % (row["taskId"], row["status"], ", ".join(row["requirementsFailed"]) or "-")
             for row in unfinished[:MAX_LISTED_TASKS]]
    if len(unfinished) > MAX_LISTED_TASKS:
        items.append(text["more"] % (len(unfinished) - MAX_LISTED_TASKS))
    if items:
        parts.append(text["items"] % text["list"].join(items))
    excluded = summary.get("invalid", 0) + summary.get("contaminated", 0) + summary.get("cancelled", 0)
    if excluded:
        parts.append(text["excluded"] % excluded)
    return text["sep"].join(parts)


def _task_row(task_id, result):
    if not result:
        return {"taskId": task_id, "caseVersion": None, "attemptId": None, "status": None, "completion": None,
                "strict": None, "requirementsFailed": [], "failed": [], "timings": None, "usage": None, "reasons": []}
    score = result.get("score")
    failed = [{"id": row.get("id"), "req": row.get("req") or [], "hint": row.get("hint")}
              for row in (score or {}).get("assertions") or [] if not row.get("pass")]
    return {"taskId": task_id, "caseVersion": result.get("caseVersion"), "attemptId": result.get("attemptId"),
            "status": result.get("status"), "completion": score.get("completion") if score else None,
            "strict": score.get("strict") if score else None,
            "requirementsFailed": result.get("requirementsFailed") or [], "failed": failed,
            "timings": result.get("timings"), "usage": result.get("usage"), "reasons": result.get("reasons") or []}


def _notices(session, everything):
    out = []
    pack = session.get("pack") or {}
    if pack.get("modified"):
        out.append(notice("PACK_MODIFIED", [", ".join(pack["modified"])]))
    if (session.get("project") or {}).get("environment") == "user-project":
        out.append(notice("PROJECT_USER"))
    if any(r.get("isolation") == "unverified" for r in everything):
        out.append(notice("ISOLATION_UNVERIFIED"))
    missing = [r for r in everything if (r.get("usage") or {}).get("tokens") is None and r.get("timings", {}).get(
        "issuedAt")]
    if missing:
        out.append(notice("USAGE_UNAVAILABLE", [missing[0]["usage"].get("unavailableReason") or "unknown"]))
    return out


def generate(rt, session):
    finals, everything = _results(rt, session)
    summary = summarize(session, finals, everything)
    tasks = [_task_row(t, finals[t]) for t in session["order"]]
    report = {"schema": 1, "sessionId": session["sessionId"], "status": session["status"],
              "generatedAt": store.now_iso(), "runnerVersion": session.get("runnerVersion"),
              "pluginVersion": session.get("pluginVersion"), "pack": session.get("pack"),
              "selection": session.get("selection"), "mode": session.get("mode"), "lang": session.get("lang"),
              "declared": session.get("declared"), "environment": session.get("environment"),
              "integrity": session.get("integrity"),
              "official": bool(session.get("official", (session.get("pack") or {}).get("official"))),
              "summary": summary, "grade": grade_for(summary),
              "verdict": build_verdict(summary, tasks, session.get("lang")),
              "tasks": tasks, "notices": _notices(session, everything)}
    paths = report_paths(rt.paths, session["sessionId"])
    store.atomic_write_json(paths["json"], report)
    store.atomic_write_text(paths["md"], render_md(report))
    session["report"] = {"json": "report.json", "md": "report.md", "generatedAt": report["generatedAt"]}
    return report


def _cell(value):
    return "-" if value is None else str(value).replace("|", "\\|")


def render_md(report):
    summary = report["summary"]
    usage = summary.get("usage") or {}
    declared = report.get("declared") or {}
    pack = report.get("pack") or {}
    env = report.get("environment") or {}
    lines = ["# UnitySkills evaluation report", "", "- Verdict: %s" % report["verdict"],
             "- Session: %s (%s, %s mode, %s)" % (report["sessionId"], report["status"], report.get("mode"),
                                                  report.get("lang")),
             "- Pack: %s %s (%s); official: %s" % (pack.get("id"), pack.get("version"), pack.get("source"),
                                                   "yes" if report.get("official") else "no"),
             "- Runner %s, plugin %s; Unity %s, %s" % (report.get("runnerVersion"), report.get("pluginVersion"),
                                                       env.get("unity"), env.get("pipeline")),
             "- Declared: host %s, model %s, effort %s" % tuple(
                 _cell((declared.get(k) or {}).get("value")) for k in ("host", "model", "effort")),
             "- Integrity: %s" % report.get("integrity"), "",
             "## Summary", "",
             "| Tasks | Completed | Pass | Partial | Fail | Invalid | Contaminated | Cancelled |",
             "|---|---|---|---|---|---|---|---|",
             "| %s |" % " | ".join(str(summary.get(k, 0)) for k in ("tasks", "completed", "pass", "partial", "fail",
                                                                     "invalid", "contaminated", "cancelled")), "",
             "- Strict pass rate: %.1f%%" % (100.0 * summary.get("strictPassRate", 0.0)),
             "- Mean completion: %.2f" % summary.get("meanCompletion", 0.0),
             "- Wall time: %.1f s" % summary.get("wallSeconds", 0.0),
             "- Tokens processed: %s (input %s, output %s, cache read %s, cache write %s, reasoning %s; sources %s)"
             % tuple([_cell(usage.get(k)) for k in TOKEN_KEYS] + [", ".join(usage.get("sources") or []) or "-"]),
             "", "## Tasks", "",
             "| Task | Case | Attempt | Status | Completion | Strict | Wall (s) | Failed requirements |",
             "|---|---|---|---|---|---|---|---|"]
    for row in report["tasks"]:
        wall = (row.get("timings") or {}).get("wall")
        lines.append("| %s |" % " | ".join(_cell(v) for v in (
            row["taskId"], row.get("caseVersion"), row.get("attemptId"), row.get("status"), row.get("completion"),
            row.get("strict"), wall, ", ".join(row.get("requirementsFailed") or []) or None)))
    for row in report["tasks"]:
        if not row.get("failed") and not row.get("reasons"):
            continue
        lines += ["", "### %s" % row["taskId"], ""]
        if row.get("reasons"):
            lines.append("- Reasons: %s" % "; ".join(row["reasons"]))
        for failed in row.get("failed") or []:
            lines.append("- %s (%s)%s" % (failed["id"], ", ".join(failed.get("req") or []) or "-",
                                          ": " + failed["hint"] if failed.get("hint") else ""))
    if report.get("notices"):
        lines += ["", "## Notices", ""] + ["- %s: %s" % (n["code"], n["message"]) for n in report["notices"]]
    lines += ["", "## Limitations", ""] + ["- " + text for text in LIMITATIONS]
    return "\n".join(lines) + "\n"

# Producer:Betsy
