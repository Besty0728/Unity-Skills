"""Session commands (contract 2.4): check, start, status, abort, report, usage, cleanup; current.json/session.json.

Attempt-level work (next/submit, teardown, finishing an attempt) lives in attempt.py.
"""
import json
import os
import shutil
import time

from . import RUNNER_DIR, submodule
from . import attempt as A
from . import environment as E
from . import fixture as F
from . import prompts
from . import report as R
from . import store
from .focus import FocusKeeper
from .paths import EVAL_ASSET_ROOT, packages_lock, resolve_package_root
from .problems import Outcome, ProblemError, notice, problem, store_problem
from .rest import RestError, StillBusy, as_problems

TRACKS = ("ops", "dev")
TIERS = ("basic", "standard", "challenge")
MODES = ("suite", "strict")
FINAL_TASK_STATES = ("done", "invalid", "cancelled")
OPEN_TASK_STATES = ("pending", "issued")
DEFAULT_REQUIRES = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}
CHECK_WAIT_S = 15.0
BUSY_WAIT_S = 90.0             # start and next N1: how long a compiling, importing or reloading editor is waited for
DECLARED_KEYS = ("host", "hostVersion", "model", "effort")
DECLARED_SOURCES = ("detected", "user", "batch", "unknown")
DECLARED_HOSTS = ("claude-code", "codex", "cursor", "other")


# ---------------------------------------------------------------- persistence


def read_current(paths):
    return store.read_json(paths.current, missing_ok=True)


def write_current(paths, session, active_aid):
    store.atomic_write_json(paths.current, {"schema": 1, "sessionId": session["sessionId"],
                                            "status": session["status"], "activeAttemptId": active_aid,
                                            "updatedAt": store.now_iso()})


def load_session(paths, sid):
    if not sid or not os.path.isfile(paths.session_file(sid)):
        raise ProblemError(problem("SESSION_NOT_FOUND", detail=sid))
    return store.read_json(paths.session_file(sid))


def save_session(paths, session):
    store.atomic_write_json(paths.session_file(session["sessionId"]), session)


def load_state(paths, sid, aid):
    path = paths.state_file(sid, aid)
    if not aid or not os.path.isfile(path):
        raise ProblemError(problem("ATTEMPT_NOT_FOUND", detail=aid))
    return store.read_json(path)


def save_state(paths, state):
    store.atomic_write_json(paths.state_file(state["sessionId"], state["attemptId"]), state)


def active_attempt(paths, session):
    current = read_current(paths)
    if current and current.get("sessionId") == session["sessionId"] and current.get("status") == "active":
        return current.get("activeAttemptId")
    return None


def resolve_session(rt):
    sid = rt.args.session
    if not sid:
        current = read_current(rt.paths)
        sid = current.get("sessionId") if current else None
        if not sid:
            raise ProblemError(problem("NO_ACTIVE_SESSION"))
    session = load_session(rt.paths, sid)
    rt.log.primary = rt.paths.session_log(sid)
    return session


def open_tasks(session):
    return [tid for tid in session["order"] if session["tasks"][tid]["state"] in OPEN_TASK_STATES]


def official(session):
    return bool(session.get("official", (session.get("pack") or {}).get("official")))


# ---------------------------------------------------------------- errors


def rest_problems(exc, rt):
    if isinstance(exc, ProblemError):
        return exc.problems
    rt.log.error("REST %s: %s" % (type(exc).__name__, exc))
    return as_problems(exc, rt.log.path)


# ---------------------------------------------------------------- pack


def open_pack(rt, pack_dir=None, private_zip=None, private_dir=None):
    try:
        return submodule("pack").open_pack(pack_dir=pack_dir, private_zip=private_zip, private_dir=private_dir)
    except ProblemError:
        raise
    except Exception as exc:
        rt.log.error("pack: %s: %s" % (type(exc).__name__, exc))
        raise ProblemError(problem("PACK_INVALID", detail="%s: %s" % (type(exc).__name__, str(exc)[:200])))


def open_session_pack(rt, session):
    info = session["pack"]
    private = info.get("private") or {}
    kind, path = private.get("kind"), private.get("path")
    pack = open_pack(rt, pack_dir=path if info.get("source") == "pack-dir" else None,
                     private_zip=path if kind == "zip" else None,
                     private_dir=path if kind == "private-dir" else None)
    if pack.manifest_sha256 != info.get("manifestSha256"):
        raise ProblemError(problem("PACK_INVALID", detail="the task pack changed since the session started"))
    return pack


