"""Suite summaries from the attempt ledger and per-run reports (local JSON in, Markdown out)."""
import json
import math
import os
import statistics

from .classify import MODEL_OUTCOMES

ENV_COHORT_KEYS = ("pluginHash", "version", "unityVersion", "pipelineType", "currentMode", "tokenLevel",
                   "surfaceProfile", "dryRunPolicy", "tools")


def load_reports(ledger_entries, eval_dir):
    out = []
    for entry in ledger_entries:
        path = os.path.join(eval_dir, entry.get("report") or "")
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as handle:
                out.append(json.load(handle))
    return out


def _median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def _evidence_value(report, key):
    if key == "docs.treeHash":
        return (report.get("docs") or {}).get("treeHash")
    if key.startswith("evalHash."):
        return (report.get("evalHash") or {}).get(key.split(".", 1)[1])
    if key.startswith("envBefore."):
        return (report.get("envBefore") or {}).get(key.split(".", 1)[1])
    return report.get(key)


COHORT_EVIDENCE = ("evalHash.harness", "evalHash.task", "docs.treeHash") + tuple(
    "envBefore." + key for key in ENV_COHORT_KEYS) + ("warm",)


def cohort_integrity(reports):
    """Return observed cohort mismatches and missing evidence without inventing defaults."""
    mismatches, missing = {}, []
    for key in COHORT_EVIDENCE:
        observed = [(r.get("attemptId"), _evidence_value(r, key)) for r in reports
                    if _evidence_value(r, key) is not None]
        if len(observed) != len(reports) or not observed:
            missing.append(key)
        if not observed:
            continue
        unique = {json.dumps(value, sort_keys=True, default=str) for _, value in observed}
        if len(unique) > 1:
            mismatches[key] = [attempt for attempt, _ in observed]
    return {"consistent": not mismatches, "mismatches": mismatches, "missing": missing}


def _check_cohort(reports):
    integrity = cohort_integrity(reports)
    if integrity["mismatches"]:
        raise ValueError("mixed calibration cohort: %s" % ", ".join(sorted(integrity["mismatches"])))


def _check_unique_outcomes(reports):
    seen = {}
    for report in reports:
        if report.get("status") not in MODEL_OUTCOMES:
            continue
        key = tuple(report.get(field) for field in ("host", "model", "effort", "task", "rep"))
        if key in seen:
            raise ValueError("duplicate valid outcome for (host, model, effort, task, rep)=%r: %s and %s" % (
                key, seen[key], report.get("attemptId", "<unknown attempt>")))
        seen[key] = report.get("attemptId", "<unknown attempt>")


def cell_stats(reports):
    _check_unique_outcomes(reports)
    valid = [r for r in reports if r.get("status") in MODEL_OUTCOMES]
    _check_cohort(valid)
    strict_reports = [r for r in valid if r.get("status") == "pass"]
    strict = len(strict_reports)
    completion = [((r.get("score") or {}).get("completion")) for r in valid]
    known_completion = [c for c in completion if c is not None]
    tokens = [((r.get("metrics") or {}).get("tokens") or {}).get("processed") for r in valid]
    output = [((r.get("metrics") or {}).get("tokens") or {}).get("output") for r in valid]
    cache_read = [((r.get("metrics") or {}).get("tokens") or {}).get("cacheRead") for r in valid]
    wall = [((r.get("subject") or {}).get("wallSeconds")) for r in valid]
    turns = [(((r.get("metrics") or {}).get("result") or {}).get("num_turns")) for r in valid]
    http = [((r.get("metrics") or {}).get("httpTotal")) for r in valid]
    execs = [((r.get("telemetry") or {}).get("executions")) for r in valid]
    passing_costs = [(((r.get("metrics") or {}).get("result") or {}).get("total_cost_usd"))
                     for r in strict_reports]
    statuses = {}
    for r in reports:
        statuses[r.get("status", "unknown")] = statuses.get(r.get("status", "unknown"), 0) + 1
    failed = {}
    for r in valid:
        for row in (r.get("score") or {}).get("assertions") or []:
            if not row.get("pass"):
                failed[row["id"]] = failed.get(row["id"], 0) + 1
    completion_range = max(known_completion) - min(known_completion) if known_completion else None
    pass_cost_total = None
    if strict_reports and all(cost is not None for cost in passing_costs):
        pass_cost_total = sum(passing_costs)
    return {"valid": len(valid), "strict": strict,
            "completionMean": round(statistics.mean(known_completion), 1) if known_completion else None,
            "completionMin": min(known_completion) if known_completion else None,
            "completionMax": max(known_completion) if known_completion else None,
            "completionRange": completion_range,
            "instability": (completion_range >= 50) if len(known_completion) >= 2 else None,
            "tokensMedian": _median(tokens), "outputMedian": _median(output), "cacheReadMedian": _median(cache_read),
            "wallMedian": _median(wall), "wallMax": max([w for w in wall if w is not None], default=None),
            "turnsMedian": _median(turns), "httpMedian": _median(http),
            "httpMax": max([h for h in http if h is not None], default=None),
            "execMedian": _median(execs), "passingCostTotal": pass_cost_total,
            "passingCostMedian": _median(passing_costs), "statuses": statuses,
            "failedAssertions": failed, "cohort": cohort_integrity(valid)}


