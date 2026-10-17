"""S01 private verifier: Crate cube with a new material.

What the task probes: gameobject_create takes x/y/z local to the parent unless space='world', and Props sits at a
seeded offset, so local numbers put Crate in the wrong place; a material of the same name already exists at
A/Common/CrateMaterial.mat and is used by Barrel, so finding "CrateMaterial" by name and reusing or editing it is
wrong; the shader must be the pipeline's default Lit (URP Lit or Standard); the material must be saved as an asset at
the given path.
"""
from usk_eval import verifylib as V

CRATE_POS = (0.0, 0.5, 0.0)
CUBE_COMPONENTS = sorted(["Transform", "MeshFilter", "MeshRenderer", "BoxCollider"])

ASSERTIONS = [
    ("A1", 1.5, False, ["R1"], "exactly one new Crate under Props, with the components of a Cube primitive"),
    ("A2", 1.5, False, ["R2"], "Crate's world position is (0, 0.5, 0)"),
    ("A3", 0.5, False, ["R2"], "Crate has identity local rotation and unit local scale"),
    ("A4", 0.5, False, ["R1"], "Crate's mesh is the built-in Cube"),
    ("A5", 1.5, False, ["R3"], "a Material asset at A/Materials/CrateMaterial.mat uses the pipeline's default Lit "
                               "shader"),
    ("A6", 2.0, False, ["R4"], "Crate's renderer has exactly one slot and slot 0 is A/Materials/CrateMaterial.mat"),
    ("A7", 1.0, True, ["R5"], "A/Common/CrateMaterial.mat and Barrel (state and material) are unchanged"),
    ("A8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("A9", 0.5, False, ["R6", "G8"], "the answer reports Crate's path and id and its material path"),
    ("A10", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "A1": {"zh-CN": "在 Props 下只新建一个 Cube 基本体 Crate（带 MeshFilter、MeshRenderer 与 BoxCollider），不要重复创建。",
           "en": "Create exactly one Cube primitive named Crate under Props (MeshFilter, MeshRenderer, BoxCollider); "
                 "do not create duplicates."},
    "A2": {"zh-CN": "gameobject_create 的 x/y/z 默认是相对父物体的本地坐标；Props 有偏移，要用 space='world' 或换算成本地坐标。",
           "en": "gameobject_create takes x/y/z local to the parent unless space='world'; Props is offset, so use "
                 "world space or convert."},
    "A3": {"zh-CN": "Crate 的本地旋转应为 0，本地缩放应为 1。",
           "en": "Crate's local rotation must be zero and its local scale one."},
    "A4": {"zh-CN": "用 primitiveType='Cube' 创建，网格应是内置 Cube。",
           "en": "Create it with primitiveType='Cube' so the mesh is the built-in Cube."},
    "A5": {"zh-CN": "材质必须新建并保存在题目给出的路径，着色器用 project_get_render_pipeline 报告的默认 Lit。",
           "en": "Create and save the material at the given path, with the default Lit shader that "
                 "project_get_render_pipeline reports."},
    "A6": {"zh-CN": "把新材质赋给 Crate 的唯一材质槽；同名的 A/Common/CrateMaterial.mat 不是新材质。",
           "en": "Put the new material in Crate's only slot; the same-named A/Common/CrateMaterial.mat is not the "
                 "new one."},
    "A7": {"zh-CN": "不要修改或复用已有的 A/Common/CrateMaterial.mat，也不要改动 Barrel。",
           "en": "Neither edit nor reuse the existing A/Common/CrateMaterial.mat, and leave Barrel alone."},
    "A8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "A9": {"zh-CN": "答案中的路径与 id 要与 Crate 的实际状态一致，并写明 declaredComplete。",
           "en": "Report Crate's actual path and id, the material path in its slot, and declaredComplete."},
    "A10": {"zh-CN": "不要绕过技能直接在 A 下写文件。",
            "en": "Do not write files under A directly; use the skills."},
}


def material_path(ctx):
    return ctx.asset("Materials", "CrateMaterial.mat")


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _crate(ctx):
    added = V.new_children(ctx.probe, ctx.handles["props"], ctx.c0.get("propsChildren") or [])
    crates = [child for child in added if child.get("name") == "Crate"]
    reported = V.answer(ctx, "objectInstanceId")
    chosen = next((child for child in crates if V.same_id(child, reported)), crates[0] if crates else None)
    return added, crates, V.object_id(chosen) if chosen else None


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    added, crates, crate = _crate(ctx)
    info = p.go(crate) if crate is not None else None
    comps = sorted((info or {}).get("components") or [])
    card.check("A1", len(added) == 1 and len(crates) == 1 and comps == CUBE_COMPONENTS,
               expected={"added": ["Crate"], "components": CUBE_COMPONENTS},
               actual={"added": [child.get("name") for child in added], "components": comps})
    card.check("A2", bool(info) and V.close_tuple(info.get("position"), CRATE_POS),
               expected=CRATE_POS, actual=(info or {}).get("position"))
    trs = V.local_trs(p, crate) if info else {}
    card.check("A3", bool(trs) and V.identity_rotation(trs.get("rotation")) and
               V.close_tuple(trs.get("scale"), (1, 1, 1)), actual=trs)
    mesh = (_read(ctx, "model_get_mesh_info", **V.address(crate)) if info else None) or {}
    card.check("A4", mesh.get("name") == "Cube" and mesh.get("vertexCount") == 24,
               expected={"name": "Cube", "vertexCount": 24},
               actual={key: mesh.get(key) for key in ("name", "vertexCount")})
    path = material_path(ctx)
    asset = p.asset(path)
    shader = (p.material(path=path) or {}).get("shader") if asset else None
    wanted = ctx.env.get("defaultShader")
    card.check("A5", bool(asset) and asset.get("type") == "Material" and bool(wanted) and shader == wanted,
               expected={"type": "Material", "shader": wanted},
               actual={"type": (asset or {}).get("type"), "shader": shader})
    slot = (p.material(iid=crate) or {}).get("materialPath") if info else None
    slots = V.as_int((p.ser(crate, "MeshRenderer") or {}).get("m_Materials.Array.size")) if info else None
    card.check("A6", slot == path and slots == 1, expected={"slot0": path, "slots": 1},
               actual={"slot0": slot, "slots": slots})
    ok_common, diff_common = V.same(c0.get("commonMaterial"), V.material_state(p, h["commonMaterial"]))
    ok_barrel, diff_barrel = V.same(c0.get("barrel"), V.go_state(p, h["barrel"]))
    barrel_material = (p.material(iid=h["barrel"]) or {}).get("materialPath")
    card.check("A7", ok_common and ok_barrel and barrel_material == c0.get("barrelMaterial"),
               actual={"commonDiff": diff_common, "barrelDiff": diff_barrel, "barrelMaterial": barrel_material})
    bad = ctx.violations()
    card.check("A8", not bad, actual={"violations": bad[:10]})
    reported_ok = (crate is not None and V.same_id(V.answer(ctx, "objectInstanceId"), crate) and
                   V.norm_path(V.answer(ctx, "objectPath")) == (info or {}).get("path") and
                   V.norm_path(V.answer(ctx, "materialPath")) == (slot or "") and
                   isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("A9", reported_ok, actual=ctx.answer)
    direct = ctx.direct_writes()
    card.check("A10", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
