"""One attempt end to end: env check, fixture, solver (model or scripted), verification, report.

kind = "model" runs a headless subject; "reference" / "negative:<name>" / "alternative:<name>"
run scripted solutions through the same pipeline, which is what the selftest matrix uses to
prove each verifier accepts correct work and rejects the targeted wrong work.
"""
import contextlib
import datetime
import fcntl
import json
import os
import time
import traceback

from . import classify as C
from . import env as E
from . import fixture as F
from . import journal as J
from . import metrics as M
from . import subject as S
from .focus import FocusKeeper
from .instance import locate
from .rest import Client
from .task import Context, digest

EVAL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS_DIR = os.path.join(EVAL_DIR, "runs")


def _now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


class _UnsupportedEnvironment(Exception):
    def __init__(self, problems):
        self.problems = problems


def _cleanup_outcome(status, reasons, cleanup):
    """Invalidate scored outcomes when teardown failed, preserving other classifications."""
    if not cleanup.get("error"):
        return status, reasons, None
    cleanup_error = cleanup["error"]
    if status in ("pass", "partial", "fail"):
        return "invalid", list(reasons) + ["cleanup_error: %s" % cleanup_error], cleanup_error
    return status, reasons, cleanup_error


def _write(path, obj):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(obj, handle, ensure_ascii=False, indent=1, default=str)


@contextlib.contextmanager
def instance_lock(instance, timeout_s=7200.0):
    """One attempt per editor at a time: every attempt owns the active scene while it runs."""
    os.makedirs(os.path.join(RUNS_DIR, "locks"), exist_ok=True)
    path = os.path.join(RUNS_DIR, "locks", instance.instance_id + ".lock")
    handle = open(path, "w")
    deadline = time.time() + timeout_s
    while True:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            break
        except BlockingIOError:
            if time.time() > deadline:
                handle.close()
                raise RuntimeError("instance %s busy for %.0fs" % (instance.instance_id, timeout_s))
            time.sleep(2.0)
    try:
        handle.write("%d\n" % os.getpid())
        handle.flush()
        yield
    finally:
        fcntl.flock(handle, fcntl.LOCK_UN)
        handle.close()


def _eval_hash(task):
    """The harness and the running task's module; other task files may be edited concurrently."""
    import hashlib
    import inspect
    path = inspect.getsourcefile(type(task))
    with open(path, "rb") as handle:
        task_hash = hashlib.sha256(handle.read()).hexdigest()
    return {"harness": E.tree_hash(os.path.join(EVAL_DIR, "harness"))[0], "task": task_hash}


def connect(project_path, wait_s=0):
    instance = locate(project_path, wait_s=wait_s)
    return instance, Client(instance)


def run_attempt(task, project_path, **kwargs):
    instance, _ = connect(project_path, wait_s=300)
    with instance_lock(instance):
        return _run_attempt(task, project_path, **kwargs)