def load_task(rt, pack, task_id):
    try:
        return pack.load_task(task_id)
    except ProblemError:
        raise
    except Exception as exc:
        rt.log.error("load_task %s: %s: %s" % (task_id, type(exc).__name__, exc))
        raise ProblemError(problem("PACK_INVALID", detail="task %s: %s" % (task_id, str(exc)[:200])))


def pack_summary(pack):
    return {"id": pack.id, "version": pack.version, "source": pack.source, "official": bool(pack.official),
            "modified": list(pack.modified or [])}


# ---------------------------------------------------------------- editor checks (check, start, next N1)


def allowed_pipelines(requires_list):
    """Pipelines every task accepts, in manifest order; None when none restricts it (requires.pipeline null).
    requires.pipeline is one pipelineType or a list of them (contract 5.1)."""
    allowed = None
    for requires in requires_list:
        value = requires.get("pipeline")
        if value is None:
            continue
        values = [value] if isinstance(value, str) else [v for v in value if isinstance(v, str)]
        allowed = values if allowed is None else [v for v in allowed if v in values]
    return allowed


def settle(rt, client, health, wait_s):
    """/health once the editor has finished compiling and importing (start, next N1); after wait_s the last busy
    answer, which editor_checks then reports as EDITOR_BUSY."""
    if wait_s <= 0 or not E.busy(health):
        return health
    rt.log.info("editor is compiling or importing; waiting up to %.0f s for it to settle" % wait_s)
    started = time.time()
    try:
        with FocusKeeper(client, rt.registry_pid(), environ=rt.environ):
            health = client.wait_idle(timeout_s=wait_s)
    except StillBusy as exc:
        rt.log.info("editor still busy after %.0f s" % wait_s)
        return exc.payload or health
    rt.log.info("editor settled after %.1f s" % (time.time() - started))
    return health


def editor_checks(rt, client, health, requires_list):
    """(problems, notices, info) for version, compile/import, Play Mode, mode, surface and pipeline."""
    problems, notices = [], []
    requires_list = requires_list or [DEFAULT_REQUIRES]
    plugin = health.get("version")
    if plugin != rt.runner_version:
        problems.append(problem("VERSION_MISMATCH", [plugin, rt.runner_version]))
    comp = health.get("compilation") or {}
    state = client.read("editor_get_state")
    editor = {"isPlaying": bool(state.get("isPlaying")),
              "isCompiling": bool(comp.get("isCompiling") or state.get("isCompiling")),
              "isUpdating": bool(comp.get("isUpdating"))}
    if E.busy(health) or editor["isCompiling"]:
        problems.append(problem("EDITOR_BUSY"))
    if editor["isPlaying"]:
        problems.append(problem("PLAY_MODE_ACTIVE"))
    mode = health.get("currentMode")
    wanted_modes = sorted({r.get("mode") or "bypass" for r in requires_list})
    if any(m != mode for m in wanted_modes):
        problems.append(problem("MODE_NOT_BYPASS", detail="current mode %s" % mode))
    if health.get("surfaceProfile") != "full":
        problems.append(problem("SURFACE_NOT_FULL", detail="current surface %s" % health.get("surfaceProfile")))
    dry_run, token_level = health.get("dryRunPolicy"), health.get("tokenLevel")
    if dry_run != E.REFERENCE_DRY_RUN or token_level != E.REFERENCE_TOKEN_LEVEL:
        notices.append(notice("SETTINGS_NONSTANDARD", [dry_run, token_level]))
    pipeline = client.read("project_get_render_pipeline")
    kind = pipeline.get("pipelineType")
    allowed = allowed_pipelines(requires_list)
    if allowed is not None and kind not in allowed:
        listed = ", ".join(allowed) or "none"
        problems.append(problem("PIPELINE_UNSUPPORTED", [listed], detail="project uses %s, the selected tasks "
                                                                         "accept %s" % (kind, listed)))
    info = {"editor": editor,
            "settings": {"mode": mode, "surface": health.get("surfaceProfile"), "dryRunPolicy": dry_run,
                         "tokenLevel": token_level, "telemetryEnabled": None},
            "pipeline": {"type": kind, "name": pipeline.get("pipelineName"),
                         "defaultShader": pipeline.get("defaultShader")},
            "pipelineRaw": pipeline}
    return problems, notices, info