def _config_tuple(config, reports):
    if isinstance(config, dict):
        model = config["model"]
        return (config.get("host") or _default_host(model), model,
                config.get("effort") or _default_effort(model))
    if isinstance(config, (tuple, list)) and len(config) == 3:
        return tuple(config)
    model = str(config)
    observed = {(r.get("host") or _default_host(model), r.get("effort") or _default_effort(model))
                for r in reports if r.get("model") == model}
    if len(observed) > 1:
        raise ValueError("model %s mixes host/effort configurations" % model)
    host, effort = next(iter(observed)) if observed else (_default_host(model), _default_effort(model))
    return host, model, effort


def _default_host(model):
    return "claude" if str(model).startswith("claude-") else "codex"


def _default_effort(model):
    return "max" if str(model) == "gpt-6-luna" else "high"


def _check_model_configs(reports, model):
    configs = {(r.get("host"), r.get("effort")) for r in reports if r.get("model") == model}
    if len(configs) > 1:
        raise ValueError("model %s mixes host/effort configurations: %r" % (model, sorted(configs)))


def suite_table(reports, tasks, models):
    lines = ["| 题目 | " + " | ".join(models) + " |", "|---|" + "---|" * len(models)]
    cells = {}
    for task in tasks:
        row = ["`%s`" % task]
        for model in models:
            rs = [r for r in reports if r.get("task") == task and r.get("model") == model
                  and r.get("kind") == "model"]
            _check_model_configs(rs, model)
            st = cell_stats(rs)
            cells[(task, model)] = st
            if not rs:
                row.append("—")
                continue
            row.append("%d/%d · C=%s · %sK tok · %ss · %s HTTP*" % (
                st["strict"], st["valid"], st["completionMean"],
                "%.0f" % (st["tokensMedian"] / 1000.0) if st["tokensMedian"] is not None else "?",
                "%.0f" % st["wallMedian"] if st["wallMedian"] is not None else "?",
                st["httpMedian"] if st["httpMedian"] is not None else "?"))
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines), cells


