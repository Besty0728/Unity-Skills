"""S03 private verifier: reparent keeping the local pose.

What the task probes: gameobject_set_parent keeps the world pose, and Props is offset, rotated and scaled, so the
local numbers drift unless they are written back; gameobject_set_transform rotX/Y/Z are world angles; another object
named Lamp sits under Props/Shelf; Lamp must end up as Props' last child, as the same object, with its Bulb child and
its components untouched.
"""
from usk_eval import verifylib as V

LAMP_POS = (0.5, 1.2, -0.3)
LAMP_ROT = (10.0, 20.0, 0.0)
LAMP_SCALE = (0.8, 0.8, 0.8)

ASSERTIONS = [
    ("B1", 1.5, False, ["R1"], "the original Lamp (same id) is now a child of Props"),
    ("B2", 2.0, False, ["R2"], "Lamp's local position equals its old local position"),
    ("B3", 2.0, False, ["R2"], "Lamp's local rotation equals its old local rotation (10, 20, 0)"),
    ("B4", 1.0, False, ["R2"], "Lamp's local scale equals its old local scale"),
    ("B5", 1.0, False, ["R3"], "Bulb (same id) is still Lamp's only child with its local pose; Lamp's components, "
                               "their order and enabled states are unchanged"),
    ("B6", 1.0, True, ["R4"], "every other object is unchanged; Props keeps its children in order and gains only "
                              "Lamp; Loose keeps its other children in order"),
    ("B7", 0.5, False, ["R5", "G8"], "the answer reports Lamp's path and local pose after the move"),
    ("B8", 0.25, False, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("B9", 0.25, False, ["G4"], "no file under A was written around the skills"),
    ("B10", 0.5, False, ["R1"], "the Lamp under Props is Props' last child"),
]

HINTS = {
    "B1": {"zh-CN": "要移动原来的 Lamp 本身（同一个对象），不要删除后重建，也不要移动 Shelf 下的另一个 Lamp。",
           "en": "Move the original Lamp itself (the same object); do not recreate it or move the other Lamp under "
                 "Shelf."},
    "B2": {"zh-CN": "gameobject_set_parent 保持世界位姿；移动后要把本地位置写回原来的数值。",
           "en": "gameobject_set_parent keeps the world pose; write the old local position back after the move."},
    "B3": {"zh-CN": "gameobject_set_transform 的 rotX/Y/Z 是世界角；本地旋转要用 localEulerAngles 或 m_LocalRotation 写入。",
           "en": "gameobject_set_transform rotX/Y/Z are world angles; write the local rotation through "
                 "localEulerAngles or m_LocalRotation."},
    "B4": {"zh-CN": "Props 有 1.5 倍缩放；移动后要把本地缩放写回原来的数值。",
           "en": "Props is scaled by 1.5; write the old local scale back after the move."},
    "B5": {"zh-CN": "Bulb 子物体及 Lamp 的组件（含启用状态与顺序）都要保持不变。",
           "en": "Keep the Bulb child and Lamp's components (enabled states and order included) as they were."},
    "B6": {"zh-CN": "不要改动其他对象，也不要改变 Props 与 Loose 其余子物体的顺序。",
           "en": "Leave every other object alone and keep the order of the other children of Props and Loose."},
    "B7": {"zh-CN": "答案里的路径与本地数值要与 Lamp 的最终状态一致，并写明 declaredComplete。",
           "en": "Report Lamp's final path and local values, and declaredComplete."},
    "B8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "B9": {"zh-CN": "不要绕过技能直接在 A 下写文件。",
           "en": "Do not write files under A directly; use the skills."},
    "B10": {"zh-CN": "Lamp 必须是 Props 的最后一个子物体。",
            "en": "Lamp must be the last child of Props."},
}


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _moved(ctx, lamp, props_children):
    """The Lamp now under Props: the original when it moved, else the last new child of Props named Lamp, so B2-B4
    grade the pose of whatever was put there while B1/B5 grade identity."""
    h = ctx.handles
    if lamp and lamp.get("parentPath") == h["propsPath"]:
        return lamp
    before = {_key(ref) for ref in ctx.c0.get("propsChildren") or []}
    added = [child for child in props_children if child.get("name") == "Lamp" and _key(child) not in before]
    return ctx.probe.go(added[-1]) if added else None