def scene_checks(scenes, active_root=None):
    """SCENE_DIRTY / SCENE_UNTITLED; the active attempt's own run scene is expected to be dirty."""
    if active_root:
        scenes = [dict(s, isDirty=False) if F.under(s.get("path"), active_root) else s for s in scenes]
    summary = F.scene_summary(scenes)
    problems = []
    if summary["dirty"]:
        problems.append(problem("SCENE_DIRTY", [", ".join(summary["dirty"])]))
    if summary["activeUntitled"]:
        problems.append(problem("SCENE_UNTITLED"))
    return problems, summary


def residue_problems(rt, active_run_id):
    owned, _foreign = F.residue(rt.paths.eval_assets, active_run_id)
    if owned:
        return owned, [problem("RESIDUE_FOUND", [", ".join(item["assetRoot"] for item in owned)])]
    return owned, []


def active_run_id(rt):
    try:
        current = read_current(rt.paths)
        if not current or current.get("status") != "active" or not current.get("activeAttemptId"):
            return None
        state = load_state(rt.paths, current["sessionId"], current["activeAttemptId"])
    except (ProblemError, store.StoreError):
        return None
    return (state.get("scope") or {}).get("runId")


# ---------------------------------------------------------------- check


def _options(args):
    return {"track": args.track or "ops", "tier": args.tier or "basic", "tasks": args.tasks,
            "packDir": args.pack_dir, "privateZip": args.private_zip, "privateDir": args.private_dir}


def run_checks(rt, opts, busy_wait_s=0.0):
    """Every check item (contract 2.4 check); returns (data, problems, notices, extras) and never stops early.
    busy_wait_s (start) is how long a busy editor is waited for before EDITOR_BUSY; check reports it at once."""
    problems, notices, extras = [], [], {}
    data = {"project": None, "package": None, "instance": None, "editor": None, "settings": None, "pipeline": None,
            "scenes": None, "session": None, "residue": [], "pack": None,
            "selection": {"track": opts["track"], "tier": opts["tier"], "tasks": []}, "privatePack": None,
            "skillDocs": [], "python": E.python_info(), "ready": False, "notices": notices}
    marker = E.marker(rt.paths.marker)
    data["project"] = {"path": rt.project, "dedicated": marker["dedicated"], "markerSha256": marker["markerSha256"],
                       "environment": marker["environment"]}
    extras["marker"] = marker
    try:
        package = resolve_package_root(rt.project)
        notices.extend(package["notices"])
        data["package"] = {"root": package["root"], "source": package["source"], "runnerVersion": rt.runner_version}
    except ProblemError as exc:
        problems.extend(exc.problems)
        data["package"] = {"root": None, "source": None, "runnerVersion": rt.runner_version}

    try:
        current = read_current(rt.paths)
    except store.StoreError as exc:
        problems.append(store_problem(exc))
        current = None
    active = bool(current and current.get("status") == "active")
    data["session"] = {"active": active, "sessionId": current.get("sessionId") if current else None,
                       "status": current.get("status") if current else None}
    if active:
        notices.append(notice("SESSION_ACTIVE", [current.get("sessionId")]))

    pack, metas = None, []
    try:
        pack = open_pack(rt, opts["packDir"], opts["privateZip"], opts["privateDir"])
        data["pack"] = pack_summary(pack)
        if pack.modified:
            notices.append(notice("PACK_MODIFIED", [", ".join(pack.modified)]))
        tasks = pack.select(opts["track"], opts["tier"], opts["tasks"])
        data["selection"]["tasks"] = tasks
        metas = [pack.meta(t) for t in tasks]
    except ProblemError as exc:
        problems.extend(exc.problems)
    extras.update({"pack": pack, "metas": metas})
    requires_list = [m["requires"] for m in metas]

    plugin_version = None
    run_id = active_run_id(rt)
    try:
        instance = rt.instance(wait_s=CHECK_WAIT_S, busy_wait_s=busy_wait_s)
        client = rt.client()
        health = settle(rt, client, client.health(), busy_wait_s)
        plugin_version = health.get("version")
        data["instance"] = {"id": instance.instance_id, "port": instance.port, "pid": instance.pid,
                            "pluginVersion": plugin_version}
        found, more, info = editor_checks(rt, client, health, requires_list)
        problems.extend(found)
        notices.extend(more)
        enabled = E.telemetry_enabled(client)
        info["settings"]["telemetryEnabled"] = enabled
        if enabled is False:
            notices.append(notice("TELEMETRY_DISABLED"))
        data.update({"editor": info["editor"], "settings": info["settings"], "pipeline": info["pipeline"]})
        scenes = F.loaded_scenes(client)
        found, _summary = scene_checks(scenes, EVAL_ASSET_ROOT + "/" + run_id if run_id else None)
        problems.extend(found)
        data["scenes"] = F.scene_summary(scenes)
        extras.update({"instance": instance, "health": health, "pipeline": info["pipelineRaw"],
                       "telemetryEnabled": enabled})
    except (ProblemError, RestError) as exc:
        problems.extend(rest_problems(exc, rt))

    if pack is not None:
        out_of_range = pack.plugin_range_problem(plugin_version or rt.runner_version)
        if out_of_range:
            problems.append(out_of_range)

    if any(r.get("dedicated") for r in requires_list) and not marker["dedicated"]:
        problems.append(problem("PROJECT_NOT_DEDICATED"))
    elif not marker["dedicated"]:
        notices.append(notice("PROJECT_USER"))
    installed = packages_lock(rt.project)
    for meta in metas:
        for name in meta["requires"].get("packages") or []:
            if name not in installed:
                problems.append(problem("PACKAGE_MISSING", [name, meta["id"]]))

    owned, found = residue_problems(rt, run_id)
    data["residue"] = owned
    problems.extend(found)

    if pack is not None and data["selection"]["tasks"]:
        probed = probe_private(rt, pack, data["selection"]["tasks"][0], plugin_version or rt.runner_version)
        data["privatePack"] = probed
        if not probed.get("reachable"):
            problems.append(private_problem("PRIVATE_PACK_UNREACHABLE", probed.get("attempts")))

    docs = E.skill_docs(rt.project, rt.environ)
    data["skillDocs"] = docs
    reference = plugin_version or rt.runner_version
    for doc in docs:
        if doc.get("version") != reference:
            notices.append(notice("SKILL_DOCS_OUTDATED", [doc["path"], doc.get("version") or "unknown"]))
    if not docs:
        notices.append(notice("SKILL_DOCS_MISSING"))
    extras["skillDocs"] = docs
    data["ready"] = not problems
    return data, problems, notices, extras


