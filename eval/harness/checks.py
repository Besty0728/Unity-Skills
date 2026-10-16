"""Shared verification helpers built on Probe: state snapshots, comparisons, answer access.

Snapshots are taken at fixture time (C0) and compared at the end; comparisons are tolerant on
floats and order-sensitive on children, so "unchanged" means same identity, same values, same
sibling order.
"""
import math

from .verify import floats, quat_close


def go_state(probe, iid, with_children=True):
    info = probe.go(iid)
    if not info:
        return None
    state = {k: info.get(k) for k in ("name", "path", "parentPath", "isActive", "tag", "layer", "position",
                                      "rotation", "scale", "components")}
    if with_children:
        state["children"] = [c.get("instanceId") for c in info.get("children") or []]
    return state


def material_state(probe, path):
    info = probe.material(path=path)
    if not info:
        return None
    props = info.get("properties") or {}
    return {"shader": info.get("shader"), "keywords": sorted(info.get("keywords") or []),
            "renderQueue": info.get("renderQueue"),
            "colors": {c["name"]: c.get("value") for c in props.get("colors") or []},
            "floats": {f["name"]: f.get("value") for f in props.get("floats") or []},
            "guid": probe.guid(path)}


def material_color(probe, path_or_state, name):
    state = path_or_state if isinstance(path_or_state, dict) else material_state(probe, path_or_state)
    value = ((state or {}).get("colors") or {}).get(name)
    if not isinstance(value, dict):
        return None
    return tuple(float(value.get(k, 0)) for k in ("r", "g", "b", "a"))


def close_tuple(a, b, tol=1e-3):
    if a is None or b is None:
        return False
    fa, fb = floats(a), floats(b)
    return len(fa) == len(fb) and all(abs(x - y) <= tol for x, y in zip(fa, fb))


def same(a, b, tol=1e-3, path=""):
    """Deep equality with float tolerance; returns (True, None) or (False, first difference)."""
    if isinstance(a, dict) and isinstance(b, dict):
        for key in sorted(set(a) | set(b)):
            ok, diff = same(a.get(key), b.get(key), tol, "%s.%s" % (path, key))
            if not ok:
                return ok, diff
        return True, None
    if isinstance(a, (list, tuple)) and isinstance(b, (list, tuple)):
        if len(a) != len(b):
            return False, "%s: length %d != %d" % (path, len(a), len(b))
        for i, (x, y) in enumerate(zip(a, b)):
            ok, diff = same(x, y, tol, "%s[%d]" % (path, i))
            if not ok:
                return ok, diff
        return True, None
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool):
        return (abs(float(a) - float(b)) <= tol, None if abs(float(a) - float(b)) <= tol else
                "%s: %r != %r" % (path, a, b))
    return (a == b, None if a == b else "%s: %r != %r" % (path, a, b))


def new_children(probe, parent_iid, before_iids):
    info = probe.go(parent_iid) or {}
    return [c for c in info.get("children") or [] if c.get("instanceId") not in set(before_iids)]


def local_trs(probe, iid):
    ser = probe.ser(iid, "Transform") or {}
    return {"position": floats(ser.get("m_LocalPosition")), "rotation": floats(ser.get("m_LocalRotation")),
            "scale": floats(ser.get("m_LocalScale"))}


def identity_rotation(quat):
    return quat_close(quat, (0, 0, 0, 1))


def answer(ctx, key, default=None):
    data = ctx.answer if isinstance(ctx.answer, dict) else {}
    return data.get(key, default)


def as_int(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def norm_path(path):
    return (path or "").replace("\\", "/").strip()


def world_from_local(parent_pos, parent_rot_euler, parent_scale, local):
    """World position of a local offset under a parent with Unity euler rotation (ZXY) and scale."""
    sx, sy, sz = floats(parent_scale)
    lx, ly, lz = floats(local)
    v = (lx * sx, ly * sy, lz * sz)
    rx, ry, rz = (math.radians(a) for a in floats(parent_rot_euler))

    def rot_z(p, a):
        return (p[0] * math.cos(a) - p[1] * math.sin(a), p[0] * math.sin(a) + p[1] * math.cos(a), p[2])

    def rot_x(p, a):
        return (p[0], p[1] * math.cos(a) - p[2] * math.sin(a), p[1] * math.sin(a) + p[2] * math.cos(a))

    def rot_y(p, a):
        return (p[0] * math.cos(a) + p[2] * math.sin(a), p[1], -p[0] * math.sin(a) + p[2] * math.cos(a))

    v = rot_y(rot_x(rot_z(v, rz), rx), ry)
    px, py, pz = floats(parent_pos)
    return (px + v[0], py + v[1], pz + v[2])

# Producer:Betsy
