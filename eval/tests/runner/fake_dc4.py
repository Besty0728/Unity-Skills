"""Offline fake editor for DC4 (ballistic launcher).

FakeEditorDC plus:
  * project gravity (`physics_get_gravity`, `physics_set_gravity`),
  * Rigidbody settings kept on the object and copied into the prefab asset,
  * Play Mode `Fire(Transform)` on the compiled launcher: the launch velocity follows the `// usk-fake: <variant>`
    marker, the flight is integrated step by step like PhysX does (gravity, then damping, then the move), and every
    flight gauge on a pad is updated exactly as the gauge script would update it.
"""
import os
import re

from .fake_dev_dc import FakeEditorDC, strip_comments
from .pack_fake_editor import RUNNER, _fail, q_inv, q_rotate

FIXED_DT = 0.02
FLIGHT_STEPS = 130
NUMBER_RE = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")
FIRE_RE = re.compile(r"\bpublic\s+(?:UnityEngine\.)?GameObject\s+Fire\s*\(\s*(?:UnityEngine\.)?Transform\s+\w+\s*\)")
GAUGE_FIELDS = {"origin", "mass", "damping", "closest", "bodies", "altered", "offOrigin"}
GAUGE_ACCUMULATE = "closest = Mathf.Min(closest, Segment(before, now, here));"
RIGIDBODY_NAMES = {"mass": "mass", "m_Mass": "mass", "drag": "drag", "m_Drag": "drag", "linearDamping": "drag",
                   "angularDrag": "angularDrag", "m_AngularDrag": "angularDrag", "useGravity": "useGravity",
                   "m_UseGravity": "useGravity", "isKinematic": "isKinematic", "m_IsKinematic": "isKinematic"}


def damping_factor(drag, dt=FIXED_DT, reciprocal=False):
    return 1.0 / (1.0 + drag * dt) if reciprocal else max(0.0, 1.0 - drag * dt)


def solve_velocity(origin, goal, gravity_y, drag, steps=60, dt=FIXED_DT, reciprocal=False):
    """Initial velocity that puts a projectile on `goal` after `steps` fixed steps."""
    factor = damping_factor(drag, dt, reciprocal)
    reach, fall, speed_g, factor_k = 0.0, 0.0, 0.0, 1.0
    for _ in range(steps):
        factor_k *= factor
        reach += factor_k * dt
        speed_g = (speed_g + gravity_y * dt) * factor
        fall += speed_g * dt
    return ((goal[0] - origin[0]) / reach, (goal[1] - origin[1] - fall) / reach, (goal[2] - origin[2]) / reach)


def parabola_velocity(origin, goal, gravity_y, seconds=1.2):
    """The vacuum parabola that ignores drag."""
    return ((goal[0] - origin[0]) / seconds, (goal[1] - origin[1] - 0.5 * gravity_y * seconds * seconds) / seconds,
            (goal[2] - origin[2]) / seconds)


def integrate(origin, velocity, gravity_y, drag, steps=FLIGHT_STEPS, dt=FIXED_DT):
    """Positions of a free-flying body at the start of each fixed step, then after the last one."""
    factor = damping_factor(drag, dt)
    pos, vel = list(origin), list(velocity)
    points = [tuple(pos)]
    for _ in range(steps):
        vel[1] += gravity_y * dt
        vel = [v * factor for v in vel]
        pos = [p + v * dt for p, v in zip(pos, vel)]
        points.append(tuple(pos))
    return points


def segment_distance(a, b, p):
    ab = [y - x for x, y in zip(a, b)]
    length = sum(v * v for v in ab)
    t = 0.0 if length <= 0.0 else max(0.0, min(1.0, sum((q - x) * v for q, x, v in zip(p, a, ab)) / length))
    return sum((x + v * t - q) ** 2 for x, v, q in zip(a, ab, p)) ** 0.5


def closest_approach(points, p):
    return min(segment_distance(a, b, p) for a, b in zip(points, points[1:]))