def private_problem(code, attempts, args=()):
    """PRIVATE_PACK_TLS when every download attempt failed TLS verification, else `code` (PRIVATE_PACK_UNREACHABLE or
    VERIFICATION_PENDING) with private.fix_hint(attempts) appended to its fix."""
    attempts = [a for a in attempts or [] if isinstance(a, dict)]
    try:
        private = submodule("private")
    except Exception:
        return problem(code, args)
    if attempts and all(a.get("error") == "tls_error" for a in attempts) and getattr(private, "TLS_HINT", None):
        fix = private.TLS_HINT + (" Then run: %s" % args[0] if args else "")
        return problem("PRIVATE_PACK_TLS", args, fix=fix)
    fix_hint = getattr(private, "fix_hint", None)
    return problem(code, args, hint=fix_hint(attempts) if fix_hint else None)


def probe_private(rt, pack, task_id, plugin_version):
    try:
        return submodule("private").probe(pack, task_id, plugin_version=plugin_version, user_agent=rt.user_agent,
                                          environ=rt.environ)
    except Exception as exc:
        rt.log.error("private probe: %s: %s" % (type(exc).__name__, exc))
        return {"kind": getattr(pack, "private_kind", None), "probedTask": task_id, "reachable": False, "via": None}


def _start_args(rt, opts):
    args = ["--project", rt.project, "--track", opts["track"], "--tier", opts["tier"]]
    for flag, key in (("--tasks", "tasks"), ("--pack-dir", "packDir"), ("--private-zip", "privateZip"),
                      ("--private-dir", "privateDir")):
        if opts.get(key):
            args += [flag, opts[key]]
    return args


def cmd_check(rt):
    opts = _options(rt.args)
    data, problems, _notices, _extras = run_checks(rt, opts)
    return Outcome(data, problems, rt.step("start", _start_args(rt, opts)) if not problems else None)


# ---------------------------------------------------------------- start


