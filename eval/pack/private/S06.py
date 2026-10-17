"""S06 private verifier: a material saved from a Windows-style path, given to one renderer, the shared one untouched.

What the task probes: the folder is given with backslashes and does not exist yet (a literal backslash in a folder
name is wrong); DisplayCube and ShelfCube share PanelBase.mat, so colouring "DisplayCube's material" before the swap
edits the shared asset; the main colour property depends on the pipeline (_BaseColor for URP Lit, _Color for
Standard), and URP Lit also carries a hidden _Color.
"""
import os

from usk_eval import verifylib as V

BLUE = (0.1, 0.4, 0.8, 1.0)

ASSERTIONS = [
    ("D1", 2.0, False, ["R1"], "A/Materials/Nested/BluePanel.mat is a Material, both folders are assets, and no name "
                               "under A contains a literal backslash"),
    ("D2", 2.5, False, ["R2"], "the new material uses the pipeline's default Lit shader with main colour "
                               "(0.1, 0.4, 0.8, 1)"),
    ("D3", 2.0, False, ["R3"], "DisplayCube has one slot and slot 0 is A/Materials/Nested/BluePanel.mat"),
    ("D4", 2.0, True, ["R4"], "PanelBase.mat is unchanged; ShelfCube is unchanged and still uses only PanelBase.mat"),
    ("D5", 0.5, False, ["R5", "G8"], "the answer reports the material path, the colour property and the read-back "
                                     "colour"),
    ("D6", 0.5, False, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("D7", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "D1": {"zh-CN": "把反斜杠路径换成正斜杠的 Assets/... 目录，逐级建好文件夹后再保存材质。",
           "en": "Turn the backslash path into the forward-slash Assets/... folder and create its folders before "
                 "saving the material."},
    "D2": {"zh-CN": "主颜色属性取决于渲染管线（URP Lit 为 _BaseColor，Standard 为 _Color）；可用 project_get_render_pipeline 查询。",
           "en": "The main colour property depends on the pipeline (_BaseColor for URP Lit, _Color for Standard); "
                 "project_get_render_pipeline tells which."},
    "D3": {"zh-CN": "把新材质赋给 DisplayCube 的唯一材质槽。", "en": "Put the new material in DisplayCube's only slot."},
    "D4": {"zh-CN": "两个方块原来共用 PanelBase.mat；先换材质再改颜色，不要改共享材质。",
           "en": "Both cubes share PanelBase.mat; swap the material before colouring, and never edit the shared one."},
    "D5": {"zh-CN": "答案中的路径、属性名和读回颜色要与材质的实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual material path, property and read-back colour, and declaredComplete."},
    "D6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "D7": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def material_path(ctx):
    return ctx.asset("Materials", "Nested", "BluePanel.mat")


def _colors(info):
    return {"colors": {c["name"]: c.get("value") for c in (info.get("properties") or {}).get("colors") or []
                       if isinstance(c, dict) and "name" in c}}


def _candidate(ctx):
    """The new material: the asset at the required path if present, else whatever non-PanelBase material sits in
    DisplayCube's slot 0 (saved elsewhere or in memory), so D2 grades shader and colour apart from D1/D3."""
    p, h = ctx.probe, ctx.handles
    path = material_path(ctx)
    if p.asset(path):
        return V.material_state(p, path)
    info = p.material(iid=h["displayCube"]) or {}
    if not info or info.get("materialPath") == h["base"]:
        return None
    state = _colors(info)
    state["shader"] = info.get("shader")
    return state


def _backslash_entries(ctx):
    out = []
    for base, dirs, files in os.walk(os.path.join(ctx.project_path, *ctx.A.split("/"))):
        for name in dirs + files:
            if "\\" in name:
                out.append(os.path.relpath(os.path.join(base, name), ctx.project_path).replace(os.sep, "/"))
    return sorted(out)


def _answer_ok(ctx, slot0):
    p = ctx.probe
    reported = V.norm_path(V.answer(ctx, "materialPath"))
    if reported != (slot0 or ""):
        return False
    state = V.material_state(p, reported) if reported else _colors(p.material(iid=ctx.handles["displayCube"]) or {})
    prop = V.answer(ctx, "colorProperty")
    actual = V.material_color(p, state, prop) if isinstance(prop, str) and prop else None
    return V.close_tuple(V.answer(ctx, "readback"), actual, 1e-2) and \
        isinstance(V.answer(ctx, "declaredComplete"), bool)


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    path = material_path(ctx)
    asset = p.asset(path)
    folders = [p.asset(ctx.asset("Materials")), p.asset(ctx.asset("Materials", "Nested"))]
    slashed = _backslash_entries(ctx)
    card.check("D1", bool(asset) and asset.get("type") == "Material" and all(folders) and not slashed,
               expected=path, actual={"type": (asset or {}).get("type"), "folders": [bool(f) for f in folders],
                                      "backslashEntries": slashed[:10]})
    state = _candidate(ctx)
    wanted, prop = ctx.env.get("defaultShader"), ctx.env.get("colorProperty")
    color = V.material_color(p, state, prop) if state and prop else None
    card.check("D2", bool(state) and bool(wanted) and state.get("shader") == wanted and V.close_tuple(color, BLUE),
               expected={"shader": wanted, prop or "colorProperty": BLUE},
               actual={"shader": (state or {}).get("shader"), prop or "colorProperty": color})
    slot0 = (p.material(iid=h["displayCube"]) or {}).get("materialPath")
    slots = V.as_int((p.ser(h["displayCube"], "MeshRenderer") or {}).get("m_Materials.Array.size"))
    card.check("D3", slot0 == path and slots == 1, expected={"slot0": path, "slots": 1},
               actual={"slot0": slot0, "slots": slots})
    ok_base, diff_base = V.same(c0.get("base"), V.material_state(p, h["base"]))
    ok_shelf, diff_shelf = V.same(c0.get("shelfCube"), V.go_state(p, h["shelfCube"]))
    shelf_material = (p.material(iid=h["shelfCube"]) or {}).get("materialPath")
    shelf_slots = V.as_int((p.ser(h["shelfCube"], "MeshRenderer") or {}).get("m_Materials.Array.size"))
    card.check("D4", ok_base and ok_shelf and shelf_material == c0.get("shelfCubeMaterial") and shelf_slots == 1,
               actual={"baseDiff": diff_base, "shelfDiff": diff_shelf, "shelfMaterial": shelf_material,
                       "shelfSlots": shelf_slots})
    card.check("D5", _answer_ok(ctx, slot0), actual=ctx.answer)
    bad = ctx.violations()
    card.check("D6", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("D7", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
