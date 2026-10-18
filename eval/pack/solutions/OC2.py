"""OC2 reference, exact-failure negatives and alternative skill routes."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative


def _set(ctx, path, node, field, value):
    return ctx.c.write("prefab_set_serialized_property", prefabPath=path, gameObjectPath=node,
                       componentType="Light", propertyPath=field, value=str(value))


def _report(f):
    return {"checkedAssets": [f[key]["path"] for key in ("base", "fast", "tank")], "declaredComplete": True}


def _finish(ctx, f, rebuild=True, add=True, speed=True):
    node = f["base"]["node"]
    if rebuild:
        ctx.c.write("prefab_create_variant", sourcePrefabPath=f["base"]["path"], variantPath=f["tank"]["path"])
        _set(ctx, f["tank"]["path"], node, "m_Range", f["tank"]["range"])
    if speed:
        _set(ctx, f["base"]["path"], node, "m_Intensity", f["base"]["range"])
    if add:
        ctx.c.write("prefab_add_component", prefabPath=f["base"]["path"], gameObjectPath=node, componentType=f["kind"])
    return _report(f)


def reference(ctx):
    return _finish(ctx, ctx.private.family(ctx))


def tank_speed_override(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f)
    _set(ctx, f["tank"]["path"], f["base"]["node"], "m_Intensity", f["fast"]["range"])
    return result


def keep_scale_override(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f)
    scale = ctx.private._node(f["tank"], f["base"]["node"])["localScale"]
    ctx.c.write("prefab_set_transform", prefabPath=f["tank"]["path"], gameObjectPath=f["base"]["node"],
                scaleX=scale["x"], scaleY=scale["y"], scaleZ=scale["z"])
    return result


def erase_fast_override(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f)
    ctx.c.write("prefab_create_variant", sourcePrefabPath=f["base"]["path"], variantPath=f["fast"]["path"])
    _set(ctx, f["fast"]["path"], f["base"]["node"], "m_Range", f["fast"]["range"])
    return result


def component_on_variants(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f, add=False)
    for row in (f["fast"], f["tank"]):
        ctx.c.write("prefab_add_component", prefabPath=row["path"], gameObjectPath=f["base"]["node"],
                    componentType=f["kind"])
    return result


def component_on_scene(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f, add=False)
    for ref in ctx.handles["objects"]:
        path = ctx.probe.go(ref)["path"] + "/" + f["base"]["node"]
        ctx.c.write("component_add", path=path, componentType=f["kind"])
    return result


def apply_scene_pollutes_base(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f)
    for ref in ctx.handles["objects"]:
        info = ctx.probe.go(ref)
        own = ctx.c.read("prefab_get_overrides", path=info["path"], includeDetails=True, limit=4096)
        if own["prefabPath"] == f["base"]["path"] and any(
                item.get("property", "").startswith("m_Color") for item in own["overrides"]):
            ctx.c.write("prefab_apply_overrides", path=info["path"])
            return result
    raise ValueError("scene color override missing")


def scene_speed_only(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f, speed=False)
    for ref in ctx.handles["objects"]:
        info = ctx.probe.go(ref)
        own = ctx.c.read("prefab_get_overrides", path=info["path"], includeDetails=True, limit=4096)
        if own["prefabPath"] == f["base"]["path"]:
            ctx.c.write("component_set_serialized_property", path=info["path"] + "/" + f["base"]["node"],
                        componentType="Light", propertyPath="m_Intensity", value=str(f["base"]["range"]))
            break
    return result


def other_variant(ctx):
    f = ctx.private.family(ctx)
    result = _finish(ctx, f)
    row = next(row for row in f["rows"] if row not in (f["base"], f["fast"], f["tank"]))
    _set(ctx, row["path"], f["base"]["node"], "m_Intensity", f["base"]["range"])
    return result


def wrong_report(ctx):
    result = reference(ctx)
    result["checkedAssets"] = result["checkedAssets"][:2]
    return result


def outside_root(ctx):
    result = reference(ctx)
    ctx.c.write("gameobject_create", name="OC2Extra")
    return result


def direct_file(ctx):
    result = reference(ctx)
    # A new file changes the file inventory and is imported by the submit refresh.
    path = os.path.join(ctx.project_path, *ctx.asset("OC2-change.txt").split("/"))
    with open(path, "wb") as handle:
        handle.write(b"changed outside the skills\n")
    return result


def scene_to_variant(ctx):
    f = ctx.private.family(ctx)
    base, tank = f["base"], f["tank"]
    root_name = ctx.private._node(base, "")["gameObject"]
    temp = V.object_id(ctx.c.write("prefab_instantiate", prefabPath=base["path"], name=root_name,
                                  **V.address(ctx.root, "parent")))
    info = ctx.probe.go(temp)
    ctx.c.write("component_set_serialized_property", path=info["path"] + "/" + base["node"],
                componentType="Light", propertyPath="m_Range", value=str(tank["range"]))
    ctx.c.write("prefab_create", savePath=tank["path"], **V.address(temp))
    ctx.c.write("gameobject_delete", **V.address(temp))
    return _finish(ctx, f, rebuild=False)


def save_run_scene(ctx):
    result = reference(ctx)
    ctx.c.write("scene_save")
    return result


NEGATIVES = [
    Negative("tank_speed_override", tank_speed_override, {"A3"}, "value is set on Tank instead of inherited"),
    Negative("keep_scale_override", keep_scale_override, {"A4"}),
    Negative("erase_fast_override", erase_fast_override, {"A2"}),
    Negative("component_on_variants", component_on_variants, {"A5"}),
    Negative("component_on_scene", component_on_scene, {"A5", "A6"}),
    Negative("apply_scene_pollutes_base", apply_scene_pollutes_base, {"A6"},
             "looks done but applying the scene instance puts its color override into the base"),
    Negative("scene_speed_only", scene_speed_only, {"A1", "A3", "A6"}),
    Negative("other_variant", other_variant, {"A6"}),
    Negative("wrong_report", wrong_report, {"A9"}),
    Negative("outside_root", outside_root, {"A7"}),
    Negative("direct_file", direct_file, {"A6", "A8"}),
]
ALTERNATIVES = [("scene_to_variant", scene_to_variant), ("save_run_scene", save_run_scene)]

# Producer:Betsy