def numbers(value):
    return [float(m) for m in NUMBER_RE.findall(str(value))]


class FakeEditorDC4(FakeEditorDC):
    extra_reads = FakeEditorDC.extra_reads | frozenset(("physics_get_gravity",))

    def __init__(self, *args, **kwargs):
        self.gravity = {"x": 0.0, "y": -9.81, "z": 0.0}
        self.first_goal = {}
        FakeEditorDC.__init__(self, *args, **kwargs)

    def _skill_physics_get_gravity(self, agent, args):
        return dict(self.gravity, success=True)

    def _skill_physics_set_gravity(self, agent, args):
        before = self.gravity["y"]
        self.gravity = {axis: float(args.get(axis, self.gravity[axis])) for axis in ("x", "y", "z")}
        if agent != RUNNER and self.gravity["y"] != before:
            self._change(agent, {"kind": "property_changed", "targetType": "PhysicsManager", "property": "m_Gravity.y",
                                 "assetPath": "ProjectSettings/DynamicsManager.asset",
                                 "previousValue": "%.9g" % before, "value": "%.9g" % self.gravity["y"]})
        return dict(self.gravity, success=True)

    # ------------------------------------------------------------ Rigidbody and prefab

    def _rigidbody(self, obj):
        return obj.fields.setdefault("Rigidbody", {"mass": 1.0, "drag": 0.0, "angularDrag": 0.05, "useGravity": True,
                                                   "isKinematic": False})

    def _skill_component_set_property(self, agent, args):
        if args["componentType"].lower() == "rigidbody":
            return self._set_rigidbody(args, args["propertyName"])
        return FakeEditorDC._skill_component_set_property(self, agent, args)

    def _skill_component_set_serialized_property(self, agent, args):
        if args["componentType"].lower() == "rigidbody":
            return self._set_rigidbody(args, args["propertyPath"])
        return FakeEditorDC._skill_component_set_serialized_property(self, agent, args)

    def _set_rigidbody(self, args, name):
        obj = self.find(args)
        if not self.component(obj, "Rigidbody"):
            raise _fail("COMPONENT_NOT_FOUND", "no Rigidbody on %s" % obj.name)
        key = RIGIDBODY_NAMES.get(name)
        if key is None:
            raise _fail("PROPERTY_NOT_FOUND", "Rigidbody.%s is not modelled" % name)
        text = str(args.get("value")).strip().lower()
        self._rigidbody(obj)[key] = text in ("true", "1") if key in ("useGravity", "isKinematic") else \
            float(text.rstrip("f"))
        return {"success": True}

    def _skill_prefab_create(self, agent, args):
        obj = self.find(args)
        out = FakeEditorDC._skill_prefab_create(self, agent, args)
        self.prefabs[args["savePath"]]["rigidbody"] = dict(self._rigidbody(obj)) if self.component(obj, "Rigidbody") \
            else None
        return out

    def _parse_value(self, kind, text):
        if kind == "Vector3":
            return ",".join("%.9g" % v for v in numbers(text)[:3])
        return FakeEditorDC._parse_value(self, kind, text)

    # ------------------------------------------------------------ Play Mode

    def enter_play(self):
        self.first_goal = {}

    def _gauges(self):
        """(object, component class) of every flight gauge component in the scene."""
        return [(obj, cls) for obj in self.walk() for cls, _enabled in obj.components
                if cls in self.classes and GAUGE_FIELDS <= set(obj.fields.get(cls, {}))]

    def _gauge_reports(self, cls):
        source = strip_comments(self.compiled_sources.get(self.classes[cls]["path"], ""))
        return GAUGE_ACCUMULATE in source

    def _launch(self, variants, muzzle, goal, holder_id, rigid):
        """(spawn position, initial velocity, Rigidbody the projectile ends up with) of one Fire call."""
        mass, drag, gravity_y = rigid["mass"], rigid["drag"], self.gravity["y"]
        origin, rotation, scale = self.world(muzzle)
        spawn = tuple(goal) if "spawn-at-pad" in variants else tuple(origin)
        body = dict(rigid)
        if "impulse-mass" in variants:
            velocity = solve_velocity(origin, goal, gravity_y, drag, steps=65, reciprocal=True)
        elif "no-drag" in variants:
            velocity = parabola_velocity(origin, goal, gravity_y)
        elif "wrong-forcemode" in variants:
            velocity = [v * FIXED_DT / mass for v in solve_velocity(origin, goal, gravity_y, drag)]
        elif "wrong-gravity" in variants:
            velocity = solve_velocity(origin, goal, -9.81, drag)
        elif "fixed-angle" in variants:
            forward = q_rotate(rotation, (0.0, 0.0, 1.0))
            direction = (forward[0], forward[1] + 0.5, forward[2])
            norm = sum(v * v for v in direction) ** 0.5
            velocity = [12.0 * v / norm for v in direction]
        elif "first-target" in variants:
            velocity = solve_velocity(origin, self.first_goal.setdefault(holder_id, goal), gravity_y, drag)
        elif "local-space" in variants:
            local = [v / s for v, s in zip(q_rotate(q_inv(rotation), [g - o for g, o in zip(goal, origin)]), scale)]
            velocity = solve_velocity(origin, [o + v for o, v in zip(origin, local)], gravity_y, drag)
        elif "strip-rigidbody" in variants:
            body.update(mass=1.0, drag=0.0)
            velocity = parabola_velocity(origin, goal, gravity_y)
        else:
            velocity = solve_velocity(origin, goal, gravity_y, drag)
        return spawn, velocity, body

    def _observe(self, obj, cls, points, body):
        """What the gauge script does while it watches one new Rigidbody fly along `points`."""
        values = obj.fields[cls]
        values["bodies"] += 1
        if sum((a - b) ** 2 for a, b in zip(points[0], numbers(values["origin"]))) ** 0.5 > 0.15:
            values["offOrigin"] += 1
        reach = closest_approach(points, self.world(obj)[0]) if self._gauge_reports(cls) else 0.0
        values["closest"] = min(values["closest"], reach)
        intact = (not body["isKinematic"] and body["useGravity"] and abs(body["mass"] - float(values["mass"])) < 1e-3
                  and abs(body["drag"] - float(values["damping"])) < 1e-3)
        if not intact:
            values["altered"] += 1

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        if not cls.startswith("BallisticLauncher_"):
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        source = strip_comments(self.compiled_sources.get(self.classes[cls]["path"], ""))
        if method != "Fire" or not FIRE_RE.search(source):
            raise _fail("SEMANTIC_INVALID", "No method %s on %s" % (method, cls))
        goal_obj = self.find(arguments[0], "reference") if arguments and isinstance(arguments[0], dict) else None
        values = obj.fields[cls]
        refs = [v for v in values.values() if isinstance(v, tuple) and v]
        muzzle_ref = next((v for v in refs if v[0] == "object" and v[2:] == ("Transform",)), None)
        prefab_ref = next((v for v in refs if v[0] == "asset"), None)
        muzzle = self.objects.get(muzzle_ref[1]) if muzzle_ref else None
        prefab = self.prefabs.get(prefab_ref[1]) if prefab_ref else None
        if goal_obj is None or muzzle is None or prefab is None or not prefab.get("rigidbody"):
            return None
        goal = self.world(goal_obj)[0]
        spawn, velocity, body = self._launch(self.variants(cls), muzzle, goal, obj.eid, prefab["rigidbody"])
        points = integrate(spawn, velocity, self.gravity["y"], body["drag"])
        for gauge_obj, gauge_cls in self._gauges():
            self._observe(gauge_obj, gauge_cls, points, body)
        eid, iid = self._new_ids()
        name = os.path.splitext(os.path.basename(prefab_ref[1]))[0]
        return {"instanceId": iid, "entityId": eid, "name": "%s(Clone)" % name, "type": "UnityEngine.GameObject"}

# Producer:Betsy
