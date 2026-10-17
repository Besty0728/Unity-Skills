"""OC7 private verifier: a yard of ~150 objects sorted into rotated, non-uniformly scaled containers by tag + suffix.

What the task probes: gameobject_set_parent keeps the world pose (Unity decomposes the new local matrix), so "restoring"
the old numbers afterwards breaks it (scaleX/Y/Z are local; under a container scaled (2, 1, 0.5) the local scale must be
world / container); objects whose rotation differs from their container cannot keep their world scale and must be
declared; two containers share object names (Respawn_X and Finish_X both hold a Crate_12_X), so name lookups pick the
wrong twin; some objects are inactive and some sit in a wrong container already; the sibling order is natural
(Crate_7 before Crate_12) and one object per container is already in place and must not be touched; gameobject_find
returns 50 objects by default and FindGameObjectsWithTag skips inactive objects.

Everything is derived from C0: the tag set from the container names, each object's container from its tag and suffix,
the objects that were already home from their C0 parent, the ones that cannot keep their scale from the angle between
their rotation and their container's.
"""
import re

from usk_eval import verifylib as V

POSITION_TOL = 1e-2      # R4: world position, absolute
ROTATION_TOL = 0.5       # R4: world rotation, degrees
ALIGNED_TOL = 0.1        # R5: an object counts as aligned with its container below this angle (degrees)
SCALE_TOL = 1e-3         # R5: world scale, relative
LOCAL_TOL = 1e-4         # R7/R8: untouched local values
CHANGE_KINDS = ("gameobject_reparented", "property_changed", "gameobject_destroyed", "components_changed",
                "gameobject_created")

