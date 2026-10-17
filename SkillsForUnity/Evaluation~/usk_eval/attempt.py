"""Attempt state machine (contract 2.7, 2.8): next builds or re-issues a task, submit freezes, verifies, tears down.

Every phase change is written to state.json before the next REST write, so a killed runner resumes where it
stopped: building is invalidated and cleaned up by ownership, submitted re-freezes, verifying re-verifies from the
frozen evidence and scored finishes its teardown.
"""
import contextlib
import json
import os
import re
import time
import traceback

from . import RUNNER_DIR, submodule
from . import classify as C
from . import environment as E
from . import fixture as F
from . import journal as J
from . import prompts
from . import session as S
from . import store
from . import telemetry as T
from .focus import FocusKeeper
from .paths import EVAL_ASSET_ROOT
from .problems import Outcome, ProblemError, notice, problem
from .rest import REQUESTS, TERMINAL_JOB_STATES, RestError, SkillError, WriteUncertain
from .task_api import BuildContext, VerifyContext, context_args

HANDLE_KEY_RE = re.compile(r"(?i)expected|target|decoy|inactive|answer|correct|wrong|trap|truth|solution|reference|"
                           r"legacy")
JOBS_BUDGET_S = 300.0
TELEMETRY_SETTLE_S = 1.0
IDLE_TIMEOUT_S = 900.0
SCENARIO_GRACE_S = 120.0
SUBMITTED_BY = {"agent": "agent", "panel": "panel", "batch": "driver"}
CALLS_FILE = "runner-calls.jsonl"
MAX_USAGE_ERRORS = 20


# ---------------------------------------------------------------- small helpers


def _persist(rt, state):
    S.save_state(rt.paths, state)


def _file(rt, state, name):
    return os.path.join(rt.paths.attempt_dir(state["sessionId"], state["attemptId"]), name)


def _answer_default(rt, state):
    return os.path.join(rt.paths.io_attempt(state["sessionId"], state["attemptId"]), "answer.json")


def _submit_step(rt, state):
    return rt.step("submit", ["--project", rt.project, "--session", state["sessionId"], "--attempt",
                              state["attemptId"], "--answer", _answer_default(rt, state)])


def _pending(rt, state):
    """VERIFICATION_PENDING (or PRIVATE_PACK_TLS) with the retry command as args[0], and that command as NextStep."""
    step = rt.step("submit", ["--project", rt.project, "--session", state["sessionId"], "--attempt",
                              state["attemptId"], "--retry-verify"])
    attempts = (state.get("private") or {}).get("attempts")
    return S.private_problem("VERIFICATION_PENDING", attempts, [step["line"]]), step


def _capture_host(rt):
    try:
        return submodule("usage").capture_host(rt.environ)
    except Exception:
        return {"kind": "none", "sessionIds": [], "cwd": os.getcwd()}


def _usage_record(host, t0, t1, reason, error=None):
    """UsageRecord without the usage module: placeholders and the fallback when a reader breaks."""
    return {"host": (host or {}).get("kind") or "none", "source": "none", "sessionIds": [],
            "window": {"from": t0, "to": t1}, "model": {"values": [], "source": None},
            "effort": {"values": [], "source": None}, "tokens": None, "raw": None, "messages": None,
            "subagents": None, "parserVersion": 1, "unavailableReason": reason,
            "errors": [{"file": "", "line": 0, "error": error}] if error else [], "computedAt": store.now_iso()}


def _frames(exc):
    """Traceback positions without source text: private verify code must never reach the log (contract 4.2)."""
    return "\n".join("%s:%d in %s" % (os.path.basename(f.filename), f.lineno, f.name)
                     for f in traceback.extract_tb(exc.__traceback__))


def _check_handles(value, where="handles"):
    if isinstance(value, dict):
        for key, item in value.items():
            if not isinstance(key, str) or HANDLE_KEY_RE.search(key):
                raise ValueError("%s key %r is not allowed (identities and paths only)" % (where, key))
            _check_handles(item, "%s.%s" % (where, key))
    elif isinstance(value, (list, tuple)):
        for index, item in enumerate(value):
            _check_handles(item, "%s[%d]" % (where, index))
    elif not (value is None or isinstance(value, (str, int, float, bool))):
        raise ValueError("%s holds a %s; handles must be JSON" % (where, type(value).__name__))


def _task_state(status, reruns_used):
    if status == "cancelled":
        return "cancelled"
    if status == "invalid":
        return "pending" if reruns_used < 1 else "invalid"
    return "done"


def _session_for(rt):
    current = S.read_current(rt.paths)
    sid = rt.args.session or (current or {}).get("sessionId")
    if not sid:
        raise ProblemError(problem("NO_ACTIVE_SESSION"))
    session = S.load_session(rt.paths, sid)
    rt.log.primary = rt.paths.session_log(sid)
    if session["status"] == "aborted":
        raise ProblemError(problem("NO_ACTIVE_SESSION", detail="session %s was aborted" % sid))
    if session["status"] == "active" and (current or {}).get("sessionId") != sid:
        raise ProblemError(problem("NO_ACTIVE_SESSION", detail="session %s is not the current session" % sid))
    return session


# ---------------------------------------------------------------- next


def cmd_next(rt):
    with rt.lock():
        session = _session_for(rt)
        for _ in range(16):
            if session["status"] != "active":
                return _done(session)
            aid = S.active_attempt(rt.paths, session)
            if not aid:
                break
            state = S.load_state(rt.paths, session["sessionId"], aid)
            phase = state["phase"]
            if phase == "building":
                rt.log.warn("attempt %s was interrupted while building" % aid)
                _fail_build(rt, session, state, "build_interrupted")
            elif phase == "ready":
                return _reissue(rt, session, state)
            elif phase in ("submitted", "verifying"):
                _advance(rt, session, state, {})
                if state["phase"] == "pending_verification":
                    issue, step = _pending(rt, state)
                    return Outcome(None, [issue], step)
            elif phase == "pending_verification":
                issue, step = _pending(rt, state)
                return Outcome(None, [issue], step)
            elif phase == "scored":
                _finish_scored(rt, session, state)
            else:
                finish_attempt(rt, session, state)
            session = S.load_session(rt.paths, session["sessionId"])
        else:
            raise RuntimeError("attempt state did not settle")
        pending = [t for t in session["order"] if session["tasks"][t]["state"] == "pending"]
        if not pending:
            if not S.open_tasks(session):
                S.finalize_session(rt, session, "completed")
            return _done(session)
        return _issue_new(rt, session, pending[0])


