"""In-memory editor for the offline tests of the dev/challenge tasks DC1, DC3, DC5 and DC7.

pack_fake_editor "compiles" scripts by parsing their public fields; FakeEditorDC adds what the four tasks touch:
[SerializeField] private fields, script_read / script_list / script_analyze (the public surface of compiled classes),
script_create_batch, GUID-keeping moves (asset_move, script_move, script_rename) and a fresh GUID for a path that
was deleted and created again, UGUI panels and buttons with persistent onClick listeners, Button.OnSubmit and the
static Time.timeScale accessors through component_invoke_method, and a Play Mode stand-in for editor_run_scenario
whose changes are put back afterwards. Each task subclass models the behaviour its prompt asks for; a
`// usk-fake: <variant>` comment in a script selects a wrong behaviour the solutions' negatives describe (a real
editor ignores the comment). This models the solutions, not C#.
"""
import copy
import hashlib
import json
import os
import re
import shutil

from .pack_fake_editor import RUNNER, _fail, _matches, _select
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
SERIALIZE_PRIVATE_RE = re.compile(r"(\[SerializeField\]\s*)(?:private|protected|internal)\s+")
COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
NAMESPACE_RE = re.compile(r"\bnamespace\s+([\w.]+)")
CLASS_DECL_RE = re.compile(r"\bclass\s+(\w+)")
PUBLIC_METHOD_RE = re.compile(r"\bpublic\s+(?!class\b|event\b|static\b)([\w.<>\[\]]+)\s+(\w+)\s*\(([^)]*)\)\s*\{")
PUBLIC_FIELD_RE = re.compile(r"\bpublic\s+(?!class\b|event\b|static\b|const\b|readonly\b)([\w.<>\[\]]+)\s+(\w+)\s*"
                             r"(?:=[^;{]*)?;")
PUBLIC_PROPERTY_RE = re.compile(r"\bpublic\s+(?!class\b|event\b)([\w.<>\[\]]+)\s+(\w+)\s*\{\s*get")
FRIENDLY = {"System.String": "string", "String": "string", "System.Int32": "int", "Int32": "int", "Single": "float",
            "System.Single": "float", "Boolean": "bool", "System.Boolean": "bool", "Void": "void"}
TYPE_NAMES = {"string": "System.String", "int": "System.Int32", "float": "System.Single", "bool": "System.Boolean",
              "uint": "System.UInt32"}


def strip_comments(source):
    return COMMENT_RE.sub("", source or "")


def top_class_body(source, cls):
    """The source text of `class cls` up to its matching brace (comments stripped), or ''."""
    text = strip_comments(source)
    match = re.search(r"\bclass\s+%s\b[^{]*\{" % re.escape(cls), text)
    if not match:
        return ""
    depth, index = 1, match.end()
    while index < len(text) and depth:
        depth += {"{": 1, "}": -1}.get(text[index], 0)
        index += 1
    return text[match.end():index - 1]