def _declared(rt, path):
    given = {}
    if path:
        try:
            with open(path, "r", encoding="utf-8-sig") as handle:
                loaded = json.load(handle)
        except (OSError, ValueError) as exc:
            raise ProblemError(problem("BAD_ARGUMENTS", ["--declared %s: %s" % (path, type(exc).__name__)]))
        if not isinstance(loaded, dict):
            raise ProblemError(problem("BAD_ARGUMENTS", ["--declared must hold a JSON object"]))
        for key in DECLARED_KEYS:
            item = loaded.get(key)
            if item is None:
                continue
            if not isinstance(item, dict) or item.get("source") not in DECLARED_SOURCES or \
                    not (item.get("value") is None or isinstance(item.get("value"), str)):
                raise ProblemError(problem("BAD_ARGUMENTS", ["--declared %s needs {value, source}" % key]))
            if key == "host" and item.get("value") is not None and item["value"] not in DECLARED_HOSTS:
                raise ProblemError(problem("BAD_ARGUMENTS", ["--declared host must be one of %s" %
                                                             "|".join(DECLARED_HOSTS)]))
            given[key] = {"value": item.get("value"), "source": item["source"]}
    try:
        detected = submodule("usage").detect_declared(env=rt.environ)
    except Exception as exc:
        rt.log.warn("declared detection: %s" % type(exc).__name__)
        detected = {}
    out = {}
    for key in DECLARED_KEYS:
        item = given.get(key) or detected.get(key)
        out[key] = item if isinstance(item, dict) else {"value": None, "source": "unknown"}
    return out


def _session_matches(session, args):
    selection = session.get("selection") or {}
    if args.track and args.track != selection.get("track"):
        return False
    if args.tier and args.tier != selection.get("tier"):
        return False
    if args.tasks and [t.strip() for t in args.tasks.split(",") if t.strip()] != selection.get("tasks"):
        return False
    if args.mode and args.mode != session.get("mode"):
        return False
    return not (args.lang and prompts.prompt_lang(args.lang) != session.get("lang"))


def _start_data(rt, session, created, notices):
    sid = session["sessionId"]
    prompt_file = os.path.join(rt.paths.io_session(sid), "session-prompt.md")
    text = None
    if rt.args.inline:
        try:
            with open(prompt_file, "r", encoding="utf-8-sig") as handle:
                text = handle.read()
        except OSError:
            text = None
    selection = session["selection"]
    data = {"sessionId": sid, "created": created, "mode": session["mode"], "lang": session["lang"],
            "n": len(session["order"]),
            "selection": {"track": selection["track"], "tier": selection["tier"], "tasks": list(selection["tasks"])},
            "official": official(session), "sessionPromptFile": prompt_file, "sessionPrompt": text,
            "notices": notices}
    return Outcome(data, [], rt.step("next", ["--project", rt.project, "--session", sid]))


def write_session_prompts(rt, session):
    sid = session["sessionId"]
    step = rt.step("next", ["--project", rt.project, "--session", sid])
    selection = session["selection"]
    text = prompts.session_prompt(session["lang"], session["mode"], {
        "session": sid, "n": len(session["order"]), "track": selection["track"], "tier": selection["tier"],
        "next_cmd": step["line"]})
    store.atomic_write_text(os.path.join(rt.paths.session_dir(sid), "session-prompt.%s.md" % session["lang"]), text)
    store.atomic_write_text(os.path.join(rt.paths.io_session(sid), "session-prompt.md"), text, bom=True)


def cmd_start(rt):
    args = rt.args
    with rt.lock():
        current = read_current(rt.paths)
        if current and current.get("status") == "active":
            session = load_session(rt.paths, current.get("sessionId"))
            rt.log.primary = rt.paths.session_log(session["sessionId"])
            if _session_matches(session, args):
                return _start_data(rt, session, False, [])
            raise ProblemError(problem("SESSION_ACTIVE", [session["sessionId"]]))
        opts = _options(args)
        data, problems, notices, extras = run_checks(rt, opts, BUSY_WAIT_S)
        if problems:
            return Outcome({"created": False, "check": data, "notices": notices}, problems)
        session = _new_session(rt, args, opts, data, extras)
        rt.log.primary = rt.paths.session_log(session["sessionId"])
        save_session(rt.paths, session)
        write_session_prompts(rt, session)
        write_current(rt.paths, session, None)
        rt.log.info("session %s started: %s/%s %s %s, %d tasks" % (
            session["sessionId"], opts["track"], opts["tier"], session["mode"], session["lang"],
            len(session["order"])))
        return _start_data(rt, session, True, notices)