def _done(session):
    completed = sum(1 for t in session["order"] if session["tasks"][t]["state"] in S.FINAL_TASK_STATES)
    return Outcome({"done": True, "sessionId": session["sessionId"], "n": len(session["order"]),
                    "completed": completed, "status": "completed"})


def _new_state(rt, session, task_id, meta, rerun_of, resting):
    aid = store.new_attempt_id()
    scope = F.Scope(F.new_run_id())
    selection = session["selection"]
    seeds = meta.get("seeds") or [0]
    seed = selection.get("seedOverride") if selection.get("seedOverride") is not None else seeds[0]
    return {
        "schema": 1, "sessionId": session["sessionId"], "attemptId": aid, "taskId": task_id,
        "caseVersion": meta.get("caseVersion"), "seed": seed, "k": session["order"].index(task_id) + 1,
        "phase": "building", "status": None, "reasons": [], "rerunOf": rerun_of,
        "createdAt": store.now_iso(), "issuedAt": None, "submittedAt": None, "scoredAt": None, "tornDownAt": None,
        "scope": scope.as_dict(), "resting": resting, "settings": {"collision": {}}, "handles": {},
        "c0": {"data": {}, "digests": {}}, "fingerprint": None,
        "journal": {"cursor0": None, "cursorEnd": None, "refreshWindow": None, "forcedPlayModeExit": False,
                    "complete": None, "gaps": [], "segments": [], "conflicts": 0, "corruptLines": 0},
        "telemetry": {"t0": None, "t1": None},
        "http": {"received0": None, "received1": None, "runnerCalls": 0},
        "env": {"atIssue": {}, "atSubmit": None},
        "host": {"kind": "none", "sessionIds": [], "cwd": os.getcwd(), "isolation": "n/a"},
        "prompt": {"lang": session["lang"], "sha256": None, "archive": None, "ioFile": None},
        "answer": {"path": None, "present": False, "sha256": None, "error": None, "schemaErrors": []},
        "submittedBy": None, "reissues": 0, "duplicateSubmits": 0,
        "evalTreeHash": {"atIssue": None, "atSubmit": None},
        "private": None, "timings": {"buildSeconds": None, "verifySeconds": None}, "teardown": None,
        "track": meta.get("track") or selection.get("track"), "tier": meta.get("tier") or selection.get("tier"),
        "minutes": meta.get("minutes"),
    }


def _issue_new(rt, session, task_id):
    pack = S.open_session_pack(rt, session)
    meta = pack.meta(task_id)
    task = S.load_task(rt, pack, task_id)
    prompt_path = os.path.join(pack.task_dir(task_id), "prompt.%s.md" % session["lang"])
    try:
        with open(prompt_path, "r", encoding="utf-8") as handle:
            lines = prompts.parse_prompt(handle.read())
    except (OSError, ValueError) as exc:
        raise ProblemError(problem("PACK_INVALID", detail="%s: %s" % (os.path.basename(prompt_path), exc)))
    # N1: same editor checks as `check`
    client = rt.client()
    health = client.health()
    found, _notices, _info = S.editor_checks(rt, client, health, [meta["requires"]])
    if found:
        raise ProblemError(found)
    host = _capture_host(rt)
    host_sid = host["sessionIds"][0] if host.get("sessionIds") else None
    if session["mode"] == "strict" and host_sid and session["hostSessions"].get(host_sid):
        raise ProblemError(problem("STRICT_NEEDS_NEW_SESSION"))
    _owned, found = S.residue_problems(rt, None)
    if found:
        raise ProblemError(found)
    # N2: resting scenes
    scenes = F.loaded_scenes(client)
    found, _summary = S.scene_checks(scenes)
    if found:
        raise ProblemError(found)
    resting = F.resting_record(scenes)
    stray = [s["path"] for s in resting["scenes"] if F.under(s["path"], EVAL_ASSET_ROOT)]
    if stray:
        raise ProblemError(problem("RESIDUE_FOUND", [", ".join(stray)]))
    env0 = E.snapshot(client, rt.registry_pid(), health)
    # N3: state first, then any REST write
    notices = []
    entry = session["tasks"][task_id]
    rerun_of = entry["attempts"][-1] if entry["attempts"] else None
    if rerun_of:
        entry["rerunsUsed"] = int(entry.get("rerunsUsed") or 0) + 1
        notices.append(notice("RERUN"))
    state = _new_state(rt, session, task_id, meta, rerun_of, resting)
    entry["state"] = "issued"
    entry["attempts"].append(state["attemptId"])
    # state.json, then current.json, then session.json: whichever write a crash interrupts, `next` either recovers
    # the attempt through current.json or finds the task still pending.
    _persist(rt, state)
    S.write_current(rt.paths, session, state["attemptId"])
    S.save_session(rt.paths, session)
    rt.log.info("attempt %s for %s: building (run %s, seed %s)" % (
        state["attemptId"], task_id, state["scope"]["runId"], state["seed"]))
    started = time.time()
    try:
        with FocusKeeper(client, rt.registry_pid(), environ=rt.environ):
            _build(rt, session, state, task, lines, client, env0, host, host_sid)
    except Exception as exc:
        return _build_failed(rt, session, state, task, exc)
    state["timings"]["buildSeconds"] = round(time.time() - started, 1)
    _persist(rt, state)
    if state["host"]["isolation"] == "unverified":
        notices.append(notice("ISOLATION_UNVERIFIED"))
    rt.log.info("attempt %s ready" % state["attemptId"])
    return _issue_data(rt, session, state, False, notices)