def discrimination(reports, task, configs, expected_reps=3):
    """Compute comparative metrics only for a complete grid with complete cohort evidence."""
    normalized = [_config_tuple(c, reports) for c in configs]
    rows_by_config, cells = {}, {}
    for config in normalized:
        host, model, effort = config
        rows = [r for r in reports if r.get("task") == task and r.get("kind") == "model"
                and (r.get("host") or _default_host(model)) == host and r.get("model") == model
                and (r.get("effort") or _default_effort(model)) == effort]
        rows_by_config[config] = rows
        cells[config] = cell_stats(rows)

    cohort_reports = [r for config in normalized for r in rows_by_config[config]
                      if r.get("status") in MODEL_OUTCOMES]
    _check_cohort(cohort_reports)
    cohort = cohort_integrity(cohort_reports)
    observed = [s for s in cells.values() if s["valid"] > 0]
    complete = bool(normalized) and all(
        s["valid"] == expected_reps and
        sorted(r.get("rep") for r in rows_by_config[config] if r.get("status") in MODEL_OUTCOMES)
        == list(range(expected_reps))
        for config, s in cells.items())
    ceiling = floor = None
    evidence_complete = not cohort["missing"]
    all_valid = cohort_reports
    if complete and evidence_complete:
        all_completion = [((r.get("score") or {}).get("completion")) for r in all_valid]
        mean_completion = statistics.mean(all_completion) if all_completion and all(
            v is not None for v in all_completion) else None
        strict_total = sum(1 for r in all_valid if r.get("status") == "pass")
        ceiling = strict_total == len(all_valid)
        floor = strict_total == 0 and mean_completion is not None and mean_completion < 30
    if not complete or not evidence_complete or len(observed) < 2:
        d = delta_c = None
        classification = "unknown"
    else:
        strict_counts = [s["strict"] for s in cells.values()]
        completions = [s["completionMean"] for s in cells.values()]
        d = max(strict_counts) - min(strict_counts)
        delta_c = (round(max(completions) - min(completions), 1)
                   if all(c is not None for c in completions) else None)
        if d >= 2 or (delta_c is not None and delta_c >= 25):
            classification = "discriminating"
        elif d == 1 or (delta_c is not None and 10 <= delta_c < 25):
            classification = "weak"
        else:
            classification = "flat"
    return {"D": d, "deltaC": delta_c, "classification": classification, "complete": complete,
            "ceiling": ceiling, "floor": floor, "cells": cells}


def _p90(values):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(0.9 * len(ordered)) - 1)] if ordered else None


def _budget_recommendation(task_reports, budget):
    passing = [r for r in task_reports if r.get("status") == "pass"]
    if not passing or any((r.get("subject") or {}).get("wallSeconds") is None for r in passing):
        return None
    p90 = _p90([(r.get("subject") or {}).get("wallSeconds") for r in passing])
    raw = math.ceil(1.5 * p90)
    recommended = min(2.0 * budget, max(0.5 * budget, raw)) if budget is not None else raw
    return {"p90": p90, "raw": raw, "recommended": recommended, "clamped": budget is not None}


def _http_budget_recommendation(task_reports, http_budget):
    passing = [r for r in task_reports if r.get("status") == "pass"]
    values = [((r.get("metrics") or {}).get("httpTotal")) for r in passing]
    if not values or any(v is None for v in values):
        return None
    p90 = _p90(values)
    raw = math.ceil(1.5 * p90)
    recommended = min(2.0 * http_budget, max(0.5 * http_budget, raw)) if http_budget is not None else raw
    return {"p90": p90, "raw": raw, "recommended": recommended, "clamped": http_budget is not None}


