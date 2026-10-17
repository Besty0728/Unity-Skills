"""T5 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

HEAVY_MASS = 50


def _number(ident):
    """An object id as the integer the answer schema asks for (entityId texts are decimal)."""
    return int(ident) if isinstance(ident, str) else ident


def _data(ctx):
    """State of the fixture as C0 holds it, read through the task's own snapshot."""
    return ctx.task.snapshot(ctx)["data"]


def _crates(ctx):
    """[(index, id)] of the Crate children of Row in sibling order, and the index of the one to replace."""
    data = _data(ctx)
    marked = ctx.private.replaced(data)[0]
    crates = [(index, ref) for index, (ref, state) in enumerate(data["children"]) if (state or {}).get("name") == "Crate"]
    return crates, marked


def _heavy(ctx):
    """Id of the Crate carrying the mass override: the one with local scale 1.2 (the others scale 0.8 to 1.1)."""
    for ref, state in _data(ctx)["children"]:
        if (state or {}).get("name") == "Crate" and abs(V.floats(state["scale"])[0] - 1.2) < 1e-6:
            return ref
    raise ValueError("no Crate with scale 1.2")


def _sibling_index(ctx, ref):
    kids = (ctx.probe.go(ctx.handles["row"]) or {}).get("children") or []
    return next((i for i, kid in enumerate(kids) if V.same_id(kid, ref)), None)


def _answer(ctx, ref):
    return {"variantPath": ctx.private.variant_path(ctx), "newInstanceId": _number(V.object_id(ref)),
            "siblingIndex": _sibling_index(ctx, ref), "declaredComplete": True}


def _on_instance(ctx, ref, action):
    """Runs action() for skills that find an object by name, instanceId or path only: from Unity 6000.4 there is no
    instanceId, so the three same-name Crates get a unique name for the call and their own name back afterwards."""
    if not isinstance(ref, str):
        return action()
    name = (ctx.probe.go(ref) or {}).get("name")
    ctx.c.write("gameobject_rename", newName="CrateEdit", **V.address(ref))
    try:
        return action()
    finally:
        ctx.c.write("gameobject_rename", newName=name, **V.address(ref))


def _variant(ctx, mass=True):
    ctx.c.write("prefab_create_variant", sourcePrefabPath=ctx.handles["basePath"],
                variantPath=ctx.private.variant_path(ctx))
    if mass:
        ctx.c.write("prefab_set_property", prefabPath=ctx.private.variant_path(ctx), componentType="Rigidbody",
                    propertyName="mass", value=str(HEAVY_MASS))


def _replace(ctx, rotate=True, place=True, resize=True, sibling=True, delete=True, duplicate=False, source=None,
             pick=None, name="Crate"):
    """Puts a new Crate where one of the old Crates was: instance of the variant (or `source`), the old world pose, the
    old sibling index. `pick` is the child index to replace (default: the Crate near a world yaw of 75 degrees)."""
    c, h = ctx.c, ctx.handles
    crates, marked = _crates(ctx)
    index = marked if pick is None else pick
    old = dict(crates)[index]
    info = ctx.probe.go(old)
    if duplicate:
        copy = c.write("gameobject_duplicate", **V.address(old))
        new = V.object_id({"entityId": copy.get("copyEntityId"), "instanceId": copy.get("copyInstanceId")})
        c.write("gameobject_rename", newName=name, **V.address(new))
    else:
        args = dict(prefabPath=source or ctx.private.variant_path(ctx), **V.address(h["row"], "parent"))
        if name:
            args["name"] = name
        new = V.object_id(c.write("prefab_instantiate", **args))
        position, rotation, scale = (V.floats(info.get(key)) for key in ("position", "rotation", "scale"))
        pose = {}
        if place:
            pose.update(zip(("posX", "posY", "posZ"), position))
        if resize:
            pose.update(zip(("scaleX", "scaleY", "scaleZ"), scale))
        if rotate:
            pose.update(zip(("rotX", "rotY", "rotZ"), rotation))
        if pose:
            c.write("gameobject_set_transform", **dict(pose, **V.address(new)))
    if delete:
        c.write("gameobject_delete", **V.address(old))
    if sibling:
        c.write("gameobject_set_sibling_index", index=index, **V.address(new))
    return new


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx))