def _new_session(rt, args, opts, data, extras):
    """session.json (contract 4.4); official needs an official pack, the manifest seeds and the manifest URLs."""
    sid = store.stamp_id("s")
    pack, health, pipeline = extras["pack"], extras["health"], extras["pipeline"]
    instance, marker = extras["instance"], extras["marker"]
    tasks = data["selection"]["tasks"]
    is_official = bool(pack.official) and args.seed is None and not rt.environ.get("USK_EVAL_PRIVATE_URLS")
    environment = {"unity": health.get("unityVersion"), "os": E.os_name(), "python": E.python_info()["version"],
                   "pipeline": pipeline.get("pipelineType"), "pipelineName": pipeline.get("pipelineName"),
                   "defaultShader": pipeline.get("defaultShader"), "colorProperty": pipeline.get("colorProperty"),
                   "mode": health.get("currentMode"), "tokenLevel": health.get("tokenLevel"),
                   "surface": health.get("surfaceProfile"), "dryRunPolicy": health.get("dryRunPolicy"),
                   "summaryAutoTruncate": health.get("summaryAutoTruncate"),
                   "summaryPageSize": health.get("summaryPageSize"),
                   "panelApprovalRequired": health.get("panelApprovalRequired"),
                   "telemetryEnabled": extras.get("telemetryEnabled"), "skillDocs": extras.get("skillDocs") or []}
    return {
        "schema": 1, "sessionId": sid, "status": "active", "createdAt": store.now_iso(), "endedAt": None,
        "abortReason": None, "runnerVersion": rt.runner_version, "pluginVersion": health.get("version"),
        "pack": {"id": pack.id, "version": pack.version, "manifestSha256": pack.manifest_sha256,
                 "source": pack.source, "official": bool(pack.official), "modified": list(pack.modified or []),
                 "private": {"kind": pack.private_kind, "path": pack.private_path}},
        "project": {"path": rt.project, "dedicated": marker["dedicated"], "markerSha256": marker["markerSha256"],
                    "environment": marker["environment"]},
        "instance": instance.as_dict(),
        "selection": {"track": opts["track"], "tier": opts["tier"], "tasks": list(tasks), "seedOverride": args.seed},
        "order": list(tasks),
        "mode": args.mode or "suite",
        "lang": prompts.prompt_lang(args.lang or "en"),
        "launcher": rt.launcher,
        "integrity": "batch-isolated" if rt.launcher == "batch" else "local-unverified",
        "declared": _declared(rt, args.declared),
        "usageReading": not args.no_usage,
        "environment": environment,
        "tasks": {t: {"state": "pending", "attempts": [], "rerunsUsed": 0} for t in tasks},
        "hostSessions": {},
        "ioDir": rt.paths.io_session(sid),
        "report": None,
        "official": is_official,
    }


# ---------------------------------------------------------------- status


def cmd_status(rt):
    session = resolve_session(rt)
    sid = session["sessionId"]
    tasks = session["tasks"]
    completed = sum(1 for tid in session["order"] if tasks[tid]["state"] in FINAL_TASK_STATES)
    active = None
    aid = active_attempt(rt.paths, session)
    if aid:
        state = load_state(rt.paths, sid, aid)
        issued = state.get("issuedAt")
        elapsed = round(max(0.0, store.epoch(store.now_iso()) - store.epoch(issued)), 1) if issued else 0.0
        active = {"taskId": state["taskId"], "attemptId": aid, "phase": state["phase"], "k": state["k"],
                  "issuedAt": issued, "elapsedSeconds": elapsed, "minutes": state.get("minutes")}
    report = None
    if session.get("report"):
        report = {"json": os.path.join(rt.paths.session_dir(sid), session["report"]["json"]),
                  "md": os.path.join(rt.paths.session_dir(sid), session["report"]["md"])}
    data = {"sessionId": sid, "status": session["status"], "mode": session["mode"], "lang": session["lang"],
            "selection": session["selection"], "n": len(session["order"]), "completed": completed,
            "remaining": len(session["order"]) - completed, "active": active,
            "tasks": [{"taskId": tid, "state": tasks[tid]["state"], "attempts": list(tasks[tid]["attempts"])}
                      for tid in session["order"]],
            "createdAt": session["createdAt"], "endedAt": session.get("endedAt"), "report": report}
    return Outcome(data)