class FakeEditorDC(OpsFakeEditor):
    extra_reads = frozenset(("script_read", "script_list", "script_analyze", "event_get_listeners",
                             "prefab_get_serialized_properties"))

    def __init__(self, *args, **kwargs):
        self.guids = {}
        self.generations = {}
        OpsFakeEditor.__init__(self, *args, **kwargs)
        self.listeners = {}
        self.time_scale = 1.0
        self.runtime_error = False
        self.compiled_sources = {}

    # ------------------------------------------------------------ guids and dispatch

    def _guid(self, asset_path):
        if asset_path not in self.guids:
            seed = "%s#%d" % (asset_path, self.generations.get(asset_path, 0))
            self.guids[asset_path] = hashlib.md5(seed.encode("utf-8")).hexdigest()
        return self.guids[asset_path]

    def short(self, kind):
        """A compiled class named with its namespace (Game.AI.Patroller) answers to its short name."""
        if isinstance(kind, str) and "." in kind and kind.rsplit(".", 1)[1] in self.classes:
            return kind.rsplit(".", 1)[1]
        return kind

    def call(self, skill, args, agent):
        if "componentType" in args:
            args = dict(args, componentType=self.short(args["componentType"]))
        return OpsFakeEditor.call(self, skill, args, agent)

    def variants(self, cls):
        spec = self.classes.get(cls) or {}
        return set(MARKER_RE.findall(self.scripts.get(spec.get("path"), "")))

    # ------------------------------------------------------------ compilation

    def compile_errors_for(self, sources):
        """Paths whose source does not compile (unbalanced braces or an unterminated field initializer)."""
        out = []
        for path, source in sources.items():
            text = strip_comments(source)
            if text.count("{") != text.count("}") or re.search(r"^\s*public\s+[\w.]+\s+\w+\s*=\s*[^;{}\n]+$", text,
                                                                re.M):
                out.append(path)
        return out

    def _compile(self):
        """The base parse on sources where [SerializeField] private fields read as public ones; a failed compilation
        keeps the previous classes, as a failed Unity compilation keeps the loaded assemblies."""
        errors = self.compile_errors_for(self.scripts)
        if errors:
            self.compile_errors = errors
            return
        original = self.scripts
        self.scripts = {path: SERIALIZE_PRIVATE_RE.sub(r"\1public ", text) for path, text in original.items()}
        try:
            OpsFakeEditor._compile(self)
        finally:
            self.scripts = original
        self.compile_errors = []
        self.compiled_sources = dict(original)
        for cls, spec in self.classes.items():
            spec["namespace"] = (NAMESPACE_RE.search(strip_comments(original.get(spec["path"], ""))) or
                                 [None, None])[1]
        self.after_compile()

    def after_compile(self):
        """Hook: a subclass rebinds its components after a successful compilation."""

    def _add_script(self, agent, path, content):
        if path in self.scripts:
            raise _fail("ASSET_EXISTS", "Script already exists: %s" % path)
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self._write_file(path, content)
        self.scripts[path] = content
        self.publish("file_changes", agent, {"imported": [path], "deleted": [], "moved": [], "count": 1,
                                             "omitted": 0}, source="editor")

    def _skill_script_create(self, agent, args):
        name = args.get("scriptName") or args.get("name")
        path = "%s/%s.cs" % ((args.get("folder") or "Assets/Scripts").rstrip("/"), name)
        self._add_script(agent, path, args.get("content") or "")
        self._compile()
        return {"success": True, "status": "accepted", "path": path, "className": name, "jobId": "job-" + name}

    def _skill_script_create_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            path = "%s/%s.cs" % ((item.get("folder") or "Assets/Scripts").rstrip("/"), item["scriptName"])
            self._add_script(agent, path, item.get("content") or "")
            results.append({"success": True, "path": path, "jobId": "job-batch"})
        self._compile()
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_script_read(self, agent, args):
        path = args.get("scriptPath")
        if path not in self.scripts:
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        text = self.scripts[path]
        return {"path": path, "lines": text.count("\n") + 1, "content": text}

    def _skill_script_list(self, agent, args):
        folder, wanted = args.get("folder") or "Assets", args.get("filter")
        rows = [{"path": path, "name": os.path.basename(path)[:-3]} for path in sorted(self.scripts)
                if path.startswith(folder) and (not wanted or wanted in path)]
        return {"count": len(rows), "scripts": rows[:int(args.get("limit") or 100)]}

    def _skill_script_analyze(self, agent, args):
        """PerceptionSkills.script_analyze on the loaded (last compiled) classes: public instance fields, properties
        and methods declared by the type, with friendly type names."""
        name = args.get("scriptName")
        cls = next((c for c in self.classes if c.lower() == str(name).lower()), None)
        if cls is None:
            raise _fail("NOT_FOUND", "Script '%s' not found" % name)
        spec = self.classes[cls]
        full = top_class_body(SERIALIZE_PRIVATE_RE.sub(r"\1public ", self.compiled_sources.get(spec["path"], "")), cls)
        body = re.sub(r"\[\s*[A-Za-z][^\]]*\]", " ", outer_level_with_heads(full))
        fields = [{"name": n, "type": t.split(".")[-1], "isSerializable": True} for t, n in PUBLIC_FIELD_RE.findall(body)]
        props = [{"name": n, "type": t, "canWrite": True} for t, n in PUBLIC_PROPERTY_RE.findall(full)]
        methods = []
        for ret, method, params in PUBLIC_METHOD_RE.findall(body):
            parts = [p.strip().split() for p in params.split(",") if p.strip()]
            methods.append({"name": method, "returnType": FRIENDLY.get(ret, ret),
                            "parameters": ", ".join("%s %s" % (FRIENDLY.get(p[-2], p[-2]), p[-1]) for p in parts)})
        namespace = spec.get("namespace")
        return {"success": True, "script": name, "fullName": "%s.%s" % (namespace, cls) if namespace else cls,
                "kind": "MonoBehaviour" if spec.get("base") in ("MonoBehaviour", "UnityEngine.MonoBehaviour")
                else "Class", "baseClass": (spec.get("base") or "").split(".")[-1] or None, "fields": fields,
                "properties": props, "methods": methods, "unityCallbacks": []}

    # ------------------------------------------------------------ moves, renames and deletion (GUID semantics)

    def _move(self, agent, src, dst):
        if src not in self.known or not os.path.exists(self.disk(src)):
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % src)
        if os.path.exists(self.disk(dst)):
            raise _fail("SKILL_ERROR", "Destination exists: %s" % dst)
        parent = dst.rsplit("/", 1)[0]
        if not os.path.isdir(self.disk(parent)):
            raise _fail("SKILL_ERROR", "Parent directory is not in asset database: %s" % parent)
        moved = sorted(p for p in self.known if p == src or p.startswith(src + "/"))
        os.rename(self.disk(src), self.disk(dst))
        if os.path.exists(self.disk(src) + ".meta"):
            os.rename(self.disk(src) + ".meta", self.disk(dst) + ".meta")
        pairs = []
        for old in moved:
            new = dst + old[len(src):]
            pairs.append({"from": old, "to": new})
            self.known.discard(old)
            self.known.add(new)
            if old in self.guids:
                self.guids[new] = self.guids.pop(old)
            if old in self.scripts:
                self.scripts[new] = self.scripts.pop(old)
            self.moved_asset(old, new)
        self.publish("file_changes", agent, {"imported": [], "deleted": [], "moved": pairs, "count": len(pairs),
                                             "omitted": 0, "importedTotal": 0, "deletedTotal": 0,
                                             "movedTotal": len(pairs)})
        if any(pair["to"].endswith(".cs") for pair in pairs):
            self._compile()
        return pairs

    def moved_asset(self, old, new):
        """Hook: a subclass follows an asset to its new path."""

    def _skill_asset_move(self, agent, args):
        self._move(agent, args["sourcePath"], args["destinationPath"])
        return {"success": True, "from": args["sourcePath"], "to": args["destinationPath"]}

    def _skill_script_move(self, agent, args):
        src, folder = args["scriptPath"], args["newFolder"].rstrip("/")
        self._ensure_folder(folder, agent)
        dst = "%s/%s" % (folder, os.path.basename(src))
        self._move(agent, src, dst)
        return {"success": True, "status": "accepted", "oldPath": src, "newPath": dst, "jobId": "job-move"}

    def _skill_script_rename(self, agent, args):
        src = args["scriptPath"]
        dst = "%s/%s.cs" % (src.rsplit("/", 1)[0], args["newName"])
        self._move(agent, src, dst)
        return {"success": True, "status": "accepted", "path": dst, "oldPath": src, "newName": args["newName"],
                "jobId": "job-rename"}

    def _skill_script_delete(self, agent, args):
        path = args["scriptPath"]
        if path not in self.scripts:
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        del self.scripts[path]
        os.remove(self.disk(path))
        if os.path.exists(self.disk(path) + ".meta"):
            os.remove(self.disk(path) + ".meta")
        self.known.discard(path)
        self.guids.pop(path, None)
        self.generations[path] = self.generations.get(path, 0) + 1
        self.publish("file_changes", agent, {"imported": [], "deleted": [path], "moved": [], "count": 1,
                                             "omitted": 0}, source="editor")
        self._compile()
        return {"success": True, "status": "accepted", "deleted": path, "jobId": "job-delete"}

    def refresh(self, agent=RUNNER):
        """The base refresh, plus files a solver moved on disk with their .meta (a GUID-keeping move) and scripts that
        disappeared from disk."""
        for path in [p for p in list(self.scripts) if not os.path.exists(self.disk(p))]:
            del self.scripts[path]
            self.known.discard(path)
        for path in list(self.known):
            if not os.path.exists(self.disk(path)):
                self.known.discard(path)
        found = self._scan_new_scripts()
        out = OpsFakeEditor.refresh(self, agent)
        if found:
            self._compile()
        return out

    def _scan_new_scripts(self):
        """.cs files on disk that the model does not know yet: their .meta decides the GUID (a moved .meta keeps it)."""
        found = []
        for base, dirs, files in os.walk(self.disk("Assets")):
            dirs.sort()
            for name in sorted(files):
                if not name.endswith(".cs"):
                    continue
                rel = os.path.relpath(os.path.join(base, name), self.project).replace(os.sep, "/")
                if rel in self.scripts:
                    continue
                with open(os.path.join(base, name), "r", encoding="utf-8-sig") as handle:
                    self.scripts[rel] = handle.read()
                meta = os.path.join(base, name) + ".meta"
                if os.path.exists(meta):
                    with open(meta, "r", encoding="utf-8") as handle:
                        guid = re.search(r"guid:\s*(\w+)", handle.read())
                    if guid:
                        self.guids[rel] = guid.group(1)
                found.append(rel)
        return found

    def _skill_asset_refresh(self, agent, args):
        self.refresh(agent)
        return {"success": True, "compileTriggered": True, "jobId": "job-refresh"}

    # ------------------------------------------------------------ UGUI and persistent listeners

    def _skill_ui_create_panel(self, agent, args):
        holder = self._canvas_for(agent, args.get("parent"))
        panel = self._ui(agent, args.get("name") or "Panel", holder, [["CanvasRenderer", None], ["Image", True]],
                         m_AnchorMin=(0.0, 0.0), m_AnchorMax=(1.0, 1.0), m_SizeDelta=(0.0, 0.0))
        return {"success": True, "name": panel.name, "entityId": panel.eid, "instanceId": panel.iid,
                "parent": holder.name}

    def _event(self, args):
        obj = self.find(args)
        component, event = args.get("componentName"), args.get("eventName")
        if not component or not self.component(obj, component):
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s on %s" % (component, obj.name))
        if event != "onClick":
            raise _fail("EVENT_NOT_FOUND", "only onClick is modelled")
        return obj, (obj.eid, component, event)

    def _skill_event_add_listener(self, agent, args):
        obj, key = self._event(args)
        target = self.find({"entityId": args.get("targetEntityId"), "instanceId": args.get("targetInstanceId"),
                            "path": args.get("targetPath"), "name": args.get("targetObjectName")})
        kind = self.short(args.get("targetComponentName"))
        if kind and not self.component(target, kind):
            raise _fail("TARGET_NOT_FOUND", "no %s on %s" % (kind, target.name))
        method = args.get("methodName")
        source = strip_comments(self.scripts.get((self.classes.get(kind) or {}).get("path"), ""))
        if not re.search(r"\bpublic\s+void\s+%s\s*\(\s*\)" % re.escape(str(method)), source):
            raise _fail("METHOD_NOT_FOUND", "%s.%s() not found" % (kind, method))
        rows = self.listeners.setdefault(key, [])
        row = {"index": len(rows), "target": target.name, "targetType": kind or "GameObject",
               "targetEntityId": target.eid, "targetInstanceId": target.iid, "targetPath": self.path(target),
               "targetAssetPath": None, "method": method, "state": args.get("mode") or "RuntimeOnly"}
        rows.append(row)
        self._change(agent, self._identity(obj, kind="property_changed", targetType=key[1],
                                           property="m_OnClick.m_PersistentCalls", previousValue="", value=""))
        return dict({"success": True, "message": "listener added", "mode": row["state"]}, **row)

    def _skill_event_get_listeners(self, agent, args):
        obj, key = self._event(args)
        rows = self.listeners.get(key, [])
        return {"success": True, "gameObject": obj.name, "component": key[1], "eventName": key[2],
                "listenerCount": len(rows), "listeners": [dict(row) for row in rows]}

    def click(self, button):
        """Button.OnSubmit: nothing when the button is inactive in the hierarchy, else the persistent listeners that
        fire at runtime, then the runtime listeners added in Play Mode."""
        if not self.active_in_hierarchy(button):
            return
        for row in self.listeners.get((button.eid, "Button", "onClick"), []):
            target = self.objects.get(row["targetEntityId"])
            if row["state"] == "Off" or target is None or not self.component(target, row["targetType"]):
                continue
            self.invoke_model(target, row["targetType"], row["method"], [])
        for target, cls, method in self.runtime_listeners.get(button.eid, []):
            self.invoke_model(target, cls, method, [])

    # ------------------------------------------------------------ Play Mode

    def _skill_component_invoke_method(self, agent, args):
        if (args.get("executionMode") or "play") == "play" and not self.playing:
            raise _fail("SEMANTIC_INVALID", "executionMode does not match the current Editor mode")
        kind, method = args.get("componentType"), args.get("methodName")
        if args.get("isStatic"):
            if kind in ("UnityEngine.Time", "Time") and method == "get_timeScale":
                return {"returnValue": self.time_scale, "signature": "get_timeScale()"}
            if kind in ("UnityEngine.Time", "Time") and method == "set_timeScale":
                self.time_scale = float(json.loads(args.get("arguments") or "[1]")[0])
                return {"returnValue": None, "signature": "set_timeScale(System.Single)"}
            raise _fail("SEMANTIC_INVALID", "the model knows Time.timeScale only")
        obj = self.find(args)
        if kind in ("Button", "UnityEngine.UI.Button") and method == "OnSubmit":
            if not self.component(obj, "Button"):
                raise _fail("COMPONENT_NOT_FOUND", "no Button on %s" % obj.name)
            self.click(obj)
            return {"returnValue": None, "signature": "OnSubmit(UnityEngine.EventSystems.BaseEventData)"}
        if kind not in self.classes or not self.component(obj, kind):
            raise _fail("SEMANTIC_INVALID", "Type not found: %s" % kind)
        arguments = json.loads(args.get("arguments") or "[]")
        wanted = json.loads(args.get("parameterTypes")) if args.get("parameterTypes") else None
        value = self.invoke_model(obj, kind, method, arguments, wanted)
        return {"returnValue": value, "signature": "%s(...)" % method}

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        """Hook: the behaviour of a task class's method; raise _fail for an unbindable call."""
        raise _fail("SEMANTIC_INVALID", "the model has no %s.%s" % (cls, method))

    def enter_play(self):
        """Hook: Awake / OnEnable / Start of the task classes."""

    def tick(self, seconds):
        """Hook: time passes in Play Mode."""

    def play_state(self):
        return None

    def restore_play_state(self, saved):
        """Hook: put back what enter_play and the steps changed besides the objects."""

    def run_scenario(self, spec, agent=RUNNER):
        """editor_run_scenario's job result on this model: steps in order, the first refused step or failed assert ends
        it, `passed` needs every assert to pass and no runtime error; the scene is put back afterwards."""
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        saved = copy.deepcopy((self.objects, self.roots))
        extra = self.play_state()
        self.playing = spec.get("executionMode", "play") == "play"
        self.time_scale, self.runtime_error, self.runtime_listeners = 1.0, False, {}
        try:
            if self.playing:
                self.enter_play()
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    self.tick(float(step.get("seconds") or 0) if kind == "wait_seconds"
                              else int(step.get("frames") or 0) / 60.0)
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except SkillError as exc:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": exc.payload, "passed": False})
                        passed, error = False, "Step refused or failed; inspect its actual response."
                        break
                if kind in ("assert", "wait_condition"):
                    selected = _select(actual, step.get("select"))
                    ok = _matches(selected, step.get("expected"), float(step.get("tolerance") or 0))
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                    "expected": step.get("expected"), "actual": selected, "passed": ok})
                    if not ok:
                        passed, error = False, "Assertion failed."
                        break
                else:
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"), "actual": actual,
                                    "passed": True})
                last = actual
            if passed and self.runtime_error:
                passed, error = False, "Runtime error logs were captured."
        finally:
            self.playing = False
            self.objects, self.roots = saved
            self.restore_play_state(extra)
            self.time_scale = 1.0
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}


