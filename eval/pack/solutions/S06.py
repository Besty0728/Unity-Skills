"""S06 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

BLUE = (0.1, 0.4, 0.8, 1.0)


def _windows_dir(ctx):
    return ctx.asset("Materials", "Nested").replace("/", "\\")


def _answer(ctx, path, prop):
    p = ctx.probe
    if path:
        state = V.material_state(p, path)
    else:
        info = p.material(iid=ctx.handles["displayCube"]) or {}
        state = {"colors": {c["name"]: c.get("value") for c in (info.get("properties") or {}).get("colors") or []}}
    color = V.material_color(p, state, prop)
    return {"materialPath": path or "", "colorProperty": prop, "readback": list(color or []), "declaredComplete": True}


def _paint(ctx, path, prop):
    ctx.c.write("material_set_color", path=path, propertyName=prop, r=BLUE[0], g=BLUE[1], b=BLUE[2], a=BLUE[3])


def _make(ctx, save, prop=None):
    c = ctx.c
    prop = prop or ctx.env["colorProperty"]
    path = c.write("material_create", name="BluePanel", savePath=save).get("path")
    _paint(ctx, path, prop)
    c.write("material_assign", materialPath=path, **V.address(ctx.handles["displayCube"]))
    return path, prop


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _answer(ctx, *_make(ctx, _windows_dir(ctx)))


def color_on_cube(ctx):
    c, h, prop = ctx.c, ctx.handles, ctx.env["colorProperty"]
    path = c.write("material_create", name="BluePanel", savePath=_windows_dir(ctx)).get("path")
    c.write("material_set_color", propertyName=prop, r=BLUE[0], g=BLUE[1], b=BLUE[2], a=BLUE[3],
            **V.address(h["displayCube"]))
    c.write("material_assign", materialPath=path, **V.address(h["displayCube"]))
    return _answer(ctx, path, prop)


def emission_color(ctx):
    return _answer(ctx, *_make(ctx, _windows_dir(ctx), prop="_EmissionColor"))


def both_cubes(ctx):
    answer = reference(ctx)
    ctx.c.write("material_assign", materialPath=answer["materialPath"], **V.address(ctx.handles["shelfCube"]))
    return answer


def wrong_folder(ctx):
    return _answer(ctx, *_make(ctx, ctx.asset("Materials")))


def flat_name(ctx):
    return _answer(ctx, *_make(ctx, ctx.asset("MaterialsNested")))


def wrong_report(ctx):
    answer = reference(ctx)
    answer["readback"] = [0.5, 0.5, 0.5, 1.0]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayPanel", primitiveType="Cube")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    path = ctx.private.material_path(ctx)
    with open(os.path.join(ctx.project_path, *path.split("/")), "rb") as handle:
        data = handle.read()
    _write_file(ctx, ctx.asset("Materials", "Nested", "BluePanelCopy.mat"), data)
    return answer


def forward_slashes(ctx):
    return _answer(ctx, *_make(ctx, ctx.private.material_path(ctx)))


def duplicate_base(ctx):
    c, h, prop = ctx.c, ctx.handles, ctx.env["colorProperty"]
    c.write("asset_create_folder", folderPath=ctx.asset("Materials"))
    c.write("asset_create_folder", folderPath=ctx.asset("Materials", "Nested"))
    path = c.write("material_duplicate", sourcePath=h["base"], newName="BluePanel",
                   savePath=ctx.asset("Materials", "Nested")).get("path")
    _paint(ctx, path, prop)
    c.write("material_assign", materialPath=path, **V.address(h["displayCube"]))
    return _answer(ctx, path, prop)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("color_on_cube", color_on_cube, {"D2", "D4"}, "colouring through the cube edits the shared PanelBase"),
    Negative("emission_color", emission_color, {"D2"}, "a colour property other than the main colour"),
    Negative("both_cubes", both_cubes, {"D4"}),
    Negative("wrong_folder", wrong_folder, {"D1", "D3"}),
    Negative("flat_name", flat_name, {"D1", "D3"}, "a sibling folder named after the joined path"),
    Negative("wrong_report", wrong_report, {"D5"}),
    Negative("outside_root", outside_root, {"D6"}),
    Negative("direct_file", direct_file, {"D7"}, "a .mat written to disk next to the skills"),
]

ALTERNATIVES = [("forward_slashes", forward_slashes), ("duplicate_base", duplicate_base),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
