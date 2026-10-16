"""Command line entry for the evaluation harness.

  python3 eval/cli.py setup-check [--record-baseline]
  python3 eval/cli.py probe [--only NAME ...]
  python3 eval/cli.py selftest [--task ID ...] [--repeat 2]
  python3 eval/cli.py run --task ID --model MODEL [--reps N] [--suite NAME]
  python3 eval/cli.py suite --plan pilot|calibration [--models ...] [--tasks ...]
  python3 eval/cli.py estimate --plan pilot|calibration
  python3 eval/cli.py report --suite NAME

The project is --project or $UNITYSKILLS_EVAL_PROJECT; it must be open in an editor whose
UnitySkills server is running (the harness never opens or closes editors).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from harness import env as E          # noqa: E402
from harness import fixture as F      # noqa: E402
from harness.instance import locate   # noqa: E402
from harness.rest import Client       # noqa: E402

DEFAULT_PROJECT = os.environ.get("UNITYSKILLS_EVAL_PROJECT", "/Users/betsy/UnityProject/UnitySkillsEval2022")
MODELS = {"opus": "claude-opus-5-5[1M]", "sonnet": "claude-sonnet-5-5[1M]", "fable": "claude-fable-5-1[1M]",
          "luna": "gpt-6-luna", "luna-max": "gpt-6-luna", "terra": "gpt-5.6-terra",
          "sol": "gpt-6.1-sol", "astra": "gpt-6-astra"}


def cmd_setup_check(args):
    instance = locate(args.project, wait_s=args.wait)
    client = Client(instance)
    snap = E.snapshot(client, args.project)
    problems = E.requirement_problems(snap)
    active = F.active_scene_path(client)
    scene_note = None
    if not active:
        F.guard_unsaved(client)
        if F.asset_exists(client, F.RESTING_SCENE):
            client.write("scene_load", scenePath=F.RESTING_SCENE)
            scene_note = "loaded resting scene %s" % F.RESTING_SCENE
            active = F.active_scene_path(client)
    docs_dir, docs_hash, files = E.freeze_docs(args.project, os.path.join(HERE, "runs", "docs"))
    baseline = os.path.join(HERE, "runs", "state", instance.instance_id + ".baseline.json")
    if args.record_baseline:
        E.record_baseline(args.project, baseline)
    out = {"instance": instance.as_dict(), "env": snap, "problems": problems, "activeScene": active,
           "sceneNote": scene_note, "docs": {"dir": docs_dir, "treeHash": docs_hash, "files": files},
           "cleanliness": E.cleanliness(args.project, baseline)}
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 1 if problems else 0


def cmd_probe(args):
    from harness.probes import run_probes
    instance = locate(args.project)
    client = Client(instance)
    results = run_probes(client, instance, args.project, only=args.only, models=MODELS,
                         out_dir=os.path.join(HERE, "runs", "probes"))
    return 0 if all(r.get("ok") for r in results) else 1


def _failed_ids(report):
    return {a["id"] for a in ((report.get("score") or {}).get("assertions") or []) if not a.get("pass")}


def _selected(args):
    import tasks as T
    have = T.available()
    ids = args.task or list(have)
    missing = [t for t in ids if t not in have]
    if missing:
        raise SystemExit("tasks not implemented: %s" % ", ".join(missing))
    return [(t, have[t]) for t in ids]


def cmd_selftest(args):
    import tasks as T
    from harness.runner import run_attempt
    if args.repeat < 2:
        raise SystemExit("selftest requires at least two reference repeats")
    rows = []
    selected = _selected(args)
    source_hashes = {tid: _gate_hash(tid) for tid, _task in selected}
    for tid, task in selected:
        if len(task.negatives()) < 5 or not task.alternatives():
            raise SystemExit("selftest requires at least five negatives and one alternative: %s" % tid)
        seeds = [T.BASE_SEEDS[tid]] + list(args.extra_seed or [])
        fingerprints = []
        for seed in seeds:
            for i in range(args.repeat if seed == seeds[0] else 1):
                rep = run_attempt(task, args.project, kind="reference", seed=seed, suite="selftest")
                fingerprints.append((rep.get("fixture") or {}).get("fingerprint")) if seed == seeds[0] else None
                rows.append({"task": tid, "case": "reference#%d@%d" % (i, seed), "expect": "pass",
                             "status": rep["status"], "failed": sorted(_failed_ids(rep)),
                             "ok": rep["status"] == "pass", "reasons": rep.get("reasons"),
                             "attempt": rep["attemptId"]})
                _echo(rows[-1])
        same = len(set(fingerprints)) == 1 and None not in fingerprints
        rows.append({"task": tid, "case": "determinism", "expect": "same fingerprint", "ok": same,
                     "status": "ok" if same else "differs", "failed": [], "fingerprints": fingerprints})
        _echo(rows[-1])
        for neg in task.negatives():
            rep = run_attempt(task, args.project, kind="negative:" + neg.name, seed=seeds[0], suite="selftest")
            failed = _failed_ids(rep)
            ok = rep["status"] in ("partial", "fail") and failed == neg.expect_failed
            rows.append({"task": tid, "case": "negative:" + neg.name, "expect": sorted(neg.expect_failed),
                         "status": rep["status"], "failed": sorted(failed), "ok": ok,
                         "reasons": rep.get("reasons"), "attempt": rep["attemptId"]})
            _echo(rows[-1])
        for name, _fn in task.alternatives():
            rep = run_attempt(task, args.project, kind="alternative:" + name, seed=seeds[0], suite="selftest")
            rows.append({"task": tid, "case": "alternative:" + name, "expect": "pass", "status": rep["status"],
                         "failed": sorted(_failed_ids(rep)), "ok": rep["status"] == "pass",
                         "reasons": rep.get("reasons"), "attempt": rep["attemptId"]})
            _echo(rows[-1])
    out_dir = os.path.join(HERE, "runs", "selftest")
    os.makedirs(out_dir, exist_ok=True)
    stamp = __import__("datetime").datetime.now().strftime("%Y%m%dT%H%M%S")
    with open(os.path.join(out_dir, stamp + ".json"), "w", encoding="utf-8") as handle:
        json.dump(rows, handle, ensure_ascii=False, indent=1)
    _update_gate(rows, source_hashes)
    bad = [r for r in rows if not r["ok"]]
    print("selftest: %d/%d ok -> runs/selftest/%s.json" % (len(rows) - len(bad), len(rows), stamp))
    return 1 if bad else 0


def _echo(row):
    print("[%s] %-4s %-34s status=%-10s failed=%s" % ("ok" if row["ok"] else "!!", row["task"], row["case"],
                                                     row.get("status"), ",".join(row.get("failed") or [])),
          flush=True)


GATE = os.path.join(HERE, "runs", "selftest", "gate.json")


def _update_gate(rows, source_hashes):
    gate = {}
    if os.path.exists(GATE):
        with open(GATE, "r", encoding="utf-8") as handle:
            gate = json.load(handle)
    for tid in sorted({r["task"] for r in rows}):
        mine = [r for r in rows if r["task"] == tid]
        gate[tid] = {"green": all(r["ok"] for r in mine) and source_hashes[tid] == _gate_hash(tid), "cases": len(mine),
                     "failing": [r["case"] for r in mine if not r["ok"]],
                     "sourceHash": source_hashes[tid]}
    with open(GATE, "w", encoding="utf-8") as handle:
        json.dump(gate, handle, ensure_ascii=False, indent=1)


def _gate_hash(tid):
    import hashlib
    import inspect
    import tasks as T
    digest = hashlib.sha256()
    paths = [__file__, inspect.getsourcefile(type(T.load(tid)))]
    root = os.path.join(HERE, "harness")
    paths += [os.path.join(root, name) for name in sorted(os.listdir(root)) if name.endswith(".py")]
    paths += [os.path.join(HERE, "tasks", "__init__.py")]
    for path in paths:
        with open(path, "rb") as handle:
            digest.update(handle.read())
    return digest.hexdigest()


def _gate_ok(tid):
    if not os.path.exists(GATE):
        return False
    with open(GATE, "r", encoding="utf-8") as handle:
        entry = json.load(handle).get(tid, {})
    return bool(entry.get("green") and entry.get("sourceHash") == _gate_hash(tid))


def _model_id(name):
    return MODELS.get(name, name)


def _effort(name, default="high"):
    return "max" if name == "luna-max" else default


def cmd_run(args):
    import tasks as T
    from harness.runner import RUNS_DIR, run_attempt
    from harness.classify import Ledger
    ledger = Ledger(os.path.join(RUNS_DIR, "ledger.jsonl"))
    task = T.load(args.task)
    if not args.force and not _gate_ok(args.task):
        raise SystemExit("selftest gate is not green for %s (use selftest first)" % args.task)
    code = 0
    for rep in range(args.reps):
        rep_id = args.rep_start + rep
        state, prior = _slot_state(ledger, args.suite, args.task, _model_id(args.model), rep_id,
                                   _effort(args.model, args.effort), args.host)
        if state != "todo":
            raise SystemExit("slot already attempted; use suite resume for registered invalid reruns")
        report = run_attempt(task, args.project, kind="model", model=_model_id(args.model), effort=_effort(args.model, args.effort),
                             seed=T.run_seed(args.task, rep_id), suite=args.suite, rep=rep_id, warm=not args.cold, host=args.host)
        print(json.dumps({k: report.get(k) for k in ("attemptId", "task", "model", "rep", "status", "reasons")},
                         ensure_ascii=False))
        code = code or (0 if report["status"] in ("pass", "partial", "fail") else 1)
        if report.get("cleanup", {}).get("error"):
            return 2
    return code


PLANS = {"pilot": 1, "calibration": 3}


def _slots(task_ids, model_keys, reps):
    """Rep-major order; within a rep, tasks in bank order with models rotated per task (Latin rows)."""
    out = []
    for rep in range(reps):
        for i, tid in enumerate(task_ids):
            k = (rep + i) % len(model_keys)
            for model in model_keys[k:] + model_keys[:k]:
                out.append((rep, tid, model))
    return out


def _slot_state(ledger, suite, tid, model_id, rep, effort="high", host="claude"):
    from harness.classify import MODEL_OUTCOMES
    mine = [e for e in ledger.entries() if e.get("suite") == suite and e.get("task") == tid
            and e.get("model") == model_id and e.get("kind") == "model" and e.get("rep") == rep
            and e.get("effort", "high") == effort and e.get("host", "claude") == host]
    if any(e.get("status") in MODEL_OUTCOMES for e in mine):
        return "done", None
    invalid = [e for e in mine if e.get("status") == "invalid"]
    replaced = {e.get("rerunOf") for e in mine if e.get("rerunOf")}
    open_invalid = [e for e in invalid if e.get("attemptId") not in replaced]
    if mine and not open_invalid:
        return "closed", None          # contaminated/unsupported/awaiting_user or rerun already spent
    if open_invalid:
        return ("rerun", open_invalid[-1]) if len(invalid) == 1 else ("closed", None)
    return "todo", None


def cmd_suite(args):
    import tasks as T
    from harness.classify import Ledger
    from harness.runner import RUNS_DIR, run_attempt
    task_ids = args.tasks or T.TASK_IDS
    model_keys = args.models
    reps = PLANS[args.plan]
    suite = args.suite or ("p0-" + args.host + "-calibration")
    ledger = Ledger(os.path.join(RUNS_DIR, "ledger.jsonl"))
    not_green = [t for t in task_ids if not _gate_ok(t)]
    if not_green and not args.force:
        raise SystemExit("selftest gate not green for: %s" % ", ".join(not_green))
    slots = _slots(task_ids, model_keys, reps)
    pending = [(r, t, m) for r, t, m in slots if _slot_state(ledger, suite, t, _model_id(m), r, _effort(m, args.effort), args.host)[0] in ("todo", "rerun")]
    print(json.dumps(_estimate(ledger, pending), ensure_ascii=False), flush=True)
    if args.dry_run:
        return 0
    _check_cohort(args, task_ids, model_keys, suite)
    for rep, tid, model in slots:
        state, prior = _slot_state(ledger, suite, tid, _model_id(model), rep, _effort(model, args.effort), args.host)
        if state not in ("todo", "rerun"):
            continue
        task = T.load(tid)
        report = run_attempt(task, args.project, kind="model", model=_model_id(model), effort=_effort(model, args.effort),
                             seed=T.run_seed(tid, rep), suite=suite, rep=rep, warm=not args.cold,
                             rerun_of=prior["attemptId"] if prior else None,
                             rerun_reason=("rerun of invalid attempt: %s" % "; ".join(prior.get("reasons") or []))
                             if prior else None, host=args.host)
        print(json.dumps({k: report.get(k) for k in ("attemptId", "task", "model", "rep", "status", "reasons")},
                         ensure_ascii=False), flush=True)
        if args.host == "codex" and (report.get("subject") or {}).get("isolationVerified") is False:
            print("Codex isolation preflight failed: stopping suite", flush=True)
            return 2
        if report.get("cleanup", {}).get("error") or report["status"] in ("awaiting_user", "unsupported"):
            print("environment or cleanup requires attention: stopping suite", flush=True)
            return 2
        if report["status"] == "invalid" and any("model_mismatch" in r for r in report.get("reasons") or []):
            print("model mismatch: aborting the suite", flush=True)
            return 2
    return 0


def _check_cohort(args, task_ids, model_keys, suite):
    """Bind a suite to frozen code, environment and model configurations before any attempt."""
    import hashlib
    instance = locate(args.project)
    snap = E.snapshot(Client(instance), args.project)
    _docs, docs_hash, _count = E.freeze_docs(args.project, os.path.join(HERE, "runs", "docs"))
    current = {"project": args.project, "host": args.host, "hostVersion": snap.get("tools", {}).get(args.host),
               "warm": not args.cold,
               "models": [[_model_id(m), _effort(m, args.effort)] for m in model_keys],
               "tasks": {tid: _gate_hash(tid) for tid in task_ids}, "docsHash": docs_hash,
               "environment": {k: snap.get(k) for k in E.DRIFT_KEYS + ("unityVersion",)}}
    key = hashlib.sha256(suite.encode("utf-8")).hexdigest()[:24]
    path = os.path.join(HERE, "runs", "cohorts", key + ".json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as handle:
            previous = json.load(handle)
        if previous != current:
            raise SystemExit("cohort changed; do not mix new code/environment/configurations into suite %s" % suite)
    else:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(current, handle, indent=1)


PRIOR_COST = {"basic": {"sonnet": 0.4, "opus": 0.8, "fable": 4.0},
              "composite": {"sonnet": 1.5, "opus": 3.0, "fable": 15.0}}
PRIOR_MINUTES = {"basic": 4.0, "composite": 12.0}


def _estimate(ledger, pending):
    import statistics
    import tasks as T
    from harness.runner import EVAL_DIR
    from harness.report import load_reports
    reports = load_reports([e for e in ledger.entries() if e.get("kind") == "model"], EVAL_DIR)
    total_cost, total_min, sources = 0.0, 0.0, {"observed": 0, "prior": 0}
    unknown_cost = 0
    for _rep, tid, model in pending:
        tier = T.load(tid).tier
        seen = [r for r in reports if r.get("task") == tid and r.get("model") == _model_id(model)]
        costs = [((r.get("metrics") or {}).get("result") or {}).get("total_cost_usd") for r in seen]
        costs = [c for c in costs if c is not None]
        walls = [(r.get("solveSeconds") or 0) + ((r.get("fixture") or {}).get("seconds") or 0) for r in seen]
        if costs:
            total_cost += statistics.median(costs)
            total_min += statistics.median(walls) / 60.0 + 1.0
            sources["observed"] += 1
        else:
            if model in PRIOR_COST[tier]:
                total_cost += PRIOR_COST[tier][model]
            else:
                unknown_cost += 1
            total_min += PRIOR_MINUTES[tier]
            sources["prior"] += 1
    return {"estimate": {"runs": len(pending), "usd": None if unknown_cost else round(total_cost, 2),
                         "knownUsdSubtotal": round(total_cost, 2), "unknownCostRuns": unknown_cost, "hours": round(total_min / 60.0, 1),
                         "basis": sources}}


def cmd_estimate(args):
    import tasks as T
    from harness.classify import Ledger
    from harness.runner import RUNS_DIR
    ledger = Ledger(os.path.join(RUNS_DIR, "ledger.jsonl"))
    suite = args.suite or ("p0-" + args.host + "-calibration")
    slots = _slots(args.tasks or T.TASK_IDS, args.models, PLANS[args.plan])
    pending = [(r, t, m) for r, t, m in slots if _slot_state(ledger, suite, t, _model_id(m), r, _effort(m, args.effort), args.host)[0] in ("todo", "rerun")]
    print(json.dumps(_estimate(ledger, pending), ensure_ascii=False, indent=1))
    return 0


def cmd_report(args):
    import tasks as T
    from harness.classify import Ledger
    from harness.report import load_reports, calibration_summary
    from harness.runner import EVAL_DIR, RUNS_DIR
    ledger = Ledger(os.path.join(RUNS_DIR, "ledger.jsonl"))
    entries = [e for e in ledger.entries() if e.get("suite") in args.suite and e.get("kind") == "model"]
    reports = load_reports(entries, EVAL_DIR)
    configs = [("codex" if _model_id(m).startswith("gpt-") else "claude", _model_id(m), _effort(m))
               for m in args.models]
    table = calibration_summary(reports, T.TASK_IDS, configs, expected_reps=3,
                                task_budgets={tid: T.load(tid).timeout_s for tid in T.TASK_IDS})
    if args.output:
        destination = os.path.abspath(args.output)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        with open(destination, "w", encoding="utf-8") as handle:
            handle.write(table + "\n")
        print(destination)
    else:
        print(table)
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(prog="eval/cli.py")
    parser.add_argument("--project", default=DEFAULT_PROJECT)
    sub = parser.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("setup-check")
    p.add_argument("--record-baseline", action="store_true")
    p.add_argument("--wait", type=float, default=0.0)
    p.set_defaults(func=cmd_setup_check)
    p = sub.add_parser("probe")
    p.add_argument("--only", nargs="*")
    p.set_defaults(func=cmd_probe)
    p = sub.add_parser("selftest")
    p.add_argument("--task", nargs="*")
    p.add_argument("--repeat", type=int, default=2)
    p.add_argument("--extra-seed", type=int, nargs="*")
    p.set_defaults(func=cmd_selftest)
    p = sub.add_parser("run")
    p.add_argument("--task", required=True)
    p.add_argument("--model", required=True)
    p.add_argument("--host", choices=("claude", "codex"), default="claude")
    p.add_argument("--reps", type=int, default=1)
    p.add_argument("--rep-start", type=int, default=0)
    p.add_argument("--suite", default="adhoc")
    p.add_argument("--effort", default="high")
    p.add_argument("--cold", action="store_true")
    p.add_argument("--force", action="store_true")
    p.set_defaults(func=cmd_run)
    for name, func in (("suite", cmd_suite), ("estimate", cmd_estimate)):
        p = sub.add_parser(name)
        p.add_argument("--plan", choices=sorted(PLANS), required=True)
        p.add_argument("--suite")
        p.add_argument("--host", choices=("claude", "codex"), default="claude")
        p.add_argument("--tasks", nargs="*")
        p.add_argument("--models", nargs="*", default=None)
        p.add_argument("--effort", default="high")
        p.add_argument("--cold", action="store_true")
        p.add_argument("--force", action="store_true")
        p.add_argument("--dry-run", action="store_true")
        p.set_defaults(func=func)
    p = sub.add_parser("report")
    p.add_argument("--suite", nargs="+", required=True)
    p.add_argument("--output")
    p.add_argument("--models", nargs="*", default=None)
    p.set_defaults(func=cmd_report)
    args = parser.parse_args(argv)
    if hasattr(args, "models") and args.models is None:
        args.models = ["astra", "sol", "terra", "luna-max"] if getattr(args, "host", "claude") == "codex" else ["opus", "sonnet", "fable"]
    args.project = os.path.realpath(args.project)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