def mass_override(ctx):
    _variant(ctx, mass=False)
    new = _replace(ctx)
    _on_instance(ctx, new, lambda: ctx.c.write("component_set_property", componentType="Rigidbody",
                                               propertyName="mass", value=str(HEAVY_MASS), **V.address(new)))
    return _answer(ctx, new)


def apply_overrides(ctx):
    heavy = _heavy(ctx)
    answer = reference(ctx)
    _on_instance(ctx, heavy, lambda: ctx.c.write("prefab_apply", **V.address(heavy)))
    return answer


def new_at_end(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, sibling=False))


def no_rotation(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, rotate=False))


def no_placement(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, place=False, resize=False))


def wrong_position(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, place=False))


def no_scale(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, resize=False))


def variant_name(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, name=None))


def duplicate_instead(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, duplicate=True))


def old_not_deleted(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, delete=False))


def plain_instance(ctx):
    _variant(ctx)
    return _answer(ctx, _replace(ctx, source=ctx.handles["basePath"]))


def mass_on_base(ctx):
    _variant(ctx, mass=False)
    ctx.c.write("prefab_set_property", prefabPath=ctx.handles["basePath"], componentType="Rigidbody",
                propertyName="mass", value=str(HEAVY_MASS))
    return _answer(ctx, _replace(ctx))


def other_crate(ctx):
    _variant(ctx)
    crates, marked = _crates(ctx)
    return _answer(ctx, _replace(ctx, pick=next(index for index, _ref in crates if index != marked)))


def edit_other(ctx):
    crates, marked = _crates(ctx)
    answer = reference(ctx)
    keep = next(ref for index, ref in crates if index != marked)
    ctx.c.write("gameobject_set_transform", rotY=10, **V.address(keep))
    return answer


def other_mass(ctx):
    crates, marked = _crates(ctx)
    answer = reference(ctx)
    keep = next(ref for index, ref in crates if index != marked)
    _on_instance(ctx, keep, lambda: ctx.c.write("component_set_property", componentType="Rigidbody",
                                                propertyName="mass", value="7", **V.address(keep)))
    return answer


def reorder_others(ctx):
    answer = reference(ctx)
    kids = (ctx.probe.go(ctx.handles["row"]) or {}).get("children") or []
    ctx.c.write("gameobject_set_sibling_index", index=len(kids) - 2, **V.address(kids[-1]))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["siblingIndex"] = answer["siblingIndex"] + 1
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCrate")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "crate.txt"), b"replaced\n")
    return answer


def world_then_parent(ctx):
    c, h = ctx.c, ctx.handles
    _variant(ctx)
    crates, marked = _crates(ctx)
    old = dict(crates)[marked]
    info = ctx.probe.go(old)
    position, rotation, scale = (V.floats(info.get(key)) for key in ("position", "rotation", "scale"))
    new = V.object_id(c.write("prefab_instantiate", prefabPath=ctx.private.variant_path(ctx), x=position[0],
                              y=position[1], z=position[2]))
    c.write("gameobject_set_transform", rotX=rotation[0], rotY=rotation[1], rotZ=rotation[2], scaleX=scale[0],
            scaleY=scale[1], scaleZ=scale[2], **V.address(new))
    c.write("gameobject_set_parent", **dict(V.address(new, "child"), **V.address(h["row"], "parent")))
    c.write("gameobject_set_sibling_index", index=marked, **V.address(new))
    c.write("gameobject_rename", newName="Crate", **V.address(new))
    c.write("gameobject_delete", **V.address(old))
    return _answer(ctx, new)