def _run_attempt(task, project_path, kind="model", model=None, effort="high", seed=None, suite="adhoc",
                 rep=0, warm=True, rerun_of=None, rerun_reason=None, keep_scene=False, solver=None, host="claude"):
    instance, client = connect(project_path, wait_s=300)
    attempt_id = C.new_attempt_id()
    run_id = F.new_run_id()
    seed = seed if seed is not None else int(time.time()) % 100000
    label = kind if kind == "model" else kind.split(":", 1)[0]
    run_dir = os.path.join(RUNS_DIR, suite, task.id, (model or label).replace("/", "_"), attempt_id)
    os.makedirs(run_dir, exist_ok=True)
    ledger = C.Ledger(os.path.join(RUNS_DIR, "ledger.jsonl"))
    report = {"attemptId": attempt_id, "runId": run_id, "suite": suite, "task": task.id,
              "caseVersion": task.case_version, "kind": kind, "model": model, "host": host, "effort": effort,
              "seed": seed, "rep": rep, "warm": warm, "rerunOf": rerun_of, "rerunReason": rerun_reason,
              "startedAt": _now(), "instance": instance.as_dict()}
    status, reasons, scope, card = "invalid", [], F.Scope(run_id), task.scorecard()
    ctx = None
    subject = metrics = None
    try:
        env_before = E.snapshot(client, project_path)
        report["envBefore"] = env_before
        problems = E.requirement_problems(env_before)
        if problems:
            raise _UnsupportedEnvironment(problems)
        report["cleanliness"] = E.cleanliness(project_path, os.path.join(RUNS_DIR, "state",
                                                                          instance.instance_id + ".baseline.json"))
        docs_dir, docs_hash, docs_files = E.freeze_docs(project_path, os.path.join(RUNS_DIR, "docs"))
        report["docs"] = {"treeHash": docs_hash, "files": docs_files}

        scope.settings = F.settings_snapshot(client, task.layer_pairs)
        F.enter(client, scope)
        ctx = Context(client, instance, scope, seed, project_path, env_before, run_dir)
        build_started = time.time()
        with FocusKeeper(instance):
            task.build(ctx)
        client.wait_idle(timeout_s=900)
        F.save_run_scene(client, scope)
        task.after_fixture(ctx)
        fp = json.loads(json.dumps(task.fingerprint(ctx), default=str).replace(run_id, "<RUN>"))
        report["fixture"] = {"scope": scope.as_dict(), "seconds": round(time.time() - build_started, 1),
                             "fingerprint": digest(fp)}
        _write(os.path.join(run_dir, "fixture.json"), {"fingerprint": fp, "facts": ctx.facts})

        eval_hash_before = _eval_hash(task)
        cursor0 = J.current_cursor(client)
        collector = J.Collector(client, cursor0).start()
        solve_started = time.time()
        try:
            if kind == "model":
                prompt = task.full_prompt(ctx)
                with FocusKeeper(instance) as keeper:
                    subject = S.run(prompt, model, effort, docs_dir, run_dir, task.timeout_s,
                                    task.budget_usd, warm=warm, host=host)
                subject["focusActivations"] = keeper.activations
                ctx.answer = subject.get("answer")
            else:
                with FocusKeeper(instance):
                    ctx.answer = (solver or _scripted(task, kind))(ctx)
        finally:
            solve_ended = time.time()
            with FocusKeeper(instance):
                client.wait_idle(timeout_s=900)
            collector.stop()
        ctx.records = J.flatten(collector.events)
        ctx.journal_complete = collector.complete
        report["journal"] = {"cursor0": cursor0, "events": len(collector.events), "records": len(ctx.records),
                             "gaps": collector.gaps, "errors": collector.errors[:20]}
        _write(os.path.join(run_dir, "journal.json"), collector.events)

        if subject is not None:
            metrics = M.parse(subject["stream"], instance.port, client.is_read_only, host=host)
            report["subject"] = {k: v for k, v in subject.items() if k not in ("answer",)}
            report["metrics"] = metrics
            report["telemetry"] = M.telemetry(project_path, solve_started - 1, solve_ended + 1)
        report["answer"] = ctx.answer
        report["solveSeconds"] = round(solve_ended - solve_started, 1)

        env_after = E.snapshot(client, project_path, include_tools=False)
        drift = E.drift(env_before, env_after)
        report["envAfter"] = env_after
        report["drift"] = drift

        verifier_error = None
        try:
            task.verify(ctx, card)
            card.finish()
        except Exception as exc:
            verifier_error = "%s: %s" % (type(exc).__name__, exc)
            report["verifierTraceback"] = traceback.format_exc()
        eval_hash_after = _eval_hash(task)
        tampered = sorted(k for k in eval_hash_before if eval_hash_before[k] != eval_hash_after[k])
        report["evalHash"] = eval_hash_before
        non_rest = [r for r in J.non_rest(ctx.records) if not task.allowed_change(ctx, r)]
        report["nonRestChanges"] = non_rest[:50]
        model_ok = M.model_matches(model, metrics) if metrics else True
        status, reasons = C.classify(card, subject=subject, metrics=metrics, drift=drift,
                                     journal_complete=ctx.journal_complete, non_rest_changes=len(non_rest),
                                     verifier_error=verifier_error, model_ok=model_ok, tampered=tampered)
    except _UnsupportedEnvironment as exc:
        status, reasons = "unsupported", exc.problems
    except F.AwaitingUser as exc:
        status, reasons = "awaiting_user", [str(exc)]
    except Exception as exc:
        status, reasons = "invalid", ["harness_error: %s: %s" % (type(exc).__name__, exc)]
        report["harnessTraceback"] = traceback.format_exc()
    finally:
        process_cleanup = (subject or {}).get("processCleanup") or {}
        if (subject or {}).get("timedOut") and not process_cleanup.get("ok", True):
            # A surviving/uninspectable subject may still write to Unity. Preserve the fixture
            # and avoid issuing teardown or cleanup commands that could race with it.
            report["cleanup"] = {"keepScene": True, "fixturePreserved": True,
                                 "error": "subject process group cleanup unconfirmed: %s" %
                                          "; ".join(process_cleanup.get("errors") or ["group state unknown"])}
        else:
            report["cleanup"] = _cleanup(client, scope, keep_scene, task, ctx)
        status, reasons, cleanup_error = _cleanup_outcome(status, reasons, report["cleanup"])
        if cleanup_error:
            report["cleanup_error"] = cleanup_error
    return _finish(report, run_dir, ledger, status, reasons, card)


