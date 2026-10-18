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
import math
import os
import re
import shutil

from .pack_fake_editor import RUNNER, _fail, _matches, _select
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
SERIALIZE_PRIVATE_RE = re.compile(r"(\[SerializeField\]\s*)(?:private|protected|internal)\s+")
GENERIC_RE = re.compile(r"\b(List)<(\w+)>")
ARRAY_RE = re.compile(r"\b(\w+)\[\]")
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
        self.bindings = {}           # (entityId, component class) -> GUID of the script the component uses

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
        self.scripts = {path: ARRAY_RE.sub(r"\1__Array", GENERIC_RE.sub(r"\1__\2", SERIALIZE_PRIVATE_RE.sub(
            r"\1public ", text))) for path, text in original.items()}
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
        """A component follows the GUID of its script: a moved or renamed file keeps it (the class inside may be
        renamed, fields carry over by name or FormerlySerializedAs), a file whose class does not match its name or a
        GUID that no file has any more leaves the component missing with its data kept, as Unity does."""
        by_guid = {self._guid(spec["path"]): cls for cls, spec in self.classes.items()
                   if os.path.basename(spec["path"])[:-len(".cs")] == cls}
        for obj in self.component_holders():
            for comp in obj.components:
                guid = self.bindings.get((obj.eid, comp[0]))
                if guid is None or by_guid.get(guid) == comp[0]:
                    continue
                now = by_guid.get(guid) or "Missing:" + guid
                values = obj.fields.pop(comp[0], {})
                del self.bindings[(obj.eid, comp[0])]
                self.bindings[(obj.eid, now)] = guid
                comp[0] = now
                obj.fields[now] = self.carry(now, values) if now in self.classes else values

    def component_holders(self):
        """Objects whose components follow script GUIDs (scene objects; DC3 adds prefab asset nodes)."""
        return list(self.objects.values())

    def carry(self, cls, values):
        """Field values of a component re-bound to `cls`: by name, else by FormerlySerializedAs, else the default."""
        return {name: values[name] if name in values else (values[old] if old and old in values else default)
                for _kind, name, default, old in self.classes[cls]["fields"]}

    def _skill_component_add(self, agent, args):
        out = OpsFakeEditor._skill_component_add(self, agent, args)
        kind = args["componentType"]
        if kind in self.classes:
            self.bindings[(self.find(args).eid, kind)] = self._guid(self.classes[kind]["path"])
        return out

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
        for path in out:
            if not os.path.exists(self.disk(path) + ".meta"):
                with open(self.disk(path) + ".meta", "w", encoding="utf-8") as handle:
                    handle.write("fileFormatVersion: 2\nguid: %s\n" % self._guid(path))
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


# ---------------------------------------------------------------- DC1: inventory stacking