def _build(rt, session, state, task, lines, client, env0, host, host_sid):
    scope = F.Scope.from_dict(state["scope"])

    def sync():
        state["scope"] = scope.as_dict()
        _persist(rt, state)

    # N4: run folder, run scene, object root
    state["settings"]["collision"] = F.collision_snapshot(client, task.layer_pairs)
    _persist(rt, state)
    F.enter(client, scope, sync)
    # N5: task build
    ctx = BuildContext(client, *context_args(session, state, task, rt.project, env0), log=rt.log.info)
    handles = task.build(ctx)
    handles = {} if handles is None else handles
    if not isinstance(handles, dict):
        raise ValueError("build must return a dict of handles")
    _check_handles(handles)
    json.dumps(handles, allow_nan=False)
    ctx.handles = handles
    state["handles"] = handles
    _persist(rt, state)
    client.wait_idle(timeout_s=IDLE_TIMEOUT_S)
    F.save_run_scene(client, scope)
    # N6: C0 and fingerprint
    snap = task.snapshot(ctx)
    if not isinstance(snap, dict) or not isinstance(snap.get("data", {}), dict) or \
            not isinstance(snap.get("digests", {}), dict):
        raise ValueError("snapshot must return {'data': {...}, 'digests': {...}}")
    state["c0"] = {"data": snap.get("data") or {}, "digests": snap.get("digests") or {}}
    json.dumps(state["c0"], allow_nan=False)
    state["fingerprint"] = submodule("verifylib").fingerprint(ctx)
    _persist(rt, state)
    # N7: evaluation tree and environment at issue
    state["evalTreeHash"]["atIssue"] = E.tree_hash(RUNNER_DIR)
    state["env"]["atIssue"] = E.snapshot(client, rt.registry_pid())
    _persist(rt, state)
    # N8: prompt, cursor0, received0, host, t0
    _write_prompt(rt, session, state, task, ctx, lines, env0)
    state["journal"]["cursor0"] = J.current_cursor(client)
    health = client.health()
    state["http"]["received0"] = (health.get("queueStats") or {}).get("totalReceived")
    state["http"]["runnerCalls"] = 0
    isolation = "n/a" if session["mode"] != "strict" else ("verified" if host_sid else "unverified")
    state["host"] = {"kind": host.get("kind") or "none", "sessionIds": list(host.get("sessionIds") or []),
                     "cwd": host.get("cwd") or os.getcwd(), "isolation": isolation}
    if host_sid:
        bucket = session["hostSessions"].setdefault(host_sid, [])
        if state["attemptId"] not in bucket:
            bucket.append(state["attemptId"])
    t0 = store.now_iso()
    state["issuedAt"] = t0
    state["telemetry"]["t0"] = t0
    state["phase"] = "ready"
    _persist(rt, state)
    S.save_session(rt.paths, session)


def _write_prompt(rt, session, state, task, ctx, lines, env0):
    instance = rt.instance()
    builtins = {"session": session["sessionId"], "k": state["k"], "n": len(session["order"]),
                "track": state["track"], "tier": state["tier"], "project": rt.project,
                "unity": env0.get("unityVersion"), "pipeline": env0.get("pipelineType"), "port": instance.port,
                "instance": instance.instance_id, "R": ctx.R, "A": ctx.A, "scene": ctx.scene,
                "minutes": state["minutes"]}
    variables = prompts.merge_vars(builtins, task.prompt_vars(ctx))
    requirements = prompts.render_requirements(lines, variables)
    schema = prompts.with_declared(task.answer_schema())
    text = prompts.render_prompt(session["lang"], state["track"], builtins, requirements,
                                 prompts.render_schema(schema, session["lang"]), _submit_step(rt, state)["line"])
    archive = _file(rt, state, "prompt.md")
    io_file = os.path.join(rt.paths.io_attempt(state["sessionId"], state["attemptId"]), "prompt.md")
    store.atomic_write_text(archive, text, bom=True)
    store.atomic_write_text(io_file, text, bom=True)
    state["prompt"] = {"lang": session["lang"], "sha256": store.sha256_text(text),
                       "archive": rt.paths.relative(archive), "ioFile": io_file, "answerSchema": schema}
    state["answer"]["path"] = _answer_default(rt, state)


def _issue_data(rt, session, state, reissued, notices):
    step = _submit_step(rt, state)
    text = None
    if rt.args.inline:
        try:
            with open(state["prompt"]["ioFile"], "r", encoding="utf-8-sig") as handle:
                text = handle.read()
        except (OSError, TypeError):
            text = None
    data = {"done": False, "sessionId": session["sessionId"], "taskId": state["taskId"],
            "caseVersion": state["caseVersion"], "attemptId": state["attemptId"], "k": state["k"],
            "n": len(session["order"]), "track": state["track"], "tier": state["tier"], "minutes": state["minutes"],
            "promptFile": state["prompt"]["ioFile"], "answerFile": _answer_default(rt, state),
            "submitCommand": step["line"], "submitArgv": step["argv"], "reissued": reissued,
            "rerunOf": state.get("rerunOf"), "prompt": text,
            "answerSchema": state["prompt"].get("answerSchema") if rt.args.inline else None, "notices": notices}
    return Outcome(data, [], step)


def _reissue(rt, session, state):
    state["reissues"] = int(state.get("reissues") or 0) + 1
    host = _capture_host(rt)
    host_sid = host["sessionIds"][0] if host.get("sessionIds") else None
    if host_sid:
        bucket = session["hostSessions"].setdefault(host_sid, [])
        if state["attemptId"] not in bucket:
            bucket.append(state["attemptId"])
            S.save_session(rt.paths, session)
    io_file = state["prompt"].get("ioFile")
    if io_file and not os.path.isfile(io_file):
        try:
            with open(os.path.join(rt.paths.root, state["prompt"]["archive"]), "rb") as handle:
                store.atomic_write_bytes(io_file, handle.read())
        except (OSError, TypeError):
            pass
    _persist(rt, state)
    notices = [notice("RERUN")] if state.get("rerunOf") else []
    if state["host"].get("isolation") == "unverified":
        notices.append(notice("ISOLATION_UNVERIFIED"))
    return _issue_data(rt, session, state, True, notices)


def _build_failed(rt, session, state, task, exc):
    rt.log.error("attempt %s build failed: %s: %s\n%s" % (state["attemptId"], type(exc).__name__, exc,
                                                          traceback.format_exc()))
    detail = "; ".join(p["code"] for p in exc.problems) if isinstance(exc, ProblemError) else str(exc)
    teardown = _fail_build(rt, session, state, "build_failed: %s: %s" % (type(exc).__name__, detail[:300]), task)
    problems = list(exc.problems) if isinstance(exc, ProblemError) else []
    problems.append(problem("FIXTURE_FAILED", [rt.log.path]))
    if not teardown.get("ok"):
        problems.append(problem("TEARDOWN_FAILED", detail=teardown.get("error")))
    return Outcome(None, problems)


def _fail_build(rt, session, state, reason, task=None, allow_complete=True):
    teardown = _teardown(rt, session, state, task)
    _write_result(rt, session, state, "invalid", [reason], teardown=teardown)
    state.update({"status": "invalid", "reasons": [reason], "teardown": teardown, "phase": "torn_down",
                  "tornDownAt": store.now_iso()})
    _persist(rt, state)
    finish_attempt(rt, session, state, allow_complete)
    return teardown


# ---------------------------------------------------------------- submit


