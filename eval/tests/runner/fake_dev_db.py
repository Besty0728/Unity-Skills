"""In-memory editor for the DB1/DB3/DB5 offline tests: pack_fake_editor plus what the three dev/basic tasks touch.

Added to the base model: UGUI creation (ui_create_canvas; ui_create_button with a TextMeshProUGUI child, which the
plugin adds wherever TextMeshPro is present, as ui_create_text in the base model does), a UnityEngine.UI.Text added by
component_add and written by component_set_property (text, fontSize), persistent UnityEvent listeners
(event_add_listener / get_listeners / get_listener_count), script_read, Button.OnSubmit through component_invoke_method
under the namespace-qualified UnityEngine.UI.Button only (a bare Button also names UnityEngine.UIElements.Button, and a
plugin that does not prefer components for instance calls refuses it as ambiguous), Light / Text / Button serialized
reads, and a simulated clock: during the scenario stand-in wait_frames and wait_seconds advance game time and the
agent's components behave as their scripts say. A script without a marker behaves as the task asks (rotation by speed
x Time.deltaTime about local Y, a Light toggle every interval starting one interval in, a click counted into the
referenced Text); a `// usk-fake: <variant>` comment in the source selects a wrong behaviour the solutions' negatives
describe (per_frame, world_axis, toggle_on_start, half_period, runtime_listener, no_text_write). This models the
solutions, not C#.
"""
import math
import re

from .pack_fake_editor import RUNNER, _fail, _matches, _select, q_from_euler, q_inv, q_mul
from .pack_offline import OpsFakeEditor

FPS = 60.0
MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
TEXT_KINDS = ("Text", "UnityEngine.UI.Text")
TEXT_TYPE = "UnityEngine.UI.Text"
BUTTON_TYPE = "UnityEngine.UI.Button"
UI_ENABLED = ("Image", "Button", "Text", "TextMeshProUGUI")
TEXT_FONT_SIZE = 14


