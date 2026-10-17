"""Test support for usk_eval: a fake UnitySkills REST server on 127.0.0.1:0, a temp Unity project, and stand-ins
for modules owned by other work packages (usk_eval.pack / private / verifylib / scope). No real editor is touched.
"""
import contextlib
import hashlib
import http.server
import io
import json
import os
import re
import shutil
import sys
import tempfile
import threading
import time
import types
import urllib.parse

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
EVAL_DIR = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
if EVAL_DIR not in sys.path:
    sys.path.insert(0, EVAL_DIR)

from usk_eval import attempt, cli, fixture, journal, lock, package_version, paths, rest, session, store  # noqa: E402
from usk_eval import pack as real_pack  # noqa: E402
from usk_eval import private as real_private  # noqa: E402
from usk_eval.verifylib import checks as real_checks  # noqa: E402
from usk_eval.task_api import PublicTask, address  # noqa: E402

PRODUCT = "EvalTest"
READ_ONLY = {"scene_get_loaded", "scene_get_info", "editor_get_state", "project_get_render_pipeline",
             "editor_get_changes", "asset_get_info", "physics_get_layer_collision", "gameobject_get_info"}
SOLVER = "ClaudeCode"


def expected_iid(path, product=PRODUCT):
    clean = re.sub(r"[^A-Za-z0-9]", "", product)
    return "%s_%s" % (clean, hashlib.sha256(path.encode("utf-8")).digest()[:4].hex().upper())


class SkillFailure(Exception):
    def __init__(self, code, message):
        Exception.__init__(self, message)
        self.code = code


def make_project(root, product=PRODUCT):
    project = os.path.realpath(os.path.join(root, "Proj"))
    os.makedirs(os.path.join(project, "Assets", "Scenes"))
    os.makedirs(os.path.join(project, "ProjectSettings"))
    os.makedirs(os.path.join(project, "Packages"))
    os.makedirs(os.path.join(project, "Library", "UnitySkills"))
    with open(os.path.join(project, "ProjectSettings", "ProjectVersion.txt"), "w") as handle:
        handle.write("m_EditorVersion: 6000.3.9f1\n")
    with open(os.path.join(project, "ProjectSettings", "ProjectSettings.asset"), "w") as handle:
        handle.write("PlayerSettings:\n  companyName: Test\n  productName: %s\n" % product)
    with open(os.path.join(project, "Assets", "Scenes", "Main.unity"), "w") as handle:
        handle.write("%YAML 1.1\n")
    with open(os.path.join(project, "Packages", "packages-lock.json"), "w") as handle:
        json.dump({"dependencies": {"com.besty.unity-skills": {
            "version": "file:" + os.path.join(REPO, "SkillsForUnity"), "depth": 0, "source": "local"}}}, handle)
    return project