# ---------------------------------------------------------------- finishing a session


def finalize_session(rt, session, status, reason=None):
    """Terminal session: recompute usage, write the report, drop the IO directory and an empty eval root."""
    session["status"] = status
    session["endedAt"] = store.now_iso()
    if status == "aborted":
        session["abortReason"] = reason
    save_session(rt.paths, session)
    write_current(rt.paths, session, None)
    A.recompute_usage(rt, session)
    R.generate(rt, session)
    save_session(rt.paths, session)
    shutil.rmtree(rt.paths.io_session(session["sessionId"]), ignore_errors=True)
    try:
        os.rmdir(rt.paths.io_root)
    except OSError:
        pass
    remove_empty_eval_root(rt)
    rt.log.info("session %s %s" % (session["sessionId"], status))


def remove_empty_eval_root(rt):
    if not F.eval_root_empty(rt.paths.eval_assets):
        return
    try:
        rt.client().write("asset_delete", assetPath=EVAL_ASSET_ROOT)
    except Exception as exc:
        rt.log.warn("could not remove empty %s: %s" % (EVAL_ASSET_ROOT, exc))


# ---------------------------------------------------------------- abort


def _abort_data(rt, session):
    cancelled, teardown = None, {"ok": True, "error": None}
    for tid in session["order"]:
        entry = session["tasks"][tid]
        if entry["state"] == "cancelled" and entry["attempts"]:
            cancelled = entry["attempts"][-1]
    if cancelled:
        try:
            state = load_state(rt.paths, session["sessionId"], cancelled)
            done = state.get("teardown") or {}
            teardown = {"ok": bool(done.get("ok")), "error": done.get("error")}
        except ProblemError:
            pass
    return {"sessionId": session["sessionId"], "status": "aborted", "cancelledAttempt": cancelled,
            "teardown": teardown}


def cmd_abort(rt):
    with rt.lock():
        session = resolve_session(rt)
        if session["status"] == "aborted":
            return Outcome(_abort_data(rt, session))
        if session["status"] != "active":
            raise ProblemError(problem("NO_ACTIVE_SESSION", detail="session %s is %s" % (
                session["sessionId"], session["status"])))
        reason = rt.args.reason or "user"
        ended, teardown = A.cancel_active(rt, session, reason)
        finalize_session(rt, session, "aborted", reason)
        data = {"sessionId": session["sessionId"], "status": "aborted", "cancelledAttempt": ended,
                "teardown": {"ok": bool(teardown.get("ok")), "error": teardown.get("error")} if teardown
                else {"ok": True, "error": None}}
        problems = [problem("TEARDOWN_FAILED", detail=teardown.get("error"))] if teardown and \
            not teardown.get("ok") else []
        return Outcome(data, problems)


# ---------------------------------------------------------------- report


def _report_outcome(rt, session):
    sid = session["sessionId"]
    paths = R.report_paths(rt.paths, sid)
    report = store.read_json(paths["json"])
    data = {"sessionId": sid, "status": session["status"], "reportJson": paths["json"], "reportMd": paths["md"],
            "summary": report.get("summary")}
    if getattr(rt.args, "format", None) == "md":
        try:
            with open(paths["md"], "r", encoding="utf-8") as handle:
                data["markdown"] = handle.read()
        except OSError:
            data["markdown"] = None
    return Outcome(data)


def cmd_report(rt):
    session = resolve_session(rt)
    if session["status"] == "active":
        raise ProblemError(problem("SESSION_NOT_FINISHED"))
    paths = R.report_paths(rt.paths, session["sessionId"])
    if session.get("report") and os.path.isfile(paths["json"]) and os.path.isfile(paths["md"]):
        return _report_outcome(rt, session)
    with rt.lock():
        session = load_session(rt.paths, session["sessionId"])
        R.generate(rt, session)
        save_session(rt.paths, session)
    return _report_outcome(rt, session)


# ---------------------------------------------------------------- usage


def _find_attempt(rt, aid):
    sessions = []
    if rt.args.session:
        sessions = [rt.args.session]
    else:
        current = read_current(rt.paths)
        if current and current.get("sessionId"):
            sessions.append(current["sessionId"])
        try:
            sessions += sorted(n for n in os.listdir(rt.paths.sessions) if n not in sessions)
        except OSError:
            pass
    for sid in sessions:
        if aid and os.path.isfile(rt.paths.state_file(sid, aid)):
            return load_session(rt.paths, sid), load_state(rt.paths, sid, aid)
    raise ProblemError(problem("ATTEMPT_NOT_FOUND", detail=aid))


