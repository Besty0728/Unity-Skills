"""OC2 verifier: loaded asset values, raw variant modifications and unchanged fixture digests.

The range fields are immutable numeric witnesses: the regular prefab supplies the new speed,
its two lowest-range variants identify Fast and Tank, and Fast's range supplies its retained speed.
The unrelated range fields and all connections are covered by the preservation assertion.
"""
import math
import os

from usk_eval import verifylib as V

ASSERTIONS = [
    ("A1", 1.5, False, ["R1"], "base speed matches its unchanged range, in loaded state and prefab YAML"),
    ("A2", 1.5, False, ["R2"], "Fast retains its speed as its own property override and resolves to that value"),
    ("A3", 1.5, False, ["R3"], "Tank inherits the new speed with no raw speed override"),
    ("A4", 1.5, False, ["R4"], "Tank inherits the base scale with no raw scale overrides"),
    ("A5", 1.5, False, ["R5"], "the new collider is owned only by the base and inherited by every variant"),
    ("A6", 1.5, True, ["R4", "R6"], "all unrelated asset and scene state, identities, GUIDs and links are preserved"),
    ("A7", 0.5, True, ["G1", "G2"], "no change outside R/A and no other scene touched"),
    ("A8", 0.25, False, ["G4"], "no file under A was written around the skills"),
    ("A9", 0.25, False, ["R7", "G8"], "the report lists the three inspected asset paths exactly once"),
]


def _near(a, b):
    a, b = V.floats(a), V.floats(b)
    return len(a) == len(b) == 1 and all(math.isfinite(v) for v in a + b) and abs(a[0] - b[0]) <= 0.001


def _mods(row, ident, field):
    return [item for doc in row["docs"] if doc.type_name == "PrefabInstance"
            for item in doc.get("m_Modification", "m_Modifications", default=[]) or []
            if str(item.get("target", {}).get("fileID")) == str(ident)
            and item.get("propertyPath", "").startswith(field)]


def family(ctx):
    rows = ctx.task.observe(ctx)
    bases = [r for r in rows if any(d.type_name == "Light" and not d.stripped for d in r["docs"])]
    if len(bases) != 1:
        raise ValueError("one regular prefab required")
    base = bases[0]
    variants = sorted([r for r in rows if r is not base], key=lambda r: float(r["range"]))
    if len(variants) < 2 or base["node"] is None:
        raise ValueError("variant family unreadable")
    light = next(d for d in base["docs"] if d.type_name == "Light" and not d.stripped)
    owner = int(light.get("m_GameObject", "fileID"))
    transform = next(d for d in base["docs"] if d.type_name == "Transform" and
                     int(d.get("m_GameObject", "fileID", default=0)) == owner)
    kind = next(d.type_name for d in base["docs"] if d.type_name in ("BoxCollider", "SphereCollider", "CapsuleCollider")
                and int(d.get("m_GameObject", "fileID", default=0)) != owner)
    return {"rows": rows, "base": base, "fast": variants[0], "tank": variants[1], "light": light,
            "transform": transform, "owner": owner, "kind": kind}


def _node(row, path):
    return next((n for n in row["nodes"] if n.get("gameObjectPath") == path), {})


def _component_count(row, path, kind):
    return sum(1 for p, comps in row["entries"] if p == path for comp in comps
               if comp.get("componentType") == "UnityEngine." + kind)


def _colliders(ctx, f):
    base, kind, owner = f["base"], f["kind"], f["owner"]
    own = [d for d in base["docs"] if d.type_name == kind and
           int(d.get("m_GameObject", "fileID", default=0)) == owner]
    if len(own) != 1 or own[0].stripped:
        return False
    for row in f["rows"]:
        if _component_count(row, base["node"], kind) != 1:
            return False
        if row is base:
            continue
        if any(d.type_name == kind and not d.stripped for d in row["docs"]):
            return False
        for doc in row["docs"]:
            if doc.type_name != "PrefabInstance":
                continue
            for item in doc.get("m_Modification", "m_RemovedComponents", default=[]) or []:
                if str(item.get("fileID")) == str(own[0].file_id):
                    return False
            if _mods(row, own[0].file_id, ""):
                return False
    for ref in ctx.handles.get("objects", []):
        info = ctx.probe.go(ref) or {}
        details = ctx.read("prefab_get_overrides", path=info.get("path"), includeDetails=True, limit=4096)
        if details.get("truncated") or any(item.get("component") == kind for item in details.get("overrides") or []):
            return False
        child = ctx.probe.go_path((info.get("path") or "") + "/" + base["node"])
        if ctx.probe.component_count(child, kind) != 1:
            return False
    return True