ASSERTIONS = [
    ("H1", 1.5, False, ["R1"], "every tagged object that had to move sits directly under its <Tag>_<Suffix> "
                               "container (fraction)"),
    ("H2", 1.5, False, ["R4"], "the moved objects under their container keep their world position (fraction)"),
    ("H3", 1.0, False, ["R4"], "the moved objects under their container keep their world rotation (fraction)"),
    ("H4", 1.0, False, ["R5"], "the moved objects aligned with their container keep their world scale (fraction)"),
    ("H5", 1.5, False, ["R6"], "each container holds exactly its objects, in natural name order (fraction of "
                               "containers)"),
    ("H6", 1.0, False, ["R7"], "the objects that were already in their container are untouched: same parent, local "
                               "pose, name, tag, active state, and no journal record about them"),
    ("H7", 0.5, False, ["R2", "R8"], "the untagged objects, the containers and the folders are unchanged"),
    ("H8", 0.5, False, ["R5"], "scaleNotPreserved lists exactly the moved objects whose rotation differs from their "
                               "container"),
    ("H9", 0.5, False, ["R3"], "no object created, destroyed, renamed, retagged or (de)activated; no object "
                               "reparented to more than one place"),
    ("H10", 0.25, False, ["R9", "G8"], "movedCount is the number of objects that had to move; declaredComplete is "
                                       "a boolean"),
    ("H11", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("H12", 0.25, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "H1": {"zh-CN": "每个带 Respawn/Finish 标签的对象都要成为 <标签>_<后缀> 容器的直接子物体，包括未激活的、已经在错误容器里的，"
                    "以及同名的两个对象（按 id 而不是按名字定位）。",
           "en": "Every Respawn/Finish object must become a direct child of its <Tag>_<Suffix> container: inactive ones, "
                 "ones already in a wrong container, and both of two same-named objects (address them by id, not by "
                 "name)."},
    "H2": {"zh-CN": "gameobject_set_parent 已经保持世界位置；不要把旧的世界坐标写进本地位置。",
           "en": "gameobject_set_parent already keeps the world position; do not write the old world coordinates into "
                 "the local position."},
    "H3": {"zh-CN": "移动后的世界旋转要与移动前相同；重设父物体后不要再改旋转。",
           "en": "The world rotation after the move must equal the one before; do not rotate after reparenting."},
    "H4": {"zh-CN": "scaleX/Y/Z 是本地缩放；容器是非均匀缩放的，把旧的缩放数字写回去会改变世界缩放。",
           "en": "scaleX/Y/Z are local; the containers are scaled non-uniformly, so writing the old scale numbers back "
                 "changes the world scale."},
    "H5": {"zh-CN": "容器内所有子物体（含原来就在里面的）必须按自然顺序排列：数字段按数值比较，Crate_7 在 Crate_12 之前；"
                    "用 gameobject_set_sibling_index 调整。",
           "en": "All children of a container (the one already there included) must be in natural order: digit runs "
                 "compare as numbers, Crate_7 before Crate_12; use gameobject_set_sibling_index."},
    "H6": {"zh-CN": "已经在正确容器中的对象不要做任何操作，它们的本地位置/旋转/缩放必须保持不变。",
           "en": "Leave the objects that are already in their container alone; their local position, rotation and "
                 "scale must stay the same."},
    "H7": {"zh-CN": "Untagged 的对象留在原处不动；不要改容器、Yard、Loose、Zones 的变换。",
           "en": "Untagged objects stay where they are; do not change the containers', Yard's, Loose's or Zones' "
                 "transforms."},
    "H8": {"zh-CN": "世界旋转与目标容器不同的对象无法在非均匀缩放的容器下保持世界缩放，要在 scaleNotPreserved 中逐一列出（移动后的路径），"
                    "其他对象不要列。",
           "en": "Objects whose world rotation differs from their container cannot keep their world scale under a "
                 "non-uniformly scaled parent; list exactly those in scaleNotPreserved (paths after the move)."},
    "H9": {"zh-CN": "移动对象本身，不要删除或重建，不要改名、改标签或改激活状态，也不要把同一对象移到两个地方。",
           "en": "Move the objects themselves: no delete and recreate, no rename, retag or active change, and no object "
                 "moved to two places."},
    "H10": {"zh-CN": "movedCount 是父物体发生改变的对象数，并写明 declaredComplete。",
            "en": "movedCount is the number of objects whose parent changed; set declaredComplete."},
    "H11": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "H12": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


# ---------------------------------------------------------------- derivations shared with the solutions


def natural_key(name):
    """Digit runs as integers, the rest as text; a shorter name sorts before a longer one with the same start."""
    return [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", name or "")]


def suffix(name):
    return (name or "").rpartition("_")[2]


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _quat(euler):
    values = V.floats(euler)
    return V.quat_from_euler(*values) if len(values) == 3 else None


def aligned(rotation, container_rotation):
    """Whether two world rotations (euler) are the same within ALIGNED_TOL degrees."""
    a, b = _quat(rotation), _quat(container_rotation)
    return a is not None and b is not None and V.quat_close(a, b, ALIGNED_TOL)


def layout(c0):
    """(dest, moved, home, untagged): the container name of every tagged object, the objects that had to move, the
    ones that were already in their container, and the objects without a container (sorted id texts)."""
    containers = c0.get("containers") or {}
    objects = c0.get("objects") or {}
    tags = {name.partition("_")[0] for name in containers}
    dest, moved, home, untagged = {}, [], [], []
    for key in sorted(objects, key=natural_key):
        state = objects[key]
        name = "%s_%s" % (state.get("tag"), suffix(state.get("name")))
        if state.get("tag") in tags and name in containers:
            dest[key] = name
            (home if state.get("parentPath") == containers[name].get("path") else moved).append(key)
        else:
            untagged.append(key)
    return dest, moved, home, untagged


def _close_scale(actual, wanted):
    a, b = V.floats(actual), V.floats(wanted)
    return len(a) == 3 and len(b) == 3 and all(abs(x - y) <= SCALE_TOL * max(1.0, abs(y)) for x, y in zip(a, b))


def _mentions(records, key, path):
    """Journal records that change the object `key` (by identity or by one of its paths)."""
    out = []
    for rec in records:
        if rec.get("kind") not in CHANGE_KINDS:
            continue
        if V.same_id(rec, key) or path and path in (rec.get("path"), rec.get("fromPath"), rec.get("toPath")):
            out.append(rec)
    return out


def _unchanged(p, key, state):
    """(ok, actual) for an object that must keep its parent, local pose, name, tag and active state."""
    info = p.go(key) or {}
    local = V.local_trs(p, key) if info else None
    actual = {"parentPath": info.get("parentPath"), "name": info.get("name"), "tag": info.get("tag"),
              "active": info.get("isActive"), "local": local}
    ok = bool(info) and all(actual[field] == state.get(field) for field in ("parentPath", "name", "tag", "active")) \
        and V.same(state.get("local"), local, LOCAL_TOL)[0]
    return ok, actual


def verify(ctx, card):
    p, c0 = ctx.probe, ctx.c0
    objects, containers = c0.get("objects") or {}, c0.get("containers") or {}
    dest, moved, home, untagged = layout(c0)
    final = {key: p.go(key) or {} for key in objects}
    cpath = {name: spec.get("path") for name, spec in containers.items()}

    under = [key for key in moved if final[key].get("parentPath") == cpath[dest[key]]]
    card.fraction("H1", len(under) / float(len(moved)) if moved else 0.0, expected=len(moved), actual=len(under),
                  missing=[objects[key].get("name") for key in moved if key not in under][:20])

    pos_ok = [key for key in under if V.close_tuple(final[key].get("position"), objects[key].get("position"),
                                                      POSITION_TOL)]
    card.fraction("H2", len(pos_ok) / float(len(under)) if under else 0.0, expected=len(under), actual=len(pos_ok),
                  drifted=[{"name": objects[key].get("name"), "was": objects[key].get("position"),
                            "now": final[key].get("position")} for key in under if key not in pos_ok][:10])
    rot_ok = [key for key in under if V.quat_close(_quat(final[key].get("rotation")),
                                                     _quat(objects[key].get("rotation")), ROTATION_TOL)]
    card.fraction("H3", len(rot_ok) / float(len(under)) if under else 0.0, expected=len(under), actual=len(rot_ok),
                  drifted=[{"name": objects[key].get("name"), "was": objects[key].get("rotation"),
                            "now": final[key].get("rotation")} for key in under if key not in rot_ok][:10])
    keepers = [key for key in moved if aligned(objects[key].get("rotation"),
                                               containers[dest[key]].get("rotation"))]
    scale_ok = []
    for key in keepers:
        if key not in under:
            continue
        now = [a * b for a, b in zip(V.floats(final[key].get("scale")),
                                     V.floats(containers[dest[key]].get("worldScale")))]
        if len(now) == 3 and _close_scale(now, objects[key].get("worldScale")):
            scale_ok.append(key)
    judged = [key for key in keepers if key in under]
    card.fraction("H4", len(scale_ok) / float(len(judged)) if judged else 0.0, expected=len(judged),
                  actual=len(scale_ok),
                  drifted=[{"name": objects[key].get("name"), "localScale": final[key].get("scale")}
                           for key in judged if key not in scale_ok][:10])

    ordered = 0
    wrong = []
    for name in sorted(containers):
        wanted = sorted([key for key in dest if dest[key] == name], key=lambda k: natural_key(objects[k].get("name")))
        actual = [_key(child) for child in p.children(containers[name].get("id"))]
        if actual == wanted:
            ordered += 1
        else:
            wrong.append({name: [child.get("name") for child in p.children(containers[name].get("id"))][:30]})
    card.fraction("H5", ordered / float(len(containers)) if containers else 0.0, expected=len(containers),
                  actual=ordered, containers=wrong[:3])

    touched = []
    for key in home:
        ok, actual = _unchanged(p, key, objects[key])
        records = _mentions(ctx.records, key, objects[key].get("path"))
        if not ok or records:
            touched.append({"name": objects[key].get("name"), "state": actual, "records": records[:3]})
    card.check("H6", bool(home) and not touched, actual=touched[:5])

    changed = []
    for key in untagged:
        ok, actual = _unchanged(p, key, objects[key])
        if not ok or _mentions(ctx.records, key, objects[key].get("path")):
            changed.append({"name": objects[key].get("name"), "state": actual})
    for name in sorted(containers):
        ok, actual = _unchanged(p, containers[name].get("id"), containers[name])
        if not ok:
            changed.append({name: actual})
    for name, state in (c0.get("folders") or {}).items():
        ok, actual = _unchanged(p, ctx.handles.get(name), state)
        if not ok:
            changed.append({name: actual})
    card.check("H7", not changed, actual=changed[:5])

    declared = V.answer(ctx, "scaleNotPreserved")
    listed = {V.norm_path(item) for item in declared if isinstance(item, str)} if isinstance(declared, list) else None
    expected_paths = {final[key].get("path") for key in moved if key not in keepers and final[key].get("path")}
    card.check("H8", listed is not None and listed == expected_paths, expected=sorted(expected_paths),
               actual=sorted(listed) if listed is not None else declared)

    identity = []
    for key, state in objects.items():
        info = final[key]
        if not info or any(info.get(field) != state.get(field) for field in ("name", "tag")) or \
                info.get("isActive") != state.get("active"):
            identity.append({"name": state.get("name"), "now": {k: info.get(k) for k in ("name", "tag", "isActive")}})
    yard = ctx.handles.get("yardPath")
    births = [rec for rec in ctx.records if rec.get("kind") in ("gameobject_created", "gameobject_destroyed") and
              (V.under(rec.get("path"), yard) or _key(rec) in objects)]
    places = {}
    for rec in ctx.records:
        if rec.get("kind") == "gameobject_reparented" and _key(rec) is not None:
            places.setdefault(_key(rec), set()).add(rec.get("newParentPath") or rec.get("toPath"))
    twice = [objects.get(key, {}).get("name") or key for key, seen in places.items() if len(seen) > 1]
    card.check("H9", not identity and not births and not twice,
               actual={"changed": identity[:5], "createdOrDestroyed": births[:5], "movedTwice": twice[:10]})

    card.check("H10", V.as_int(V.answer(ctx, "movedCount")) == len(moved) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), expected=len(moved),
               actual={"movedCount": V.answer(ctx, "movedCount"),
                       "declaredComplete": V.answer(ctx, "declaredComplete")})
    bad = ctx.violations()
    card.check("H11", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("H12", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