def cmd_submit(rt):
    args = rt.args
    if sum(1 for flag in (args.answer, args.no_answer, args.retry_verify) if flag) != 1:
        raise ProblemError(problem("BAD_ARGUMENTS", ["submit needs exactly one of --answer, --no-answer, "
                                                     "--retry-verify"]))
    if (args.private_zip or args.private_dir) and not args.retry_verify:
        raise ProblemError(problem("BAD_ARGUMENTS", ["--private-zip/--private-dir go with --retry-verify"]))
    submitted_by = args.submitted_by or SUBMITTED_BY.get(rt.launcher, "agent")
    with rt.lock():
        session = _session_for(rt)
        state = S.load_state(rt.paths, session["sessionId"], args.attempt)
        phase = state["phase"]
        if phase in ("scored", "torn_down"):
            state["duplicateSubmits"] = int(state.get("duplicateSubmits") or 0) + 1
            _persist(rt, state)
            return _accepted(rt, session, state, already=True)
        if session["status"] != "active":
            raise ProblemError(problem("NO_ACTIVE_SESSION", detail="session %s is %s" % (
                session["sessionId"], session["status"])))
        if S.active_attempt(rt.paths, session) != state["attemptId"]:
            raise ProblemError(problem("ATTEMPT_NOT_ACTIVE", [state["attemptId"]]))
        if phase == "building":
            raise ProblemError(problem("ATTEMPT_NOT_READY"))
        overrides = {}
        if args.retry_verify:
            if phase != "pending_verification":
                raise ProblemError(problem("RETRY_NOT_APPLICABLE"))
            overrides = {"privateZip": os.path.abspath(args.private_zip) if args.private_zip else None,
                         "privateDir": os.path.abspath(args.private_dir) if args.private_dir else None}
            state["verifiedLate"] = True
            state["phase"] = "verifying"
            _persist(rt, state)
        elif phase == "pending_verification":
            issue, step = _pending(rt, state)
            out = _accepted(rt, session, state, pending=True)
            return Outcome(out.data, [issue], step)
        elif phase == "ready":
            _accept_answer(rt, state, args.answer, args.no_answer, submitted_by)
        _advance(rt, session, state, overrides)
        session = S.load_session(rt.paths, session["sessionId"])
        if state["phase"] == "pending_verification":
            issue, step = _pending(rt, state)
            out = _accepted(rt, session, state, pending=True)
            return Outcome(out.data, [issue], step)
        out = _accepted(rt, session, state)
        teardown = state.get("teardown") or {}
        if teardown and teardown.get("ok") is False:
            out.problems.append(problem("TEARDOWN_FAILED", detail=teardown.get("error")))
        return out


def _accepted(rt, session, state, already=False, pending=False):
    sid, tid = session["sessionId"], state["taskId"]
    remaining = sum(1 for t in session["order"] if t != tid and session["tasks"][t]["state"] in S.OPEN_TASK_STATES)
    strict = session["mode"] == "strict"
    step = None if strict or pending else rt.step("next", ["--project", rt.project, "--session", sid])
    notices = []
    if rt.launcher == "panel" and not pending:
        result = store.read_json(rt.paths.result_file(sid, state["attemptId"]), missing_ok=True) or {}
        usage = result.get("usage") or {}
        if usage and usage.get("tokens") is None:
            notices.append(notice("USAGE_UNAVAILABLE", [usage.get("unavailableReason") or "unknown"]))
    data = {"accepted": True, "alreadySubmitted": already, "pending": pending, "sessionId": sid,
            "attemptId": state["attemptId"], "taskId": tid, "remaining": remaining,
            "done": session["status"] == "completed", "stop": strict,
            "nextCommand": step["line"] if step else None, "notices": notices}
    return Outcome(data, [], step)


def _accept_answer(rt, state, answer_path, no_answer, submitted_by):
    """S1: copy the answer, record its hash and parse/schema errors (never told to the agent), t1 = now."""
    answer = dict(state["answer"])
    if no_answer:
        answer.update({"present": False, "sha256": None, "error": None, "schemaErrors": [],
                       "declaredComplete": None})
    else:
        path = os.path.abspath(answer_path)
        if not os.path.isfile(path):
            raise ProblemError(problem("ANSWER_NOT_FOUND", [path]))
        try:
            with open(path, "rb") as handle:
                raw = handle.read()
        except OSError as exc:
            raise ProblemError(problem("IO_ERROR", [path, exc.strerror or str(exc)]))
        store.atomic_write_bytes(_file(rt, state, "answer.json"), raw)
        error, errors, declared = None, [], None
        try:
            value = json.loads(raw.decode("utf-8-sig"))
        except (ValueError, UnicodeDecodeError):
            error = "invalid_json"
        else:
            if not isinstance(value, dict):
                error = "not_object"
            else:
                schema = state["prompt"].get("answerSchema") or prompts.with_declared({})
                errors = prompts.validate_answer(schema, value)
                flag = value.get(prompts.DECLARED_KEY)
                declared = flag if isinstance(flag, bool) else None
        answer.update({"path": path, "present": True, "sha256": store.sha256_bytes(raw), "error": error,
                       "schemaErrors": errors, "declaredComplete": declared})
    state["answer"] = answer
    t1 = store.now_iso()
    state["submittedAt"] = t1
    state["telemetry"]["t1"] = t1
    state["submittedBy"] = submitted_by
    host = _capture_host(rt)
    if host.get("kind") == state["host"].get("kind") and host.get("sessionIds"):
        try:
            merged = submodule("usage").merge_session_ids(state["host"].get("sessionIds"), host["sessionIds"])
        except Exception:
            merged = list(dict.fromkeys(list(state["host"].get("sessionIds") or []) + list(host["sessionIds"])))
        state["host"]["sessionIds"] = merged
    state["phase"] = "submitted"
    _persist(rt, state)
    rt.log.info("attempt %s submitted by %s" % (state["attemptId"], submitted_by))


def _advance(rt, session, state, overrides):
    if state["phase"] == "submitted":
        _freeze(rt, session, state)
    if state["phase"] == "verifying":
        _verify(rt, session, state, overrides)
    if state["phase"] == "scored":
        _finish_scored(rt, session, state)


# ---------------------------------------------------------------- S2-S6 freeze


def _side_calls(rt, state):
    return sum(int(line.get("calls") or 0) for line in store.read_jsonl(_file(rt, state, CALLS_FILE)))