class FakeUnity(object):
    """Just enough of the UnitySkills server: /health, /skills/schema, /skill/<name>, /jobs, /analytics.

    Write skills publish journal entries (Library/UnitySkills/editor_changes.jsonl) and every skill call writes a
    telemetry line, the way the plugin does.
    """

    def __init__(self, project, version=None):
        self.project = project
        self.version = version or package_version()
        self.lock = threading.RLock()
        self.instance_id = expected_iid(project)
        self.mode, self.surface, self.dry_run, self.token_level = "bypass", "full", "off", "maximum"
        self.compiling = False
        self.playing = False
        self.pipeline = "URP"
        self.telemetry_enabled = True
        self.received = 0
        self.requests = []
        self.seq = 0
        self.scenes = [{"name": "Main", "path": "Assets/Scenes/Main.unity", "isLoaded": True, "isDirty": False,
                        "isActive": True}]
        self.objects = {}             # entityId -> {name, path, scene, instanceId}
        self.next_iid = -1000
        self.entity_mode = "legacy"   # "6000.4": instanceId is always 0, entityId is a ulong as text
        self.jobs = {}
        self.fail_skill = {}          # skill -> SkillFailure to raise (once unless sticky)
        self.http_status = []         # statuses answered (and consumed) before routing
        self.spoil = {}               # path -> ["empty" (200, no body) | "garbled" (no status line), both not run |
                                      #          "cut" (run, half the body)], applied to that path's answers in turn
        self.delete_fails = False
        self.collision = {}
        self.journal_path = os.path.join(project, "Library", "UnitySkills", "editor_changes.jsonl")
        self.telemetry_path = os.path.join(project, "Library", "UnitySkillsTelemetry.jsonl")
        fake = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.0"

            def log_message(self, *args):
                pass

            def do_GET(self):
                fake._handle(self, "GET")

            def do_POST(self):
                fake._handle(self, "POST")

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.port = self.server.server_address[1]
        self._thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def start(self):
        self._thread.start()
        return self

    def stop(self):
        self.server.shutdown()
        self.server.server_close()

    # ------------------------------------------------------------------ journal / telemetry

    def publish(self, etype, source, agent, payload):
        with self.lock:
            self.seq += 1
            entry = {"seq": self.seq, "type": etype, "source": source, "agent": agent if source == "rest" else None,
                     "isPlaying": self.playing, "tsUtc": store.now_iso(), "payload": payload}
            with open(self.journal_path, "a", encoding="utf-8") as handle:
                handle.write(json.dumps(entry, separators=(",", ":")) + "\n")
            return entry

    def editor_change(self, path, kind="property_changed", scene=None):
        """A change made in the editor by hand (source editor)."""
        return self.publish("scene_changes", "editor", None, {"count": 1, "changes": [
            {"kind": kind, "name": path.split("/")[-1], "path": path, "scenePath": scene or self.active()["path"]}]})

    def _telemetry(self, skill, agent, ok, code=None):
        line = {"ts": store.now_iso(), "skill": skill, "agent": agent, "mode": "execute", "ok": ok}
        if not ok:
            line["errorCode"] = code
        line["ms"] = 1
        with open(self.telemetry_path, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(line, separators=(",", ":")) + "\n")

    # ------------------------------------------------------------------ HTTP

    def _send(self, req, status, body):
        data = b"" if body is None else json.dumps(body).encode("utf-8")
        req.send_response(status)
        req.send_header("Content-Type", "application/json")
        req.send_header("Content-Length", str(len(data)))
        req.end_headers()
        req.wfile.write(data[:len(data) // 2] if req.spoiled == "cut" else data)

    def _handle(self, req, method):
        parts = urllib.parse.urlsplit(req.path)
        query = {k: v[0] for k, v in urllib.parse.parse_qs(parts.query).items()}
        agent = req.headers.get("X-Agent-Id") or "unknown"
        with self.lock:
            self.received += 1
            self.requests.append((method, parts.path, agent))
            forced = self.http_status.pop(0) if self.http_status else None
            spoils = self.spoil.get(parts.path)
            req.spoiled = spoils.pop(0) if spoils and not forced else None
        if forced:
            return self._send(req, forced, {"status": "error", "errorCode": "FORCED", "error": "forced %d" % forced})
        if req.spoiled == "empty":    # a listener shutting down for a domain reload
            return self._send(req, 200, None)
        if req.spoiled == "garbled":
            return req.wfile.write(b"garbled\r\n\r\n")
        expect = req.headers.get("X-Expect-Instance")
        if expect and expect != self.instance_id:
            return self._send(req, 409, {"status": "error", "errorCode": "INSTANCE_MISMATCH"})
        if method == "GET" and parts.path == "/health":
            return self._send(req, 200, self.health())
        if method == "GET" and parts.path == "/analytics":
            return self._send(req, 200, {"status": "ok", "window": query.get("window"),
                                         "telemetryEnabled": self.telemetry_enabled})
        if method == "GET" and parts.path == "/skills/schema":
            names = [n for n in (query.get("names") or "").split(",") if n]
            return self._send(req, 200, {"wire": "v2", "skills": [
                {"name": n, "flags": ["readOnly"] if n in READ_ONLY else ["mutatesScene"]} for n in names]})
        if method == "GET" and parts.path in ("/jobs", "/jobs/"):
            with self.lock:
                jobs = [dict(j) for j in self.jobs.values()]
            return self._send(req, 200, {"count": len(jobs), "jobs": jobs})
        if method == "GET" and parts.path.startswith("/jobs/"):
            job = self.jobs.get(parts.path[len("/jobs/"):])
            if job is None:
                return self._send(req, 404, {"status": "error", "errorCode": "NOT_FOUND"})
            return self._send(req, 200, dict(job, terminal=job["status"] in rest.TERMINAL_JOB_STATES))
        if method == "POST" and parts.path.startswith("/skill/"):
            length = int(req.headers.get("Content-Length") or 0)
            args = json.loads(req.rfile.read(length).decode("utf-8") or "{}") if length else {}
            name = parts.path[len("/skill/"):]
            try:
                result = self.call(name, args, agent)
            except SkillFailure as exc:
                self._telemetry(name, agent, False, exc.code)
                return self._send(req, 200, {"status": "error", "errorCode": exc.code, "error": str(exc)})
            self._telemetry(name, agent, True)
            return self._send(req, 200, {"status": "success", "result": result})
        return self._send(req, 404, {"status": "error", "errorCode": "NOT_FOUND"})

    def health(self):
        with self.lock:
            return {"status": "ok", "version": self.version, "unityVersion": "6000.3.9f1",
                    "instanceId": self.instance_id, "projectName": PRODUCT, "currentMode": self.mode,
                    "tokenLevel": self.token_level, "surfaceProfile": self.surface, "dryRunPolicy": self.dry_run,
                    "summaryAutoTruncate": True, "summaryPageSize": 50, "panelApprovalRequired": False,
                    "allowlistCount": 0, "domainReloadPending": False, "port": self.port,
                    "compilation": {"isCompiling": self.compiling, "isUpdating": False, "domainReloadPending": False},
                    "queueStats": {"queued": 0, "totalReceived": self.received}}

    # ------------------------------------------------------------------ editor model

    def active(self):
        return next(s for s in self.scenes if s["isActive"])

    def disk(self, asset_path):
        return os.path.join(self.project, *asset_path.split("/"))

    def _new_ids(self):
        self.next_iid -= 2
        if self.entity_mode == "6000.4":
            return str(2 ** 32 + 7000 - self.next_iid), 0
        return str(self.next_iid), self.next_iid

    def _find(self, instanceId=0, entityId=None, path=None):
        if entityId:
            return entityId if entityId in self.objects else None
        for eid, obj in sorted(self.objects.items()):
            if (instanceId and obj["instanceId"] == instanceId) or (path and obj["path"] == path):
                return eid
        return None

    def call(self, name, args, agent):
        with self.lock:
            failure = self.fail_skill.get(name)
            if failure is not None:
                if not getattr(failure, "sticky", False):
                    self.fail_skill.pop(name)
                raise failure
            handler = getattr(self, "_skill_" + name, None)
            if handler is None:
                raise SkillFailure("SKILL_NOT_FOUND", "no fake for %s" % name)
            return handler(agent, **args)

    def _skill_scene_get_loaded(self, agent):
        return {"success": True, "count": len(self.scenes), "scenes": [dict(s, rootCount=0, isPseudoScene=False)
                                                                      for s in self.scenes]}

    def _skill_scene_get_info(self, agent):
        scene = self.active()
        return {"sceneName": scene["name"], "scenePath": scene["path"], "isDirty": scene["isDirty"]}

    def _skill_editor_get_state(self, agent):
        return {"isPlaying": self.playing, "isPaused": False, "isCompiling": self.compiling}

    def _skill_project_get_render_pipeline(self, agent):
        return {"success": True, "pipelineType": self.pipeline, "pipelineName": "PC_RPAsset",
                "defaultShader": "Universal Render Pipeline/Lit", "colorProperty": "_BaseColor"}

    def _skill_editor_get_changes(self, agent, since=0, limit=100, **_):
        return {"success": True, "cursor": self.seq, "changes": [], "returned": 0}

    def _skill_asset_get_info(self, agent, assetPath):
        path = self.disk(assetPath)
        if not os.path.exists(path):
            raise SkillFailure("ASSET_NOT_FOUND", "Asset not found: %s" % assetPath)
        return {"path": assetPath, "name": os.path.basename(assetPath),
                "type": "Folder" if os.path.isdir(path) else "Asset", "guid": "g" + str(abs(hash(assetPath)))}

    def _skill_asset_create_folder(self, agent, folderPath):
        os.makedirs(self.disk(folderPath), exist_ok=True)
        with open(self.disk(folderPath) + ".meta", "w") as handle:
            handle.write("guid: x\n")
        self.publish("file_changes", "rest", agent, {"imported": [folderPath], "deleted": [], "moved": [],
                                                     "count": 1, "omitted": 0})
        return {"success": True, "path": folderPath}

    def _skill_asset_delete(self, agent, assetPath):
        if self.delete_fails:
            raise SkillFailure("DELETE_FAILED", "cannot delete %s" % assetPath)
        path = self.disk(assetPath)
        if os.path.isdir(path):
            shutil.rmtree(path)
        elif os.path.exists(path):
            os.remove(path)
        if os.path.exists(path + ".meta"):
            os.remove(path + ".meta")
        self.publish("file_changes", "rest", agent, {"imported": [], "deleted": [assetPath], "moved": [],
                                                     "count": 1, "omitted": 0})
        return {"success": True, "deleted": assetPath}

    def _skill_asset_refresh(self, agent):
        return {"success": True, "message": "Asset database refreshed", "compileTriggered": False}

    def _skill_scene_create(self, agent, scenePath):
        with open(self.disk(scenePath), "w") as handle:
            handle.write("%YAML 1.1\n")
        self.scenes = [{"name": os.path.basename(scenePath)[:-6], "path": scenePath, "isLoaded": True,
                        "isDirty": False, "isActive": True}]
        self.objects = {}
        self.publish("file_changes", "rest", agent, {"imported": [scenePath], "deleted": [], "moved": [],
                                                     "count": 1, "omitted": 0})
        return {"success": True, "scenePath": scenePath, "sceneName": self.scenes[0]["name"]}

    def _skill_scene_save(self, agent, scenePath=None):
        scene = self.active()
        scene["isDirty"] = False
        self.publish("scene_saved", "rest", agent, {"name": scene["name"], "path": scene["path"]})
        return {"success": True, "scenePath": scene["path"]}

    def _skill_scene_load(self, agent, scenePath, additive=False):
        if not os.path.isfile(self.disk(scenePath)):
            raise SkillFailure("SCENE_NOT_FOUND", "Scene not found: %s" % scenePath)
        entry = {"name": os.path.basename(scenePath)[:-6], "path": scenePath, "isLoaded": True, "isDirty": False,
                 "isActive": not additive}
        if additive:
            self.scenes.append(entry)
        else:
            self.scenes = [entry]
            self.objects = {}
        self.publish("scene_opened", "rest", agent, {"path": scenePath})
        return {"success": True, "sceneName": entry["name"], "scenePath": scenePath}

    def _skill_scene_set_active(self, agent, sceneName):
        for scene in self.scenes:
            if scene["name"] == sceneName or scene["path"].endswith(sceneName + ".unity"):
                for other in self.scenes:
                    other["isActive"] = other is scene
                return {"success": True, "activeScene": scene["name"]}
        raise SkillFailure("SCENE_NOT_FOUND", "Scene '%s' not found in loaded scenes" % sceneName)

    def _skill_gameobject_create(self, agent, name, parentInstanceId=0, parentEntityId=None, parentPath=None, **_):
        parent = None
        if parentEntityId or parentInstanceId or parentPath:
            parent = self._find(parentInstanceId, parentEntityId, parentPath)
            if parent is None:
                raise SkillFailure("TARGET_NOT_FOUND", "parent not found")
        eid, iid = self._new_ids()
        path = (self.objects[parent]["path"] + "/" if parent else "") + name
        self.objects[eid] = {"name": name, "path": path, "scene": self.active()["path"], "instanceId": iid}
        self.active()["isDirty"] = True
        self.publish("scene_changes", "rest", agent, {"count": 1, "changes": [
            {"kind": "gameobject_created", "name": name, "entityId": eid, "instanceId": iid, "path": path,
             "scenePath": self.active()["path"]}]})
        return {"success": True, "name": name, "instanceId": iid, "entityId": eid, "path": path}

    def _skill_gameobject_get_info(self, agent, name=None, instanceId=0, path=None, entityId=None):
        eid = self._find(instanceId, entityId, path)
        if eid is None:
            raise SkillFailure("GAMEOBJECT_NOT_FOUND", "not found")
        obj = self.objects[eid]
        children = [{"name": o["name"], "entityId": e, "instanceId": o["instanceId"]}
                    for e, o in sorted(self.objects.items()) if "/" in o["path"]
                    and o["path"].rsplit("/", 1)[0] == obj["path"]]
        return {"name": obj["name"], "entityId": eid, "instanceId": obj["instanceId"], "path": obj["path"],
                "isActive": True, "children": children}

    def _skill_editor_stop(self, agent):
        if not self.playing:
            raise SkillFailure("NOT_PLAYING", "Not in play mode")
        self.playing = False
        return {"success": True, "mode": "stopped"}

    def _skill_physics_get_layer_collision(self, agent, layer1, layer2):
        return {"layer1": layer1, "layer2": layer2, "collisionEnabled": self.collision.get((layer1, layer2), True)}

    def _skill_physics_set_layer_collision(self, agent, layer1, layer2, enableCollision=True):
        self.collision[(layer1, layer2)] = enableCollision
        return {"success": True, "collisionEnabled": enableCollision}


# ---------------------------------------------------------------- stand-ins for other work packages


class S99(PublicTask):
    id = "S99"
    track = "ops"
    tier = "basic"
    case_version = 1
    minutes = 5

    def build(self, ctx):
        props = ctx.c.write("gameobject_create", name="Props", **address(ctx.root, "parent"))
        return {"props": fixture.object_id(props), "propsPath": ctx.path("Props")}

    def snapshot(self, ctx):
        return {"data": {"props": ctx.handles["props"]}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"props": ctx.handles["propsPath"]}

    def answer_schema(self):
        return {"type": "object", "required": ["objectPath"], "properties": {
            "objectPath": {"type": "string", "desc": {"zh-CN": "Crate 的完整层级路径", "en": "full hierarchy path of Crate"}},
            "objectInstanceId": {"type": "integer"}}}


class S98(S99):
    id = "S98"


PROMPTS = {
    "en": "<!-- usk-eval prompt; task {id}; lang en -->\nR1. Create a cube named `Crate` under `{{props}}`.\n"
          "   Keep its local position at zero.\nR2. Do not change anything else.\n",
    "zh-CN": "<!-- usk-eval prompt; task {id}; lang zh-CN -->\nR1. 在 `{{props}}` 下新建名为 `Crate` 的立方体。\n"
             "   保持本地坐标为 0。\nR2. 不要修改其他对象和资产。\n",
}


class FakePack(object):
    def __init__(self, root, tasks, source="package", modified=(), official=True, plugin_min=None):
        self.root = root
        self._tasks = dict((t.id, t) for t in tasks)
        ids = [t.id for t in tasks]
        self.manifest = {"schema": 1, "packId": "unity-skills-eval", "packVersion": "1.1.0",
                         "plugin": {"min": plugin_min, "max": None},
                         "tracks": {"ops": {"basic": ids, "standard": [], "challenge": []},
                                    "dev": {"basic": [], "standard": [], "challenge": []}},
                         "tasks": dict((t.id, {"track": t.track, "tier": t.tier, "caseVersion": t.case_version,
                                               "minutes": t.minutes, "seeds": [7001],
                                               "requires": dict(t.requires),
                                               "private": {"file": t.id + ".py", "sha256": "0" * 64, "bytes": 1}})
                                       for t in tasks)}
        self.manifest_sha256 = store.canonical_digest(self.manifest)
        self.id, self.version = self.manifest["packId"], self.manifest["packVersion"]
        self.source, self.modified, self.official = source, list(modified), official
        self.private_kind, self.private_path = "url", None
        for task_id in ids:
            os.makedirs(os.path.join(root, "tasks", task_id), exist_ok=True)
            for lang, text in PROMPTS.items():
                with open(os.path.join(root, "tasks", task_id, "prompt.%s.md" % lang), "w", encoding="utf-8") as h:
                    h.write(text.format(id=task_id))

    def task_dir(self, task_id):
        return os.path.join(self.root, "tasks", task_id)

    def load_task(self, task_id):
        if task_id not in self._tasks:
            raise KeyError(task_id)
        return self._tasks[task_id]

    def select(self, track, tier, tasks=None):
        return real_pack.select_tasks(self.manifest, track, tier, tasks)

    def meta(self, task_id):
        meta = dict(self.manifest["tasks"][task_id])
        meta["id"] = task_id
        meta["requires"] = real_pack.normalize_requires(meta.get("requires"))
        return meta

    def plugin_range_problem(self, version):
        return real_pack.plugin_range_problem(self.manifest, version)


def verify_module(verify=None):
    """The private verify module of S99/S98: A1 the Crate exists where the answer says, A2 no scope violation."""
    module = types.ModuleType("usk_eval_private_fake")
    module.ASSERTIONS = [("A1", 6.0, False, ["R1"], "Crate exists under Props"),
                         ("A2", 4.0, True, ["R2", "G1"], "no side effects outside R/A")]
    module.HINTS = {"A1": {"zh-CN": "检查父对象", "en": "check the parent"}}

    def default_verify(ctx, card):
        want = ctx.handles["propsPath"] + "/Crate"
        reported = (ctx.answer or {}).get("objectPath")
        try:
            found = ctx.read("gameobject_get_info", path=want)
        except rest.SkillError:
            found = None
        card.check("A1", bool(found) and reported == want, expected=want, actual=reported)
        card.check("A2", not ctx.violations(), actual=len(ctx.violations()))

    module.verify = verify or default_verify
    module.verify_runtime = lambda ctx, card: None
    module.allowed_change = lambda ctx, rec: False
    return module


class Fetched(object):
    def __init__(self, module):
        self.kind, self.via, self.file, self.sha256, self.official = "url", "https://example/S99.py", "S99.py", \
            "a" * 64, True
        self.attempts = [{"via": "https://example/S99.py", "error": None}]
        self.module = module
        self.deleted = False


class PrivateUnavailable(Exception):
    def __init__(self, attempts, hint=None):
        Exception.__init__(self, "unavailable")
        self.attempts = attempts
        self.hint = hint


class PrivateLoadError(PrivateUnavailable):
    """WP8: the machine could not hold the verifier file (raised when entering loaded())."""


class Scorecard(object):
    def __init__(self, spec):
        self.spec = {a[0]: {"weight": float(a[1]), "critical": bool(a[2]), "req": list(a[3]), "desc": a[4]}
                     for a in spec}
        self.order = [a[0] for a in spec]
        if abs(sum(v["weight"] for v in self.spec.values()) - 10.0) > 1e-6:
            raise ValueError("weights must sum to 10")
        self.results = {}

    def check(self, aid, passed, expected=None, actual=None, note=None, **evidence):
        self.results[aid] = {"pass": bool(passed), "fraction": 1.0 if passed else 0.0, "expected": expected,
                             "actual": actual, "note": note, "evidence": evidence or None}

    def finish(self):
        for aid in self.order:
            self.results.setdefault(aid, {"pass": False, "fraction": 0.0, "expected": None, "actual": None,
                                          "note": "unobservable", "evidence": None})
        return self

    @property
    def score(self):
        return round(sum(self.spec[a]["weight"] * r["fraction"] for a, r in self.results.items()), 4)

    @property
    def strict(self):
        return len(self.results) == len(self.spec) and all(r["pass"] for r in self.results.values())

    def failed(self):
        return [a for a in self.order if not self.results.get(a, {}).get("pass")]

    def as_dict(self):
        rows = [dict(id=a, req=self.spec[a]["req"], weight=self.spec[a]["weight"],
                     critical=self.spec[a]["critical"], desc=self.spec[a]["desc"], **self.results.get(a, {}))
                for a in self.order]
        return {"score": self.score, "completion": round(self.score * 10, 2), "strict": self.strict,
                "criticalFailed": [a for a in self.order if self.spec[a]["critical"] and
                                   not self.results.get(a, {}).get("pass")], "assertions": rows}


def _under(path, root):
    return fixture.under(path, root)


class Stand(object):
    """Configurable fakes installed as usk_eval.pack / private / verifylib / scope."""

    def __init__(self, pack_root):
        self.pack = FakePack(pack_root, [S99(), S98()])
        self.verify = verify_module()
        self.private_fail = 0           # number of upcoming fetches that fail
        self.fail_attempts = [{"via": "https://example/S99.py", "error": "http 404"}]
        self.load_fail = 0              # number of upcoming loads that cannot write the verifier
        self.probe_reachable = True
        self.probe_attempts = [{"via": "https://example/S99.py", "error": "http 404"}]
        self.probes = []
        self.fetches = []

    def modules(self):
        stand = self
        pack = types.ModuleType("usk_eval.pack")
        pack.PackInvalid = ValueError
        pack.open_pack = lambda pack_dir=None, private_zip=None, private_dir=None: stand.pack

        private = types.ModuleType("usk_eval.private")
        private.PrivateUnavailable = PrivateUnavailable
        private.PrivateLoadError = PrivateLoadError
        private.TLS_HINT = real_private.TLS_HINT
        private.fix_hint = real_private.fix_hint

        def fetch(pack_obj, task_id, plugin_version=None, user_agent=None, private_zip=None, private_dir=None,
                  environ=None):
            stand.fetches.append({"task": task_id, "zip": private_zip, "dir": private_dir, "ua": user_agent,
                                  "environ": environ})
            if stand.private_fail:
                stand.private_fail -= 1
                raise PrivateUnavailable(list(stand.fail_attempts), real_private.fix_hint(stand.fail_attempts))
            return Fetched(stand.verify)

        @contextlib.contextmanager
        def loaded(fetched, attempt_id):
            if stand.load_fail:
                stand.load_fail -= 1
                raise PrivateLoadError([{"via": tempfile.gettempdir(), "error": "temp_write_failed"}])
            try:
                yield fetched.module
            finally:
                fetched.deleted = True

        private.fetch = fetch
        private.loaded = loaded
        def probe(pack_obj, task_id, plugin_version=None, user_agent=None, environ=None):
            stand.probes.append({"task": task_id, "environ": environ})
            out = {"kind": "url", "probedTask": task_id, "reachable": stand.probe_reachable,
                   "via": "https://example/%s.py" % task_id if stand.probe_reachable else None,
                   "attempts": [] if stand.probe_reachable else list(stand.probe_attempts)}
            hint = None if stand.probe_reachable else real_private.fix_hint(stand.probe_attempts)
            if hint:
                out["hint"] = hint
            return out

        private.probe = probe
        private.cleanup_stale = lambda: None

        verifylib = types.ModuleType("usk_eval.verifylib")
        verifylib.Scorecard = Scorecard
        verifylib.Probe = lambda client: types.SimpleNamespace(c=client)
        verifylib.fingerprint = lambda ctx: store.canonical_digest(
            json.loads(json.dumps(ctx.handles).replace(ctx.scope.run_id, "<RUN>")))
        verifylib.object_id = real_checks.object_id
        verifylib.same_id = real_checks.same_id
        verifylib.address = real_checks.address

        scope = types.ModuleType("usk_eval.scope")

        def scope_violations(records, object_root, asset_root, run_scene=None, extra_allowed=None):
            bad = []
            for rec in records:
                if extra_allowed and extra_allowed(rec):
                    continue
                kind = rec.get("kind")
                if kind in ("imported", "deleted", "moved"):
                    path = rec.get("assetPath") or ""
                    path = path[:-5] if path.endswith(".meta") else path
                    if not _under(path, asset_root):
                        bad.append(rec)
                elif kind in ("scene_saved", "scene_opened"):
                    if rec.get("path") != run_scene:
                        bad.append(rec)
                elif rec.get("type") == "scene_changes" and not _under(rec.get("path"), object_root):
                    bad.append(rec)
            return bad

        def non_rest(records, asset_root, run_scene, allowed=None):
            out = []
            for rec in records:
                if rec.get("source") != "editor" or rec.get("isPlaying"):
                    continue
                if rec.get("kind") in ("imported", "deleted", "moved") and _under(
                        (rec.get("assetPath") or "").replace(".meta", ""), asset_root):
                    continue
                if rec.get("kind") in ("scene_saved", "scene_opened") and rec.get("path") == run_scene:
                    continue
                if allowed and allowed(rec):
                    continue
                out.append(rec)
            return out

        scope.scope_violations = scope_violations
        scope.non_rest = non_rest
        scope.direct_writes = lambda records, asset_root, run_scene=None: [
            r for r in records if r.get("kind") in ("imported", "deleted", "moved")
            and _under(r.get("assetPath"), asset_root) and (r.get("origin") == "refresh" or r.get("source") == "editor")]
        scope.write_records = lambda records, run_scene, runner_agent: [
            r for r in records if not r.get("isPlaying") and not (r.get("agent") == runner_agent and
                                                                  r.get("origin") == "attempt")]
        return {"usk_eval.pack": pack, "usk_eval.private": private, "usk_eval.verifylib": verifylib,
                "usk_eval.scope": scope}


@contextlib.contextmanager
def installed(modules):
    saved = {name: sys.modules.get(name) for name in modules}
    sys.modules.update(modules)
    try:
        yield
    finally:
        for name, module in saved.items():
            if module is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = module


class Bench(object):
    """A temp project + fake editor + registry + stand-in modules; run() calls cli.main in-process."""

    def __init__(self, test, stand_ins=True):
        self.tmp = tempfile.mkdtemp(prefix="usk-runner-test-")
        test.addCleanup(shutil.rmtree, self.tmp, True)
        self.project = make_project(self.tmp)
        self.home = os.path.join(self.tmp, "home")
        os.makedirs(self.home)
        self.unity = FakeUnity(self.project).start()
        test.addCleanup(self.unity.stop)
        self.registry = os.path.join(self.tmp, "registry.json")
        self.write_registry()
        self.stand = Stand(os.path.join(self.tmp, "pack"))
        self.env = {"USK_EVAL_REGISTRY": self.registry, "HOME": self.home, "USERPROFILE": self.home,
                    "USK_EVAL_MANUAL_FOCUS": "1", "USK_EVAL_CLAUDE_ROOT": os.path.join(self.tmp, "claude"),
                    "USK_EVAL_CODEX_ROOT": os.path.join(self.tmp, "codex")}
        self.paths = paths.StatePaths(self.project)
        if stand_ins:
            stack = contextlib.ExitStack()
            test.addCleanup(stack.close)
            stack.enter_context(installed(self.stand.modules()))
        self._patch(test, rest, "IDLE_SETTLE_S", 0.01)
        self._patch(test, journal, "RETRY_DELAY_S", 0.01)
        self._patch(test, fixture, "RETRY_DELAY_S", 0.0)
        self._patch(test, lock, "POLL_S", 0.01)
        self._patch(test, attempt, "TELEMETRY_SETTLE_S", 0.0)
        self._patch(test, session, "BUSY_WAIT_S", 0.3)

    @staticmethod
    def _patch(test, module, name, value):
        old = getattr(module, name)
        setattr(module, name, value)
        test.addCleanup(setattr, module, name, old)

    def write_registry(self, pid=None, path=None, status="running", last_active=None, iid=None):
        path = path or self.project
        entry = {"id": iid or expected_iid(path), "name": PRODUCT, "path": path, "port": self.unity.port,
                 "pid": pid or os.getpid(), "last_active": int(last_active or time.time()),
                 "unityVersion": "6000.3.9f1", "status": status, "statusSince": int(time.time())}
        with open(self.registry, "w", encoding="utf-8") as handle:
            json.dump({path: entry}, handle)

    def run(self, *argv, **env):
        merged = dict(self.env)
        merged.update(env)
        argv = list(argv)
        if "--project" not in argv:
            argv[1:1] = ["--project", self.project]
        out = io.StringIO()
        code = cli.main(argv, environ=merged, stdout=out)
        text = out.getvalue()
        envelope = json.loads(text) if text else None
        return code, envelope

    def agent_call(self, skill, agent=SOLVER, **args):
        """A REST call made by the agent under test (journal + telemetry carry its agent id)."""
        time.sleep(0.003)        # telemetry has millisecond stamps and the window is t0 < ts: never share t0's ms
        target = types.SimpleNamespace(instance_id=self.unity.instance_id, base_url="http://127.0.0.1:%d" %
                                       self.unity.port)
        client = rest.Client(target, agent_id=agent, agent_ua="test-agent", retry_window_s=5.0)
        return client.write(skill, **args)

    def agent_create(self, name, parent_path, agent=SOLVER):
        parent = self.unity.call("gameobject_get_info", {"path": parent_path}, agent)
        return self.agent_call("gameobject_create", agent=agent, name=name, **address(parent, "parent"))

    def answer(self, data, payload):
        path = data["answerFile"]
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            if isinstance(payload, str):
                handle.write(payload)
            else:
                json.dump(payload, handle)
        return path

    def state(self, sid, aid):
        return store.read_json(self.paths.state_file(sid, aid))

    def result(self, sid, aid):
        return store.read_json(self.paths.result_file(sid, aid))

    def session(self, sid):
        return store.read_json(self.paths.session_file(sid))

    def solve(self, data, correct=True):
        """Do the S99 task the right way (or put the Crate in the wrong place) and write the answer."""
        props = self.state(data["sessionId"], data["attemptId"])["handles"]["propsPath"]
        if correct:
            self.agent_create("Crate", props)
            return self.answer(data, {"objectPath": props + "/Crate", "declaredComplete": True})
        root = props.split("/")[0]
        self.agent_create("Crate", root)
        return self.answer(data, {"objectPath": root + "/Crate", "declaredComplete": True})

# Producer:Betsy