def _preserved(ctx, f):
    now = ctx.task.snapshot(ctx)["digests"]
    before = ctx.c0_digests
    differences = []
    for row in f["rows"]:
        path = row["path"]
        fields = ["content", "tree", "guid"]
        if row is not f["tank"]:
            fields.extend(("pose", "scale"))
        if row not in (f["base"], f["fast"], f["tank"]):
            fields.append("file")
        for field in fields:
            if now.get(path, {}).get(field) != before.get(path, {}).get(field):
                differences.append([path, field])
        if row is not f["base"]:
            source = [d.get("m_SourcePrefab", "guid") for d in row["docs"] if d.type_name == "PrefabInstance"]
            try:
                guid = V.uyaml.meta_guid(os.path.join(ctx.project_path, *f["base"]["path"].split("/")))
            except OSError:
                guid = None
            if not guid or source != [guid]:
                differences.append([path, "source"])
    for field in ("scene", "listing"):
        if not before.get(field) or now.get(field) != before.get(field):
            differences.append(field)
    # Freshly loaded assets and existing scene instances must agree on inherited values.
    for ref in ctx.handles.get("objects", []):
        info = ctx.probe.go(ref) or {}
        try:
            overrides = ctx.read("prefab_get_overrides", path=info.get("path"), includeDetails=True, limit=4096)
            row = next(r for r in f["rows"] if r["path"] == overrides.get("prefabPath"))
            path = info["path"] + "/" + f["base"]["node"]
            child = ctx.probe.go_path(path)
            actual = ctx.probe.ser(child, "Light") or {}
            trs = V.local_trs(ctx.probe, child)
            if not _near(actual.get("m_Intensity"), row["speed"]) or not V.vec_close(
                    trs.get("scale"), _node(row, f["base"]["node"]).get("localScale")):
                differences.append([V.object_id(ref), "inheritance"])
        except Exception:
            differences.append([V.object_id(ref), "unreadable"])
    return not differences, differences


def verify(ctx, card):
    try:
        f = family(ctx)
    except Exception as exc:
        f = None
        for aid in ("A1", "A2", "A3", "A4", "A5", "A6"):
            card.check(aid, False, note="unreadable prefab family: %s" % type(exc).__name__)
    if f is not None:
        base, fast, tank = f["base"], f["fast"], f["tank"]
        tests = {
            "A1": lambda: _near(base["speed"], base["range"]) and _near(f["light"].get("m_Intensity"), base["range"]),
            "A2": lambda: _near(fast["speed"], fast["range"]) and
            len(_mods(fast, f["light"].file_id, "m_Intensity")) == 1 and
            _near(_mods(fast, f["light"].file_id, "m_Intensity")[0].get("value"), fast["range"]),
            "A3": lambda: _near(tank["speed"], base["range"]) and not _mods(tank, f["light"].file_id, "m_Intensity"),
            "A4": lambda: V.vec_close(_node(tank, base["node"]).get("localScale"),
                                      _node(base, base["node"]).get("localScale")) and
            not _mods(tank, f["transform"].file_id, "m_LocalScale"),
            "A5": lambda: _colliders(ctx, f),
        }
        for aid, test in tests.items():
            try:
                card.check(aid, bool(test()))
            except Exception as exc:
                card.check(aid, False, note="unreadable state: %s" % type(exc).__name__)
        try:
            passed, differences = _preserved(ctx, f)
            card.check("A6", passed, actual=differences[:20])
        except Exception as exc:
            card.check("A6", False, note="unreadable preserved state: %s" % type(exc).__name__)
    bad = ctx.violations()
    card.check("A7", not bad and not ctx.saved_other_scenes(), actual=bad[:10])
    direct = ctx.direct_writes()
    card.check("A8", not direct, actual=direct[:10])
    reported = V.answer(ctx, "checkedAssets")
    wanted = [f[key]["path"] for key in ("base", "fast", "tank")] if f else []
    card.check("A9", len(wanted) == 3 and isinstance(reported, list) and len(reported) == 3 and
               all(isinstance(path, str) for path in reported) and sorted(reported) == sorted(wanted) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), actual=reported)

# Producer:Betsy