def _wait_jobs(client, budget_s=JOBS_BUDGET_S):
    """S2: long-poll every non-terminal job up to budget_s in total; the rest count as inflight."""
    listing = client.get("/jobs", {"limit": 100})
    jobs = [j for j in listing.get("jobs") or [] if j.get("jobId") and j.get("status") not in TERMINAL_JOB_STATES]
    deadline = time.time() + budget_s
    left = 0
    for job in jobs:
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                left += 1
                break
            wait = int(max(1, min(30, remaining)))
            try:
                snap = client.job(job["jobId"], wait=wait, timeout=wait + 30)
            except RestError as exc:
                if exc.status == 404:
                    break
                raise
            if snap.get("status") in TERMINAL_JOB_STATES:
                break
    return left


def _refresh(client):
    """S3: the runner's own asset_refresh; imports in its window are the agent's direct writes."""
    try:
        res = client.write("asset_refresh")
    except WriteUncertain:
        client.wait_idle(timeout_s=IDLE_TIMEOUT_S)
        res = client.write("asset_refresh")
    job = (res or {}).get("jobId")
    if job:
        try:
            client.wait_job(job, timeout_s=IDLE_TIMEOUT_S)
        except RestError:
            pass


def _stop_play(client):
    if not client.read("editor_get_state").get("isPlaying"):
        return False
    client.write("editor_stop")
    deadline = time.time() + 60
    while client.read("editor_get_state").get("isPlaying"):
        if time.time() > deadline:
            raise RestError("editor still in Play Mode after editor_stop")
        time.sleep(0.5)
    client.wait_idle(timeout_s=IDLE_TIMEOUT_S)
    return True


def _freeze_journal(rt, state, cursor0, cursor_end):
    journal_state = state["journal"]
    path = _file(rt, state, "journal.jsonl")
    if cursor_end < cursor0:
        journal_state.update({"complete": False, "seqReset": True, "gaps": [], "segments": [], "conflicts": 0,
                              "corruptLines": 0})
        J.write_entries(path, [])
        return
    read = J.read_range(rt.paths.journal_dir, cursor0, cursor_end)
    J.write_entries(path, read.entries)
    journal_state.update(read.as_state())
    journal_state["seqReset"] = False


def _freeze_telemetry(rt, state, end_iso, settle=True):
    if settle:
        wait = store.epoch(end_iso) + TELEMETRY_SETTLE_S - time.time()
        if wait > 0:
            time.sleep(wait)
    state["telemetry"]["summary"] = T.freeze(rt.paths.library, state["issuedAt"], end_iso,
                                             _file(rt, state, "telemetry.jsonl"))


def _freeze(rt, session, state):
    client = rt.client()
    health = client.health()                                   # S2: received1 closes the runnerCalls window
    state["http"]["received1"] = (health.get("queueStats") or {}).get("totalReceived")
    state["http"]["runnerCalls"] = _side_calls(rt, state) + (REQUESTS.value - rt.requests_at_start)
    _persist(rt, state)
    state["freeze"] = {"inflightJobs": _wait_jobs(client)}
    journal_state = state["journal"]
    with FocusKeeper(client, rt.registry_pid(), environ=rt.environ):
        cursor_from = J.current_cursor(client)                 # S3
        _refresh(client)
        client.wait_idle(timeout_s=IDLE_TIMEOUT_S)
        journal_state["refreshWindow"] = {"from": cursor_from, "to": J.current_cursor(client)}
    if _stop_play(client):                                     # S4
        journal_state["forcedPlayModeExit"] = True
    _persist(rt, state)
    cursor_end = J.current_cursor(client)                      # S5
    journal_state["cursorEnd"] = cursor_end
    _freeze_journal(rt, state, journal_state.get("cursor0") or 0, cursor_end)   # S6
    _freeze_telemetry(rt, state, state["submittedAt"])
    state["env"]["atSubmit"] = E.snapshot(client, rt.registry_pid())
    state["evalTreeHash"]["atSubmit"] = E.tree_hash(RUNNER_DIR)
    state["phase"] = "verifying"
    _persist(rt, state)


# ---------------------------------------------------------------- S7-S10 verify


def _verify_context(rt, session, state, task, client, module):
    answer = None
    if state["answer"].get("present") and state["answer"].get("error") is None:
        try:
            with open(_file(rt, state, "answer.json"), "rb") as handle:
                answer = json.loads(handle.read().decode("utf-8-sig"))
        except (OSError, ValueError):
            answer = None
    journal_state = state["journal"]
    records = J.flatten(J.load_entries(_file(rt, state, "journal.jsonl")), journal_state.get("refreshWindow"))
    return VerifyContext(
        client, *context_args(session, state, task, rt.project), handles=state.get("handles"), log=None,
        c0=state["c0"].get("data"), c0_digests=state["c0"].get("digests"), answer=answer,
        answer_error=state["answer"].get("error"), answer_schema_errors=state["answer"].get("schemaErrors") or [],
        records=records, journal_complete=bool(journal_state.get("complete")),
        journal={"cursor0": journal_state.get("cursor0"), "cursorEnd": journal_state.get("cursorEnd"),
                 "refreshWindow": journal_state.get("refreshWindow"),
                 "forcedPlayModeExit": bool(journal_state.get("forcedPlayModeExit"))},
        telemetry=state["telemetry"].get("summary"), allowed_change=getattr(module, "allowed_change", None))


def _run_scenario(rt, client, state, definition, ctx):
    """S9: (scenario, infra_reason). Agent-caused failures are scored; lost jobs and dead REST are infra."""
    spec = definition(ctx) if callable(definition) else definition
    steps = spec.get("steps") or []
    timeout = int(spec.get("timeoutSeconds") or 60)
    try:
        client.wait_idle(timeout_s=IDLE_TIMEOUT_S)
        _stop_play(client)
        started = client.write("editor_run_scenario", steps=json.dumps(steps),
                               executionMode=spec.get("executionMode") or "play", timeoutSeconds=timeout)
    except SkillError as exc:
        return {"status": "rejected", "passed": False, "error": "%s: %s" % (exc.code, str(exc)[:300]),
                "interrupted": False, "executionUncertain": False, "steps": [], "compilation": None,
                "logWindow": None}, None
    except RestError as exc:
        return None, "scenario_infra: %s: %s" % (type(exc).__name__, str(exc)[:300])
    job_id = (started or {}).get("jobId")
    if not job_id:
        return None, "scenario_infra: editor_run_scenario returned no jobId"
    try:
        with FocusKeeper(client, rt.registry_pid(), environ=rt.environ):
            job = client.wait_job(job_id, timeout_s=timeout + SCENARIO_GRACE_S)
    except RestError as exc:
        return None, "scenario_infra: job %s: %s" % (job_id, str(exc)[:300])
    store.atomic_write_json(_file(rt, state, "scenario.json"), dict({"schema": 1}, **job))
    data = job.get("resultData") or {}
    return {"status": data.get("status") or job.get("status"), "passed": data.get("passed"),
            "error": data.get("error") or job.get("error"), "interrupted": bool(data.get("interrupted")),
            "executionUncertain": bool(data.get("executionUncertain")), "steps": data.get("steps") or [],
            "compilation": data.get("compilation"), "logWindow": data.get("logWindow")}, None