def outer_level_with_heads(body):
    """A class body with member bodies emptied, keeping each `{` so method heads stay recognisable."""
    out, depth = [], 0
    for char in body:
        if char == "{":
            if depth == 0:
                out.append("{")
            depth += 1
            continue
        if char == "}":
            depth -= 1
            if depth == 0:
                out.append("}")
            continue
        if depth == 0:
            out.append(char)
    return "".join(out)


# ---------------------------------------------------------------- DC5: pause menu


class FakeEditorDC5(FakeEditorDC):
    """PauseController_*: TogglePause / Resume over Time.timeScale and the GameObject the class's GameObject field
    references (whatever the field is called). Variants: paused-start, timescale-only, stale-flag, runtime-listener,
    no-panel-ref (same as the plain behaviour; the field is simply left empty)."""

    def __init__(self, *args, **kwargs):
        FakeEditorDC.__init__(self, *args, **kwargs)
        self.paused = {}
        self.runtime_listeners = {}

    def _panel(self, obj, cls):
        for kind, name, _default, _old in (self.classes.get(cls) or {}).get("fields", ()):
            value = obj.fields.get(cls, {}).get(name)
            if kind == "GameObject" and isinstance(value, tuple) and value[0] == "object":
                return self.objects.get(value[1])
        return None

    def _set_state(self, obj, cls, paused):
        variants = self.variants(cls)
        if not paused and "stale-flag" in variants:
            pass
        else:
            self.paused[(obj.eid, cls)] = paused
        self.time_scale = 0.0 if paused else 1.0
        panel = self._panel(obj, cls)
        if panel is not None and "timescale-only" not in variants:
            panel.active = paused

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        if not cls.startswith("PauseController_"):
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        if method == "TogglePause":
            self._set_state(obj, cls, not self.paused.get((obj.eid, cls), False))
        elif method == "Resume":
            self._set_state(obj, cls, False)
        else:
            raise _fail("SEMANTIC_INVALID", "no method %s" % method)
        return None

    def enter_play(self):
        self.paused = {}
        for obj in self.walk():
            for cls in [kind for kind, _enabled in obj.components if kind in self.classes]:
                variants = self.variants(cls)
                if "paused-start" in variants:
                    self._set_state(obj, cls, True)
                if "runtime-listener" in variants:
                    name = re.search(r'GameObject\.Find\("([^"]+)"\)', self.scripts[self.classes[cls]["path"]])
                    button = next((o for o in self.walk() if name and o.name == name.group(1)), None)
                    if button is not None:
                        self.runtime_listeners.setdefault(button.eid, []).append((obj, cls, "TogglePause"))

# Producer:Betsy