def cmd_usage(rt):
    args = rt.args
    with rt.lock():
        session, state = _find_attempt(rt, args.attempt)
        rt.log.primary = rt.paths.session_log(session["sessionId"])
        result_path = rt.paths.result_file(session["sessionId"], state["attemptId"])
        if not os.path.isfile(result_path):
            raise ProblemError(problem("BAD_ARGUMENTS", ["attempt %s has no result yet" % state["attemptId"]]))
        result = store.read_json(result_path)
        usage = submodule("usage")
        if args.manual:
            try:
                values, model = usage.parse_manual_args(args.manual)
                end = state.get("submittedAt") or state.get("tornDownAt") or state.get("createdAt")
                record = usage.manual_usage(values, state.get("issuedAt") or end, end,
                                            host=(state.get("host") or {}).get("kind") or "none", model=model)
            except ValueError as exc:
                raise ProblemError(problem("BAD_ARGUMENTS", ["--manual: %s" % exc]))
        else:
            record = A.compute_usage(rt, session, state)
        record = A.store_usage(rt, session, state, result, record)
        if session["status"] != "active":
            R.generate(rt, session)        # a finished session's report must show the new numbers
            save_session(rt.paths, session)
        notices = []
        if record.get("tokens") is None:
            notices.append(notice("USAGE_UNAVAILABLE", [record.get("unavailableReason") or "unknown"]))
        return Outcome({"attemptId": state["attemptId"], "usage": record, "notices": notices})


# ---------------------------------------------------------------- cleanup


def _resting_for(rt, run_id):
    try:
        sids = os.listdir(rt.paths.sessions)
    except OSError:
        return None
    for sid in sids:
        attempts = os.path.join(rt.paths.sessions, sid, "attempts")
        try:
            aids = os.listdir(attempts)
        except OSError:
            continue
        for aid in aids:
            try:
                state = store.read_json(os.path.join(attempts, aid, "state.json"), missing_ok=True)
            except store.StoreError:
                continue
            if state and (state.get("scope") or {}).get("runId") == run_id and (state.get("resting") or {}).get(
                    "scenes"):
                return state["resting"]
    return None


def cmd_cleanup(rt):
    args = rt.args
    if not args.stale:
        raise ProblemError(problem("BAD_ARGUMENTS", ["cleanup needs --stale"]))
    with rt.lock():
        run_id = active_run_id(rt)
        owned, foreign = F.residue(rt.paths.eval_assets, run_id)
        skipped = [{"runId": name, "reason": "ownership"} for name in foreign]
        if run_id and os.path.isdir(rt.paths.asset_dir(EVAL_ASSET_ROOT + "/" + run_id)):
            skipped.append({"runId": run_id, "reason": "active_attempt"})
        data = {"dryRun": bool(args.dry_run), "removed": [], "skipped": skipped, "restoredScenes": []}
        if args.dry_run:
            data["removed"] = owned
            return Outcome(data)
        if not owned:
            if not run_id:
                remove_empty_eval_root(rt)
            return Outcome(data)
        client = rt.client()
        for item in owned:
            scenes = F.loaded_scenes(client)
            if any(F.under(s.get("path"), item["assetRoot"]) for s in scenes):
                resting = _resting_for(rt, item["runId"])
                if resting is None:
                    return Outcome(data, [problem("CLEANUP_NEEDS_SCENE", detail=item["assetRoot"])])
                found, _summary = scene_checks(scenes)
                dirty = [p for p in found if p["code"] == "SCENE_DIRTY"]
                if dirty:
                    return Outcome(data, dirty)
                F.restore_resting(client, resting)
                data["restoredScenes"].extend(s["path"] for s in resting["scenes"] if s.get("isLoaded", True))
            try:
                client.write("asset_delete", assetPath=item["assetRoot"])
                if F.asset_exists(client, item["assetRoot"]):
                    raise RuntimeError("still present after asset_delete")
                data["removed"].append(item)
            except Exception as exc:
                rt.log.error("cleanup %s: %s: %s" % (item["assetRoot"], type(exc).__name__, exc))
                skipped.append({"runId": item["runId"], "reason": "error"})
        if not run_id:
            remove_empty_eval_root(rt)
        return Outcome(data)

# Producer:Betsy