def _score(card, hints, lang):
    data = card.as_dict()
    lang = prompts.prompt_lang(lang)
    for row in data.get("assertions") or []:
        row.setdefault("req", [])
        hint = None
        if not row.get("pass"):
            entry = hints.get(row.get("id")) if isinstance(hints, dict) else None
            hint = (entry.get(lang) or entry.get("en")) if isinstance(entry, dict) else (
                entry if isinstance(entry, str) else None)
        row["hint"] = hint
    return data


def _verify(rt, session, state, overrides):
    started = time.time()
    pack = S.open_session_pack(rt, session)
    task = S.load_task(rt, pack, state["taskId"])
    meta = pack.meta(state["taskId"])
    private = submodule("private")
    try:                                                       # S7
        fetched = private.fetch(pack, state["taskId"], plugin_version=session.get("pluginVersion"),
                                user_agent=rt.user_agent, private_zip=overrides.get("privateZip"),
                                private_dir=overrides.get("privateDir"), environ=rt.environ)
    except Exception as exc:
        attempts = list(getattr(exc, "attempts", None) or [{"via": None, "error": type(exc).__name__}])
        state["private"] = {"kind": (session["pack"].get("private") or {}).get("kind"),
                            "file": (meta.get("private") or {}).get("file"), "sha256": None, "attempts": attempts}
        _wait_for_private(rt, state)
        return
    state["private"] = {"kind": fetched.kind, "file": fetched.file, "sha256": fetched.sha256,
                        "attempts": list(getattr(fetched, "attempts", None) or [])}
    _persist(rt, state)
    client = rt.client()
    card = scenario = infra = error = None
    non_rest = direct = 0
    hints = {}
    with contextlib.ExitStack() as stack:                      # S8-S9; the module is deleted on exit
        try:
            module = stack.enter_context(private.loaded(fetched, state["attemptId"]))
        except private.PrivateUnavailable as exc:
            # PrivateLoadError: this machine could not hold the verifier file (temp folder, disk); retry like a
            # failed download, which fetches again.
            state["private"]["attempts"].extend(getattr(exc, "attempts", None) or
                                                [{"via": None, "error": type(exc).__name__}])
            _wait_for_private(rt, state)
            return
        ctx = _verify_context(rt, session, state, task, client, module)
        try:
            card = submodule("verifylib").Scorecard(module.ASSERTIONS)
            module.verify(ctx, card)
            if state["track"] == "dev" and getattr(module, "SCENARIO", None) is not None:
                scenario, infra = _run_scenario(rt, client, state, module.SCENARIO, ctx)
                if infra is None:
                    ctx.scenario = scenario
                    module.verify_runtime(ctx, card)
            card.finish()
            non_rest = len(ctx.non_rest())
            direct = len(ctx.direct_writes())
        except Exception as exc:
            error = "%s: %s" % (type(exc).__name__, str(exc)[:300])
            rt.log.error("attempt %s verifier error %s\n%s" % (state["attemptId"], type(exc).__name__, _frames(exc)))
            if card is not None:
                try:
                    card.finish()
                except Exception:
                    card = None
        hints = getattr(module, "HINTS", None) or {}
    verifier = {"file": fetched.file, "sha256": fetched.sha256, "kind": fetched.kind, "via": fetched.via,
                "official": bool(fetched.official), "deleted": bool(getattr(fetched, "deleted", False))}
    score = _score(card, hints, session["lang"]) if card is not None else None
    invalid = []                                               # S10
    if error:
        invalid.append("verifier_error: %s" % error)
    if infra:
        invalid.append(infra)
    journal_reason = C.journal_reason(state["journal"])
    if journal_reason:
        invalid.append(journal_reason)
    invalid += C.drift_reasons(E.drift(state["env"].get("atIssue"), state["env"].get("atSubmit")))
    contaminated = []
    tampered = _tampered(state)
    if tampered:
        contaminated.append("eval_files_changed: %s" % json.dumps(tampered))
    if non_rest:
        contaminated.append("non_rest_changes: %d" % non_rest)
    wall = store.seconds_between(state.get("issuedAt"), state.get("submittedAt"))
    flags = C.flag_reasons(over_budget=_over_budget(wall, state.get("minutes")),
                           forced_play_mode_exit=state["journal"].get("forcedPlayModeExit"),
                           inflight_jobs=(state.get("freeze") or {}).get("inflightJobs") or 0,
                           isolation=state["host"].get("isolation"), verified_late=state.get("verifiedLate"))
    status, reasons = C.classify(card, invalid=invalid, contaminated=contaminated, flags=flags)
    state["timings"]["verifySeconds"] = round(time.time() - started, 1)
    evidence_scenario = None
    if scenario is not None:
        evidence_scenario = {k: scenario.get(k) for k in ("status", "passed", "error", "interrupted",
                                                           "executionUncertain", "steps", "compilation")}
    _write_result(rt, session, state, status, reasons, score=score, verifier=verifier, scenario=evidence_scenario,
                  non_rest=non_rest, direct_writes=direct)
    state.update({"status": status, "reasons": reasons, "phase": "scored", "scoredAt": store.now_iso()})
    _persist(rt, state)
    rt.log.info("attempt %s scored" % state["attemptId"])


def _wait_for_private(rt, state):
    try:
        hint = submodule("private").fix_hint(state["private"]["attempts"])
    except Exception:
        hint = None
    if hint:
        state["private"]["hint"] = hint            # optional; the panel shows it next to the pending attempt
    else:
        state["private"].pop("hint", None)
    state["phase"] = "pending_verification"
    _persist(rt, state)
    rt.log.warn("attempt %s waits for verification files: %s" % (state["attemptId"],
                                                                 json.dumps(state["private"]["attempts"])))


def _tampered(state):
    hashes = state.get("evalTreeHash") or {}
    if hashes.get("atIssue") and hashes.get("atSubmit") and hashes["atIssue"] != hashes["atSubmit"]:
        return ["Evaluation~"]
    return []


def _over_budget(wall, minutes):
    return wall is not None and isinstance(minutes, (int, float)) and wall > minutes * 60


