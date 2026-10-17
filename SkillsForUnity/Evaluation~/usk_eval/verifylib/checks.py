"""Number, rotation, reference, object id and state-comparison helpers for verifiers.

Ported from v1 eval/harness/verify.py (value parsing) and checks.py (state snapshots, comparisons, answer access).
Snapshots are taken at fixture time (C0) and compared at the end; comparisons are tolerant on floats and order-sensitive
on children, so "unchanged" means same identity, same values, same sibling order. The value helpers (floats,
vec_close, quat_close, as_int, norm_path) take agent answers and missing readings: they answer "not close" or None
instead of raising, so a malformed answer fails an assertion rather than the verifier.

Object ids: Unity before 6000.4 names an object by an int instanceId (its entityId is that number as text); from 6000.4
instanceId is always 0 and only the entityId text (an unsigned 64-bit number) names it. object_id(), same_id() and
address() hide the difference, so verifiers and builders stay the same on every version.
"""
import math
import re

REF_RE = re.compile(r"^(-?\d+):([^:]+):(.*)$")
NUM_RE = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")
_ID_TEXT_RE = re.compile(r"^-?[0-9]{1,20}$")
_INT_MIN, _INT_MAX = -2 ** 31, 2 ** 31 - 1          # what a C# int parameter (instanceId) can carry
_VECTOR_KEYS = (("x", "y", "z", "w"), ("r", "g", "b", "a"))


# ---------------------------------------------------------------- value parsing


def _numbers(items):
    out = []
    for item in items:
        if item is None or isinstance(item, bool):
            return []
        try:
            out.append(float(item))
        except (TypeError, ValueError):
            return []
    return out


def floats(value):
    """Numbers of a vector-like value: a number, an {x,y,z,w} or {r,g,b,a} dict, a list, or text such as "(1, 2, 3)".
    [] means there is nothing comparable (missing or malformed)."""
    if value is None or isinstance(value, bool):
        return []
    if isinstance(value, (int, float)):
        return [float(value)]
    if isinstance(value, dict):
        for keys in _VECTOR_KEYS:
            if any(k in value for k in keys):
                return _numbers(value[k] for k in keys if k in value)
        return []
    if isinstance(value, (list, tuple)):
        return _numbers(value)
    return [float(m) for m in NUM_RE.findall(str(value))]


def vec_close(a, b, tol=1e-3):
    fa, fb = floats(a), floats(b)
    return bool(fa) and len(fa) == len(fb) and all(abs(x - y) <= tol for x, y in zip(fa, fb))


def quat_from_euler(x, y, z):
    """Unity order: Z, then X, then Y (intrinsic), returned as (x, y, z, w)."""
    hx, hy, hz = (math.radians(v) / 2.0 for v in (x, y, z))
    cx, sx, cy, sy, cz, sz = math.cos(hx), math.sin(hx), math.cos(hy), math.sin(hy), math.cos(hz), math.sin(hz)
    qx = cy * sx * cz + sy * cx * sz
    qy = sy * cx * cz - cy * sx * sz
    qz = cy * cx * sz - sy * sx * cz
    qw = cy * cx * cz + sy * sx * sz
    return (qx, qy, qz, qw)


def quat_angle_deg(a, b):
    """Angle between two rotations; q and -q are the same rotation. None when either is not a finite quaternion."""
    qa, qb = floats(a), floats(b)
    if len(qa) != 4 or len(qb) != 4 or not all(math.isfinite(v) for v in qa + qb):
        return None
    na = math.sqrt(sum(x * x for x in qa)) or 1.0
    nb = math.sqrt(sum(x * x for x in qb)) or 1.0
    dot = abs(sum(x * y for x, y in zip(qa, qb))) / (na * nb)
    return math.degrees(2.0 * math.acos(min(1.0, dot)))


def quat_close(a, b, tol_deg=0.1):
    angle = quat_angle_deg(a, b)
    return angle is not None and angle <= tol_deg


def parse_ref(value):
    """Return (id, fullType, name) for an object reference string, or None for null/missing."""
    if value is None:
        return None
    match = REF_RE.match(str(value))
    if not match:
        return None
    return int(match.group(1)), match.group(2), match.group(3)


