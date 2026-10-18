"""Fake editor model for DS2 (trigger-activated sliding door with a kinematic Rigidbody player and a non-Player
visitor): subclasses OpsFakeEditor (and shares DevFakeEditor's clock-based scenario style) without modifying any
shared test file.
"""
import copy
import math
import re

from .pack_fake_editor import CLASS_RE, RUNNER, _fail, _g9, _matches, _numbers, _select, euler_from_q
from .pack_offline import OpsFakeEditor
from usk_eval import verifylib  # noqa: E402
from usk_eval.rest import SkillError  # noqa: E402

MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
ARRAY_FIELD_RE = re.compile(r"public\s+([\w.]+)\s*\[\s*\]\s+(\w+)\s*(?:=\s*([^;\n]+))?;")
VEC3_DEFAULT_RE = re.compile(r"new\s+Vector3\(\s*([+-]?[\d.]+)f?\s*,\s*([+-]?[\d.]+)f?\s*,\s*([+-]?[\d.]+)f?\s*\)")
FRAME_S = 0.02


def _move_towards(cur, goal, max_delta):
    dx, dy, dz = goal[0] - cur[0], goal[1] - cur[1], goal[2] - cur[2]
    dist = math.sqrt(dx * dx + dy * dy + dz * dz)
    if dist <= max_delta or dist <= 1e-9:
        return tuple(goal)
    scale = max_delta / dist
    return (cur[0] + dx * scale, cur[1] + dy * scale, cur[2] + dz * scale)


def _as_bool(val, fallback=False):
    if isinstance(val, bool):
        return val
    if isinstance(val, (int, float)):
        return bool(val)
    if isinstance(val, str):
        low = val.strip().lower()
        if low in ("true", "1"):
            return True
        if low in ("false", "0"):
            return False
    return fallback