class FakeEditorDC1(FakeEditorDC):
    """Inventory_*: maxStack / slotCount / stacks (a list of [item, count]) with Add / Remove / CountOf modelled from
    the specification; the fixture's untouched source (it still merges with `stack.count += count;`) behaves like
    the original. Variants: overflow-merge, negative-add, example-only, remove-silent, remove-front, event-per-stack.
    A Changed raise adds to `notifications` of every InventoryWatcher_* whose inventory field points at the object.
    Methods bind against the compiled source, so Add(string, uint) refuses a (string, int) call."""

    def _inventory(self, cls):
        return cls.startswith("Inventory_")

    def set_field(self, obj, cls, name, args):
        if not (self._inventory(cls) and name.startswith("stacks.")):
            return FakeEditorDC.set_field(self, obj, cls, name, args)
        stacks = obj.fields[cls].get("stacks")
        stacks = stacks if isinstance(stacks, list) else []
        if name == "stacks.Array.size":
            size = int(args.get("value"))
            stacks = stacks[:size] + [[None, 0] for _ in range(size - len(stacks))]
        else:
            match = re.match(r"^stacks\.Array\.data\[(\d+)\]\.(item|count)$", name)
            if not match or int(match.group(1)) >= len(stacks):
                raise _fail("PROPERTY_NOT_FOUND", "%s is not modelled" % name)
            row = stacks[int(match.group(1))]
            if match.group(2) == "item":
                row[0] = str(args.get("value"))
            else:
                row[1] = int(args.get("value"))
        obj.fields[cls]["stacks"] = stacks

    def _serialized(self, obj, kind):
        if not (self._inventory(kind) and kind in obj.fields and self.component(obj, kind)):
            return FakeEditorDC._serialized(self, obj, kind)
        values = obj.fields[kind]
        stacks = values.get("stacks") if isinstance(values.get("stacks"), list) else []
        out = {"maxStack": str(values.get("maxStack")), "slotCount": str(values.get("slotCount")),
               "stacks.Array.size": str(len(stacks))}
        for index, (item, count) in enumerate(stacks):
            out["stacks.Array.data[%d].item" % index] = item
            out["stacks.Array.data[%d].count" % index] = str(count)
        return out

    def _signature(self, cls, method):
        body = top_class_body(self.compiled_sources.get(self.classes[cls]["path"], ""), cls)
        for _ret, name, params in PUBLIC_METHOD_RE.findall(outer_level_with_heads(body)):
            if name == method:
                return [TYPE_NAMES.get(p.strip().split()[-2], p.strip().split()[-2])
                        for p in params.split(",") if p.strip()]
        return None

    def _behaviour(self, cls):
        source = self.compiled_sources.get(self.classes[cls]["path"], "")
        found = set(MARKER_RE.findall(source))
        return found or ({"original"} if "stack.count += count;" in source else set())

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        if not self._inventory(cls):
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        signature = self._signature(cls, method)
        if signature is None or (parameter_types is not None and signature != list(parameter_types)):
            raise _fail("SEMANTIC_INVALID", "No method %s(%s) on %s" % (method, parameter_types, cls))
        values = obj.fields[cls]
        stacks = values.get("stacks") if isinstance(values.get("stacks"), list) else []
        values["stacks"] = stacks
        variants = self._behaviour(cls)
        item = arguments[0]
        if method == "CountOf":
            return sum(count for name, count in stacks if name == item)
        count = int(arguments[1])
        size, slots = int(values.get("maxStack") or 0), int(values.get("slotCount") or 0)
        if method == "Add":
            value, raised = self._add(stacks, size, slots, item, count, variants)
        elif method == "Remove":
            value, raised = self._remove(stacks, item, count, variants)
        else:
            raise _fail("SEMANTIC_INVALID", "No method %s" % method)
        for holder in self.walk():
            for kind in [k for k, _enabled in holder.components if k.startswith("InventoryWatcher_")]:
                link = holder.fields.get(kind, {}).get("inventory")
                if isinstance(link, tuple) and link[1] == obj.eid:
                    holder.fields[kind]["notifications"] = int(holder.fields[kind].get("notifications") or 0) + raised
        return value

    @staticmethod
    def _add(stacks, size, slots, item, count, variants):
        """(return value, Changed raises) of Add, mutating stacks."""
        if "original" in variants:
            row = next((r for r in stacks if r[0] == item), None)
            if row is not None:
                row[1] += count
                return 0, 1
            if len(stacks) >= slots:
                return count, 0
            stacks.append([item, count])
            return 0, 1
        if count == 0 or (count < 0 and "negative-add" not in variants):
            return 0, 0
        if count < 0:
            cap = 10 if "example-only" in variants else size
            row = next((r for r in stacks if r[0] == item and r[1] < cap), None)
            if row is None:
                return count, 0
            row[1] += count
            return 0, 0
        if "overflow-merge" in variants:
            row = next((r for r in stacks if r[0] == item), None)
            if row is not None:
                row[1] += count
                return 0, 1
        cap = 10 if "example-only" in variants else size
        left, touched = count, 0
        for row in stacks:
            if left and row[0] == item and row[1] < cap:
                take = min(cap - row[1], left)
                row[1] += take
                left -= take
                touched += 1
        while left and len(stacks) < slots:
            take = min(cap, left)
            stacks.append([item, take])
            left -= take
            touched += 1
        if "event-per-stack" in variants:
            return left, touched
        return left, 1 if left < count else 0

    @staticmethod
    def _remove(stacks, item, count, variants):
        """(return value, Changed raises) of Remove, mutating stacks."""
        if "original" in variants:
            row = next((r for r in stacks if r[0] == item), None)
            if row is None or count <= 0:
                return 0, 0
            taken = min(count, row[1])
            row[1] -= taken
            if row[1] == 0:
                stacks.remove(row)
            return taken, 0
        if count <= 0:
            return 0, 0
        order = range(len(stacks)) if "remove-front" in variants else reversed(range(len(stacks)))
        left, touched, gone = count, 0, []
        for index in list(order):
            if left and stacks[index][0] == item:
                take = min(stacks[index][1], left)
                stacks[index][1] -= take
                left -= take
                touched += 1 if take else 0
                if stacks[index][1] == 0:
                    gone.append(index)
        for index in sorted(gone, reverse=True):
            del stacks[index]
        if "remove-silent" in variants:
            return count - left, 0
        if "event-per-stack" in variants:
            return count - left, touched
        return count - left, 1 if left < count else 0


