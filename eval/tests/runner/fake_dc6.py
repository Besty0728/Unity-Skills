"""Offline fake editor for DC6 (coin pickups with trigger colliders and a score manager).

OpsFakeEditor plus:
  * the tag manager's layer names, the tags, project gravity and the layer collision matrix (a solver's change of the
    matrix is journaled against ProjectSettings/DynamicsManager.asset, as a settings file record),
  * gameobject_set_layer_batch, Rigidbody (isKinematic, useGravity) and collider isTrigger state,
  * Transform[] fields of compiled classes and fields typed as a compiled class (stored as component references),
  * batch_query_gameobjects rows with activeSelf,
  * a Play Mode model for editor_run_scenario: a clock in 0.02 s steps that moves every component with a waypoints
    array along it with MoveTowards (stopping at the last), fires a coin's OnTriggerEnter once per Player-side collider
    that starts to overlap it (only with a trigger, a Rigidbody on either side and colliding layers), and polls
    wait_condition steps until they match or the scenario times out. The coin behaviour follows the
    `// usk-fake: <variant>` marker of its class (none: a collected flag plus SetActive(false)).
"""
import copy
import json
import math
import re

from .pack_fake_editor import RUNNER, _fail, _matches, _select, q_inv, q_rotate
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

TAG_MANAGER = "ProjectSettings/TagManager.asset"
DYNAMICS = "ProjectSettings/DynamicsManager.asset"
MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
ARRAY_FIELD_RE = re.compile(r"public\s+([\w.]+)\s*\[\s*\]\s+(\w+)\s*;")
COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider")
TAGS = ["Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"]
FRAME_S = 0.02


def _flag(value, fallback=False):
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    return True if text in ("true", "1") else False if text in ("false", "0") else fallback


def _move_towards(cur, goal, step):
    delta = [g - c for c, g in zip(cur, goal)]
    dist = math.sqrt(sum(d * d for d in delta))
    if dist <= step or dist <= 1e-9:
        return tuple(goal)
    return tuple(c + d * step / dist for c, d in zip(cur, delta))