def _scripted(task, kind):
    if kind == "reference":
        return task.reference
    name = kind.split(":", 1)[1]
    pool = {n.name: n.apply for n in task.negatives()} if kind.startswith("negative:") else \
        {n: fn for n, fn in task.alternatives()}
    if name not in pool:
        raise KeyError("%s has no %s" % (task.id, kind))
    return pool[name]


def _cleanup(client, scope, keep_scene, task=None, ctx=None):
    out = {"keepScene": keep_scene}
    try:
        if task is not None and ctx is not None:
            out["teardown"] = task.teardown(ctx)
        if scope.settings:
            out["collisionRestored"] = F.restore_collision(client, scope.settings)
        if (scope.folder_created or scope.scene_created or scope.root_iid is not None) and not keep_scene:
            try:
                out["actions"] = F.exit(client, scope)
            except Exception as first:
                out["firstError"] = "%s: %s" % (type(first).__name__, first)
                time.sleep(5)
                client.wait_idle(timeout_s=900)
                out["actions"] = F.exit(client, scope)
            client.wait_idle(timeout_s=900)
    except Exception as exc:
        out["error"] = "%s: %s" % (type(exc).__name__, exc)
    return out


def _finish(report, run_dir, ledger, status, reasons, card):
    report["status"] = status
    report["reasons"] = reasons
    report["score"] = card.as_dict() if card.results else None
    report["endedAt"] = _now()
    _write(os.path.join(run_dir, "report.json"), report)
    ledger.append({"attemptId": report["attemptId"], "runId": report["runId"], "suite": report["suite"],
                   "task": report["task"], "caseVersion": report["caseVersion"], "kind": report["kind"],
                   "model": report["model"], "host": report.get("host", "claude"),
                   "effort": report["effort"], "seed": report["seed"],
                   "rep": report["rep"], "status": status, "reasons": reasons,
                   "score": card.score if card.results else None,
                   "strict": card.strict if card.results else None,
                   "rerunOf": report.get("rerunOf"), "rerunReason": report.get("rerunReason"),
                   "report": os.path.relpath(os.path.join(run_dir, "report.json"), EVAL_DIR)})
    return report

# Producer:Betsy