# ---------------------------------------------------------------- DC7: assembly definitions


class FakeEditorDC7(FakeEditorDC):
    """Assembly definitions decide whether the scripts compile: a script's assembly is the nearest folder's .asmdef
    (or the .asmdef an .asmref there names), else Assembly-CSharp (Assembly-CSharp-Editor below a folder named
    Editor). A script that names a class of another assembly needs a reference to it (by name or GUID:<guid>);
    the predefined assemblies reference every auto-referenced .asmdef; nothing but an Editor-only assembly may
    reference an Editor-only one. Combat_*.power is hp x factor of the classes its Awake names, read from their
    compiled sources when Play Mode starts."""

    def _json(self, path):
        try:
            with open(self.disk(path), "r", encoding="utf-8-sig") as handle:
                data = json.load(handle)
        except (OSError, ValueError):
            return None
        return data if isinstance(data, dict) else None

    def _definitions(self):
        """{asmdef path: {name, guid, refs, editor, auto}} of every .asmdef on disk under Assets."""
        out = {}
        for base, dirs, files in os.walk(self.disk("Assets")):
            dirs.sort()
            for name in sorted(files):
                if name.endswith(".asmdef"):
                    path = os.path.relpath(os.path.join(base, name), self.project).replace(os.sep, "/")
                    data = self._json(path)
                    out[path] = None if data is None else {
                        "name": data.get("name"), "guid": self._guid(path),
                        "refs": [str(item) for item in data.get("references") or []],
                        "editor": data.get("includePlatforms") == ["Editor"],
                        "auto": data.get("autoReferenced", True) is not False}
        return out

    def _named(self, defs, reference):
        for path, info in defs.items():
            if info and (reference == info["name"] or (reference.startswith("GUID:") and reference[5:] == info["guid"])):
                return path
        return None

    def _assembly(self, path, defs):
        folder = os.path.dirname(path)
        while folder and folder != "Assets":
            names = sorted(os.listdir(self.disk(folder))) if os.path.isdir(self.disk(folder)) else []
            found = ["%s/%s" % (folder, n) for n in names if n.endswith(".asmdef")]
            if found:
                return found[0]
            refs = ["%s/%s" % (folder, n) for n in names if n.endswith(".asmref")]
            if refs:
                return self._named(defs, str((self._json(refs[0]) or {}).get("reference")))
            folder = os.path.dirname(folder)
        return "Assembly-CSharp-Editor" if "/Editor/" in "/%s/" % os.path.dirname(path) else "Assembly-CSharp"

    def _sees(self, user, owner, defs):
        """Whether code of assembly `user` may use types of assembly `owner`."""
        if user == owner:
            return True
        if owner not in defs or not defs[owner]:
            return False
        if user in ("Assembly-CSharp", "Assembly-CSharp-Editor"):
            return defs[owner]["auto"] and (user == "Assembly-CSharp-Editor" or not defs[owner]["editor"])
        info = defs.get(user)
        if not info:
            return False
        named = {self._named(defs, ref) for ref in info["refs"]}
        return owner in named and (info["editor"] or not defs[owner]["editor"])

    def compile_errors_for(self, sources):
        errors = set(FakeEditorDC.compile_errors_for(self, sources))
        defs = self._definitions()
        broken = {path for path, info in defs.items() if info is None}
        names = [info["name"] for info in defs.values() if info]
        broken |= {path for path, info in defs.items() if info and names.count(info["name"]) > 1}
        folders = [os.path.dirname(path) for path in defs]
        broken |= {path for path in defs if folders.count(os.path.dirname(path)) > 1}
        owners = {path: self._assembly(path, defs) for path in sources}
        declared = {}
        for path, source in sources.items():
            for cls in CLASS_DECL_RE.findall(strip_comments(source)):
                declared[cls] = path
        for path, source in sources.items():
            if owners[path] in broken:
                errors.add(path)
                continue
            text = strip_comments(source)
            for cls, home in declared.items():
                if home != path and re.search(r"\b%s\b" % re.escape(cls), text) and \
                        not self._sees(owners[path], owners[home], defs):
                    errors.add(path)
        return sorted(errors)

    def refresh(self, agent=RUNNER):
        out = FakeEditorDC.refresh(self, agent)
        self._compile()
        return out

    def _skill_asset_delete(self, agent, args):
        out = FakeEditorDC._skill_asset_delete(self, agent, args)
        for path in [p for p in list(self.scripts) if not os.path.exists(self.disk(p))]:
            del self.scripts[path]
        self._compile()
        return out

    def enter_play(self):
        for obj in self.walk():
            for cls in [kind for kind, _enabled in obj.components if kind in self.classes]:
                source = self.compiled_sources.get(self.classes[cls]["path"], "")
                match = re.search(r"new\s+(\w+)\(\)\.hp\s*\*\s*(\w+)\.factor", source)
                if not match or match.group(1) not in self.classes or match.group(2) not in self.classes:
                    continue
                hp = re.search(r"\bhp\s*=\s*(\d+)", self.compiled_sources.get(
                    self.classes[match.group(1)]["path"], ""))
                factor = re.search(r"\bfactor\s*=\s*(\d+)", self.compiled_sources.get(
                    self.classes[match.group(2)]["path"], ""))
                if hp and factor:
                    obj.fields[cls]["power"] = int(hp.group(1)) * int(factor.group(1))


