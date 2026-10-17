"""T4 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

RED = (0.8, 0.1, 0.1, 1.0)


def _answer(ctx):
    """What Car's renderer holds in slots 0 and 1 right now, reported the way the task asks."""
    paths = ctx.private.slot_paths(ctx, ctx.handles["car"], "Car")["paths"] + [None, None]
    return {"bodyMaterialPath": paths[0] or "", "glowMaterialPath": paths[1] or "", "declaredComplete": True}


def _slot(ctx, ref, index, path):
    ctx.c.write("component_set_serialized_property", componentType="MeshRenderer",
                propertyPath="m_Materials.Array.data[%d]" % index, assetPath=path, **V.address(ref))


def _set_slots(ctx, ref, paths):
    ctx.c.write("component_set_serialized_property", componentType="MeshRenderer",
                propertyPath="m_Materials.Array.size", value=str(len(paths)), **V.address(ref))
    for index, path in enumerate(paths):
        _slot(ctx, ref, index, path)


def _new_body(ctx, prop=None, duplicate=False):
    """A new red body material under A: created from scratch or duplicated from the shared one."""
    c = ctx.c
    path = ctx.asset("Materials", "CarBody.mat")
    if duplicate:
        path = c.write("material_duplicate", sourcePath=ctx.handles["bodyPaint"], newName="CarBody",
                       savePath=ctx.asset("Materials")).get("path") or path
    else:
        c.write("material_create", name="CarBody", savePath=path)
    c.write("material_set_color", path=path, propertyName=prop or ctx.env["colorProperty"], r=RED[0], g=RED[1],
            b=RED[2], a=RED[3])
    return path


def _new_glow(ctx, path=None, intensity=2.0):
    path = path or ctx.handles["glowPath"]
    ctx.c.write("material_create", name=ctx.handles["glowName"], savePath=path)
    ctx.c.write("material_set_emission", path=path, r=1, g=0.5, b=0, intensity=intensity)
    return path


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    _slot(ctx, h["car"], 1, _new_glow(ctx))
    return _answer(ctx)


def recolor_bodypaint(ctx):
    h = ctx.handles
    ctx.c.write("material_set_color", path=h["bodyPaint"], propertyName=ctx.env["colorProperty"], r=RED[0],
                g=RED[1], b=RED[2], a=RED[3])
    _slot(ctx, h["car"], 1, _new_glow(ctx))
    return _answer(ctx)


def color_via_renderer(ctx):
    h = ctx.handles
    ctx.c.write("material_set_color", propertyName=ctx.env["colorProperty"], r=RED[0], g=RED[1], b=RED[2], a=RED[3],
                **V.address(h["car"]))
    _slot(ctx, h["car"], 1, _new_glow(ctx))
    return _answer(ctx)


def glow_slot0(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    ctx.c.write("material_assign", materialPath=_new_glow(ctx), **V.address(h["car"]))
    return _answer(ctx)


def three_slots(ctx):
    h = ctx.handles
    _set_slots(ctx, h["car"], [_new_body(ctx), _new_glow(ctx), h["trim"]])
    return _answer(ctx)


def emission_off(ctx):
    answer = reference(ctx)
    ctx.c.write("material_set_gi_flags", path=ctx.handles["glowPath"], flags="EmissiveIsBlack")
    return answer


def weak_emission(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    _slot(ctx, h["car"], 1, _new_glow(ctx, intensity=1.0))
    return _answer(ctx)


def glow_wrong_path(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    _slot(ctx, h["car"], 1, _new_glow(ctx, path=ctx.asset("%s.mat" % h["glowName"])))
    return _answer(ctx)


def glow_on_trim(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    ctx.c.write("material_set_emission", path=h["trim"], r=1, g=0.5, b=0, intensity=2)
    return _answer(ctx)


def reuse_old_glow(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx))
    ctx.c.write("material_set_emission", path=h["oldGlow"], r=1, g=0.5, b=0, intensity=2)
    _slot(ctx, h["car"], 1, h["oldGlow"])
    return _answer(ctx)


def twin_edited(ctx):
    answer = reference(ctx)
    _slot(ctx, ctx.handles["carTwin"], 0, answer["bodyMaterialPath"])
    return answer


def only_legacy_color(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx, prop="_Color", duplicate=True))
    _slot(ctx, h["car"], 1, _new_glow(ctx))
    return _answer(ctx)


def emission_color(ctx):
    h = ctx.handles
    _slot(ctx, h["car"], 0, _new_body(ctx, prop="_EmissionColor"))
    _slot(ctx, h["car"], 1, _new_glow(ctx))
    return _answer(ctx)


def wrong_report(ctx):
    answer = reference(ctx)
    answer["bodyMaterialPath"] = ctx.handles["bodyPaint"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCar", primitiveType="Cube")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "car.txt"), b"painted\n")
    return answer


def duplicate_and_hdr(ctx):
    h, c = ctx.handles, ctx.c
    body = _new_body(ctx, duplicate=True)
    c.write("material_create", name=h["glowName"], savePath=ctx.asset("Materials"))
    c.write("material_set_emission", path=h["glowPath"], r=2, g=1, b=0, intensity=1)
    _slot(ctx, h["car"], 1, h["glowPath"])
    _slot(ctx, h["car"], 0, body)
    return _answer(ctx)


def emission_via_color(ctx):
    h, c = ctx.handles, ctx.c
    body = _new_body(ctx)
    c.write("material_create", name=h["glowName"], savePath=h["glowPath"])
    c.write("material_set_color", path=h["glowPath"], propertyName="_EmissionColor", r=1, g=0.5, b=0, intensity=2)
    c.write("material_assign", materialPath=body, **V.address(h["car"]))
    _slot(ctx, h["car"], 1, h["glowPath"])
    return _answer(ctx)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("recolor_bodypaint", recolor_bodypaint, {"M2", "M3"}, "the shared BodyPaint asset is recoloured"),
    Negative("color_via_renderer", color_via_renderer, {"M2", "M3"},
             "colouring through Car's renderer writes the shared asset"),
    Negative("glow_slot0", glow_slot0, {"M2", "M6"}, "material_assign puts the glow material into slot 0"),
    Negative("three_slots", three_slots, {"M1"}, "the material array grows to three"),
    Negative("emission_off", emission_off, {"M8"}, "GI flags set to EmissiveIsBlack"),
    Negative("weak_emission", weak_emission, {"M9"}, "intensity 1 leaves the HDR colour at (1, 0.5, 0)"),
    Negative("glow_wrong_path", glow_wrong_path, {"M6", "M7", "M8", "M9"}, "the glow material is saved one folder up"),
    Negative("glow_on_trim", glow_on_trim, {"M4", "M6", "M7", "M8", "M9"},
             "Trim is made emissive instead of creating the glow material"),
    Negative("reuse_old_glow", reuse_old_glow, {"M4", "M6", "M7", "M8", "M9"},
             "the unused emissive material is edited and used instead"),
    Negative("twin_edited", twin_edited, {"M5"}, "CarTwin gets the new body material"),
    Negative("only_legacy_color", only_legacy_color, {"M2"}, "the hidden compatibility colour property is written"),
    Negative("emission_color", emission_color, {"M2"}, "the colour goes to the emission colour, not the main colour"),
    Negative("wrong_report", wrong_report, {"M11"}),
    Negative("outside_root", outside_root, {"M10"}),
    Negative("direct_file", direct_file, {"M12"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("duplicate_and_hdr", duplicate_and_hdr), ("emission_via_color", emission_via_color),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
