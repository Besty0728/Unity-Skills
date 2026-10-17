"""T4 private verifier: a new body material and a glow material for one of two cubes that share their materials.

What the task probes: Car and CarTwin share BodyPaint (slot 0) and Trim (slot 1), so colouring "Car's material" by name,
id or renderer edits the shared asset and changes CarTwin; material_assign only writes slot 0, so slot 1 needs the
serialized material array; the pipeline's main colour property (_BaseColor for URP Lit) differs from the hidden
compatibility property _Color; emission needs the keyword, the GI flags and an HDR colour; the unused emissive material
GlowOld invites editing it instead of creating a new one.

A slot beyond 0 is read back by material name from the serialized array and tied to an asset path through
scene_materials (the used materials with their paths and users), so no rule about names being unique in the project is
needed. slot_paths() is shared with the solutions through ctx.private.
"""
from usk_eval import verifylib as V

RED = (0.8, 0.1, 0.1, 1.0)
GLOW_HDR = (2.0, 1.0, 0.0)

ASSERTIONS = [
    ("M1", 0.5, False, ["R3"], "Car still has exactly 2 material slots"),
    ("M2", 1.5, False, ["R1"], "Car's slot 0 is a new material asset saved under A with the pipeline's default Lit "
                               "shader and main colour (0.8, 0.1, 0.1, 1)"),
    ("M3", 1.5, True, ["R4"], "the shared BodyPaint asset is unchanged"),
    ("M4", 0.5, False, ["R4", "R5"], "Trim and the archived glow material are unchanged"),
    ("M5", 1.0, False, ["R4", "R5"], "CarTwin keeps [BodyPaint, Trim] and its state; Car's own object state is "
                                      "unchanged"),
    ("M6", 1.5, False, ["R2", "R3"], "Car's slot 1 holds the material asset saved at the glow path"),
    ("M7", 1.0, False, ["R2"], "the glow path is a Material asset with the glow name and the pipeline's default Lit "
                               "shader"),
    ("M8", 0.5, False, ["R2"], "the glow material has emission enabled (_EMISSION on, GI flags not EmissiveIsBlack)"),
    ("M9", 0.5, False, ["R2"], "the glow material's emission colour is (2, 1, 0)"),
    ("M10", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("M11", 0.5, False, ["R6", "G8"], "the answer reports the asset paths of Car's slot 0 and slot 1 materials"),
    ("M12", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "M1": {"zh-CN": "Car 要保持两个材质槽；把数组改大或改小都会破坏这一点。",
           "en": "Car must keep two material slots; growing or shrinking the array breaks that."},
    "M2": {"zh-CN": "槽 0 要换成新建并保存在 A 下的材质（默认 Lit 着色器，主颜色 0.8/0.1/0.1/1）；URP Lit 的主颜色属性是 _BaseColor，"
                    "_Color 只是隐藏的兼容属性。",
           "en": "Slot 0 must be a new material saved under A (default Lit shader, main colour 0.8/0.1/0.1/1); the main "
                 "colour property of URP Lit is _BaseColor, _Color is only a hidden compatibility property."},
    "M3": {"zh-CN": "BodyPaint 是 Car 和 CarTwin 共用的资产：按名称、id 或渲染器改色都会直接改写它。先新建材质、给新材质上色，再赋值。",
           "en": "BodyPaint is shared by Car and CarTwin: colouring through the name, id or renderer rewrites it. Create "
                 "the new material, colour that, then assign it."},
    "M4": {"zh-CN": "Trim 和 A/Archive 里的旧自发光材质都不能改；不要拿它们当作新的自发光材质。",
           "en": "Leave Trim and the old emissive material in A/Archive alone; do not turn them into the new glow "
                 "material."},
    "M5": {"zh-CN": "CarTwin 仍要使用 BodyPaint 和 Trim，Car 自身（名称、位置、组件）也不要改。",
           "en": "CarTwin must still use BodyPaint and Trim, and Car itself (name, position, components) must not "
                 "change."},
    "M6": {"zh-CN": "material_assign 只写槽 0；槽 1 要用 component_set_serialized_property 写 m_Materials.Array.data[1]，"
                    "并且放的是保存在指定路径的那个新材质。",
           "en": "material_assign only writes slot 0; write slot 1 through component_set_serialized_property on "
                 "m_Materials.Array.data[1], with the new material saved at the given path."},
    "M7": {"zh-CN": "在题目给出的路径新建名称正确的材质，并使用 project_get_render_pipeline 报告的默认 Lit 着色器。",
           "en": "Create the material with the given name at the given path, using the default Lit shader that "
                 "project_get_render_pipeline reports."},
    "M8": {"zh-CN": "自发光要同时满足 _EMISSION 关键字开启和 GI 标志不是 EmissiveIsBlack；material_set_emission 会一并处理。",
           "en": "Emission needs the _EMISSION keyword on and GI flags other than EmissiveIsBlack; "
                 "material_set_emission handles both."},
    "M9": {"zh-CN": "HDR 自发光颜色的 RGB 要是 (2, 1, 0)：例如 material_set_emission 用 r=1 g=0.5 b=0 intensity=2。",
           "en": "The HDR emission colour must have RGB (2, 1, 0), e.g. material_set_emission with r=1 g=0.5 b=0 "
                 "intensity=2."},
    "M10": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "M11": {"zh-CN": "答案中的两个路径要与 Car 槽 0 和槽 1 实际使用的材质资产一致，并写明 declaredComplete。",
            "en": "Report the asset paths of the materials Car actually uses in slot 0 and slot 1, and "
                  "declaredComplete."},
    "M12": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _used_materials(ctx, object_name):
    """[(material name, asset path)] of the materials that renderers on scene objects with this name use."""
    out = []
    for group in (_read(ctx, "scene_materials") or {}).get("shaders") or []:
        for material in group.get("materials") or []:
            if object_name in (material.get("users") or []):
                out.append((material.get("name"), material.get("path") or None))
    return out


def slot_paths(ctx, ref, object_name):
    """Slot count, material names and asset paths of a cube's renderer. Slot 0 is read from the renderer; a later slot
    is the one used material with that name, None when no or several materials of the name are in use."""
    p = ctx.probe
    ser = p.ser(ref, "MeshRenderer") or {}
    size = V.as_int(ser.get("m_Materials.Array.size")) or 0
    names = []
    for index in range(size):
        parsed = V.parse_ref(ser.get("m_Materials.Array.data[%d]" % index))
        names.append(parsed[2] if parsed else None)
    used = _used_materials(ctx, object_name)
    paths = []
    for index, slot_name in enumerate(names):
        if index == 0:
            paths.append((p.material(iid=ref) or {}).get("materialPath") or None)
            continue
        found = sorted({path for name, path in used if name == slot_name and path})
        paths.append(found[0] if len(found) == 1 else None)
    return {"size": size, "names": names, "paths": paths}


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    wanted, prop = ctx.env.get("defaultShader"), ctx.env.get("colorProperty")
    car = slot_paths(ctx, h["car"], "Car")
    slot0, slot1 = (car["paths"] + [None, None])[:2]
    card.check("M1", car["size"] == 2, expected=2, actual=car)

    fixture_materials = {h["bodyPaint"], h["trim"], h["oldGlow"], h["glowPath"]}
    state0 = V.material_state(p, slot0) if slot0 else None
    color0 = V.material_color(p, state0, prop) if state0 and prop else None
    card.check("M2", bool(state0) and slot0 not in fixture_materials and V.under(slot0, ctx.A) and bool(wanted) and
               state0.get("shader") == wanted and V.close_tuple(color0, RED),
               expected={"shader": wanted, prop or "colorProperty": RED},
               actual={"slot0": slot0, "shader": (state0 or {}).get("shader"), prop or "colorProperty": color0})

    ok_body, diff_body = V.same(c0["materials"].get("bodyPaint"), V.material_state(p, h["bodyPaint"]))
    card.check("M3", ok_body, actual=diff_body)
    kept = {}
    for key in ("trim", "oldGlow"):
        ok, diff = V.same(c0["materials"].get(key), V.material_state(p, h[key]))
        if not ok:
            kept[key] = diff
    card.check("M4", not kept, actual=kept)

    twin = {"go": V.go_state(p, h["carTwin"]), "slots": slot_paths(ctx, h["carTwin"], "CarTwin")}
    ok_twin, diff_twin = V.same(c0["carTwin"], twin)
    ok_car, diff_car = V.same(c0["car"], V.go_state(p, h["car"]))
    card.check("M5", ok_twin and ok_car, actual={"twin": diff_twin, "car": diff_car})

    card.check("M6", slot1 is not None and slot1 == h["glowPath"], expected=h["glowPath"],
               actual={"names": car["names"], "paths": car["paths"]})

    asset = p.asset(h["glowPath"])
    glow = p.material(path=h["glowPath"]) if asset else None
    card.check("M7", bool(asset) and asset.get("type") == "Material" and asset.get("name") == h["glowName"] and
               bool(wanted) and (glow or {}).get("shader") == wanted,
               expected={"type": "Material", "name": h["glowName"], "shader": wanted},
               actual={"type": (asset or {}).get("type"), "name": (asset or {}).get("name"),
                       "shader": (glow or {}).get("shader")})
    keywords = (glow or {}).get("keywords") or []
    flags = (glow or {}).get("giFlags")
    card.check("M8", "_EMISSION" in keywords and flags not in (None, "EmissiveIsBlack", 4, "4"),
               actual={"keywords": keywords, "giFlags": flags})
    emission = V.material_color(p, V.material_state(p, h["glowPath"]), "_EmissionColor") if glow else None
    card.check("M9", emission is not None and V.close_tuple(emission[:3], GLOW_HDR, 0.01), expected=GLOW_HDR,
               actual=emission)

    bad = ctx.violations()
    card.check("M10", not bad, actual={"violations": bad[:10]})
    reported = (V.norm_path(V.answer(ctx, "bodyMaterialPath")), V.norm_path(V.answer(ctx, "glowMaterialPath")))
    card.check("M11", bool(slot0) and bool(slot1) and reported == (slot0, slot1) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), expected={"slot0": slot0, "slot1": slot1},
               actual=ctx.answer)
    direct = ctx.direct_writes()
    card.check("M12", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
