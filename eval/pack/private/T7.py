"""T7 private verifier: an incremental repair of the crates under R/Yard.

What the task probes: nine matching crates are inactive (six themselves, three through the inactive group Stack) and
batch_query_gameobjects skips inactive objects unless includeInactive is set; its namePattern ignores case unless
caseSensitive is set, so crate_A01, Crate_a01 and CRATE_A01 come along; gameobject_find cuts its list at 50 objects by
default; R/Archive/Crate_A01 matches the pattern but lies outside the Yard (Row_A holds a Crate_A01 too); Crate_B07
exists in rows B and E, so a name reaches the same object twice; six matches carry a Sphere or Mesh collider and no
BoxCollider (it stays, a BoxCollider is added), two carry a disabled BoxCollider (it stays disabled); eleven matches
have a Rigidbody whose mass is not 4.5, the others already have it.

The run state holds the neutral state of every object under R (name, path, active flag, components, children and
Rigidbody masses). The matching objects and the intended changes are derived here from that state and the rules of the
prompt, so nothing in it names them.
"""
import re

from usk_eval import verifylib as V

PATTERN = r"^Crate_[A-F][0-9]{2}$"
MASS = 4.5
TOLERANCE = 1e-4

ASSERTIONS = [
    ("Y1", 1.75, False, ["R1", "R2"], "every matching object that had no BoxCollider now has exactly one, enabled "
                                      "(fraction)"),
    ("Y2", 1.0, False, ["R2"], "matching objects that already had a BoxCollider keep the same BoxColliders with the "
                               "same enabled states"),
    ("Y3", 0.5, False, ["R2"], "matching objects keep their other components with the same enabled states"),
    ("Y4", 1.75, False, ["R1", "R3"], "every matching object whose Rigidbody mass was not 4.5 now has mass 4.5 "
                                      "(fraction)"),
    ("Y5", 1.0, False, ["R3"], "no Rigidbody added or removed; masses that were 4.5 stay 4.5"),
    ("Y6", 1.0, True, ["R4"], "objects that do not match (similar names, the Archive crate, fillers, groups) keep "
                              "their components and masses"),
    ("Y7", 0.5, False, ["R4"], "hierarchy unchanged: no object created or deleted, same names, paths, active flags "
                               "and sibling order"),
    ("Y8", 0.5, False, ["R5"], "addedBoxCollider lists exactly the objects that gained a BoxCollider"),
    ("Y9", 0.5, False, ["R5"], "fixedMass lists exactly the objects whose Rigidbody mass changed"),
    ("Y10", 0.5, False, ["R5", "G8"], "matchedCount equals the number of matching objects; declaredComplete is a "
                                      "boolean"),
    ("Y11", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("Y12", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "Y1": {"zh-CN": "每个自身没有 BoxCollider 的匹配对象都要得到恰好一个启用的 BoxCollider，包括未激活的对象和未激活父物体下的对象；"
                    "batch_query_gameobjects 默认不含未激活对象，gameobject_find 默认最多返回 50 个。",
           "en": "Every matching object without a BoxCollider needs exactly one enabled BoxCollider, inactive objects "
                 "and objects below an inactive parent included; batch_query_gameobjects skips inactive objects by "
                 "default and gameobject_find returns at most 50."},
    "Y2": {"zh-CN": "已有 BoxCollider 的对象不要再添加，也不要改变其启用状态（有两个对象的 BoxCollider 是禁用的）。",
           "en": "Do not add another BoxCollider to an object that has one, and leave its enabled state alone (two "
                 "of them are disabled)."},
    "Y3": {"zh-CN": "Sphere/Mesh 碰撞体和渲染器要原样保留，只额外添加 BoxCollider。",
           "en": "Keep Sphere and Mesh colliders and renderers as they are and only add the BoxCollider."},
    "Y4": {"zh-CN": "质量不是 4.5 的 Rigidbody 都要改成 4.5，包括未激活的对象。",
           "en": "Set every Rigidbody whose mass is not 4.5 to 4.5, inactive objects included."},
    "Y5": {"zh-CN": "只修改已有的 Rigidbody，不要给没有 Rigidbody 的对象添加，也不要移除已有的。",
           "en": "Only change Rigidbodies that exist; add none and remove none."},
    "Y6": {"zh-CN": "匹配是区分大小写且完整匹配的：crate_A01、Crate_a01、CRATE_A01、Crate_A1 等不算；R/Archive 下的 Crate_A01 在 Yard 之外。"
                    "batch_query_gameobjects 的 namePattern 默认不区分大小写，要加 caseSensitive:true。",
           "en": "The match is case-sensitive and whole-name: crate_A01, Crate_a01, CRATE_A01, Crate_A1 and the like "
                 "do not count, and R/Archive/Crate_A01 is outside the Yard. batch_query_gameobjects namePattern "
                 "ignores case unless caseSensitive:true."},
    "Y7": {"zh-CN": "不要新建或删除对象，也不要改变任何对象的激活状态、名称、位置层级或同级顺序（未激活的对象要保持未激活）。",
           "en": "Do not create or delete objects, and do not change the active state, name, place in the hierarchy "
                 "or sibling order of any object (inactive objects stay inactive)."},
    "Y8": {"zh-CN": "addedBoxCollider 要列出所有新加了 BoxCollider 的对象，不多不少；两个同名的 Crate_B07 要按各自的 id 区分，不能按名字寻址。",
           "en": "addedBoxCollider must list every object that received a BoxCollider, no more and no fewer; the two "
                 "Crate_B07 must be told apart by id, not addressed by name."},
    "Y9": {"zh-CN": "fixedMass 只列质量真的被改变的对象，质量本来就是 4.5 的不算。",
           "en": "fixedMass lists only the objects whose mass really changed; a mass that was 4.5 already does not "
                 "count."},
    "Y10": {"zh-CN": "matchedCount 要等于匹配对象的总数，并写明 declaredComplete。",
            "en": "matchedCount must equal the number of matching objects; also give declaredComplete."},
    "Y11": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "Y12": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _index(nodes):
    out = {}
    for node in nodes or []:
        key = _key(node.get("id"))
        if key is not None:
            out[key] = node
    return out


def _components(node):
    return [(comp[0], comp[1]) for comp in (node or {}).get("components") or [] if comp]


def _count(node, type_name):
    return sum(1 for kind, _enabled in _components(node) if kind == type_name)


def _box_flags(node):
    return [enabled for kind, enabled in _components(node) if kind == "BoxCollider"]


def _rest(node):
    return [(kind, enabled) for kind, enabled in _components(node) if kind not in ("BoxCollider", "Rigidbody")]


def _number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def _close(value, wanted):
    value = _number(value)
    return value is not None and abs(value - wanted) <= TOLERANCE


def _same_mass(before, after):
    first, second = _number(before), _number(after)
    return first is not None and second is not None and abs(first - second) <= TOLERANCE


def _matches(nodes, yard_path):
    """Keys of the objects below the Yard whose name fully matches the pattern."""
    rx = re.compile(PATTERN)
    prefix = yard_path.rstrip("/") + "/"
    return [key for key, node in nodes.items()
            if str(node.get("path") or "").startswith(prefix) and rx.fullmatch(str(node.get("name") or ""))]


def _masses(probe, nodes):
    out = {}
    for key, node in nodes.items():
        if _count(node, "Rigidbody"):
            values = V.floats((probe.ser(node.get("id"), "Rigidbody") or {}).get("m_Mass"))
            out[key] = values[0] if len(values) == 1 else None
    return out


def _hierarchy(node):
    if node is None:
        return None
    return [node.get("name"), node.get("path"), node.get("active"), [_key(kid) for kid in node.get("children") or []]]


def _listed(ctx, name):
    """Sorted id keys of the list the answer gives under `name`; None when it is no list of object ids."""
    items = V.answer(ctx, name)
    if not isinstance(items, list):
        return None
    keys = [_key(item) for item in items]
    return None if None in keys else sorted(keys)


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    before = _index(c0.get("nodes"))
    mass0 = c0.get("mass") or {}
    nodes = V.structure(p, [ctx.root])
    after = _index(nodes)
    mass1 = _masses(p, after)
    targets = _matches(before, h["yardPath"])
    chosen = set(targets)

    lacking = [key for key in targets if not _count(before[key], "BoxCollider")]
    boxed = [key for key in targets if _count(before[key], "BoxCollider")]
    done = [key for key in lacking if _box_flags(after.get(key)) == [True]]
    card.fraction("Y1", len(done) / float(len(lacking)) if lacking else 0.0, expected=len(lacking),
                  actual={"ok": len(done), "missing": sorted(set(lacking) - set(done))[:10]})
    changed = [key for key in boxed if _box_flags(after.get(key)) != _box_flags(before[key])]
    card.check("Y2", bool(boxed) and not changed, actual={"changed": sorted(changed)[:10]})
    changed = [key for key in targets if _rest(after.get(key)) != _rest(before[key])]
    card.check("Y3", not changed, actual={"changed": sorted(changed)[:10]})

    wrong = [key for key in targets if key in mass0 and _number(mass0[key]) is not None
             and not _close(mass0[key], MASS)]
    fixed = [key for key in wrong if _count(after.get(key), "Rigidbody") == 1 and _close(mass1.get(key), MASS)]
    card.fraction("Y4", len(fixed) / float(len(wrong)) if wrong else 0.0, expected=len(wrong),
                  actual={"ok": len(fixed), "missing": sorted(set(wrong) - set(fixed))[:10]})
    bad = [key for key in targets
           if _count(after.get(key), "Rigidbody") != _count(before[key], "Rigidbody")
           or (key in mass0 and _close(mass0[key], MASS) and not _close(mass1.get(key), MASS))]
    card.check("Y5", not bad, actual={"changed": sorted(bad)[:10]})

    bad = [key for key in before if key not in chosen
           and (_components(after.get(key)) != _components(before[key])
                or (key in mass0 and _number(mass0[key]) is not None
                    and not _same_mass(mass0[key], mass1.get(key))))]
    card.check("Y6", not bad, actual={"changed": [(before[key].get("name"), key) for key in sorted(bad)[:10]]})
    bad = [key for key in set(before) | set(after) if _hierarchy(before.get(key)) != _hierarchy(after.get(key))]
    card.check("Y7", not bad, actual={"changed": sorted(bad)[:10]})

    gained = sorted(key for key, node in after.items() if _count(node, "BoxCollider") > _count(before.get(key),
                                                                                                "BoxCollider"))
    moved = sorted(key for key in mass0 if _number(mass0[key]) is not None and _number(mass1.get(key)) is not None
                   and abs(mass1[key] - mass0[key]) > TOLERANCE)
    listed = _listed(ctx, "addedBoxCollider")
    card.check("Y8", listed is not None and listed == gained, expected=gained, actual=listed)
    listed = _listed(ctx, "fixedMass")
    card.check("Y9", listed is not None and listed == moved, expected=moved, actual=listed)
    card.check("Y10", V.as_int(V.answer(ctx, "matchedCount")) == len(targets)
               and isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected=len(targets), actual={key: V.answer(ctx, key) for key in ("matchedCount", "declaredComplete")})
    bad = ctx.violations()
    card.check("Y11", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("Y12", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