# ---------------------------------------------------------------- DC3: rename and move with prefabs


class FakeEditorDC3(FakeEditorDC):
    """Prefab assets as detached object trees (base and variant), scene instances as copies of them; a component
    follows its script's GUID through moves and class renames (base model) on scene objects and prefab nodes alike,
    carrying field data by name or FormerlySerializedAs. Transform[] fields hold ("object", id, "Transform") lists.
    LapSeconds() is computed from the class's own return statement (its speed, pause and route fields) and the
    world positions of the linked objects; the variant no-pause drops the pause term."""

    def __init__(self, *args, **kwargs):
        FakeEditorDC.__init__(self, *args, **kwargs)
        self.assets = {}
        self.asset_nodes = {}

    # ------------------------------------------------------------ arrays

    def set_field(self, obj, cls, name, args):
        match = re.match(r"^(\w+)\.Array\.(?:size|data\[(\d+)\])$", name)
        if not match:
            return FakeEditorDC.set_field(self, obj, cls, name, args)
        field = match.group(1)
        if self._field_type(cls, field) is None:
            raise _fail("PROPERTY_NOT_FOUND", "%s has no field %s" % (cls, field))
        items = obj.fields[cls].get(field)
        items = list(items) if isinstance(items, list) else []
        if match.group(2) is None:
            size = int(args.get("value"))
            items = items[:size] + [None] * (size - len(items))
        else:
            index = int(match.group(2))
            if index >= len(items):
                raise _fail("PROPERTY_NOT_FOUND", "%s is out of range" % name)
            node = args.get("_node")
            if node is None:
                node = self.find({"instanceId": args.get("referenceInstanceId"), "path": args.get("referencePath"),
                                  "entityId": args.get("referenceEntityId")})
            items[index] = ("object", node.eid, "Transform")
        obj.fields[cls][field] = items

    def _serialized(self, obj, kind):
        if kind in self.classes and kind in obj.fields and self.component(obj, kind):
            out = {}
            for name, value in obj.fields[kind].items():
                if isinstance(value, list) or self._field_type(kind, name) == "Transform__Array":
                    items = value if isinstance(value, list) else []
                    out[name + ".Array.size"] = str(len(items))
                    for index, item in enumerate(items):
                        out["%s.Array.data[%d]" % (name, index)] = self.format_value(item)
                else:
                    out[name] = self.format_value(value)
            return out
        return FakeEditorDC._serialized(self, obj, kind)

    def format_value(self, value):
        if isinstance(value, tuple) and value and value[0] == "object" and value[1] in self.asset_nodes:
            node = self.asset_nodes[value[1]]
            return "%s:UnityEngine.Transform:%s" % (self.transform_id(node), node.name)
        return FakeEditorDC.format_value(self, value)

    # ------------------------------------------------------------ prefab assets and instances

    def component_holders(self):
        return list(self.objects.values()) + list(self.asset_nodes.values())

    def after_compile(self):
        for node in self.asset_nodes.values():
            for kind, _enabled in node.components:
                if kind in self.classes and kind in node.fields:
                    node.fields[kind] = self.carry(kind, node.fields[kind])
        FakeEditorDC.after_compile(self)

    def _copy(self, source, parent, scene, agent=None):
        """A copy of the tree at `source` with fresh ids, internal links remapped, script bindings kept."""
        mapping, made = {}, []

        def clone(node, owner):
            eid, iid = self._new_ids()
            copy_ = type(node)(eid, iid, node.name, owner)
            copy_.pos, copy_.rot, copy_.scale, copy_.active = node.pos, node.rot, node.scale, node.active
            copy_.components = [list(comp) for comp in node.components]
            copy_.fields = copy.deepcopy(node.fields)
            copy_.tag, copy_.layer = node.tag, node.layer
            mapping[node.eid] = eid
            made.append((node, copy_))
            copy_.children = [clone(child, copy_) for child in node.children]
            return copy_

        root = clone(source, parent)

        def relink(value):
            if isinstance(value, tuple) and value and value[0] == "object" and value[1] in mapping:
                return (value[0], mapping[value[1]]) + tuple(value[2:])
            if isinstance(value, list):
                return [relink(item) for item in value]
            return value

        for node, copy_ in made:
            for kind in copy_.fields:
                copy_.fields[kind] = {name: relink(value) for name, value in copy_.fields[kind].items()}
            for kind, _enabled in node.components:
                if (node.eid, kind) in self.bindings:
                    self.bindings[(copy_.eid, kind)] = self.bindings[(node.eid, kind)]
            if scene:
                self.objects[copy_.eid] = copy_
                self.owners[self.transform_id(copy_)] = copy_.eid
            else:
                self.asset_nodes[copy_.eid] = copy_
        if scene:
            self._siblings(root).append(root)
            for _node, copy_ in made:
                self._change(agent, self._identity(copy_, kind="gameobject_created"))
        return root

    def _node(self, root, path):
        node = root
        for part in [p for p in (path or "").split("/") if p]:
            node = next((child for child in node.children if child.name == part), None)
            if node is None:
                raise _fail("TARGET_NOT_FOUND", "Prefab node not found: %s" % path)
        return node

    def _save_asset(self, agent, path, root):
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self.assets[path] = root
        self.prefabs[path] = {"name": root.name, "mesh": None}
        self._write_file(path, "%%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: %s\n" % root.name)
        self._imported(agent, [path])

    def _skill_prefab_create(self, agent, args):
        obj = self.find(args)
        path = args["savePath"]
        self._save_asset(agent, path, self._copy(obj, None, False))
        return {"success": True, "prefabPath": path, "name": obj.name, "connected": True}

    def _skill_prefab_create_variant(self, agent, args):
        source = self.assets.get(args["sourcePrefabPath"])
        if source is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % args["sourcePrefabPath"])
        path = args["variantPath"]
        self._save_asset(agent, path, self._copy(source, None, False))
        return {"success": True, "sourcePath": args["sourcePrefabPath"], "variantPath": path, "isVariant": True}

    def _skill_prefab_set_serialized_property(self, agent, args):
        root = self.assets.get(args["prefabPath"])
        if root is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % args["prefabPath"])
        node = self._node(root, args.get("gameObjectPath"))
        kind = args["componentType"]
        if not self.component(node, kind) or kind not in node.fields:
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s" % kind)
        extra = dict(args)
        if args.get("referenceGameObjectPath"):
            extra["_node"] = self._node(root, args["referenceGameObjectPath"])
        self.set_field(node, kind, args["propertyPath"], extra)
        self._imported(agent, [args["prefabPath"]])
        return {"success": True, "prefabPath": args["prefabPath"]}

    def _skill_prefab_get_serialized_properties(self, agent, args):
        root = self.assets.get(args["prefabPath"])
        if root is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % args["prefabPath"])
        node = self._node(root, args.get("gameObjectPath"))
        kind = args["componentType"]
        if not self.component(node, kind):
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s" % kind)
        return {"success": True, "prefabPath": args["prefabPath"], "componentType": kind,
                "properties": [{"propertyPath": k, "value": v} for k, v in self._serialized(node, kind).items()]}

    def _skill_prefab_instantiate(self, agent, args):
        source = self.assets.get(args["prefabPath"])
        if source is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % args["prefabPath"])
        parent = self.find(args, "parent", required=False)
        root = self._copy(source, parent, True, agent)
        if args.get("name"):
            root.name = args["name"]
        root.pos = (float(args.get("x", 0)), float(args.get("y", 0)), float(args.get("z", 0)))
        return {"success": True, "name": root.name, "entityId": root.eid, "instanceId": root.iid,
                "path": self.path(root)}

    # ------------------------------------------------------------ behaviour

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        source = strip_comments(self.compiled_sources.get(self.classes[cls]["path"], ""))
        match = re.search(r"return\s+RouteLength\(\)\s*/\s*(\w+)(?:\s*\+\s*(\w+)\s*\*\s*(\w+)\.Length)?\s*;",
                          source)
        if method != "LapSeconds" or not match:
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        values = obj.fields[cls]
        route = next((name for kind, name, _d, _o in self.classes[cls]["fields"] if kind == "Transform__Array"), None)
        links = [self.objects.get(item[1]) for item in values.get(route) or [] if isinstance(item, tuple)]
        spots = [self.world(node)[0] for node in links if node is not None]
        length = sum(math.dist(spots[i - 1], spots[i]) for i in range(1, len(spots)))
        speed = float(values.get(match.group(1)) or 0.0)
        lap = length / speed if speed else float("inf")
        if match.group(2) and "no-pause" not in self.variants(cls):
            lap += float(values.get(match.group(2)) or 0.0) * len(values.get(route) or [])
        return lap

# Producer:Betsy
