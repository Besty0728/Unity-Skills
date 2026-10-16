"""Assertion library for task verifiers.

Identity is always the instanceId captured at fixture time (2022.3: entityId == instanceId);
object references read from component_get_serialized_properties look like
"<id>:<FullType>:<name>" and are resolved back to their owning GameObject. Asset references can
only be pinned by path where a skill reports one (material slot 0, saved-asset dependency trees);
elsewhere the check falls back to name + type + project-unique name and records that limitation.
"""
import json
import math
import re

from .rest import SkillError

REF_RE = re.compile(r"^(-?\d+):([^:]+):(.*)$")
NUM_RE = re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")


class Scorecard(object):
    """Weighted assertions; weights sum to 10 per task (plan section 5.3)."""

    def __init__(self, spec):
        # spec: list of (id, weight, critical, description)
        self.spec = {a[0]: {"weight": float(a[1]), "critical": bool(a[2]), "desc": a[3]} for a in spec}
        self.order = [a[0] for a in spec]
        total = sum(v["weight"] for v in self.spec.values())
        if abs(total - 10.0) > 1e-6:
            raise ValueError("assertion weights sum to %s, expected 10" % total)
        self.results = {}

    def check(self, aid, passed, expected=None, actual=None, note=None, **evidence):
        return self.fraction(aid, 1.0 if passed else 0.0, expected, actual, note, **evidence)

    def fraction(self, aid, value, expected=None, actual=None, note=None, **evidence):
        """Partial credit: weight * value; only value == 1 counts as passed for strict."""
        if aid not in self.spec:
            raise KeyError("unknown assertion %s" % aid)
        value = max(0.0, min(1.0, float(value)))
        self.results[aid] = {"pass": value >= 1.0, "fraction": round(value, 4), "expected": expected,
                             "actual": actual, "note": note, "evidence": evidence or None}
        return value >= 1.0

    def unobservable(self, aid, reason):
        return self.check(aid, False, note="unobservable: %s" % reason, unobservable=True)

    def finish(self):
        for aid in self.order:
            if aid not in self.results:
                self.unobservable(aid, "verifier did not evaluate this assertion")
        return self

    @property
    def score(self):
        return round(sum(self.spec[a]["weight"] * r.get("fraction", 1.0 if r["pass"] else 0.0)
                         for a, r in self.results.items()), 4)

    @property
    def strict(self):
        return all(r["pass"] for r in self.results.values()) and len(self.results) == len(self.spec)

    @property
    def critical_failed(self):
        return [a for a, r in self.results.items() if self.spec[a]["critical"] and not r["pass"]]

    def failed(self):
        return [a for a in self.order if not self.results.get(a, {}).get("pass")]

    def as_dict(self):
        rows = []
        for aid in self.order:
            meta = self.spec[aid]
            res = self.results.get(aid, {})
            rows.append(dict(id=aid, weight=meta["weight"], critical=meta["critical"], desc=meta["desc"], **res))
        return {"score": self.score, "completion": round(self.score * 10, 2), "strict": self.strict,
                "criticalFailed": self.critical_failed, "assertions": rows}


# ---------------------------------------------------------------- value parsing


def floats(value):
    if value is None:
        return []
    if isinstance(value, (int, float)):
        return [float(value)]
    if isinstance(value, dict):
        return [float(value[k]) for k in ("x", "y", "z", "w") if k in value]
    if isinstance(value, (list, tuple)):
        return [float(v) for v in value]
    return [float(m) for m in NUM_RE.findall(str(value))]


def vec_close(a, b, tol=1e-3):
    fa, fb = floats(a), floats(b)
    return len(fa) == len(fb) and all(abs(x - y) <= tol for x, y in zip(fa, fb))


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
    """Angle between two rotations; q and -q are the same rotation."""
    qa, qb = floats(a), floats(b)
    if len(qa) != 4 or len(qb) != 4:
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


# ---------------------------------------------------------------- read helpers


class Probe(object):
    def __init__(self, client):
        self.c = client

    def go(self, iid):
        try:
            return self.c.read("gameobject_get_info", instanceId=int(iid))
        except SkillError:
            return None

    def go_path(self, path):
        try:
            return self.c.read("gameobject_get_info", path=path)
        except SkillError:
            return None

    def children(self, iid):
        info = self.go(iid) or {}
        return info.get("children") or []

    def components(self, iid):
        res = self.c.read("component_list", instanceId=int(iid))
        return res.get("components") or []

    def component_count(self, iid, type_name):
        return sum(1 for c in self.components(iid) if c.get("type") == type_name or c.get("fullType") == type_name)

    def ser(self, iid, component_type, limit=5000):
        try:
            res = self.c.read("component_get_serialized_properties", instanceId=int(iid),
                              componentType=component_type, limit=limit)
        except SkillError:
            return None
        return {p.get("propertyPath"): p.get("value") for p in res.get("properties") or []}

    def owner(self, ref_value):
        """GameObject instanceId owning the referenced object (component ids resolve to their GO)."""
        ref = parse_ref(ref_value)
        if not ref:
            return None
        info = self.go(ref[0])
        return info.get("instanceId") if info else None

    def material(self, path=None, iid=None):
        args = {"path": path} if path else {"instanceId": int(iid)}
        try:
            return self.c.read("material_get_properties", **args)
        except SkillError:
            return None

    def asset(self, path):
        try:
            return self.c.read("asset_get_info", assetPath=path)
        except SkillError:
            return None

    def guid(self, path):
        info = self.asset(path)
        return info.get("guid") if info else None

    def deps(self, path):
        try:
            res = self.c.read("cleaner_get_dependency_tree", assetPath=path, recursive=False)
        except SkillError:
            return None
        return res

    def so(self, path):
        try:
            res = self.c.read("scriptableobject_get_serialized_properties", assetPath=path)
        except SkillError:
            return None
        return res

    def query(self, query_json, sample_limit=500):
        payload = query_json if isinstance(query_json, str) else json.dumps(query_json)
        return self.c.read("batch_query_gameobjects", queryJson=payload, sampleLimit=sample_limit)

# Producer:Betsy