class FakeEditorDS2(OpsFakeEditor):
    extra_reads = frozenset()

    def __init__(self, *args, **kwargs):
        self.variants = {}
        self.clock = 0.0
        OpsFakeEditor.__init__(self, *args, **kwargs)

    def _create(self, agent, spec, parent):
        obj = OpsFakeEditor._create(self, agent, spec, parent)
        if self.component(obj, "BoxCollider"):
            obj.fields.setdefault("BoxCollider", {"m_IsTrigger": False, "isTrigger": False})
        return obj

    def _skill_script_create(self, agent, args):
        out = OpsFakeEditor._skill_script_create(self, agent, args)
        source = args.get("content") or ""
        marker = MARKER_RE.search(source)
        for match in CLASS_RE.finditer(source):
            if marker:
                self.variants[match.group(1)] = marker.group(1)
        return out

    def _skill_script_replace(self, agent, args):
        out = OpsFakeEditor._skill_script_replace(self, agent, args)
        source = self.scripts.get(args.get("scriptPath"), "")
        marker = MARKER_RE.search(source)
        for match in CLASS_RE.finditer(source):
            if marker:
                self.variants[match.group(1)] = marker.group(1)
        return out

    def _compile(self):
        array_snap = {}
        for obj in self.walk():
            for cls, fields in list(obj.fields.items()):
                arr = {k: v for k, v in fields.items() if ".Array." in k}
                if arr:
                    array_snap[(obj.eid, cls)] = arr
        OpsFakeEditor._compile(self)
        if self.compile_errors:
            return
        for _path, source in sorted(self.scripts.items()):
            for match in CLASS_RE.finditer(source):
                cls = match.group(1)
                if cls in self.classes:
                    existing = {name for _k, name, _d, _o in self.classes[cls]["fields"]}
                    for kind, name, _default in ARRAY_FIELD_RE.findall(source[match.end():]):
                        if name not in existing:
                            self.classes[cls]["fields"].append((kind + "[]", name, None, None))
        for (eid, cls), arr in array_snap.items():
            obj = self.objects.get(eid)
            if obj is not None and cls in obj.fields:
                obj.fields[cls].update(arr)

    def _parse_value(self, kind, text):
        if kind == "Vector3":
            m = VEC3_DEFAULT_RE.search(str(text or ""))
            if m:
                return _g9((float(m.group(1)), float(m.group(2)), float(m.group(3))))
            nums = verifylib.floats(text)
            if len(nums) == 3:
                return _g9(nums)
            return "0,0,0"
        return OpsFakeEditor._parse_value(self, kind, text)

    def _skill_component_add(self, agent, args):
        out = OpsFakeEditor._skill_component_add(self, agent, args)
        obj = self.find(args)
        kind = args["componentType"]
        if kind == "Rigidbody":
            obj.fields["Rigidbody"] = {"m_IsKinematic": False, "isKinematic": False,
                                       "m_UseGravity": True, "useGravity": True}
        elif kind == "BoxCollider":
            obj.fields["BoxCollider"] = {"m_IsTrigger": False, "isTrigger": False}
        return out

    def _set_builtin_Or_array(self, obj, comp_type, prop, args):
        low = comp_type.lower()
        if low == "boxcollider" and self.component(obj, "BoxCollider"):
            fields = obj.fields.setdefault("BoxCollider", {"m_IsTrigger": False, "isTrigger": False})
            if prop in ("isTrigger", "m_IsTrigger"):
                b = _as_bool(args.get("value"), False)
                fields["isTrigger"] = b
                fields["m_IsTrigger"] = b
                return True
        if low == "rigidbody" and self.component(obj, "Rigidbody"):
            fields = obj.fields.setdefault("Rigidbody", {"m_IsKinematic": False, "isKinematic": False,
                                                         "m_UseGravity": True, "useGravity": True})
            if prop in ("isKinematic", "m_IsKinematic"):
                b = _as_bool(args.get("value"), False)
                fields["isKinematic"] = b
                fields["m_IsKinematic"] = b
                return True
            if prop in ("useGravity", "m_UseGravity"):
                b = _as_bool(args.get("value"), True)
                fields["useGravity"] = b
                fields["m_UseGravity"] = b
                return True
        if comp_type in obj.fields and ".Array." in prop:
            if args.get("referenceEntityId") or args.get("referenceInstanceId") not in (None, 0) or args.get("referencePath"):
                target = self.find({"entityId": args.get("referenceEntityId"),
                                    "instanceId": args.get("referenceInstanceId"),
                                    "path": args.get("referencePath")})
                obj.fields[comp_type][prop] = ("object", target.eid,
                                               "Transform" if args.get("objectType") == "Transform" else "GameObject")
            else:
                obj.fields[comp_type][prop] = str(args.get("value"))
            return True
        return False

    def _skill_component_set_property(self, agent, args):
        obj = self.find(args)
        comp_type, prop = args["componentType"], args["propertyName"]
        if self._set_builtin_Or_array(obj, comp_type, prop, args):
            self._change(agent, self._identity(obj, kind="property_changed", targetType=comp_type,
                                               property=prop, previousValue="", value=str(args.get("value"))))
            return {"success": True}
        return OpsFakeEditor._skill_component_set_property(self, agent, args)

    def _skill_component_set_serialized_property(self, agent, args):
        obj = self.find(args)
        comp_type, path = args["componentType"], args["propertyPath"]
        if self._set_builtin_Or_array(obj, comp_type, path, args):
            self._change(agent, self._identity(obj, kind="property_changed", targetType=comp_type,
                                               property=path, previousValue="", value=str(args.get("value"))))
            return {"success": True}
        return OpsFakeEditor._skill_component_set_serialized_property(self, agent, args)

    def _serialized(self, obj, kind):
        low = kind.lower()
        if low == "boxcollider" and self.component(obj, "BoxCollider"):
            fields = obj.fields.get("BoxCollider") or {"m_IsTrigger": False, "isTrigger": False}
            return {k: self.format_value(v) for k, v in fields.items()}
        if low == "rigidbody" and self.component(obj, "Rigidbody"):
            fields = obj.fields.get("Rigidbody") or {"m_IsKinematic": False, "isKinematic": False,
                                                     "m_UseGravity": True, "useGravity": True}
            return {k: self.format_value(v) for k, v in fields.items()}
        return OpsFakeEditor._serialized(self, obj, kind)

    def _skill_component_get_properties(self, agent, args):
        obj = self.find(args)
        low = args["componentType"].lower()
        if low == "boxcollider" and self.component(obj, "BoxCollider"):
            fields = obj.fields.get("BoxCollider") or {"isTrigger": False}
            return {"gameObject": obj.name, "component": "BoxCollider",
                    "properties": [{"name": "isTrigger", "value": self.format_value(bool(fields.get("isTrigger")))}]}
        if low == "rigidbody" and self.component(obj, "Rigidbody"):
            fields = obj.fields.get("Rigidbody") or {"isKinematic": False, "useGravity": True}
            return {"gameObject": obj.name, "component": "Rigidbody",
                    "properties": [{"name": "isKinematic", "value": self.format_value(bool(fields.get("isKinematic")))},
                                   {"name": "useGravity", "value": self.format_value(bool(fields.get("useGravity")))}]}
        return OpsFakeEditor._skill_component_get_properties(self, agent, args)

    # ------------------------------------------------------------ Play Mode simulation

    def run_scenario(self, spec, agent=RUNNER):
        steps = spec.get("steps") or []
        records, passed, error, last = [], True, None, None
        want_play = spec.get("executionMode", "play") == "play"
        self.playing = want_play and not bool(self.compile_errors)
        self.clock = 0.0
        saved_state = [(obj, obj.pos, obj.rot, obj.scale, copy.deepcopy(obj.fields)) for obj in self.walk()]
        sim = self._init_sim() if self.playing else None
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    delta = float(step.get("seconds") or 0) if kind == "wait_seconds" \
                        else int(step.get("frames") or 0) * FRAME_S
                    remaining = max(0.0, delta)
                    while remaining > 1e-9:
                        tick = min(FRAME_S, remaining)
                        self.clock += tick
                        if sim is not None:
                            self._step_sim(sim, tick)
                        remaining -= tick
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
        finally:
            self.playing = False
            for obj, pos, rot, scale, fields in saved_state:
                obj.pos, obj.rot, obj.scale, obj.fields = pos, rot, scale, fields
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

    def _ref_obj(self, ref_val):
        if isinstance(ref_val, tuple) and len(ref_val) >= 2 and ref_val[0] in ("object", "component"):
            return self.objects.get(ref_val[1])
        return None

    def _init_sim(self):
        walkers, doors = [], []
        for obj in self.walk():
            for cls, fields in list(obj.fields.items()):
                if cls.startswith("PathMover_") and self.component(obj, cls):
                    size = verifylib.as_int(fields.get("waypoints.Array.size")) or 0
                    wps = []
                    for i in range(size):
                        wp = self._ref_obj(fields.get("waypoints.Array.data[%d]" % i))
                        if wp is not None:
                            wps.append(wp)
                    walkers.append({"obj": obj, "wps": wps,
                                    "speed": float(fields.get("speed") or 4.0),
                                    "pause": float(fields.get("pauseSeconds") or 0.5),
                                    "next": 1, "waited": -1.0})
                elif cls.startswith("TriggerDoor_") and self.component(obj, cls):
                    door_obj = self._ref_obj(fields.get("door"))
                    closed = tuple(door_obj.pos) if door_obj is not None else (0.0, 0.0, 0.0)
                    off = verifylib.floats(fields.get("openOffset"))
                    offset = (off[0], off[1], off[2]) if len(off) == 3 else (0.0, 1.0, 0.0)
                    doors.append({"holder": obj, "cls": cls, "fields": fields,
                                  "door": door_obj, "closed": closed, "offset": offset,
                                  "speed": float(fields.get("speed") or 2.0),
                                  "variant": self.variants.get(cls),
                                  "inside_ids": set(), "inside_count": 0})
        return {"walkers": walkers, "doors": doors}

    def _zone_contains(self, zone_obj, mover_obj):
        if zone_obj.parent is not None and zone_obj.parent is mover_obj.parent:
            zx, zy, zz = zone_obj.pos
            mx, my, mz = mover_obj.pos
        else:
            zx, zy, zz = self.world(zone_obj)[0]
            mx, my, mz = self.world(mover_obj)[0]
        sx, sy, sz = zone_obj.scale
        rx, ry, rz = abs(sx) * 0.5 + 0.5, abs(sy) * 0.5 + 1.0, abs(sz) * 0.5 + 0.5
        return abs(mx - zx) <= rx and abs(my - zy) <= ry and abs(mz - zz) <= rz

    def _step_sim(self, sim, dt):
        for w in sim["walkers"]:
            wps = w["wps"]
            if not wps:
                continue
            obj = w["obj"]
            if w["waited"] >= 0.0:
                w["waited"] += dt
                if w["waited"] < w["pause"] - 1e-9:
                    continue
                w["waited"] = -1.0
                w["next"] = (w["next"] + 1) % len(wps)
            goal_obj = wps[w["next"] % len(wps)]
            if obj.parent is not None and obj.parent is goal_obj.parent:
                nxt = _move_towards(obj.pos, goal_obj.pos, w["speed"] * dt)
                obj.pos = nxt
                dist = math.sqrt(sum((a - b) ** 2 for a, b in zip(obj.pos, goal_obj.pos)))
            else:
                cur = self.world(obj)[0]
                goal = self.world(goal_obj)[0]
                nxt = _move_towards(cur, goal, w["speed"] * dt)
                self._set_world(obj, pos=nxt)
                dist = math.sqrt(sum((a - b) ** 2 for a, b in zip(self.world(obj)[0], goal)))
            if dist <= 1e-6:
                w["waited"] = 0.0

        for d in sim["doors"]:
            holder = d["holder"]
            box_fields = holder.fields.get("BoxCollider") or {}
            if not self.component(holder, "BoxCollider") or not _as_bool(box_fields.get("isTrigger"), False):
                continue
            fields = d["fields"]
            variant = d["variant"]
            for w in sim["walkers"]:
                mover = w["obj"]
                has_rb = bool(self.component(mover, "Rigidbody") or self.component(holder, "Rigidbody"))
                if not has_rb:
                    continue
                if self.component(mover, "Rigidbody"):
                    rb_fields = mover.fields.get("Rigidbody") or {}
                    if _as_bool(rb_fields.get("useGravity"), True) and not _as_bool(rb_fields.get("isKinematic"), False):
                        mover.pos = (mover.pos[0], mover.pos[1] - 9.81 * dt, mover.pos[2])
                in_zone = self._zone_contains(holder, mover)
                was_in = mover.eid in d["inside_ids"]
                tag_ok = (mover.tag == "Player") or (variant == "no-tag-check")
                if in_zone and not was_in:
                    d["inside_ids"].add(mover.eid)
                    if tag_ok:
                        en = int(fields.get("enterCount") or 0)
                        if variant == "once-only":
                            if en == 0:
                                fields["enterCount"] = 1
                                d["inside_count"] = 1
                        elif variant == "no-count":
                            d["inside_count"] += 1
                        else:
                            fields["enterCount"] = en + 1
                            d["inside_count"] += 1
                elif not in_zone and was_in:
                    d["inside_ids"].discard(mover.eid)
                    if tag_ok:
                        ex = int(fields.get("exitCount") or 0)
                        if variant == "once-only":
                            if ex == 0 and d["inside_count"] > 0:
                                fields["exitCount"] = 1
                                d["inside_count"] = 0
                        elif variant == "no-close":
                            fields["exitCount"] = ex + 1
                        elif variant == "no-count":
                            d["inside_count"] = max(0, d["inside_count"] - 1)
                        else:
                            fields["exitCount"] = ex + 1
                            d["inside_count"] = max(0, d["inside_count"] - 1)

            door_obj = d["door"]
            if door_obj is not None:
                closed = d["closed"]
                off = d["offset"]
                goal = (closed[0] + off[0], closed[1] + off[1], closed[2] + off[2]) if d["inside_count"] > 0 else closed
                door_obj.pos = _move_towards(door_obj.pos, goal, d["speed"] * dt)

# Producer:Betsy