# ---------------------------------------------------------------- results, S11 teardown, S12 finish


def _write_result(rt, session, state, status, reasons, score=None, verifier=None, scenario=None, non_rest=0,
                  direct_writes=0, teardown=None):
    entries = J.load_entries(_file(rt, state, "journal.jsonl"))
    journal_state = state["journal"]
    http = state["http"]
    numbers = (http.get("received0"), http.get("received1"), http.get("runnerCalls"))
    approx = numbers[1] - numbers[0] - numbers[2] if all(isinstance(n, int) for n in numbers) else None
    env = state["env"]
    rows = (score or {}).get("assertions") or []
    wall = store.seconds_between(state.get("issuedAt"), state.get("submittedAt"))
    answer = state["answer"]
    host = state.get("host") or {}
    result = {
        "schema": 1, "sessionId": state["sessionId"], "attemptId": state["attemptId"], "taskId": state["taskId"],
        "caseVersion": state["caseVersion"], "seed": state["seed"], "packId": session["pack"]["id"],
        "packVersion": session["pack"]["version"], "rerunOf": state.get("rerunOf"), "status": status,
        "reasons": list(reasons), "score": score,
        "requirementsFailed": sorted({req for row in rows if not row.get("pass") for req in row.get("req") or []}),
        "declaredComplete": answer.get("declaredComplete"),
        "answer": {"present": bool(answer.get("present")), "sha256": answer.get("sha256"),
                   "error": answer.get("error"), "schemaErrors": list(answer.get("schemaErrors") or [])},
        "submittedBy": state.get("submittedBy"),
        "timings": {"issuedAt": state.get("issuedAt"), "submittedAt": state.get("submittedAt"), "wall": wall,
                    "fixture": state["timings"].get("buildSeconds"), "verify": state["timings"].get("verifySeconds"),
                    "minutes": state.get("minutes"), "overBudget": _over_budget(wall, state.get("minutes"))},
        "evidence": {
            "journal": {"complete": bool(journal_state.get("complete")), "gaps": journal_state.get("gaps") or [],
                        "segments": journal_state.get("segments") or [],
                        "records": len(J.flatten(entries, journal_state.get("refreshWindow"))), "nonRest": non_rest,
                        "agents": J.agents(entries), "refreshWindow": journal_state.get("refreshWindow"),
                        "forcedPlayModeExit": bool(journal_state.get("forcedPlayModeExit")),
                        "directWrites": direct_writes},
            "telemetry": state["telemetry"].get("summary") or T.summarize([], []),
            "httpApprox": approx,
            "freeze": {"inflightJobs": (state.get("freeze") or {}).get("inflightJobs") or 0},
            "scenario": scenario,
            "drift": E.drift(env.get("atIssue"), env.get("atSubmit")) if env.get("atSubmit") else {},
            "tampered": _tampered(state),
        },
        "usage": _usage_record(host, state.get("issuedAt"), state.get("submittedAt"), None),
        "verifier": verifier,
        "integrity": session.get("integrity"),
        "isolation": host.get("isolation") or "n/a",
        "verifiedLate": bool(state.get("verifiedLate")),
        "teardown": teardown or {"ok": None, "actions": [], "error": None},
    }
    store.atomic_write_json(rt.paths.result_file(state["sessionId"], state["attemptId"]), result)
    return result


def _teardown(rt, session, state, task=None):
    """S11 with the task's teardown hook when the task module loads; never raises."""
    try:
        client = rt.client()
    except ProblemError as exc:
        return {"ok": False, "actions": [], "error": "; ".join(p["code"] for p in exc.problems)}
    if task is None:
        try:
            task = S.load_task(rt, S.open_session_pack(rt, session), state["taskId"])
        except ProblemError as exc:
            rt.log.warn("teardown of %s without the task hook: %s" % (state["attemptId"], exc))
    hook = None
    if task is not None:
        ctx = BuildContext(client, *context_args(session, state, task, rt.project), handles=state.get("handles"),
                           log=rt.log.info)

        def hook():
            return task.teardown(ctx)
    with FocusKeeper(client, rt.registry_pid(), environ=rt.environ):
        outcome = F.teardown(client, F.Scope.from_dict(state["scope"]), state.get("resting"),
                             (state.get("settings") or {}).get("collision"), hook)
    if outcome["ok"]:
        rt.log.info("attempt %s torn down: %s" % (state["attemptId"], "; ".join(outcome["actions"])))
    else:
        rt.log.error("attempt %s teardown failed: %s" % (state["attemptId"], outcome["error"]))
    return outcome


def _finish_scored(rt, session, state, allow_complete=True):
    teardown = _teardown(rt, session, state)
    path = rt.paths.result_file(state["sessionId"], state["attemptId"])
    result = store.read_json(path)
    status, reasons = C.after_teardown(result["status"], result["reasons"], teardown)
    result.update({"status": status, "reasons": reasons, "teardown": teardown})
    store.atomic_write_json(path, result)
    state.update({"status": status, "reasons": reasons, "teardown": teardown, "phase": "torn_down",
                  "tornDownAt": store.now_iso()})
    _persist(rt, state)
    finish_attempt(rt, session, state, allow_complete)


def compute_usage(rt, session, state):
    host = state.get("host") or {"kind": "none", "sessionIds": [], "cwd": None}
    t0 = state.get("issuedAt")
    t1 = state.get("submittedAt") or state.get("cancelledAt") or state.get("tornDownAt") or store.now_iso()
    try:
        usage = submodule("usage")
        if not t0:
            return usage.unavailable_usage(host.get("kind") or "none", "no_messages_in_window",
                                           state.get("createdAt") or t1, t1)
        return usage.read_usage(host, t0, t1, enabled=bool(session.get("usageReading", True)), env=rt.environ)
    except Exception as exc:
        rt.log.warn("usage for %s: %s" % (state["attemptId"], type(exc).__name__))
        return _usage_record(host, t0 or t1, t1, "parse_error", type(exc).__name__)


def _ledger_usage(record):
    try:
        return submodule("usage").ledger_summary(record)
    except Exception:
        tokens = record.get("tokens")
        return {"source": record.get("source"), "processed": tokens.get("processed") if isinstance(tokens, dict)
                else None}


def _merge_usage(previous, record):
    """Contract 11 #23: a recompute without tokens never replaces a record that has them (old Codex rollouts get
    compressed and become unreadable); the failure is appended to the kept record's errors instead."""
    if record.get("tokens") is not None or not isinstance((previous or {}).get("tokens"), dict):
        return record
    kept = dict(previous)
    failure = {"file": "", "line": 0, "error": record.get("unavailableReason") or "unknown"}
    kept["errors"] = (list(previous.get("errors") or []) + list(record.get("errors") or []) + [failure])[
        -MAX_USAGE_ERRORS:]
    return kept