def serialized_mass(ctx):
    _variant(ctx, mass=False)
    ctx.c.write("prefab_set_serialized_property", prefabPath=ctx.private.variant_path(ctx), componentType="Rigidbody",
                propertyPath="m_Mass", value=str(HEAVY_MASS))
    return _answer(ctx, _replace(ctx))


def instance_as_variant(ctx):
    c = ctx.c
    temp = V.object_id(c.write("prefab_instantiate", prefabPath=ctx.handles["basePath"], name="CrateTemp",
                               **V.address(ctx.root, "parent")))
    c.write("component_set_property", componentType="Rigidbody", propertyName="mass", value=str(HEAVY_MASS),
            **V.address(temp))
    c.write("prefab_create", savePath=ctx.private.variant_path(ctx), **V.address(temp))
    c.write("gameobject_delete", **V.address(temp))
    return _answer(ctx, _replace(ctx))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


def standalone_prefab(ctx):
    c = ctx.c
    temp = V.object_id(c.write("prefab_instantiate", prefabPath=ctx.handles["basePath"], name="CrateTemp",
                               **V.address(ctx.root, "parent")))
    c.write("prefab_unpack", **V.address(temp))
    c.write("component_set_property", componentType="Rigidbody", propertyName="mass", value=str(HEAVY_MASS),
            **V.address(temp))
    c.write("prefab_create", savePath=ctx.private.variant_path(ctx), **V.address(temp))
    c.write("gameobject_delete", **V.address(temp))
    return _answer(ctx, _replace(ctx))


NEGATIVES = [
    Negative("mass_override", mass_override, {"V2"}, "the mass is set on the new instance, not in the variant asset"),
    Negative("apply_overrides", apply_overrides, {"V3"}, "the overrides of the Crate that has them are applied to Crate.prefab"),
    Negative("new_at_end", new_at_end, {"V6"}, "the new instance stays at the end of Row"),
    Negative("no_rotation", no_rotation, {"V7"}, "the new instance keeps the prefab's identity rotation"),
    Negative("no_placement", no_placement, {"V7"}, "the new instance keeps the local origin of Row and the prefab's scale"),
    Negative("duplicate_instead", duplicate_instead, {"V6"}, "a duplicate of the old Crate is no prefab instance"),
    Negative("old_not_deleted", old_not_deleted, {"V4"}),
    Negative("plain_instance", plain_instance, {"V6"}, "an instance of Crate.prefab instead of the variant"),
    Negative("standalone_prefab", standalone_prefab, {"V1", "V2"}, "a prefab saved from an unpacked copy is no variant"),
    Negative("mass_on_base", mass_on_base, {"V2", "V3"}, "the mass is written into Crate.prefab"),
    Negative("other_crate", other_crate, {"V4", "V5", "V6", "V7"}, "another Crate is replaced"),
    Negative("other_mass", other_mass, {"V5"}, "the mass of a kept Crate is changed"),
    Negative("wrong_report", wrong_report, {"V9"}),
    Negative("outside_root", outside_root, {"V8"}),
    Negative("direct_file", direct_file, {"V10"}, "a file written to disk next to the skills"),
]

# Not part of the live gate: each of these exercises one condition of the verifier and is run by the offline tests only.
EXTRA_NEGATIVES = [
    Negative("wrong_position", wrong_position, {"V7"}, "the new instance stays at the local origin of Row"),
    Negative("no_scale", no_scale, {"V7"}, "the new instance keeps the prefab's scale"),
    Negative("variant_name", variant_name, {"V6"}, "the new instance keeps the name of the variant"),
    Negative("edit_other", edit_other, {"V5"}, "a kept Crate is turned"),
    Negative("reorder_others", reorder_others, {"V5"}, "the last two children of Row swap places"),
]

ALTERNATIVES = [("world_then_parent", world_then_parent), ("serialized_mass", serialized_mass),
                ("instance_as_variant", instance_as_variant), ("save_run_scene", save_run_scene)]

# Producer:Betsy