class FakeEditorDB(OpsFakeEditor):
    extra_reads = frozenset(("script_read", "event_get_listeners", "event_get_listener_count"))

    def __init__(self, *args, **kwargs):
        super(FakeEditorDB, self).__init__(*args, **kwargs)
        self.clock = 0.0
        self.listeners = {}          # (eid, component, event) -> [listener rows]
        self.texts = {}              # eid -> Text.text
        self.font_sizes = {}         # eid -> Text.fontSize
        self.counts = {}             # (eid, class) -> clicks counted at runtime
        self.runtime_error = False

    # ------------------------------------------------------------ ids and values

    def text_id(self, obj):
        base = int(obj.eid) if self.entity_mode == "6000.4" else obj.iid
        return str(base * 1000 + (1 if base > 0 else -1))

    def format_value(self, value):
        if isinstance(value, tuple) and value and value[0] == "object" and value[2] == "Text":
            obj = self.objects.get(value[1])
            return "null" if obj is None else "%s:UnityEngine.UI.Text:%s" % (self.text_id(obj), obj.name)
        return super(FakeEditorDB, self).format_value(value)

    def set_field(self, obj, cls, name, args):
        kind = self._field_type(cls, name)
        located = args.get("referencePath") or args.get("referenceEntityId") or \
            args.get("referenceInstanceId") not in (None, 0)
        if kind in ("Text", "UnityEngine.UI.Text") and located:
            target = self.find({"entityId": args.get("referenceEntityId"), "instanceId": args.get("referenceInstanceId"),
                                "path": args.get("referencePath")})
            if not self.component(target, "Text"):
                raise _fail("TARGET_NOT_FOUND", "no Text component on %s" % target.name)
            obj.fields[cls][name] = ("object", target.eid, "Text")
            return
        super(FakeEditorDB, self).set_field(obj, cls, name, args)

    # ------------------------------------------------------------ components

    def _skill_component_add(self, agent, args):
        if args.get("componentType") in TEXT_KINDS:
            return self._add_text(agent, self.find(args))
        res = super(FakeEditorDB, self)._skill_component_add(agent, args)
        if args["componentType"] == "Light":
            self.component(self.find(args), "Light")[1] = True
        return res

    def _add_text(self, agent, obj):
        """UnityEngine.UI.Text as Undo.AddComponent adds it: the RectTransform it requires replaces the Transform, a
        CanvasRenderer comes along and the text starts empty."""
        found = {"gameObject": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "component": "Text",
                 "fullTypeName": TEXT_TYPE}
        if self.component(obj, "Text"):
            return dict(found, warning="Component Text already exists on %s" % obj.name, alreadyPresent=True)
        if obj.components[0][0] == "Transform":
            obj.components[0] = ["RectTransform", None]
        if not self.component(obj, "CanvasRenderer"):
            obj.components.append(["CanvasRenderer", None])
        obj.components.append(["Text", True])
        self.owners[self.text_id(obj)] = obj.eid
        self.texts[obj.eid] = ""
        self._change(agent, self._identity(obj, kind="components_changed"))
        return dict(found, success=True)

    def _skill_component_set_property(self, agent, args):
        if args.get("componentType") not in TEXT_KINDS:
            return super(FakeEditorDB, self)._skill_component_set_property(agent, args)
        obj = self.find(args)
        if not self.component(obj, "Text"):
            raise _fail("TARGET_NOT_FOUND", "Component not found: %s" % args["componentType"])
        member, value = str(args.get("propertyName") or ""), args.get("value")
        if member.lower() == "text":
            store, path, now = self.texts, "m_Text", "" if value is None else str(value)
        elif member.lower() == "fontsize":
            store, path, now = self.font_sizes, "m_FontData.m_FontSize", int(value)
        else:
            raise _fail("PROPERTY_NOT_FOUND", "Text.%s is not modelled" % member)
        before, store[obj.eid] = store.get(obj.eid), now
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Text", property=path,
                                           previousValue="" if before is None else str(before), value=str(now)))
        return {"success": True, "gameObject": obj.name, "component": "Text", "property": member,
                "valueSet": str(now), "fullTypeName": TEXT_TYPE}

    def _serialized(self, obj, kind):
        if kind == "Light" and self.component(obj, "Light"):
            return {"m_Enabled": "True" if self.component(obj, "Light")[1] else "False", "m_Type": "2"}
        if kind in TEXT_KINDS and self.component(obj, "Text"):
            return {"m_Text": self.texts.get(obj.eid, ""),
                    "m_FontData.m_FontSize": str(self.font_sizes.get(obj.eid, TEXT_FONT_SIZE))}
        if kind == "Button" and self.component(obj, "Button"):
            key = (obj.eid, "Button", "onClick")
            return {"m_Interactable": "True",
                    "m_OnClick.m_PersistentCalls.m_Calls.Array.size": str(len(self.listeners.get(key, [])))}
        return super(FakeEditorDB, self)._serialized(obj, kind)

    # ------------------------------------------------------------ UGUI

    def _ui_object(self, agent, name, parent, components, text=None):
        item = {"name": name}
        if parent is not None:
            item["parentEntityId"] = parent.eid
        res = self._skill_gameobject_create(agent, item)
        obj = self.objects[res["entityId"]]
        obj.components += [[kind, True if kind in UI_ENABLED else None] for kind in components]
        self.owners[self.text_id(obj)] = obj.eid
        if text is not None:
            self.texts[obj.eid] = text
        return obj

    def _parent_canvas(self, args):
        parent = args.get("parent")
        if not parent:
            raise _fail("TARGET_NOT_FOUND", "the model needs an explicit parent")
        return self.find({"name": parent})

    def _skill_ui_create_canvas(self, agent, args):
        obj = self._ui_object(agent, args.get("name") or "Canvas", None, ("Canvas", "CanvasScaler", "GraphicRaycaster"))
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid,
                "renderMode": args.get("renderMode") or "ScreenSpaceOverlay"}

    def _skill_ui_create_button(self, agent, args):
        parent = self._parent_canvas(args)
        obj = self._ui_object(agent, args.get("name") or "Button", parent, ("Image", "Button"))
        self._ui_object(agent, "Text", obj, ("TextMeshProUGUI",))
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "parent": parent.name,
                "text": args.get("text") or "Button"}

    # ------------------------------------------------------------ events and scripts

    def _event(self, args):
        obj = self.find(args)
        component, event = args.get("componentName"), args.get("eventName")
        if not component or not self.component(obj, component):
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s on %s" % (component, obj.name))
        if not event:
            raise _fail("MISSING_PARAM", "eventName is required")
        return obj, (obj.eid, component, event)

    def _skill_event_add_listener(self, agent, args):
        obj, key = self._event(args)
        target = self.find({"entityId": args.get("targetEntityId"), "instanceId": args.get("targetInstanceId"),
                            "path": args.get("targetPath"), "name": args.get("targetObjectName")})
        kind = args.get("targetComponentName")
        if kind and not self.component(target, kind):
            raise _fail("TARGET_NOT_FOUND", "no %s on %s" % (kind, target.name))
        rows = self.listeners.setdefault(key, [])
        row = {"index": len(rows), "target": target.name, "targetType": kind or "GameObject",
               "targetEntityId": target.eid, "targetInstanceId": target.iid, "targetPath": self.path(target),
               "targetAssetPath": None, "method": args.get("methodName"), "state": args.get("mode") or "RuntimeOnly"}
        rows.append(row)
        self._change(agent, self._identity(obj, kind="property_changed", targetType=key[1],
                                           property="m_OnClick.m_PersistentCalls", previousValue="", value=""))
        return dict({"success": True, "message": "listener added", "mode": row["state"]}, **row)

    def _skill_event_get_listeners(self, agent, args):
        obj, key = self._event(args)
        rows = self.listeners.get(key, [])
        return {"success": True, "gameObject": obj.name, "component": key[1], "eventName": key[2],
                "listenerCount": len(rows), "listeners": [dict(row) for row in rows]}

    def _skill_event_get_listener_count(self, agent, args):
        _obj, key = self._event(args)
        return {"success": True, "count": len(self.listeners.get(key, []))}

    def _skill_script_read(self, agent, args):
        path = args.get("scriptPath")
        if path not in self.scripts:
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        text = self.scripts[path]
        return {"path": path, "lines": text.count("\n") + 1, "content": text}

    def _skill_component_invoke_method(self, agent, args):
        obj = self.find(args)
        if args.get("componentType") == "Button":
            raise _fail("SEMANTIC_INVALID", "Ambiguous type; use an assembly-qualified name: Button")
        if args.get("componentType") == BUTTON_TYPE and args.get("methodName") == "OnSubmit":
            if not self.component(obj, "Button"):
                raise _fail("COMPONENT_NOT_FOUND", "no Button on %s" % obj.name)
            self._click(obj)
            return {"success": True, "status": "completed", "returnValue": None}
        raise _fail("TARGET_NOT_FOUND", "the model invokes %s.OnSubmit only" % BUTTON_TYPE)

    # ------------------------------------------------------------ behaviour models

    def _source(self, cls):
        spec = self.classes.get(cls) or {}
        return self.scripts.get(spec.get("path"), "")

    def _variants(self, cls):
        return set(MARKER_RE.findall(self._source(cls)))

    def _components(self, prefix):
        for obj in list(self.objects.values()):
            for kind, _enabled in obj.components:
                if kind.startswith(prefix) and kind in obj.fields:
                    yield obj, kind

    def _click(self, button):
        """Button.OnSubmit: the persistent onClick listeners that fire at runtime, plus the listener a component on
        the button added itself at Start (the runtime_listener variant)."""
        for row in self.listeners.get((button.eid, "Button", "onClick"), []):
            target = self.objects.get(row["targetEntityId"])
            if row["state"] == "Off" or target is None or row["targetType"] not in target.fields:
                continue
            self._invoke(target, row["targetType"], row["method"])
        for cls in [kind for kind, _enabled in button.components if kind in button.fields]:
            if "runtime_listener" in self._variants(cls):
                self._invoke(button, cls, "OnClick")

    def _invoke(self, target, cls, method):
        """OnClick counts one click and writes the count into every assigned Text field of the component; an
        unassigned Text field is a NullReferenceException in Unity. Any other method does nothing."""
        if method != "OnClick":
            return
        key = (target.eid, cls)
        self.counts[key] = self.counts.get(key, 0) + 1
        if "no_text_write" in self._variants(cls):
            return
        for kind, name, _default, _old in (self.classes.get(cls) or {}).get("fields", ()):
            if kind not in TEXT_KINDS:
                continue
            value = target.fields[cls].get(name)
            if not (isinstance(value, tuple) and value[0] == "object" and self.objects.get(value[1])):
                self.runtime_error = True
                continue
            self.texts[value[1]] = str(self.counts[key])

    def _enter_play(self):
        self.clock = 0.0
        self.counts = {}
        self.runtime_error = False
        for obj, cls in self._components("Blinker_"):
            if "toggle_on_start" in self._variants(cls):
                self._flip(obj)

    def _flip(self, obj):
        light = self.component(obj, "Light")
        if light is not None:
            light[1] = not light[1]

    def _tick(self, seconds):
        before, self.clock = self.clock, self.clock + seconds
        frames = int(round(seconds * FPS))
        for obj, cls in self._components("Spinner_"):
            variants = self._variants(cls)
            speed = float(obj.fields[cls].get("degreesPerSecond") or 0.0)
            turn = speed * frames if "per_frame" in variants else speed * seconds
            step = q_from_euler((0.0, turn, 0.0))
            if "world_axis" in variants:
                parent = self.world(obj.parent)[1] if obj.parent is not None else (0.0, 0.0, 0.0, 1.0)
                obj.rot = q_mul(q_mul(q_mul(q_inv(parent), step), parent), obj.rot)
            else:
                obj.rot = q_mul(obj.rot, step)
        for obj, cls in self._components("Blinker_"):
            period = float(obj.fields[cls].get("interval") or 0.0)
            if "half_period" in self._variants(cls):
                period *= 0.5
            if period <= 0:
                continue
            flips = int(math.floor(self.clock / period + 1e-9)) - int(math.floor(before / period + 1e-9))
            for _ in range(flips):
                self._flip(obj)

    # ------------------------------------------------------------ scenario stand-in with time

    def run_scenario(self, spec, agent=RUNNER):
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        self.playing = spec.get("executionMode", "play") == "play"
        self._enter_play()
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    self._tick(float(step.get("seconds") or 0) if kind == "wait_seconds"
                               else int(step.get("frames") or 0) / FPS)
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except Exception as exc:                 # SkillError payloads, like the plugin's refusals
                        payload = getattr(exc, "payload", None) or {"error": str(exc)}
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": payload, "passed": False})
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
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

# Producer:Betsy
