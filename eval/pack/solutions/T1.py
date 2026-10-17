"""T1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

EDITOR_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("setup"); }}
}}
"""


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _fixture(ctx):
    return ctx.task.snapshot(ctx)["data"]


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _scripts(ctx, ai_src=None):
    cfg_class, ai_class = ctx.private.class_names(ctx.handles)
    cfg_src, default_ai = ctx.private.expected_sources(ctx.handles)
    return [_create_script(ctx, cfg_class, cfg_src), _create_script(ctx, ai_class, ai_src or default_ai)]


def _config(ctx, path=None, prefab=None):
    c, h = ctx.c, ctx.handles
    cfg_class, _ai = ctx.private.class_names(h)
    path = path or h["configAsset"]
    c.write("scriptableobject_create", typeName=cfg_class, savePath=path)
    c.write("scriptableobject_set", assetPath=path, fieldName="fireRate", value="2.5")
    c.write("scriptableobject_set", assetPath=path, fieldName="burstCount", value="3")
    c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="projectilePrefab",
            valueAssetPath=prefab or h["projectilePrefab"])
    return path


def _wire(ctx, turret, config_path, target, turn_speed=90):
    c = ctx.c
    _cfg, ai_class = ctx.private.class_names(ctx.handles)
    at = V.address(turret)
    c.write("component_add", componentType=ai_class, **at)
    c.write("component_set_serialized_property", componentType=ai_class, propertyPath="config",
            assetPath=config_path, **at)
    c.write("component_set_serialized_property", componentType=ai_class, propertyPath="target",
            referenceInstanceId=_number(target), objectType="Transform", **at)
    if turn_speed is not None:
        c.write("component_set_property", componentType=ai_class, propertyName="turnSpeed", value=str(turn_speed),
                **at)


def _answer(ctx, scripts, config_path, turret, target):
    return {"scriptPaths": scripts, "configAssetPath": config_path, "turretInstanceId": _number(turret),
            "dummyInstanceId": _number(target), "declaredComplete": True}


def _solve(ctx, turret=None, target=None, prefab=None, config_path=None, ai_src=None, turn_speed=90):
    fixture = _fixture(ctx)
    scripts = _scripts(ctx, ai_src)
    path = _config(ctx, config_path, prefab)
    turret = turret if turret is not None else ctx.private.main_turret(fixture)
    target = target if target is not None else ctx.private.respawn_dummy(fixture)
    _wire(ctx, turret, path, target, turn_speed)
    return _answer(ctx, scripts, path, turret, target)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def wrong_turret(ctx):
    fixture = _fixture(ctx)
    main = ctx.private.main_turret(fixture)
    return _solve(ctx, turret=next(ref for ref, _state in fixture["turrets"] if not V.same_id(ref, main)))


def untagged_target(ctx):
    fixture = _fixture(ctx)
    respawn = ctx.private.respawn_dummy(fixture)
    return _solve(ctx, target=next(ref for ref, _state in fixture["dummies"] if not V.same_id(ref, respawn)))


def other_prefab(ctx):
    h = ctx.handles
    return _solve(ctx, prefab=next(path for path in h["prefabs"] if path != h["projectilePrefab"]
                                   and "_Old" not in path))


def config_wrong_path(ctx):
    return _solve(ctx, config_path=ctx.asset("MainTurretConfig.asset"))


def edited_script(ctx):
    _cfg_src, ai_src = ctx.private.expected_sources(ctx.handles)
    return _solve(ctx, ai_src=ai_src.replace("30f", "45f"))


def editor_helper(ctx):
    answer = _solve(ctx)
    cfg_class, _ai = ctx.private.class_names(ctx.handles)
    name = "TurretSetup_" + cfg_class[len("TurretConfig_"):]
    _create_script(ctx, name, EDITOR_SRC.format(cls=name), folder=ctx.asset("Editor"))
    return answer


def turnspeed_unset(ctx):
    return _solve(ctx, turn_speed=None)


def all_three(ctx):
    fixture = _fixture(ctx)
    answer = _solve(ctx)
    main = ctx.private.main_turret(fixture)
    for ref, _state in fixture["turrets"]:
        if not V.same_id(ref, main):
            _wire(ctx, ref, ctx.handles["configAsset"], ctx.private.respawn_dummy(fixture))
    return answer


def moved_dummy(ctx):
    fixture = _fixture(ctx)
    respawn = ctx.private.respawn_dummy(fixture)
    answer = _solve(ctx)
    other = next(ref for ref, _state in fixture["dummies"] if not V.same_id(ref, respawn))
    ctx.c.write("gameobject_set_transform", localPosY=3, **V.address(other))
    return answer


def wrong_report(ctx):
    fixture = _fixture(ctx)
    main = ctx.private.main_turret(fixture)
    answer = _solve(ctx)
    answer["turretInstanceId"] = _number(next(ref for ref, _state in fixture["turrets"] if not V.same_id(ref, main)))
    return answer


def outside_root(ctx):
    answer = _solve(ctx)
    ctx.c.write("gameobject_create", name="StrayTurret")
    return answer


def direct_file(ctx):
    answer = _solve(ctx)
    _write_file(ctx, ctx.asset("Data", "notes.txt"), b"configured\n")
    return answer


def property_setters(ctx):
    c, h = ctx.c, ctx.handles
    fixture = _fixture(ctx)
    cfg_class, ai_class = ctx.private.class_names(h)
    scripts = _scripts(ctx)
    path = h["configAsset"]
    c.write("scriptableobject_create", typeName=cfg_class, savePath=path)
    for prop, value in (("fireRate", "2.5"), ("burstCount", "3")):
        c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath=prop, value=value)
    c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="projectilePrefab",
            valueAssetPath=h["projectilePrefab"])
    turret, target = ctx.private.main_turret(fixture), ctx.private.respawn_dummy(fixture)
    at = V.address(turret)
    c.write("component_add", componentType=ai_class, **at)
    c.write("component_set_property", componentType=ai_class, propertyName="config", assetPath=path, **at)
    c.write("component_set_serialized_property", componentType=ai_class, propertyPath="target",
            referenceInstanceId=_number(target), objectType="Transform", **at)
    c.write("component_set_serialized_property", componentType=ai_class, propertyPath="turnSpeed", value="90", **at)
    return _answer(ctx, scripts, path, turret, target)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("wrong_turret", wrong_turret, {"B7"}, "sibling order differs from world X order"),
    Negative("untagged_target", untagged_target, {"B9"}),
    Negative("other_prefab", other_prefab, {"B6"}, "the same-name prefab under A/Common"),
    Negative("config_wrong_path", config_wrong_path, {"B4", "B5", "B6", "B8"}),
    Negative("edited_script", edited_script, {"B1"}),
    Negative("editor_helper", editor_helper, {"B3"}),
    Negative("turnspeed_unset", turnspeed_unset, {"B10", "B14"}),
    Negative("all_three", all_three, {"B7"}),
    Negative("moved_dummy", moved_dummy, {"B11"}),
    Negative("wrong_report", wrong_report, {"B13"}),
    Negative("outside_root", outside_root, {"B12"}),
    Negative("direct_file", direct_file, {"B15"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("property_setters", property_setters), ("save_run_scene", save_run_scene)]

# Producer:Betsy
