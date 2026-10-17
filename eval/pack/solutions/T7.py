"""T7 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os
import re

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

PATTERN = r"^Crate_[A-F][0-9]{2}$"
MASS = 4.5
OTHER_COLLIDERS = ("SphereCollider", "MeshCollider")
TWINS = ("Crate_B07",)


def _number(ident):
    """An object id as the integer the answer schema asks for (entityId texts are decimal)."""
    return int(ident) if isinstance(ident, str) else ident


def _read(ctx, objects, case_sensitive=True, yard_only=True):
    """Matches among the query rows, with their paths, component types and Rigidbody mass (read-only skills)."""
    p = ctx.probe
    rx = re.compile(PATTERN if case_sensitive else "(?i)" + PATTERN)
    prefix = (ctx.handles["yardPath"] if yard_only else ctx.R) + "/"
    out = []
    for obj in objects:
        if not rx.match(obj.get("name") or "") or not (obj.get("path") or "").startswith(prefix):
            continue
        ref = V.object_id(obj)
        types = [item.get("type") for item in p.components(ref)]
        mass = None
        if "Rigidbody" in types:
            values = V.floats((p.ser(ref, "Rigidbody") or {}).get("m_Mass"))
            mass = values[0] if values else None
        out.append({"ref": ref, "name": obj["name"], "path": obj["path"], "types": types, "mass": mass})
    return out


def _scan(ctx, include_inactive=True, case_sensitive=True, yard_only=True):
    query = {"namePattern": PATTERN, "includeInactive": include_inactive, "caseSensitive": case_sensitive,
             "limit": 500}
    return _read(ctx, ctx.probe.query(query, sample_limit=500).get("objects") or [], case_sensitive, yard_only)


def _plan(found):
    add = [item for item in found if "BoxCollider" not in item["types"]]
    fix = [item for item in found if item["mass"] is not None and abs(item["mass"] - MASS) > 1e-4]
    return add, fix


def _answer(found, add, fix):
    return {"matchedCount": len(found), "addedBoxCollider": [_number(item["ref"]) for item in add],
            "fixedMass": [_number(item["ref"]) for item in fix], "declaredComplete": True}


def _apply(ctx, found, by_name=()):
    """The two batches of the repair; the objects named in by_name are addressed by their name, all others by path."""
    add, fix = _plan(found)

    def where(item):
        return {"name": item["name"]} if item["name"] in by_name else {"path": item["path"]}

    if add:
        ctx.c.write("component_add_batch", items=json.dumps([dict(where(item), componentType="BoxCollider")
                                                            for item in add]))
    if fix:
        ctx.c.write("component_set_property_batch", items=json.dumps([
            dict(where(item), componentType="Rigidbody", propertyName="mass", value=MASS) for item in fix]))
    return _answer(found, add, fix)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _apply(ctx, _scan(ctx))


def skip_inactive(ctx):
    return _apply(ctx, _scan(ctx, include_inactive=False))


def case_insensitive(ctx):
    return _apply(ctx, _scan(ctx, case_sensitive=False))


def include_archive(ctx):
    return _apply(ctx, _scan(ctx, yard_only=False))


def by_name(ctx):
    return _apply(ctx, _scan(ctx), by_name=TWINS)


def enable_disabled(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    for item in found:
        if any(comp.get("type") == "BoxCollider" and comp.get("enabled") is False
               for comp in ctx.probe.components(item["ref"])):
            ctx.c.write("component_set_enabled", componentType="BoxCollider", enabled=True, **V.address(item["ref"]))
    return answer


def replace_colliders(ctx):
    found = _scan(ctx)
    for item in found:
        for kind in OTHER_COLLIDERS:
            if kind in item["types"] and "BoxCollider" not in item["types"]:
                ctx.c.write("component_remove", componentType=kind, **V.address(item["ref"]))
    return _apply(ctx, found)


def add_rigidbody(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    bare = [item for item in found if item["mass"] is None]
    ctx.c.write("component_add_batch", items=json.dumps([{"path": item["path"], "componentType": "Rigidbody"}
                                                        for item in bare]))
    ctx.c.write("component_set_property_batch", items=json.dumps([
        {"path": item["path"], "componentType": "Rigidbody", "propertyName": "mass", "value": MASS}
        for item in bare]))
    return answer


def remove_bodies(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    for item in found:
        if item["mass"] is not None and abs(item["mass"] - MASS) <= 1e-4:
            ctx.c.write("component_remove", componentType="Rigidbody", **V.address(item["ref"]))
    return answer


def box_everywhere(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    boxed = [item for item in found if "BoxCollider" in item["types"]]
    ctx.c.write("component_add_batch", items=json.dumps([{"path": item["path"], "componentType": "BoxCollider"}
                                                        for item in boxed]))
    answer["addedBoxCollider"] += [_number(item["ref"]) for item in boxed]
    return answer


def reactivate(ctx):
    found = _scan(ctx)
    dormant = [node for node in ctx.task.snapshot(ctx)["data"]["nodes"] if node.get("active") is False]
    ctx.c.write("gameobject_set_active_batch", items=json.dumps([{"path": node["path"], "active": True}
                                                                for node in dormant]))
    return _apply(ctx, found)


def rename_match(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    ctx.c.write("gameobject_rename", newName="Crate_checked", **V.address(found[0]["ref"]))
    return answer


def create_helper(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="CheckedMarker", **V.address(ctx.root, "parent"))
    return answer


def wrong_count(ctx):
    answer = reference(ctx)
    answer["matchedCount"] += 1
    return answer


def report_all_bodies(ctx):
    found = _scan(ctx)
    answer = _apply(ctx, found)
    answer["fixedMass"] = [_number(item["ref"]) for item in found if item["mass"] is not None]
    return answer


def report_too_few(ctx):
    answer = reference(ctx)
    answer["addedBoxCollider"] = answer["addedBoxCollider"][:-1]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCrate")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "yard.txt"), b"repaired\n")
    return answer


def substring_scan_serialized(ctx):
    """A substring query and a local regex, one component_add step per object, the mass through the serialized path."""
    rows = ctx.probe.query({"name": "Crate_", "includeInactive": True, "limit": 500}, sample_limit=500)
    found = _read(ctx, rows.get("objects") or [])
    add, fix = _plan(found)
    steps = [{"skill": "component_add", "args": dict(V.address(item["ref"]), componentType="BoxCollider")}
             for item in add]
    for start in range(0, len(steps), 50):
        ctx.c.batch(steps[start:start + 50])
    if fix:
        ctx.c.write("component_set_serialized_property_batch", items=json.dumps([
            {"path": item["path"], "componentType": "Rigidbody", "propertyPath": "m_Mass", "value": "4.5"}
            for item in fix]))
    return _answer(found, add, fix)


def find_regex(ctx):
    res = ctx.c.read("gameobject_find", name=PATTERN, useRegex=True, limit=500)
    return _apply(ctx, _read(ctx, res.get("objects") or []))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("skip_inactive", skip_inactive, {"Y1", "Y4", "Y10"}, "inactive matches are never queried"),
    Negative("case_insensitive", case_insensitive, {"Y6", "Y10"}, "crate_A01, Crate_a01 and CRATE_A01 are repaired too"),
    Negative("include_archive", include_archive, {"Y6", "Y10"}, "R/Archive/Crate_A01 is repaired as well"),
    Negative("by_name", by_name, {"Y1", "Y8"}, "the two Crate_B07 are addressed by name and reach one object"),
    Negative("enable_disabled", enable_disabled, {"Y2"}, "the disabled BoxColliders are switched on"),
    Negative("replace_colliders", replace_colliders, {"Y3"}, "Sphere and Mesh colliders are removed first"),
    Negative("add_rigidbody", add_rigidbody, {"Y5"}, "matches without a Rigidbody get one"),
    Negative("remove_bodies", remove_bodies, {"Y5"}, "a Rigidbody that already had mass 4.5 is removed"),
    Negative("box_everywhere", box_everywhere, {"Y2"}, "matches that had a BoxCollider get a second one"),
    Negative("reactivate", reactivate, {"Y7"}, "inactive objects are switched on"),
    Negative("rename_match", rename_match, {"Y7"}),
    Negative("create_helper", create_helper, {"Y7"}, "an extra object is created under R"),
    Negative("wrong_count", wrong_count, {"Y10"}),
    Negative("report_all_bodies", report_all_bodies, {"Y9"}, "fixedMass lists every Rigidbody, not the changed ones"),
    Negative("report_too_few", report_too_few, {"Y8"}),
    Negative("outside_root", outside_root, {"Y11"}),
    Negative("direct_file", direct_file, {"Y12"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("substring_scan_serialized", substring_scan_serialized), ("find_regex", find_regex),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
