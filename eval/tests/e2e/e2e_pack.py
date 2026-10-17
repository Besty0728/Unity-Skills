"""A self-contained evaluation pack for the E2E and driver tests: two tiny ops tasks, their verifiers and solutions.

The tasks need nothing but gameobject_create and gameobject_get_info, so they run on any render pipeline (requires.pipeline
is null) and on the offline fake editor alike. E01: create the cube `Crate` under `Props`; E02: `Barrel` under `Shelf`.

Layouts written under `root`:
  pack/manifest.json, pack/tasks/<ID>/        usable as --pack-dir, or as <pkg>/Evaluation~/pack of a runner copy
  pack/private/<ID>.py, pack/solutions/<ID>.py  verifiers (served by PrivateServer) and reference/negative solutions
"""
import contextlib
import hashlib
import http.server
import json
import os
import shutil
import string
import threading

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
RUNNER_DIR = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
PACKAGE_JSON = os.path.join(REPO, "SkillsForUnity", "package.json")

SPECS = {"E01": {"parent": "Props", "child": "Crate"}, "E02": {"parent": "Shelf", "child": "Barrel"}}

TASK = string.Template('''from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address
$imports

class $id(PublicTask):
    id = "$id"
    track = "ops"
    tier = "basic"
    case_version = $case_version
    minutes = 5
    requires = {"pipeline": None, "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        name = $parent_name
        parent = ctx.c.write("gameobject_create", name=name, x=ctx.rng.choice([3, 4, 5]), y=0, z=0,
                             **address(ctx.root, "parent"))
        return {"parent": V.object_id(parent), "parentPath": ctx.path(name)}

    def snapshot(self, ctx):
        return {"data": {"parent": ctx.handles["parent"]}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"parentPath": ctx.handles["parentPath"], "child": "$child"}

    def answer_schema(self):
        return {"type": "object", "required": ["objectPath"], "properties": {
            "objectPath": {"type": "string", "desc": {"zh-CN": "完整层级路径", "en": "full hierarchy path"}}}}


TASK = $id()
''')

PROMPT_EN = ("<!-- usk-eval prompt; task $id; lang en -->\n"
             "R1. Create a cube named `{child}` under `{parentPath}`.\n"
             "   Leave every other object and asset as it is.\n"
             "R2. Do not change anything outside the run's object root and asset root.\n")
PROMPT_ZH = ("<!-- usk-eval prompt; task $id; lang zh-CN -->\n"
             "R1. 在 `{parentPath}` 下新建名为 `{child}` 的立方体。\n"
             "   其他对象和资产保持不变。\n"
             "R2. 不要修改本轮对象根和资产根之外的任何内容。\n")

PRIVATE = string.Template('''from usk_eval import verifylib as V

ASSERTIONS = [
    ("A1", 4.0, False, ["R1"], "$child exists under $parent"),
    ("A2", 2.0, False, ["R1"], "the answer reports its path"),
    ("A3", 4.0, True, ["R2", "G1"], "no side effects outside R/A"),
]
HINTS = {"A1": {"zh-CN": "检查父对象", "en": "check the parent object"}}


def verify(ctx, card):
    want = ctx.handles["parentPath"] + "/$child"
    card.check("A1", bool(ctx.probe.go_path(want)), expected=want)
    reported = V.answer(ctx, "objectPath")
    card.check("A2", reported == want, expected=want, actual=reported)
    bad = ctx.violations()
    card.check("A3", not bad, actual=len(bad))


def verify_runtime(ctx, card):
    pass
''')

SOLUTIONS = string.Template('''from usk_eval.task_api import Negative, address

CHILD = "$child"


def _crate(ctx, under_parent=True, **extra):
    target = ctx.handles["parent"] if under_parent else ctx.root
    return ctx.c.write("gameobject_create", name=CHILD, primitiveType="Cube", **dict(address(target, "parent"), **extra))


def _answer(path):
    return {"objectPath": path, "declaredComplete": True}


def _right_path(ctx):
    return ctx.handles["parentPath"] + "/" + CHILD


def reference(ctx):
    _crate(ctx)
    return _answer(_right_path(ctx))


def wrong_parent(ctx):
    _crate(ctx, under_parent=False)
    return _answer(_right_path(ctx))


def wrong_report(ctx):
    _crate(ctx)
    return _answer(ctx.R + "/" + CHILD)


def nothing_done(ctx):
    return _answer(_right_path(ctx))


def stray_object(ctx):
    ans = reference(ctx)
    ctx.c.write("gameobject_create", name="Stray")
    return ans


def stray_and_wrong_parent(ctx):
    ans = wrong_parent(ctx)
    ctx.c.write("gameobject_create", name="Stray")
    return ans


def by_path(ctx):
    ctx.c.write("gameobject_create", name=CHILD, primitiveType="Cube", parentPath=ctx.handles["parentPath"])
    return _answer(_right_path(ctx))


NEGATIVES = [
    Negative("wrong_parent", wrong_parent, {"A1"}),
    Negative("wrong_report", wrong_report, {"A2"}),
    Negative("nothing_done", nothing_done, {"A1"}),
    Negative("stray_object", stray_object, {"A3"}),
    Negative("stray_and_wrong_parent", stray_and_wrong_parent, {"A1", "A3"}),
]
ALTERNATIVES = [("by_path", by_path)]
''')


def sha256(data):
    return hashlib.sha256(data).hexdigest()