def calibration_summary(reports, tasks, models, expected_reps=3, task_budgets=None, task_http_budgets=None):
    """Build a compact calibration markdown summary; incomplete evidence stays explicitly unknown."""
    task_budgets = task_budgets or {}
    task_http_budgets = task_http_budgets or {}
    configs = [_config_tuple(model, reports) for model in models]
    headers = ["题目"]
    for host, model, effort in configs:
        headers.append("%s · %s · %s" % (host, model, effort))
    headers += ["D", "ΔC", "区分度", "天花板", "地板"]
    lines = ["## Calibration summary", "", "严格通过和完成度按有效 attempt 统计；缺失数据记为 `unknown`。", "",
             "HTTP counts use the harness URL parser heuristic; they are approximate, not authoritative request telemetry.",
             "", "| " + " | ".join(headers) + " |", "|" + "---|" * len(headers)]
    budget_rows = []
    for task in tasks:
        task_reports = [r for r in reports if r.get("task") == task and r.get("kind") == "model"]
        _check_cohort([r for r in task_reports if r.get("status") in MODEL_OUTCOMES])
        for _, model, _ in configs:
            _check_model_configs(task_reports, model)
        cell_values = []
        for config in configs:
            host, model, effort = config
            rows = [r for r in task_reports if (r.get("host") or _default_host(model)) == host
                    and r.get("model") == model and (r.get("effort") or _default_effort(model)) == effort]
            st = cell_stats(rows)
            comp = "unknown" if st["completionMean"] is None else "%.1f" % st["completionMean"]
            cost = "unknown" if st["passingCostMedian"] is None else "$%.4g" % st["passingCostMedian"]
            cache = "unknown" if st["cacheReadMedian"] is None else "%.0f" % st["cacheReadMedian"]
            processed = "unknown" if st["tokensMedian"] is None else "%.0f" % st["tokensMedian"]
            wall = "unknown" if st["wallMedian"] is None else "%.0fs" % st["wallMedian"]
            http = "unknown" if st["httpMedian"] is None else "%.1f*" % st["httpMedian"]
            status_text = ",".join("%s:%s" % (name, count) for name, count in sorted(st["statuses"].items())) or "none"
            instability = "unknown" if st["instability"] is None else ("yes" if st["instability"] else "no")
            cell_values.append("%d/%d · status %s · C %s (range %s; unstable %s) · processed %s · cache %s · cost %s · %s · %s HTTP*" % (
                st["strict"], st["valid"], status_text, comp,
                "unknown" if st["completionRange"] is None else "%.1f" % st["completionRange"],
                instability, processed, cache, cost, wall, http))
        disc = discrimination(reports, task, configs, expected_reps)
        def flag(value):
            return "unknown" if value is None else ("yes" if value else "no")
        lines.append("| `%s` | %s | %s | %s | %s | %s | %s |" % (
            task, " | ".join(cell_values), disc["D"] if disc["D"] is not None else "unknown",
            disc["deltaC"] if disc["deltaC"] is not None else "unknown", disc["classification"], flag(disc["ceiling"]),
            flag(disc["floor"])))
        rec = _budget_recommendation(task_reports, task_budgets.get(task))
        hrec = _http_budget_recommendation(task_reports, task_http_budgets.get(task))
        if rec is None:
            wall_text = "unknown (no complete strict-pass wall samples)"
        else:
            suffix = "; clamped to 0.5–2× pilot timeout" if rec["clamped"] else " (unclamped; no pilot timeout supplied)"
            wall_text = "P90 %.1fs × 1.5 → %ds%s" % (rec["p90"], rec["recommended"], suffix)
        if hrec is None:
            http_text = "unknown (no complete strict-pass HTTP samples)"
        else:
            suffix = "; clamped to 0.5–2× pilot HTTP cap" if hrec["clamped"] else " (unclamped; no pilot HTTP cap supplied)"
            http_text = "P90 %.1f × 1.5 → %d%s" % (hrec["p90"], hrec["recommended"], suffix)
        budget_rows.append((task, wall_text, http_text))
    lines += ["", "### Budget calibration", "", "| 题目 | Wall timeout | HTTP request cap* |", "|---|---|---|"]
    lines.extend("| `%s` | %s | %s |" % row for row in budget_rows)
    lines += ["", "### Evidence completeness", ""]
    for task in tasks:
        task_reports = [r for r in reports if r.get("task") == task and r.get("kind") == "model"
                        and r.get("status") in MODEL_OUTCOMES]
        if not task_reports:
            lines.append("- `%s`: no valid attempts; cohort evidence is unknown." % task)
            continue
        integrity = cohort_integrity(task_reports)
        absent = integrity["missing"]
        if absent:
            lines.append("- `%s`: missing cohort evidence: %s." % (task, ", ".join(absent)))
        elif integrity["consistent"]:
            lines.append("- `%s`: observed cohort evidence is consistent." % task)
        else:
            lines.append("- `%s`: mixed cohort evidence: %s." % (task, ", ".join(integrity["mismatches"])))
    return "\n".join(lines)

# Producer:Betsy