class FakeEditorDC6(OpsFakeEditor):
    extra_reads = frozenset(("physics_get_layer_collision", "editor_get_layers", "editor_get_tags",
                             "physics_get_gravity"))

    def __init__(self, *args, **kwargs):
        self.layer_names = {0: "Default", 1: "TransparentFX", 2: "Ignore Raycast", 4: "Water", 5: "UI"}
        self.collision = {}
        self.tags = list(TAGS)
        self.gravity = {"x": 0.0, "y": -9.81, "z": 0.0}
        self.clock = 0.0
        OpsFakeEditor.__init__(self, *args, **kwargs)

    # ------------------------------------------------------------ project settings

    def _skill_editor_get_layers(self, agent, args):
        return {"layers": [{"index": i, "name": n} for i, n in sorted(self.layer_names.items()) if n]}

    def _skill_editor_get_tags(self, agent, args):
        return {"tags": list(self.tags)}

    def _skill_physics_get_gravity(self, agent, args):
        return dict(self.gravity, success=True)

    @staticmethod
    def _layer_index(args, key):
        value = args.get(key)
        if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 31:
            raise _fail("INVALID_LAYER", "%s must be a layer index from 0 to 31" % key)
        return value

    def collides(self, first, second):
        return self.collision.get(tuple(sorted((first, second))), True)

    def _skill_physics_get_layer_collision(self, agent, args):
        first, second = self._layer_index(args, "layer1"), self._layer_index(args, "layer2")
        return {"layer1": first, "layer2": second, "collisionEnabled": self.collides(first, second)}

    def _skill_physics_set_layer_collision(self, agent, args):
        first, second = self._layer_index(args, "layer1"), self._layer_index(args, "layer2")
        enabled = bool(args.get("enableCollision", True))
        before = self.collides(first, second)
        self.collision[tuple(sorted((first, second)))] = enabled
        if agent != RUNNER and before != enabled:
            self._change(agent, {"kind": "property_changed", "targetType": "PhysicsManager",
                                 "property": "m_LayerCollisionMatrix", "assetPath": DYNAMICS,
                                 "previousValue": str(before), "value": str(enabled)})
        return {"success": True, "layer1": first, "layer2": second, "collisionEnabled": enabled}

    def _skill_scriptableobject_set_serialized_property(self, agent, args):
        if args.get("assetPath") == DYNAMICS:
            if agent != RUNNER:
                self._change(agent, {"kind": "property_changed", "targetType": "PhysicsManager",
                                     "property": args.get("propertyPath"), "assetPath": DYNAMICS,
                                     "previousValue": "", "value": str(args.get("value"))})
            return {"success": True, "assetPath": DYNAMICS, "propertyPath": args.get("propertyPath"),
                    "valueSet": str(args.get("value"))}
        if args.get("assetPath") != TAG_MANAGER:
            return OpsFakeEditor._skill_scriptableobject_set_serialized_property(self, agent, args)
        match = re.match(r"^layers\.Array\.data\[(\d+)\]$", args.get("propertyPath") or "")
        if not match or not 8 <= int(match.group(1)) <= 31:
            raise _fail("PROPERTY_NOT_FOUND", "Serialized property not found: %s" % args.get("propertyPath"))
        value = args.get("value")
        if value:
            self.layer_names[int(match.group(1))] = value
        else:
            self.layer_names.pop(int(match.group(1)), None)
        return {"success": True, "assetPath": TAG_MANAGER, "propertyPath": args["propertyPath"],
                "valueSet": value or ""}

    def layer_index(self, name):
        return next((i for i, n in self.layer_names.items() if n == name), None)

    def _skill_gameobject_set_layer_batch(self, agent, args):
        jobs = []
        for item in json.loads(args["items"]):
            obj = self.find(item)
            if self.layer_index(item.get("layer")) is None:
                raise _fail("LAYER_NOT_FOUND", "Layer not found: %s" % item.get("layer"))
            jobs.append((obj, item["layer"], bool(item.get("recursive"))))
        for obj, layer, recursive in jobs:
            for each in (self.walk([obj]) if recursive else [obj]):
                before, each.layer = each.layer, layer
                self._change(agent, self._identity(each, kind="property_changed", targetType="GameObject",
                                                   property="m_Layer", previousValue=before, value=layer))
        return {"success": True, "totalItems": len(jobs), "successCount": len(jobs), "failCount": 0,
                "results": [{"target": obj.name, "success": True, "layer": layer} for obj, layer, _r in jobs]}

    # ------------------------------------------------------------ built-in component state

    def _state(self, obj, kind):
        if kind == "Rigidbody":
            return obj.fields.setdefault("Rigidbody", {"isKinematic": False, "useGravity": True})
        return obj.fields.setdefault(kind, {"isTrigger": False})

    def _builtin(self, obj, kind, prop, value):
        """Writes a modelled Rigidbody or collider property; False when the property is not modelled."""
        name = prop[2].lower() + prop[3:] if prop.startswith("m_") else prop
        if kind == "Rigidbody" and name in ("isKinematic", "useGravity"):
            self._state(obj, kind)[name] = _flag(value)
            return True
        if kind in COLLIDERS and name == "isTrigger":
            self._state(obj, kind)[name] = _flag(value)
            return True
        return False

    def _canonical(self, kind):
        return next((k for k in ("Rigidbody",) + COLLIDERS if k.lower() == str(kind).lower()), None)

    def _skill_component_set_property(self, agent, args):
        kind = self._canonical(args.get("componentType"))
        if kind is None:
            return OpsFakeEditor._skill_component_set_property(self, agent, args)
        obj = self.find(args)
        if not self.component(obj, kind) or not self._builtin(obj, kind, args["propertyName"], args.get("value")):
            raise _fail("PROPERTY_NOT_FOUND", "%s.%s is not modelled" % (kind, args["propertyName"]))
        self._change(agent, self._identity(obj, kind="property_changed", targetType=kind,
                                           property=args["propertyName"], previousValue="",
                                           value=str(args.get("value"))))
        return {"success": True}

    def _skill_component_set_serialized_property(self, agent, args):
        kind = self._canonical(args.get("componentType"))
        if kind is None:
            return OpsFakeEditor._skill_component_set_serialized_property(self, agent, args)
        return self._skill_component_set_property(agent, dict(args, propertyName=args["propertyPath"]))

    def _serialized(self, obj, kind):
        canon = self._canonical(kind)
        if canon is not None and self.component(obj, canon):
            state = dict(self._state(obj, canon))
            return {"m_" + name[0].upper() + name[1:]: self.format_value(bool(value)) for name, value in state.items()}
        return OpsFakeEditor._serialized(self, obj, kind)

    def _skill_component_get_properties(self, agent, args):
        canon = self._canonical(args.get("componentType"))
        obj = self.find(args)
        if canon is not None and self.component(obj, canon):
            return {"gameObject": obj.name, "component": canon,
                    "properties": [{"name": name, "value": self.format_value(bool(value))}
                                   for name, value in self._state(obj, canon).items()]}
        return OpsFakeEditor._skill_component_get_properties(self, agent, args)

    # ------------------------------------------------------------ compiled classes

    def _compile(self):
        arrays = {}
        for obj in self.walk():
            for cls, fields in obj.fields.items():
                kept = {key: value for key, value in fields.items() if ".Array." in key}
                if kept:
                    arrays[(obj.eid, cls)] = kept
        OpsFakeEditor._compile(self)
        if self.compile_errors:
            return
        for (eid, cls), kept in arrays.items():
            obj = self.objects.get(eid)
            if obj is not None and cls in obj.fields:
                obj.fields[cls].update(kept)

    def set_field(self, obj, cls, name, args):
        refers = args.get("referenceEntityId") or args.get("referenceInstanceId") not in (None, 0) or \
            args.get("referencePath")
        if ".Array." in name:
            if refers:
                found = self.find({"entityId": args.get("referenceEntityId"),
                                   "instanceId": args.get("referenceInstanceId"), "path": args.get("referencePath")})
                obj.fields[cls][name] = ("object", found.eid, "Transform")
            else:
                obj.fields[cls][name] = str(args.get("value"))
            return
        kind = self._field_type(cls, name)
        if kind in self.classes and refers:
            found = self.find({"entityId": args.get("referenceEntityId"), "instanceId": args.get("referenceInstanceId"),
                               "path": args.get("referencePath")})
            if not self.component(found, kind):
                raise _fail("COMPONENT_NOT_FOUND", "Component '%s' not found on '%s'" % (kind, found.name))
            obj.fields[cls][name] = ("component", found.eid, kind)
            return
        OpsFakeEditor.set_field(self, obj, cls, name, args)

    def _skill_batch_query_gameobjects(self, agent, args):
        out = OpsFakeEditor._skill_batch_query_gameobjects(self, agent, args)
        for row in out.get("objects") or []:
            obj = self.objects.get(row["entityId"])
            row.update(activeSelf=obj.active, activeInHierarchy=self.active_in_hierarchy(obj), layer=obj.layer,
                       tag=obj.tag)
        return out

    # ------------------------------------------------------------ Play Mode

    def _variant(self, cls):
        path = (self.classes.get(cls) or {}).get("path")
        found = MARKER_RE.search(self.scripts.get(path, ""))
        return found.group(1) if found else None

    def _class_fields(self, cls):
        return {name for _kind, name, _default, _old in (self.classes.get(cls) or {}).get("fields", ())}

    def _init_sim(self):
        movers, coins = [], []
        for obj in self.walk():
            for cls, fields in obj.fields.items():
                if cls not in self.classes or not self.component(obj, cls):
                    continue
                if "waypoints.Array.size" in fields:
                    size = int(fields.get("waypoints.Array.size") or 0)
                    points = [self.objects.get((fields.get("waypoints.Array.data[%d]" % i) or (None, None))[1])
                              for i in range(size)]
                    movers.append({"obj": obj, "points": [p for p in points if p is not None],
                                   "speed": float(fields.get("speed") or 0.0), "next": 1, "fall": 0.0})
                elif {"manager", "value"} <= self._class_fields(cls):
                    coins.append((obj, cls))
        return {"movers": movers, "coins": coins, "inside": set(), "collected": set()}

    def _body(self, obj):
        return self.component(obj, "Rigidbody") is not None

    def _falls(self, obj):
        state = obj.fields.get("Rigidbody") or {}
        return self._body(obj) and _flag(state.get("useGravity"), True) and not _flag(state.get("isKinematic"))

    def _overlap(self, part, kind, center, radius):
        pos, rot, scale = self.world(part)
        local = q_rotate(q_inv(rot), [c - p for c, p in zip(center, pos)])
        if kind == "CapsuleCollider":
            half = 0.5 * abs(scale[1])
            near = (0.0, max(-half, min(half, local[1])), 0.0)
            reach = 0.5 * max(abs(scale[0]), abs(scale[2])) + radius
        elif kind == "SphereCollider":
            near, reach = (0.0, 0.0, 0.0), 0.5 * max(abs(s) for s in scale) + radius
        else:
            near = tuple(max(-0.5 * abs(s), min(0.5 * abs(s), v)) for s, v in zip(scale, local))
            reach = radius
        return math.sqrt(sum((a - b) ** 2 for a, b in zip(local, near))) < reach

    def _enter(self, sim, coin, cls, part):
        if part.tag != "Player":
            return
        variant, values = self._variant(cls), coin.fields[cls]
        if variant != "no-guard":
            if coin.eid in sim["collected"]:
                return
            sim["collected"].add(coin.eid)
        link = values.get("manager")
        keeper = self.objects.get(link[1]) if isinstance(link, tuple) and link[0] == "component" else None
        if keeper is not None and link[2] in keeper.fields:
            board = keeper.fields[link[2]]
            board["score"] = int(board.get("score") or 0) + int(values.get("value") or 0)
        if variant is None:
            coin.active = False

    def _step_sim(self, sim, dt):
        for mover in sim["movers"]:
            obj, points = mover["obj"], mover["points"]
            if mover["next"] < len(points):
                goal = self.world(points[mover["next"]])[0]
                now = _move_towards(self.world(obj)[0], goal, mover["speed"] * dt)
                self._set_world(obj, pos=now)
                if now == tuple(goal):
                    mover["next"] += 1
            if self._falls(obj):
                mover["fall"] += 9.81 * dt
                pos = self.world(obj)[0]
                self._set_world(obj, pos=(pos[0], pos[1] - mover["fall"] * dt, pos[2]))
        parts = []
        for mover in sim["movers"]:
            for part in self.walk([mover["obj"]]):
                kind = next((c[0] for c in part.components if c[0] in COLLIDERS and c[1] is not False), None)
                if kind is not None:
                    parts.append((part, kind, self._body(mover["obj"]) or self._body(part)))
        for coin, cls in sim["coins"]:
            kind = next((c[0] for c in coin.components if c[0] in COLLIDERS and c[1] is not False), None)
            usable = (kind is not None and self.active_in_hierarchy(coin) and
                      _flag(self._state(coin, kind).get("isTrigger")))
            center, _rot, scale = self.world(coin)
            radius = 0.5 * max(abs(s) for s in scale)
            for part, part_kind, part_body in parts:
                key = (coin.eid, part.eid)
                hit = (usable and (part_body or self._body(coin)) and
                       self.collides(self.layer_index(coin.layer), self.layer_index(part.layer)) and
                       self._overlap(part, part_kind, center, radius))
                if hit and key not in sim["inside"]:
                    sim["inside"].add(key)
                    self._enter(sim, coin, cls, part)
                    usable = self.active_in_hierarchy(coin)
                elif not hit:
                    sim["inside"].discard(key)

    def _advance(self, sim, seconds):
        remaining = max(0.0, seconds)
        while remaining > 1e-9:
            tick = min(FRAME_S, remaining)
            self.clock += tick
            if sim is not None:
                self._step_sim(sim, tick)
            remaining -= tick

    def run_scenario(self, spec, agent=RUNNER):
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        timeout = float(spec.get("timeoutSeconds") or 60)
        self.playing = spec.get("executionMode", "play") == "play" and not self.compile_errors
        self.clock = 0.0
        saved = [(obj, obj.pos, obj.rot, obj.scale, obj.active, obj.layer, copy.deepcopy(obj.fields))
                 for obj in self.walk()]
        sim = self._init_sim() if self.playing else None
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    self._advance(sim, float(step.get("seconds") or 0) if kind == "wait_seconds"
                                  else int(step.get("frames") or 0) * FRAME_S)
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                while True:
                    if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                        try:
                            actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                        except SkillError as exc:
                            records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                            "actual": exc.payload, "passed": False})
                            passed, error = False, "Step refused or failed; inspect its actual response."
                            break
                    if kind != "wait_condition":
                        break
                    if _matches(_select(actual, step.get("select")), step.get("expected"),
                                float(step.get("tolerance") or 0)):
                        break
                    if self.clock >= timeout:
                        passed, error = False, "Scenario timeout; steps will not be replayed."
                        break
                    self._advance(sim, FRAME_S)
                if not passed:
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
            for obj, pos, rot, scale, active, layer, fields in saved:
                obj.pos, obj.rot, obj.scale, obj.active, obj.layer, obj.fields = pos, rot, scale, active, layer, fields
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

# Producer:Betsy
