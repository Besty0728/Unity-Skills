"""T3 private verifier: a hinge chain over five same-name planks, a physic material, a layer and a collision pair.

What the task probes: the planks are told apart only by world X (the sibling order is shuffled and the Spare group
holds one more Plank), so a lookup by name hits the wrong object; one plank carries a SphereCollider ahead of its
BoxCollider and physics_set_material writes to the first collider; gameobject_set_layer_batch recursive also moves the
Rail children; HingeJoint.m_ConnectedBody is a hidden property (component_get_serialized_properties needs
includeHidden); the physic material must exist once, at the given path, with the given values; the collision pair is
the one allowed project-settings change and no other pair or layer may move.

The run state holds the planks in sibling order and their states; the numbering by world X is derived here from the
states at issue.
"""
import os
import re
import time

from usk_eval import verifylib as V

MASS = 2.0
AXIS = (0, 0, 1)
FRICTION = (("dynamicFriction", 0.8), ("staticFriction", 0.9), ("bounciness", 0.0))
ADDED = ("Rigidbody", "HingeJoint")
MATERIAL_TYPE = re.compile(r"Physics?Material")
MATERIAL_FILES = (".physicMaterial", ".physicsMaterial")
SETTINGS_FILES = ("ProjectSettings/DynamicsManager.asset", "ProjectSettings/TagManager.asset")
PAUSE = 0.02

