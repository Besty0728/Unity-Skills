"""OC1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

COUNT_KEYS = ("propertyOverrides", "addedComponents", "removedComponents", "addedGameObjects")


def _number(ident):
    """An object id as the integer the answer schema asks for (entityId texts are decimal)."""
    return int(ident) if isinstance(ident, str) else ident


def _pose(ctx):
    return ctx.private.muzzle_pose(ctx.seed)


def _node_path(ctx):
    return ctx.handles["barrelNode"] + "/Muzzle"


def _instances(ctx):
    """(ref, root-relative path of the Barrel node in the instance) for the three instances."""
    h = ctx.handles
    return [(ref, h["turretNode"] + "/" + h["barrelNode"]) for ref in h["tanks"]] + \
           [(ref, h["barrelNode"]) for ref in h["turrets"]]


def _barrel(ctx, ref, rel):
    return ctx.private.descend(ctx.probe, ref, rel.split("/"))


def _carrier(ctx):
    """Path of the instance that currently has overrides (the fixture gives exactly one), read live."""
    for ref, _rel in _instances(ctx):
        info = ctx.probe.go(ref) or {}
        res = ctx.c.read("prefab_get_overrides", path=info.get("path"))
        if res.get("hasOverrides"):
            return info.get("path")
    return None


def _answer(ctx, carrier):
    p = ctx.probe
    rows = []
    for ref, _rel in _instances(ctx):
        for node in ctx.private.muzzles_under(p, ref):
            rows.append({"path": node.get("path"), "instanceId": _number(V.object_id(node))})
    counts = ctx.c.read("prefab_get_overrides", path=carrier) if carrier else {}
    total = sum(V.as_int(counts.get(key)) or 0 for key in COUNT_KEYS)
    return {"turretPrefabPath": ctx.handles["turret"], "muzzleNodePath": _node_path(ctx), "sceneMuzzles": rows,
            "overrideInstancePath": carrier or "", "overrideCount": total, "declaredComplete": True}


def _transform_args(pose, prefix_pos="pos"):
    out = {}
    out.update({prefix_pos + axis: value for axis, value in zip("XYZ", pose["position"])})
    out.update({"rot" + axis: value for axis, value in zip("XYZ", pose["rotation"])})
    out.update({"scale" + axis: pose["scale"] for axis in "XYZ"})
    return out


def _asset_muzzle(ctx, prefab_path, parent_path, pose=None):
    """Creates Muzzle under parent_path inside the prefab asset at prefab_path and sets its local pose."""
    pose = pose or _pose(ctx)
    ctx.c.write("prefab_create_gameobject", prefabPath=prefab_path, name="Muzzle", parentPath=parent_path)
    node = (parent_path + "/" if parent_path else "") + "Muzzle"
    ctx.c.write("prefab_set_transform", prefabPath=prefab_path, gameObjectPath=node, **_transform_args(pose))
    return node


def _scene_muzzle(ctx, parent_ref, pose=None):
    """Creates Muzzle under a scene object with the local pose (gameobject_create takes parent-local values)."""
    pose = pose or _pose(ctx)
    args = {"name": "Muzzle"}
    args.update({axis: value for axis, value in zip("xyz", pose["position"])})
    args.update({"rot" + axis: value for axis, value in zip("XYZ", pose["rotation"])})
    args.update({"scale" + axis: pose["scale"] for axis in "XYZ"})
    args.update(V.address(parent_ref, "parent"))
    return V.object_id(ctx.c.write("gameobject_create", **args))


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    carrier = _carrier(ctx)
    _asset_muzzle(ctx, ctx.handles["turret"], ctx.handles["barrelNode"])
    return _answer(ctx, carrier)


def scene_instances(ctx):
    carrier = _carrier(ctx)
    for ref, rel in _instances(ctx):
        _scene_muzzle(ctx, _barrel(ctx, ref, rel))
    return _answer(ctx, carrier)


def tank_prefab(ctx):
    h = ctx.handles
    carrier = _carrier(ctx)
    _asset_muzzle(ctx, h["tank"], h["turretNode"] + "/" + h["barrelNode"])
    return _answer(ctx, carrier)


def apply_tank(ctx):
    carrier = _carrier(ctx)
    ref, rel = next((ref, rel) for ref, rel in _instances(ctx) if (ctx.probe.go(ref) or {}).get("path") == carrier)
    _scene_muzzle(ctx, _barrel(ctx, ref, rel))
    ctx.c.write("prefab_apply", path=carrier)
    return _answer(ctx, carrier)


def wrong_parent(ctx):
    carrier = _carrier(ctx)
    _asset_muzzle(ctx, ctx.handles["turret"], "")
    return _answer(ctx, carrier)


def wrong_pose(ctx):
    carrier = _carrier(ctx)
    pose = _pose(ctx)
    swapped = {"position": tuple(reversed(pose["position"])), "rotation": pose["rotation"],
               "scale": pose["scale"] + 1.0}
    _asset_muzzle(ctx, ctx.handles["turret"], ctx.handles["barrelNode"], swapped)
    return _answer(ctx, carrier)


def with_component(ctx):
    answer = reference(ctx)
    ctx.c.write("prefab_add_component", prefabPath=ctx.handles["turret"], gameObjectPath=_node_path(ctx),
                componentType="Light")
    return answer


def revert_overrides(ctx):
    carrier = _carrier(ctx)
    _asset_muzzle(ctx, ctx.handles["turret"], ctx.handles["barrelNode"])
    ctx.c.write("prefab_revert_overrides", name=carrier.rsplit("/", 1)[-1])
    return _answer(ctx, carrier)


def extra_object(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="Spare", **V.address(ctx.handles["container"], "parent"))
    return answer


def move_dome(ctx):
    answer = reference(ctx)
    ctx.c.write("prefab_set_transform", prefabPath=ctx.handles["turret"], gameObjectPath="Dome", posY=1.5)
    return answer


def barrel_prefab(ctx):
    carrier = _carrier(ctx)
    _asset_muzzle(ctx, ctx.handles["barrel"], "")
    return _answer(ctx, carrier)


def wrong_report(ctx):
    answer = reference(ctx)
    answer["overrideCount"] = answer["overrideCount"] + 1
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayMuzzle")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Prefabs", "notes.txt"), b"muzzle added\n")
    return answer


def edit_batch(ctx):
    h = ctx.handles
    carrier = _carrier(ctx)
    steps = [{"skill": "prefab_create_gameobject", "args": {"name": "Muzzle", "parentPath": h["barrelNode"]}},
             {"skill": "prefab_set_transform", "args": dict({"gameObjectPath": _node_path(ctx)},
                                                            **_transform_args(_pose(ctx)))}]
    ctx.c.write("prefab_edit_batch", prefabPath=h["turret"], steps=json.dumps(steps))
    return _answer(ctx, carrier)


def apply_turret_instance(ctx):
    """The scene route: Muzzle under the standalone Turret instance's Barrel, then the instance is applied."""
    h = ctx.handles
    carrier = _carrier(ctx)
    ref = h["turrets"][0]
    _scene_muzzle(ctx, _barrel(ctx, ref, h["barrelNode"]))
    ctx.c.write("prefab_apply", path=(ctx.probe.go(ref) or {}).get("path"))
    return _answer(ctx, carrier)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("scene_instances", scene_instances, {"A1", "A2", "A5"},
             "Muzzle added on each scene instance instead of in the asset"),
    Negative("tank_prefab", tank_prefab, {"A1", "A2", "A3", "A6"}, "Muzzle added inside Tank.prefab"),
    Negative("apply_tank", apply_tank, {"A1", "A2", "A3", "A5", "A6", "A7"},
             "Muzzle added on the Tank instance with overrides, then the instance applied to Tank.prefab"),
    Negative("wrong_parent", wrong_parent, {"A1", "A2", "A6"}, "Muzzle under the Turret root, not the Barrel node"),
    Negative("wrong_pose", wrong_pose, {"A2", "A6"}),
    Negative("with_component", with_component, {"A2"}),
    Negative("revert_overrides", revert_overrides, {"A5", "A7"}, "the instance overrides are reverted"),
    Negative("extra_object", extra_object, {"A7"}),
    Negative("move_dome", move_dome, {"A4"}, "another node of Turret.prefab is moved"),
    Negative("barrel_prefab", barrel_prefab, {"A1", "A3"}, "Muzzle added inside Barrel.prefab"),
    Negative("wrong_report", wrong_report, {"A8"}),
    Negative("outside_root", outside_root, {"A9"}),
    Negative("direct_file", direct_file, {"A7", "A10"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("edit_batch", edit_batch), ("apply_turret_instance", apply_turret_instance),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
