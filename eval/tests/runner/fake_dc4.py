"""Offline fake editor for DC4 (ballistic launcher).

Extends FakeEditorDC from fake_dev_dc with:
  * project gravity state (`physics_get_gravity`, `physics_set_gravity`),
  * prefab creation capturing the source GameObject's Rigidbody properties (`mass`, `m_Drag`, `useGravity`,
    `isKinematic`),
  * Play Mode invocation of `Fire(Transform)` on the compiled launcher component, computing the launched projectile's
    initial velocity and Rigidbody state according to the C# source / `// usk-fake: <variant>` marker and simulating
    the discrete FixedUpdate trajectory under the active project gravity and Rigidbody drag.
"""
import json
import math
import os
import re

from .fake_dev_dc import COMMENT_RE, FakeEditorDC
from .fake_dev_ds import _world_position

VARIANT_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
FIXED_DT = 0.02


def _num(val, default=0.0):
    try:
        return float(str(val).strip().rstrip("fF"))
    except (TypeError, ValueError):
        return default


def _solve_discrete_velocity(origin, goal, gravity_y, drag, steps=60, dt=FIXED_DT, reciprocal=False):
    factor = (1.0 / (1.0 + float(drag) * dt)) if reciprocal else max(0.0, 1.0 - float(drag) * dt)
    sum_f = 0.0
    cur_f = 1.0
    vy_g = 0.0
    grav_y = 0.0
    for _ in range(steps):
        cur_f *= factor
        sum_f += cur_f * dt
        vy_g = (vy_g + float(gravity_y) * dt) * factor
        grav_y += vy_g * dt
    return (
        (float(goal[0]) - float(origin[0])) / sum_f,
        (float(goal[1]) - float(origin[1]) - grav_y) / sum_f,
        (float(goal[2]) - float(origin[2])) / sum_f,
    )


def _inverse_transform_point(origin_obj, editor_objects, world_pt):
    """Computes origin_obj.transform.InverseTransformPoint(world_pt) using the ancestor yaw/pitch chain."""
    ox, oy, oz = _world_position(editor_objects, origin_obj)
    dx, dy, dz = float(world_pt[0]) - ox, float(world_pt[1]) - oy, float(world_pt[2]) - oz
    chain = []
    cur = origin_obj
    while cur is not None:
        chain.append(cur)
        pid = cur.get("parent")
        cur = editor_objects.get(pid) if pid else None
    for node in reversed(chain):
        rot = node.get("rotation") or {"x": 0.0, "y": 0.0, "z": 0.0}
        yaw = -math.radians(float(rot.get("y") or 0.0))
        cy, sy = math.cos(yaw), math.sin(yaw)
        dx, dz = dx * cy + dz * sy, -dx * sy + dz * cy
        pitch = -math.radians(float(rot.get("x") or 0.0))
        cp, sp = math.cos(pitch), math.sin(pitch)
        dy, dz = dy * cp - dz * sp, dy * sp + dz * cp
    return (dx, dy, dz)


def _forward_vector(origin_obj, editor_objects):
    """World-space forward vector of origin_obj."""
    chain = []
    cur = origin_obj
    while cur is not None:
        chain.append(cur)
        pid = cur.get("parent")
        cur = editor_objects.get(pid) if pid else None
    fx, fy, fz = 0.0, 0.0, 1.0
    for node in chain:
        rot = node.get("rotation") or {"x": 0.0, "y": 0.0, "z": 0.0}
        pitch = math.radians(float(rot.get("x") or 0.0))
        cp, sp = math.cos(pitch), math.sin(pitch)
        fy, fz = fy * cp - fz * sp, fy * sp + fz * cp
        yaw = math.radians(float(rot.get("y") or 0.0))
        cy, sy = math.cos(yaw), math.sin(yaw)
        fx, fz = fx * cy + fz * sy, -fx * sy + fz * cy
    return (fx, fy, fz)