ASSERTIONS = [
    ("P1", 1.0, False, ["R1"], "each plank has exactly one Rigidbody (mass 2) and exactly one HingeJoint (fraction)"),
    ("P2", 1.5, False, ["R2"], "hinge chain by world X: plank 1 -> AnchorLeft, plank k -> plank k-1 (fraction)"),
    ("P3", 0.5, False, ["R3"], "every plank's hinge axis is (0, 0, 1)"),
    ("P4", 1.0, False, ["R4"], "the physic material at the given path has dynamic 0.8, static 0.9, bounciness 0"),
    ("P5", 0.5, False, ["R4"], "exactly one physic material asset under A"),
    ("P6", 1.5, False, ["R5"], "the material is on every plank's BoxCollider and on no other collider (the plank's "
                               "SphereCollider, AnchorLeft, Spare/Plank)"),
    ("P7", 1.0, False, ["R6"], "the five planks are on the first layer; their Rail children keep their layer"),
    ("P8", 1.0, False, ["R7"], "the pair of the two layers no longer collides; every other pair with either layer "
                               "and the set of layer names are unchanged"),
    ("P9", 1.0, True, ["R8"], "everything else is unchanged: Spare/Plank, AnchorLeft, the planks' names, poses, "
                              "children, order and original components, the Rails, the Bridge hierarchy"),
    ("P10", 0.25, True, ["G1", "G2"], "no change outside R/A (the collision pair aside), no other scene saved or "
                                      "opened"),
    ("P11", 0.5, False, ["R9", "G8"], "the answer reports the plank numbers and ids, the material path and the "
                                      "layer index"),
    ("P12", 0.25, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "P1": {"zh-CN": "每块木板上要有且只有一个 Rigidbody（质量 2）和一个 HingeJoint；重复添加组件或漏设质量都会失败。",
           "en": "Every plank needs exactly one Rigidbody with mass 2 and exactly one HingeJoint; adding a component "
                 "twice or leaving the default mass fails."},
    "P2": {"zh-CN": "木板按世界坐标 X 编号，不是按层级顺序；木板 1 连到 AnchorLeft，其余每块连到编号小一的木板。"
                    "connectedBody 是隐藏属性，读回要用 component_get_serialized_properties 的 includeHidden。",
           "en": "Planks are numbered by world X, not by hierarchy order; plank 1 connects to AnchorLeft and every "
                 "other plank to the plank numbered one lower. connectedBody is a hidden property: read it back "
                 "with includeHidden on component_get_serialized_properties."},
    "P3": {"zh-CN": "所有 HingeJoint 的轴要设为 (0, 0, 1)，默认值不是。",
           "en": "Set every HingeJoint axis to (0, 0, 1); the default is different."},
    "P4": {"zh-CN": "物理材质要保存在题目给出的路径，并且动摩擦 0.8、静摩擦 0.9、弹性 0（不要对调两个摩擦值）。",
           "en": "Save the physic material at the given path with dynamic friction 0.8, static friction 0.9 and "
                 "bounciness 0 (do not swap the two frictions)."},
    "P5": {"zh-CN": "physics_create_material 遇到同名会自动改名（例如 'BridgeGrip_x 1'），重复创建会在 A 下留下多余的物理材质。",
           "en": "physics_create_material renames a clash (for example 'BridgeGrip_x 1'), so creating the material "
                 "twice leaves a second physic material under A."},
    "P6": {"zh-CN": "physics_set_material 作用于对象上的第一个碰撞体，而有一块木板的 SphereCollider 排在 BoxCollider 之前；"
                    "请直接设置每个 BoxCollider 的 m_Material，并且不要碰 Spare 下的 Plank 和 AnchorLeft。",
           "en": "physics_set_material acts on the first collider of an object and one plank has its SphereCollider "
                 "ahead of the BoxCollider; set the m_Material of each BoxCollider directly and leave the Plank "
                 "under Spare and AnchorLeft alone."},
    "P7": {"zh-CN": "只设置木板本身的 Layer（recursive 要保持关闭），子物体 Rail 保持原来的层。",
           "en": "Set only the planks' own Layer (leave recursive off); the Rail children keep their layer."},
    "P8": {"zh-CN": "只关闭这两个层之间的碰撞；不要改动任何其他层对，也不要增加、删除或重命名层。",
           "en": "Disable collision between these two layers only; do not touch any other pair and do not add, "
                 "remove or rename a layer."},
    "P9": {"zh-CN": "不要改动 Spare 下的 Plank、AnchorLeft，以及木板的名称、位置、子物体和组件顺序。",
           "en": "Leave the Plank under Spare and AnchorLeft alone, and keep the planks' names, positions, children "
                 "and component order as they were."},
    "P10": {"zh-CN": "所有修改都必须在 R 与 A 之内（题目允许的碰撞设置除外），且只能保存本轮场景。",
            "en": "Keep every change inside R and A (the allowed collision setting aside), and save no scene but "
                  "this run's."},
    "P11": {"zh-CN": "答案中的木板编号与 id、材质路径和层序号要与最终状态一致，并写明 declaredComplete。",
            "en": "Report the actual plank numbers and ids, the material path, the layer index and "
                  "declaredComplete."},
    "P12": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def allowed_change(ctx, rec):
    path = "/".join((rec.get("assetPath") or "").split("\\"))
    if path.endswith(".meta"):
        path = path[:-5]
    return path in SETTINGS_FILES


def by_x(pairs):
    """Plank ids in ascending order of world X; `pairs` are (id, x)."""
    return [ref for ref, _x in sorted(pairs, key=lambda pair: pair[1])]


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _null_ref(value):
    ref = V.parse_ref(value)
    return ref is None or ref[0] == 0


def _x(state):
    return (((state or {}).get("position")) or {}).get("x", 0.0)


def _order(c0):
    """Plank ids by world X at issue."""
    return by_x([(row[0], _x(row[1])) for row in c0.get("planks") or []])


def _layers(ctx):
    res = _read(ctx, "editor_get_layers") or {}
    return {str(row["index"]): row.get("name") for row in res.get("layers") or []
            if isinstance(row, dict) and "index" in row}


def _plank_view(state):
    state = dict(state or {})
    state.pop("layer", None)
    state["components"] = [kind for kind in state.get("components") or [] if kind not in ADDED]
    return state


def _rail_view(state):
    state = dict(state or {})
    state.pop("layer", None)
    return state


def _joint_body(ctx, ref):
    """Id of the GameObject owning the Rigidbody that ref's HingeJoint is connected to; None when there is none."""
    res = _read(ctx, "component_get_serialized_properties", componentType="HingeJoint", includeHidden=True,
                limit=500, **V.address(ref))
    for row in (res or {}).get("properties") or []:
        if isinstance(row, dict) and row.get("propertyPath") == "m_ConnectedBody":
            return ctx.probe.owner(row.get("value"))
    return None


def _friction(props):
    out = []
    for name, _want in FRICTION:
        value = V.floats(props.get(name) if name in props else props.get("m_" + name[0].upper() + name[1:]))
        out.append(value[0] if len(value) == 1 else None)
    return out


def _physic_files(ctx):
    out = []
    for base, _dirs, files in os.walk(os.path.join(ctx.project_path, *ctx.A.split("/"))):
        for name in files:
            if name.endswith(MATERIAL_FILES):
                out.append("/".join(os.path.relpath(os.path.join(base, name), ctx.project_path).split(os.sep)))
    return sorted(out)


def _reported(ctx, order):
    """(ok, rows) for the plank list of the answer: five rows, numbers 1 to 5, each with the id of its plank."""
    rows = V.answer(ctx, "planks")
    rows = rows if isinstance(rows, list) else []
    seen = {}
    for row in rows:
        if isinstance(row, dict):
            number = V.as_int(row.get("number"))
            if number is not None and number not in seen:
                seen[number] = V.object_id(row.get("instanceId"))
    ok = len(rows) == len(order) and all(V.same_id(seen.get(index + 1), ref) for index, ref in enumerate(order))
    return ok, rows


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    order = _order(c0)
    anchor = h["anchor"]
    plank_layer, guard_layer = h["plankLayer"], h["guardLayer"]

    rows, good = [], 0
    for ref in order:
        bodies, joints = p.component_count(ref, "Rigidbody"), p.component_count(ref, "HingeJoint")
        mass = V.floats((p.ser(ref, "Rigidbody") or {}).get("m_Mass"))
        rows.append({"rigidbodies": bodies, "hinges": joints, "mass": mass})
        good += bodies == 1 and joints == 1 and len(mass) == 1 and abs(mass[0] - MASS) < 1e-3
    card.fraction("P1", good / float(len(order)) if order else 0.0, expected="1 Rigidbody (mass 2) + 1 HingeJoint",
                  actual=rows)

    hits, owners = 0, []
    for index, ref in enumerate(order):
        owner = _joint_body(ctx, ref)
        want = anchor if index == 0 else order[index - 1]
        owners.append({"plank": index + 1, "connectedTo": (p.go(owner) or {}).get("path") if owner is not None
                       else None})
        hits += V.same_id(owner, want)
    card.fraction("P2", hits / float(len(order)) if order else 0.0, expected="plank 1 -> AnchorLeft, plank k -> k-1",
                  actual=owners)

    axes = [(p.ser(ref, "HingeJoint") or {}).get("m_Axis") for ref in order]
    card.check("P3", bool(axes) and all(V.close_tuple(axis, AXIS) for axis in axes), expected=AXIS, actual=axes)

    path = h["materialPath"]
    so = p.so(path)
    props = {row.get("propertyPath"): row.get("value") for row in (so or {}).get("properties") or []
             if isinstance(row, dict)}
    values = _friction(props)
    typed = bool(so) and (not (so or {}).get("typeName") or bool(MATERIAL_TYPE.search(so["typeName"])))
    card.check("P4", typed and all(value is not None and abs(value - want) < 1e-3
                                    for value, (_name, want) in zip(values, FRICTION)),
               expected=dict(FRICTION), actual={"type": (so or {}).get("typeName"), "values": values})

    files = _physic_files(ctx)
    card.check("P5", files == [path], expected=[path], actual=files)

    mat_name = os.path.splitext(os.path.basename(path))[0]
    rows, planks_ok = [], bool(order)
    for ref in order:
        box = V.parse_ref((p.ser(ref, "BoxCollider") or {}).get("m_Material"))
        row = {"box": box}
        ok = bool(box) and box[2] == mat_name and bool(MATERIAL_TYPE.search(box[1]))
        sphere = p.ser(ref, "SphereCollider")
        if sphere is not None:
            row["sphere"] = sphere.get("m_Material")
            ok = ok and _null_ref(sphere.get("m_Material"))
        planks_ok = planks_ok and ok
        rows.append(row)
    others = {"anchor": (p.ser(anchor, "BoxCollider") or {}).get("m_Material"),
              "spare": (p.ser(h["spare"], "BoxCollider") or {}).get("m_Material")}
    card.check("P6", planks_ok and all(_null_ref(value) for value in others.values()), expected=mat_name,
               actual={"planks": rows, "others": others})

    layers_now = [(p.go(ref) or {}).get("layer") for ref in order]
    rail_layers = [((V.go_state(p, ref) or {}).get("layer"), (state or {}).get("layer"))
                   for ref, state in c0.get("rails") or []]
    card.check("P7", bool(order) and all(layer == plank_layer["name"] for layer in layers_now) and
               bool(rail_layers) and all(now == before for now, before in rail_layers),
               expected=plank_layer["name"], actual={"planks": layers_now, "rails": rail_layers})

    before = c0.get("collision") or {}
    first, second = plank_layer["index"], guard_layer["index"]
    target = "%d,%d" % tuple(sorted((first, second)))
    now = {}
    for key in sorted(before):
        low, high = (int(part) for part in key.split(","))
        time.sleep(PAUSE)
        now[key] = (_read(ctx, "physics_get_layer_collision", layer1=low, layer2=high) or {}).get("collisionEnabled")
    moved = sorted(key for key in now if key != target and now[key] != before[key])
    names_now, names_then = _layers(ctx), c0.get("layers") or {}
    card.check("P8", bool(before) and now.get(target) is False and not moved and names_now == names_then,
               expected={target: False}, actual={target: now.get(target), "otherPairsChanged": moved[:10],
                                                 "layers": names_now, "layersAtIssue": names_then})

    diffs = []

    def compare(label, was, is_now):
        same, diff = V.same(was, is_now)
        if not same:
            diffs.append({label: diff})

    compare("bridge", c0.get("bridge"), V.go_state(p, h["bridge"]))
    compare("planksRoot", c0.get("planksRoot"), V.go_state(p, h["planksRoot"]))
    for ref, state in c0.get("planks") or []:
        compare("plank %s" % V.object_id(ref), _plank_view(state), _plank_view(V.go_state(p, ref)))
    for ref, state in c0.get("rails") or []:
        compare("rail %s" % V.object_id(ref), _rail_view(state), _rail_view(V.go_state(p, ref)))
    anchor_c0 = c0.get("anchor") or {}
    compare("anchor", {"go": anchor_c0.get("go"), "rb": anchor_c0.get("rb")},
            {"go": V.go_state(p, anchor), "rb": p.ser(anchor, "Rigidbody")})
    spare_c0 = c0.get("spare") or {}
    compare("spare", {"go": spare_c0.get("go"), "root": spare_c0.get("root")},
            {"go": V.go_state(p, h["spare"]), "root": V.go_state(p, h["spareRoot"])})
    card.check("P9", bool(c0.get("planks")) and not diffs, actual=diffs[:5])

    bad = ctx.violations()
    card.check("P10", not bad, actual={"violations": bad[:10]})

    numbered, reported_rows = _reported(ctx, order)
    plank_now = (p.go(order[0]) or {}).get("layer") if order else None
    index = V.as_int(V.answer(ctx, "plankLayerIndex"))
    named = {int(key): value for key, value in names_now.items()}
    reported_ok = (numbered and V.norm_path(V.answer(ctx, "materialPath")) == path and index is not None and
                   bool(plank_now) and named.get(index) == plank_now and
                   isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("P11", bool(reported_ok), expected={"layerIndex": first}, actual=ctx.answer)

    direct = ctx.direct_writes()
    card.check("P12", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