class E2EPack(object):
    """Files of the test pack under `root`; see the module docstring."""

    def __init__(self, root, tasks, seeds, case_version):
        self.root = root
        self.pack_dir = os.path.join(root, "pack")
        self.private_dir = os.path.join(self.pack_dir, "private")
        self.solutions_dir = os.path.join(self.pack_dir, "solutions")
        self.tasks = list(tasks)
        self.seeds = list(seeds)
        self.case_version = case_version

    @classmethod
    def create(cls, root, tasks=("E01", "E02"), seeds=(7001,), case_version=1, nondeterministic=(),
               private_override=None):
        """Writes the pack. `nondeterministic` task ids build a differently named parent every time (the
        determinism gate must catch them); private_override maps task id -> source replacing the verifier."""
        self = cls(root, tasks, seeds, case_version)
        for directory in (self.private_dir, self.solutions_dir):
            os.makedirs(directory, exist_ok=True)
        entries = {}
        for task_id in self.tasks:
            spec = SPECS[task_id]
            parent_name = ('"%s%%d" %% (__import__("time").time_ns() %% 100000)' % spec["parent"]
                           if task_id in nondeterministic else '"%s"' % spec["parent"])
            files = {
                "task.py": TASK.substitute(id=task_id, case_version=case_version, parent_name=parent_name,
                                           child=spec["child"], imports=""),
                "prompt.en.md": string.Template(PROMPT_EN).substitute(id=task_id),
                "prompt.zh-CN.md": string.Template(PROMPT_ZH).substitute(id=task_id),
            }
            directory = os.path.join(self.pack_dir, "tasks", task_id)
            os.makedirs(directory, exist_ok=True)
            public = {}
            for name, text in files.items():
                data = text.encode("utf-8")
                with open(os.path.join(directory, name), "wb") as handle:
                    handle.write(data)
                public[name] = sha256(data)
            private_text = (private_override or {}).get(task_id) or PRIVATE.substitute(**spec)
            private = private_text.encode("utf-8")
            with open(os.path.join(self.private_dir, task_id + ".py"), "wb") as handle:
                handle.write(private)
            with open(os.path.join(self.solutions_dir, task_id + ".py"), "wb") as handle:
                handle.write(SOLUTIONS.substitute(**spec).encode("utf-8"))
            entries[task_id] = {
                "track": "ops", "tier": "basic", "caseVersion": case_version, "minutes": 5, "seeds": self.seeds,
                "requires": {"pipeline": None, "mode": "bypass", "dedicated": False, "packages": []},
                "public": public, "private": {"file": task_id + ".py", "sha256": sha256(private), "bytes": len(private)}}
        manifest = {
            "schema": 1, "packId": "usk-e2e-pack", "packVersion": "1.0.0", "plugin": {"min": None, "max": None},
            "tracks": {"ops": {"basic": self.tasks, "standard": [], "challenge": []},
                       "dev": {"basic": [], "standard": [], "challenge": []}},
            "tasks": entries,
            "private": {"urlTemplates": ["https://raw.githubusercontent.com/Besty0728/Unity-Skills/{ref}/eval/pack/"
                                         "private/{file}"], "refs": ["v{plugin}", "main"]}}
        with open(os.path.join(self.pack_dir, "manifest.json"), "w", encoding="utf-8") as handle:
            json.dump(manifest, handle, indent=1)
        self.manifest = manifest
        return self

    def install_runner(self, destination):
        """A copy of the runner whose package pack is this pack: <destination>/package.json and Evaluation~/.

        Returns the path of the copied run_eval.py. The copy is what a user's runner is: the same code, its own
        package.json (the version the editor must report), and the test pack as the shipped pack.
        """
        runner = os.path.join(destination, "Evaluation~")
        shutil.copytree(RUNNER_DIR, runner, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "pack"))
        shutil.copy(PACKAGE_JSON, os.path.join(destination, "package.json"))
        shutil.copytree(self.pack_dir, os.path.join(runner, "pack"),
                        ignore=shutil.ignore_patterns("private", "solutions"))
        return os.path.join(runner, "run_eval.py")


class PrivateServer(object):
    """http://127.0.0.1:<port>/<file>: the private verifiers, with a switch for the failure the tests need.

    mode: "ok" serves the files, "not_found" answers 404, "error" answers 500. `served` lists (file, status).
    """

    def __init__(self, directory):
        self.directory = directory
        self.mode = "ok"
        self.served = []
        server = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.0"

            def log_message(self, *args):
                pass

            def do_GET(self):
                name = self.path.strip("/").split("?")[0]
                path = os.path.join(server.directory, name)
                if server.mode == "ok" and name and os.path.isfile(path):
                    with open(path, "rb") as handle:
                        data = handle.read()
                    status = 200
                else:
                    data = b"not available"
                    status = 500 if server.mode == "error" else 404
                server.served.append((name, status))
                self.send_response(status)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        self.httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.httpd.daemon_threads = True
        self.port = self.httpd.server_address[1]
        self._thread = threading.Thread(target=self.httpd.serve_forever, daemon=True)
        self._thread.start()

    @property
    def template(self):
        return "http://127.0.0.1:%d/{file}" % self.port

    @property
    def environ(self):
        """The runner environment that points the private download at this server (session becomes unofficial)."""
        return {"USK_EVAL_PRIVATE_URLS": json.dumps([self.template])}

    def stop(self):
        with contextlib.suppress(Exception):
            self.httpd.shutdown()
            self.httpd.server_close()

# Producer:Betsy