class FakeEditorDC4(FakeEditorDC):
    def __init__(self, project_path, **kwargs):
        super().__init__(project_path, **kwargs)
        self.gravity = {"x": 0.0, "y": -9.81, "z": 0.0}
        self._cached_first_goal = {}
        self._spawn_counter = 0

    def _dispatch(self, skill, args, agent):
        if skill == "physics_get_gravity":
            return True, dict(self.gravity), []
        if skill == "physics_set_gravity":
            self.gravity = {
                "x": float(args.get("x", 0.0)),
                "y": float(args.get("y", -9.81)),
                "z": float(args.get("z", 0.0)),
            }
            return True, {"success": True, "gravity": dict(self.gravity)}, []
        if skill == "prefab_create":
            src = self._find(args)
            res = super()._dispatch(skill, args, agent)
            save_path = args.get("savePath") or args.get("prefabPath") or ""
            if src is not None and save_path in self.prefabs:
                rb = self._comp(src, "Rigidbody") or {}
                props = dict(rb.get("props") or {})
                ser = dict(rb.get("ser") or {})
                self.prefabs[save_path]["rigidbody"] = {
                    "mass": float(_num(props.get("mass"), 1.0)),
                    "drag": float(_num(ser.get("m_Drag", props.get("drag")), 0.0)),
                    "angularDrag": float(_num(ser.get("m_AngularDrag", props.get("angularDrag")), 0.05)),
                    "useGravity": str(props.get("useGravity", "true")).lower() != "false",
                    "isKinematic": str(props.get("isKinematic", "false")).lower() == "true",
                }
            return res
        return super()._dispatch(skill, args, agent)

    def _compile_script(self, path, raw):
        ok = super()._compile_script(path, raw)
        if not ok:
            return False
        cls = os.path.basename(path)[:-len(".cs")]
        info = self.scripts.get(cls)
        if info is not None:
            m = VARIANT_RE.search(raw or "")
            info["variant"] = m.group(1) if m else None
            clean = COMMENT_RE.sub("", raw or "")
            info["hasFire"] = bool(re.search(
                r"\bpublic\s+(?:UnityEngine\.)?GameObject\s+Fire\s*\(\s*(?:UnityEngine\.)?Transform\s+\w+\s*\)",
                clean))
        return True

    def _resolve_transform_arg(self, arg):
        if not isinstance(arg, dict):
            return None
        probe_args = {}
        if "referenceEntityId" in arg:
            probe_args["entityId"] = arg["referenceEntityId"]
        if "referenceInstanceId" in arg:
            probe_args["instanceId"] = arg["referenceInstanceId"]
        if "referencePath" in arg:
            probe_args["path"] = arg["referencePath"]
        if "referenceName" in arg:
            probe_args["name"] = arg["referenceName"]
        return self._find(probe_args)

    def _resolve_muzzle_obj(self, ser_val):
        if not ser_val:
            return None
        text = str(ser_val)
        m = re.match(r"^([^:]+):UnityEngine\.Transform:", text)
        if m:
            token = m.group(1)
            for obj in self.objects.values():
                if str(obj["id"]) == token or self._eid(obj["id"]) == token:
                    return obj
        return None

    def _resolve_prefab_info(self, ser_val):
        if not ser_val:
            return None
        text = str(ser_val)
        for path, info in self.prefabs.items():
            base = os.path.basename(path)[:-len(".prefab")]
            if base in text or path in text:
                return info
        return None

    def _invoke(self, args):
        obj = self._find(args)
        ctype = self._short(args.get("componentType") or "")
        method = args.get("methodName") or ""
        comp = self._comp(obj, ctype) if obj is not None else None
        if obj is None or comp is None:
            return False, {"status": "error", "error": "component %s not found" % ctype}, []
        info = self.scripts.get(ctype) or {}
        if method != "Fire" or not info.get("hasFire"):
            return False, {"status": "error", "error": "method %s not found on %s" % (method, ctype)}, []

        try:
            call_args = json.loads(args.get("arguments") or "[]")
        except ValueError:
            call_args = []
        target_obj = self._resolve_transform_arg(call_args[0]) if call_args else None
        if target_obj is None:
            return True, {"success": True, "returnValue": None}, []

        ser = comp.get("ser") or {}
        muzzle_obj = self._resolve_muzzle_obj(ser.get("muzzle"))
        prefab_info = self._resolve_prefab_info(ser.get("projectilePrefab"))
        if muzzle_obj is None or prefab_info is None:
            return True, {"success": True, "returnValue": None}, []

        origin = _world_position(self.objects, muzzle_obj)
        goal = _world_position(self.objects, target_obj)
        rb_template = dict(prefab_info.get("rigidbody") or {
            "mass": 2.0, "drag": 0.45, "useGravity": True, "isKinematic": False
        })
        mass = float(rb_template.get("mass", 2.0))
        drag = float(rb_template.get("drag", 0.45))
        grav_y = float(self.gravity.get("y", -9.81))
        variant = info.get("variant")

        spawn_rb = {
            "mass": mass,
            "drag": drag,
            "useGravity": bool(rb_template.get("useGravity", True)),
            "isKinematic": bool(rb_template.get("isKinematic", False)),
        }

        if variant == "impulse-mass":
            v0 = _solve_discrete_velocity(origin, goal, grav_y, drag, steps=65, dt=FIXED_DT, reciprocal=True)
        elif variant == "no-drag":
            t = 1.2
            v0 = (
                (goal[0] - origin[0]) / t,
                (goal[1] - origin[1] - 0.5 * grav_y * t * t) / t,
                (goal[2] - origin[2]) / t,
            )
        elif variant == "wrong-forcemode":
            raw_v = _solve_discrete_velocity(origin, goal, grav_y, drag, steps=60, dt=FIXED_DT)
            scale = FIXED_DT / mass
            v0 = (raw_v[0] * scale, raw_v[1] * scale, raw_v[2] * scale)
        elif variant == "wrong-gravity":
            v0 = _solve_discrete_velocity(origin, goal, -9.81, drag, steps=60, dt=FIXED_DT)
        elif variant == "fixed-angle":
            fx, fy, fz = _forward_vector(muzzle_obj, self.objects)
            ux, uy, uz = fx, fy + 0.5, fz
            norm = math.sqrt(ux * ux + uy * uy + uz * uz) or 1.0
            v0 = (12.0 * ux / norm, 12.0 * uy / norm, 12.0 * uz / norm)
        elif variant == "first-target":
            holder_id = obj["id"]
            if holder_id not in self._cached_first_goal:
                self._cached_first_goal[holder_id] = goal
            cached = self._cached_first_goal[holder_id]
            v0 = _solve_discrete_velocity(origin, cached, grav_y, drag, steps=60, dt=FIXED_DT)
        elif variant == "local-space":
            local_disp = _inverse_transform_point(muzzle_obj, self.objects, goal)
            fake_goal = (origin[0] + local_disp[0], origin[1] + local_disp[1], origin[2] + local_disp[2])
            v0 = _solve_discrete_velocity(origin, fake_goal, grav_y, drag, steps=60, dt=FIXED_DT)
        elif variant == "strip-rigidbody":
            spawn_rb["mass"] = 1.0
            spawn_rb["drag"] = 0.0
            t = 1.2
            v0 = (
                (goal[0] - origin[0]) / t,
                (goal[1] - origin[1] - 0.5 * grav_y * t * t) / t,
                (goal[2] - origin[2]) / t,
            )
        else:
            v0 = _solve_discrete_velocity(origin, goal, grav_y, drag, steps=60, dt=FIXED_DT)

        self._spawn_counter += 1
        spawn_id = 90000 + self._spawn_counter
        ret = {
            "instanceId": spawn_id,
            "entityId": self._eid(spawn_id),
            "name": "%s(Clone)" % os.path.basename(prefab_info.get("path", "Shell"))[:-len(".prefab")],
            "type": "UnityEngine.GameObject",
            "spawnPosition": {"x": round(origin[0], 4), "y": round(origin[1], 4), "z": round(origin[2], 4)},
            "launchVelocity": {"x": round(v0[0], 4), "y": round(v0[1], 4), "z": round(v0[2], 4)},
            "rigidbody": spawn_rb,
            "gravityY": grav_y,
        }
        return True, {"success": True, "returnValue": ret}, []

# Producer:Betsy