def _answer_ok(ctx, moved, trs):
    if not moved or not trs:
        return False
    euler = V.floats(V.answer(ctx, "localEulerAngles"))
    if len(euler) != 3:
        return False
    return (V.norm_path(V.answer(ctx, "newPath")) == moved.get("path") and
            V.close_tuple(V.answer(ctx, "localPosition"), trs["position"], 1e-2) and
            V.quat_close(V.quat_from_euler(*euler), trs["rotation"], 0.5) and
            V.close_tuple(V.answer(ctx, "localScale"), trs["scale"], 1e-2) and
            isinstance(V.answer(ctx, "declaredComplete"), bool))


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    lamp = p.go(h["lamp"])
    props_children = p.children(h["props"])
    card.check("B1", bool(lamp) and lamp.get("name") == "Lamp" and lamp.get("parentPath") == h["propsPath"],
               expected=h["propsPath"], actual=(lamp or {}).get("parentPath"))
    moved = _moved(ctx, lamp, props_children)
    trs = V.local_trs(p, moved) if moved else {}
    card.check("B2", bool(trs) and V.close_tuple(trs.get("position"), LAMP_POS),
               expected=LAMP_POS, actual=trs.get("position"))
    want_rot = V.quat_from_euler(*LAMP_ROT)
    card.check("B3", bool(trs) and V.quat_close(trs.get("rotation"), want_rot),
               expected=want_rot, actual=trs.get("rotation"))
    card.check("B4", bool(trs) and V.close_tuple(trs.get("scale"), LAMP_SCALE),
               expected=LAMP_SCALE, actual=trs.get("scale"))

    bulb = p.go(h["bulb"])
    bulb_local = V.local_trs(p, h["bulb"]) if bulb else None
    components = [[comp.get("type"), comp.get("enabled")] for comp in p.components(h["lamp"])] if lamp else None
    lamp_children = [V.object_id(child) for child in (lamp or {}).get("children") or []]
    bulb_ok = (bool(bulb) and bulb.get("name") == "Bulb" and V.same(c0.get("bulbLocal"), bulb_local)[0] and
               len(lamp_children) == 1 and V.same_id(lamp_children[0], h["bulb"]))
    card.check("B5", bulb_ok and components == c0.get("lampComponents"),
               actual={"bulbLocal": bulb_local, "lampChildren": lamp_children, "components": components},
               expected={"bulbLocal": c0.get("bulbLocal"), "components": c0.get("lampComponents")})

    diffs = []
    for ref, state in c0.get("others") or []:
        ok, diff = V.same(state, V.go_state(p, ref))
        if not ok:
            diffs.append({str(ref): diff})
    for name in ("props", "loose"):
        ok, diff = V.same(c0.get(name), V.go_state(p, h[name], with_children=False))
        if not ok:
            diffs.append({name: diff})
    before = [_key(ref) for ref in c0.get("propsChildren") or []]
    kept = [_key(child) for child in props_children if _key(child) in before]
    added = [child for child in props_children if _key(child) not in before]
    props_ok = kept == before and len(added) <= 1 and all(child.get("name") == "Lamp" for child in added)
    loose_children = [_key(child) for child in p.children(h["loose"]) if not V.same_id(child, h["lamp"])]
    loose_ok = loose_children == [_key(ref) for ref in c0.get("looseChildren") or [] if not V.same_id(ref, h["lamp"])]
    card.check("B6", not diffs and props_ok and loose_ok,
               actual={"diffs": diffs[:5], "looseChildren": loose_children,
                       "propsChildren": [(child.get("name"), _key(child)) for child in props_children]})

    card.check("B7", _answer_ok(ctx, moved, trs), actual=ctx.answer)
    bad = ctx.violations()
    card.check("B8", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("B9", not direct, actual={"directWrites": direct[:10]})
    last = props_children[-1] if props_children else None
    card.check("B10", bool(moved) and last is not None and V.same_id(last, moved),
               expected=(moved or {}).get("path"), actual=(last or {}).get("path"))

# Producer:Betsy
