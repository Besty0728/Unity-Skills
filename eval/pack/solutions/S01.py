"""S01 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

CRATE_POS = (0.0, 0.5, 0.0)


def _number(ident):
    """An object id as the integer the answer schema asks for (entityId texts are decimal)."""
    return int(ident) if isinstance(ident, str) else ident


def _answer(ctx, crate):
    info = ctx.probe.go(crate) or {}
    slot = (ctx.probe.material(iid=crate) or {}).get("materialPath")
    return {"objectPath": info.get("path"), "objectInstanceId": _number(V.object_id(info)), "materialPath": slot,
            "declaredComplete": True}


def _create_crate(ctx, world=True, parent=True):
    args = dict(name="Crate", primitiveType="Cube", x=CRATE_POS[0], y=CRATE_POS[1], z=CRATE_POS[2])
    if parent:
        args.update(V.address(ctx.handles["props"], "parent"))
    if world:
        args["space"] = "world"
    return V.object_id(ctx.c.write("gameobject_create", **args))


def _new_material(ctx, path, **args):
    ctx.c.write("material_create", name="CrateMaterial", savePath=path, **args)


def _assign(ctx, crate, path):
    ctx.c.write("material_assign", materialPath=path, **V.address(crate))


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    path = ctx.private.material_path(ctx)
    res = ctx.c.batch([
        {"skill": "gameobject_create", "args": dict(name="Crate", primitiveType="Cube", x=0, y=0.5, z=0,
                                                    space="world", **V.address(ctx.handles["props"], "parent"))},
        {"skill": "material_create", "args": {"name": "CrateMaterial", "savePath": path}},
        {"skill": "material_assign", "args": {"entityId": {"$ref": "$0.entityId"}, "materialPath": path}},
    ])
    return _answer(ctx, V.object_id(res["results"][0]["result"]))


def local_coords(ctx):
    path = ctx.private.material_path(ctx)
    crate = _create_crate(ctx, world=False)
    _new_material(ctx, path)
    _assign(ctx, crate, path)
    return _answer(ctx, crate)


def reuse_common(ctx):
    crate = _create_crate(ctx)
    _assign(ctx, crate, ctx.handles["commonMaterial"])
    return _answer(ctx, crate)


def not_assigned(ctx):
    crate = _create_crate(ctx)
    _new_material(ctx, ctx.private.material_path(ctx))
    return _answer(ctx, crate)


def nested_path(ctx):
    crate = _create_crate(ctx)
    path = ctx.asset("Materials", "Nested", "CrateMaterial.mat")
    _new_material(ctx, path)
    _assign(ctx, crate, path)
    return _answer(ctx, crate)


def rotated(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_transform", rotY=45, **V.address(answer["objectInstanceId"]))
    return answer


def sphere_crate(ctx):
    path = ctx.private.material_path(ctx)
    crate = V.object_id(ctx.c.write("gameobject_create", name="Crate", primitiveType="Sphere", x=CRATE_POS[0],
                                    y=CRATE_POS[1], z=CRATE_POS[2], space="world",
                                    **V.address(ctx.handles["props"], "parent")))
    _new_material(ctx, path)
    _assign(ctx, crate, path)
    return _answer(ctx, crate)


def two_crates(ctx):
    answer = reference(ctx)
    _create_crate(ctx)
    return answer


def edit_common(ctx):
    answer = reference(ctx)
    ctx.c.write("material_set_color", path=ctx.handles["commonMaterial"], propertyName=ctx.env["colorProperty"],
                r=1, g=0, b=0, a=1)
    return answer


def unlit_shader(ctx):
    path = ctx.private.material_path(ctx)
    crate = _create_crate(ctx)
    _new_material(ctx, path, shaderName="Unlit/Color")
    _assign(ctx, crate, path)
    return _answer(ctx, crate)


def wrong_report(ctx):
    answer = reference(ctx)
    answer["objectInstanceId"] = _number(ctx.handles["props"])
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayHelper")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    path = ctx.private.material_path(ctx)
    with open(os.path.join(ctx.project_path, *path.split("/")), "rb") as handle:
        data = handle.read()
    _write_file(ctx, ctx.asset("Materials", "CrateMaterialCopy.mat"), data)
    return answer


def root_then_move(ctx):
    crate = _create_crate(ctx, world=True, parent=False)
    ctx.c.write("gameobject_set_parent", **dict(V.address(crate, "child"), **V.address(ctx.handles["props"], "parent")))
    ctx.c.write("gameobject_set_transform", posX=CRATE_POS[0], posY=CRATE_POS[1], posZ=CRATE_POS[2], **V.address(crate))
    ctx.c.write("material_create", name="CrateMaterial", savePath=ctx.asset("Materials"))
    ctx.c.write("component_set_property", componentType="MeshRenderer", propertyName="sharedMaterial",
                assetPath=ctx.private.material_path(ctx), **V.address(crate))
    return _answer(ctx, crate)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("local_coords", local_coords, {"A2"}, "x/y/z taken as local under the offset Props"),
    Negative("reuse_common", reuse_common, {"A5", "A6"}, "the same-named existing material is assigned instead"),
    Negative("not_assigned", not_assigned, {"A6"}),
    Negative("nested_path", nested_path, {"A5", "A6"}, "saved one folder too deep"),
    Negative("rotated", rotated, {"A3"}),
    Negative("sphere_crate", sphere_crate, {"A1", "A4"}, "a Sphere primitive named Crate"),
    Negative("two_crates", two_crates, {"A1"}),
    Negative("edit_common", edit_common, {"A7"}),
    Negative("unlit_shader", unlit_shader, {"A5"}, "a shader other than the pipeline's default Lit"),
    Negative("wrong_report", wrong_report, {"A9"}),
    Negative("outside_root", outside_root, {"A8"}),
    Negative("direct_file", direct_file, {"A10"}, "a .mat written to disk next to the skills"),
]

ALTERNATIVES = [("root_then_move", root_then_move), ("save_run_scene", save_run_scene)]

# Producer:Betsy