def store_usage(rt, session, state, result, record, ledger=True):
    """Writes the attempt's usage to result.json; a ledger `usage` line only when its summary changed."""
    previous = result.get("usage") or {}
    record = _merge_usage(previous, record)
    result["usage"] = record
    store.atomic_write_json(rt.paths.result_file(state["sessionId"], state["attemptId"]), result)
    if ledger and _ledger_usage(record) != _ledger_usage(previous):
        store.append_jsonl(rt.paths.ledger, {"schema": 1, "kind": "usage", "recordedAt": store.now_iso(),
                                             "attemptId": state["attemptId"], "usage": _ledger_usage(record)})
    return record


def _usage_changed(before, after):
    keys = ("source", "tokens", "unavailableReason", "sessionIds", "messages")
    return any((before or {}).get(k) != (after or {}).get(k) for k in keys)


def recompute_usage(rt, session):
    """At session end, re-read every attempt's usage (host logs may lag); user-entered values and earlier records
    with tokens (when the recompute has none) are kept."""
    for task_id in session["order"]:
        for aid in session["tasks"][task_id]["attempts"]:
            path = rt.paths.result_file(session["sessionId"], aid)
            try:
                result = store.read_json(path, missing_ok=True)
                if not result or (result.get("usage") or {}).get("source") == "user":
                    continue
                state = S.load_state(rt.paths, session["sessionId"], aid)
                record = compute_usage(rt, session, state)
                if _usage_changed(result.get("usage"), record):
                    store_usage(rt, session, state, result, record)
            except (ProblemError, store.StoreError) as exc:
                rt.log.warn("usage recompute for %s: %s" % (aid, exc))


def _ledger_has(path, aid):
    return any(line.get("kind") == "attempt" and line.get("attemptId") == aid for line in store.read_jsonl(path))


def _ledger_row(rt, session, state, result):
    score = result.get("score") or {}
    declared = session.get("declared") or {}
    verifier = result.get("verifier")
    return {"schema": 1, "kind": "attempt", "recordedAt": store.now_iso(), "sessionId": state["sessionId"],
            "attemptId": state["attemptId"], "taskId": state["taskId"], "caseVersion": state["caseVersion"],
            "packId": session["pack"]["id"], "packVersion": session["pack"]["version"], "track": state["track"],
            "tier": state["tier"], "seed": state["seed"], "mode": session["mode"], "lang": session["lang"],
            "launcher": session["launcher"], "status": result["status"], "reasons": result["reasons"],
            "score": score.get("score") if result.get("score") else None,
            "completion": score.get("completion") if result.get("score") else None,
            "strict": score.get("strict") if result.get("score") else None, "rerunOf": state.get("rerunOf"),
            "declared": {k: (declared.get(k) or {}).get("value") for k in ("host", "model", "effort")},
            "usage": _ledger_usage(result.get("usage") or {}), "integrity": session.get("integrity"),
            "official": S.official(session) and (verifier is None or bool(verifier.get("official"))),
            "result": rt.paths.relative(rt.paths.result_file(state["sessionId"], state["attemptId"]))}


def finish_attempt(rt, session, state, allow_complete=True):
    """S12, idempotent: usage, one ledger row, the task state, the current pointer, session completion."""
    path = rt.paths.result_file(state["sessionId"], state["attemptId"])
    result = store.read_json(path, missing_ok=True)
    if result is None:
        result = _write_result(rt, session, state, state.get("status") or "invalid",
                               state.get("reasons") or ["build_interrupted"], teardown=state.get("teardown"))
    if (result.get("usage") or {}).get("source") != "user":
        store_usage(rt, session, state, result, compute_usage(rt, session, state), ledger=False)
    if not _ledger_has(rt.paths.ledger, state["attemptId"]):
        store.append_jsonl(rt.paths.ledger, _ledger_row(rt, session, state, result))
    entry = session["tasks"][state["taskId"]]
    if entry["attempts"] and entry["attempts"][-1] == state["attemptId"]:
        entry["state"] = _task_state(result["status"], int(entry.get("rerunsUsed") or 0))
    S.save_session(rt.paths, session)
    if S.active_attempt(rt.paths, session) == state["attemptId"]:
        S.write_current(rt.paths, session, None)
    if allow_complete and session["status"] == "active" and not S.open_tasks(session):
        S.finalize_session(rt, session, "completed")


# ---------------------------------------------------------------- abort


def _freeze_for_cancel(rt, state):
    journal_state = state["journal"]
    cursor0 = journal_state.get("cursor0")
    if cursor0 is None:
        return
    try:
        cursor_end = J.current_cursor(rt.client())
    except Exception:
        cursor_end = J.max_seq_on_disk(rt.paths.journal_dir)
    journal_state["cursorEnd"] = cursor_end
    _freeze_journal(rt, state, cursor0, cursor_end)
    try:
        _freeze_telemetry(rt, state, state.get("submittedAt") or state["cancelledAt"], settle=False)
    except (ValueError, store.StoreError):
        pass
    _persist(rt, state)


def cancel_active(rt, session, reason):
    """End the active attempt for `abort`: (attemptId or None, teardown or None); never completes the session."""
    aid = S.active_attempt(rt.paths, session)
    if not aid:
        return None, None
    state = S.load_state(rt.paths, session["sessionId"], aid)
    phase = state["phase"]
    if phase == "building":
        return aid, _fail_build(rt, session, state, "build_interrupted", allow_complete=False)
    if phase == "scored":
        _finish_scored(rt, session, state, allow_complete=False)
        return aid, state.get("teardown")
    if phase == "torn_down":
        finish_attempt(rt, session, state, allow_complete=False)
        return aid, state.get("teardown")
    state["cancelledAt"] = store.now_iso()
    if phase in ("ready", "submitted"):
        _freeze_for_cancel(rt, state)
    teardown = _teardown(rt, session, state)
    status, reasons = C.classify(cancelled=reason)
    _write_result(rt, session, state, status, reasons, teardown=teardown)
    state.update({"status": status, "reasons": reasons, "teardown": teardown, "phase": "torn_down",
                  "tornDownAt": store.now_iso()})
    _persist(rt, state)
    finish_attempt(rt, session, state, allow_complete=False)
    rt.log.info("attempt %s cancelled: %s" % (aid, reason))
    return aid, teardown

# Producer:Betsy