# ---------------------------------------------------------------- object ids


def object_id(value):
    """Canonical id of an object, or None when `value` names nothing.

    `value` is a result or child entry ({entityId, instanceId}), an int instanceId, an entityId text or an integral
    float. The id is the non-zero int instanceId when there is one (what skills and v1 handles use), else the entityId
    as normalised text; 0 never names an object. Store this in handles and compare with same_id().
    """
    if isinstance(value, dict):
        for key in ("instanceId", "entityId"):
            found = object_id(value.get(key))
            if found is not None:
                return found
        return None
    if isinstance(value, float):
        value = as_int(value)
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, int):
        return value or None
    if isinstance(value, str):
        text = value.strip()
        if _ID_TEXT_RE.match(text) and int(text) != 0:
            return str(int(text))
    return None


def same_id(a, b):
    """True when both name an object and the same one; ints, entityId texts and result dicts may be mixed."""
    first, second = object_id(a), object_id(b)
    return first is not None and second is not None and str(first) == str(second)


def address(ref, prefix=""):
    """Skill arguments naming the object `ref` stands for (a canonical id, an entityId text or a result dict).

    An int that fits C# int goes out as {"instanceId": n}; anything else (text, a number beyond C# int) as
    {"entityId": "<text>"}, the only spelling Unity 6000.4+ accepts. `prefix` addresses a related object's parameters:
    "parent" gives parentInstanceId / parentEntityId. Raises ValueError when `ref` names no object.
    """
    ident = object_id(ref)
    if ident is None:
        raise ValueError("%r names no object (no non-zero instanceId, no entityId)" % (ref,))
    if isinstance(ident, int) and _INT_MIN <= ident <= _INT_MAX:
        name, value = "instanceId", ident
    else:
        name, value = "entityId", str(ident)
    return {(prefix + name[0].upper() + name[1:]) if prefix else name: value}


def _id_key(value):
    ident = object_id(value)
    return None if ident is None else str(ident)


# ---------------------------------------------------------------- state snapshots


def go_state(probe, iid, with_children=True):
    info = probe.go(iid)
    if not info:
        return None
    state = {k: info.get(k) for k in ("name", "path", "parentPath", "isActive", "tag", "layer", "position",
                                      "rotation", "scale", "components")}
    if with_children:
        state["children"] = [object_id(c) for c in info.get("children") or []]
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


# ---------------------------------------------------------------- comparisons


def close_tuple(a, b, tol=1e-3):
    if a is None or b is None:
        return False
    return vec_close(a, b, tol)


def _is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def same(a, b, tol=1e-3, path=""):
    """Deep equality with float tolerance; returns (True, None) or (False, first difference)."""
    if isinstance(a, dict) and isinstance(b, dict):
        for key in sorted(set(a) | set(b), key=str):
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
    if _is_number(a) and _is_number(b):
        if abs(float(a) - float(b)) <= tol:
            return True, None
        return False, "%s: %r != %r" % (path, a, b)
    if isinstance(a, bool) != isinstance(b, bool) or a != b:
        return False, "%s: %r != %r" % (path, a, b)
    return True, None


def new_children(probe, parent_iid, before_iids):
    before = {key for key in map(_id_key, before_iids) if key is not None}
    info = probe.go(parent_iid) or {}
    return [c for c in info.get("children") or [] if _id_key(c) not in before]


def local_trs(probe, iid):
    ser = probe.ser(iid, "Transform") or {}
    return {"position": floats(ser.get("m_LocalPosition")), "rotation": floats(ser.get("m_LocalRotation")),
            "scale": floats(ser.get("m_LocalScale"))}


def identity_rotation(quat):
    return quat_close(quat, (0, 0, 0, 1))


# ---------------------------------------------------------------- answers and paths


def answer(ctx, key, default=None):
    data = ctx.answer if isinstance(ctx.answer, dict) else {}
    return data.get(key, default)


def as_int(value):
    """int for ints, integral floats and numeric text; None for anything else (bool, 2.5, "x", lists)."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return int(value) if math.isfinite(value) and value.is_integer() else None
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            return None
    return None


def norm_path(path):
    return path.replace("\\", "/").strip() if isinstance(path, str) else ""


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
