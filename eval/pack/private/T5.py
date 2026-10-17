"""T5 private verifier: swap one of three same-name prefab instances for an instance of a new prefab variant.

What the task probes: three Crate instances share one name and differ only by world rotation (their parent Row is
rotated too, so local and world values differ); another Crate carries a mass override, and applying it would rewrite
Crate.prefab; an object made by gameobject_duplicate is no longer an instance of the prefab; a new instance lands at the
end of its parent with the prefab's identity rotation; the mass belongs in the variant asset, not on an instance.

Comparisons are semantic. Crate.prefab is compared by the parsed content of its documents plus its GUID; the variant by
its PrefabInstance document (source GUID, and the modification that sets m_Mass on the root Rigidbody of Crate.prefab).
C0 holds state only: the replaced crate is the Crate whose world rotation Y was closest to 75 degrees at issue.
"""
import os
import re

from usk_eval import verifylib as V

MARKED_YAW = 75.0
MARK_TOLERANCE = 10.0
HEAVY_MASS = 50.0
POSE_TOLERANCE = 5e-3
ROTATION_TOLERANCE_DEG = 0.2

ASSERTIONS = [
    ("V1", 1.0, False, ["R1"], "A/Prefabs/Crate_Heavy.prefab exists and is a prefab variant of Crate.prefab"),
    ("V2", 1.5, False, ["R2"], "the variant asset itself overrides the mass of its root Rigidbody to 50"),
    ("V3", 1.5, True, ["R6"], "Crate.prefab is unchanged (parsed content and GUID)"),
    ("V4", 0.5, False, ["R3"], "the old Crate with world rotation Y about 75 degrees is gone"),
    ("V5", 1.0, False, ["R7"], "the other Crates, the Pallets, Row, Depot and the object root are unchanged and keep "
                               "their sibling order"),
    ("V6", 1.5, False, ["R3", "R4"], "exactly one new instance of the variant, named Crate, under Row at the old "
                                     "sibling index"),
    ("V7", 1.5, False, ["R5"], "the new instance has the old world position, world rotation and scale"),
    ("V8", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("V9", 0.5, False, ["R8", "G8"], "the answer reports the variant path, the new instance's id and its sibling "
                                     "index"),
    ("V10", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "V1": {"zh-CN": "用 prefab_create_variant 以 Crate.prefab 为基础创建变体；另存成独立的预制体不是变体。",
           "en": "Create the variant from Crate.prefab with prefab_create_variant; a standalone prefab is not a "
                 "variant."},
    "V2": {"zh-CN": "质量要写进变体资产（对变体路径使用 prefab_set_property 或 prefab_set_serialized_property），"
                    "而不是场景实例上的覆盖。",
           "en": "Write the mass into the variant asset (prefab_set_property or prefab_set_serialized_property on "
                 "the variant path), not as an override on a scene instance."},
    "V3": {"zh-CN": "不要改动 Crate.prefab：不要对其他 Crate 实例调用 prefab_apply，也不要直接改基础预制体的属性。",
           "en": "Leave Crate.prefab alone: do not call prefab_apply on the other Crates and do not edit the base "
                 "prefab's properties."},
    "V4": {"zh-CN": "世界旋转 Y 约为 75° 的那个 Crate 必须被删除；先读出每个 Crate 的世界旋转再决定。",
           "en": "The Crate whose world rotation Y is about 75 degrees must be deleted; read each Crate's world "
                 "rotation first."},
    "V5": {"zh-CN": "不要改动另外两个 Crate、Pallet 和其他对象，也不要改变它们的同级顺序或覆盖。",
           "en": "Leave the other two Crates, the Pallets and everything else alone, including their sibling order "
                 "and overrides."},
    "V6": {"zh-CN": "用 prefab_instantiate 从变体创建新实例（gameobject_duplicate 复制出的对象不再是预制体实例），"
                    "命名为 Crate，并放回被替换 Crate 的同级位置（新实例默认排在末尾）。",
           "en": "Create the new instance from the variant with prefab_instantiate (an object made by "
                 "gameobject_duplicate is no longer a prefab instance), name it Crate and put it at the replaced "
                 "Crate's sibling index (new instances land at the end)."},
    "V7": {"zh-CN": "世界位置、旋转和缩放要与旧 Crate 一致；Row 有旋转，prefab_instantiate 的 x/y/z 是局部坐标，"
                    "且不带旋转。",
           "en": "Match the old world position, rotation and scale; Row is rotated, and prefab_instantiate takes "
                 "local x/y/z and applies no rotation."},
    "V8": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "V9": {"zh-CN": "答案中的变体路径、新实例 id 和同级顺序（从 0 计）要与最终状态一致，并写明 declaredComplete。",
           "en": "Report the final variant path, the new instance's id and its sibling index (from 0), and "
                 "declaredComplete."},
    "V10": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def variant_path(ctx):
    return ctx.asset("Prefabs", "Crate_Heavy.prefab")


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def prefab_digest(path):
    """Content digest of a prefab file: its parsed documents, so formatting does not matter."""
    return V.canonical_digest([[d.class_id, d.file_id, d.type_name, d.body] for d in V.uyaml.load(path)])


def _safe_digest(path):
    """prefab_digest, or None when the file is missing or cannot be read as Unity YAML."""
    try:
        return prefab_digest(path) if os.path.isfile(path) else None
    except Exception:
        return None


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _gap(a, b):
    return abs((a - b + 180.0) % 360.0 - 180.0)


def replaced(data):
    """(index, id, state) of the Crate in C0 whose world rotation Y is closest to 75 degrees; None if none is near."""
    best = None
    for index, (ref, state) in enumerate(data.get("children") or []):
        state = state or {}
        euler = V.floats(state.get("rotation"))
        if state.get("name") != "Crate" or len(euler) != 3:
            continue
        gap = _gap(euler[1], MARKED_YAW)
        if best is None or gap < best[0]:
            best = (gap, index, ref, state)
    return best[1:] if best is not None and best[0] <= MARK_TOLERANCE else None


def _ref(value):
    """{fileID, guid} of a flow mapping; uyaml returns text when Unity wraps the line."""
    if isinstance(value, dict):
        return {"fileID": str(value.get("fileID")), "guid": value.get("guid")}
    text = str(value or "")
    fid, guid = re.search(r"fileID:\s*(-?\d+)", text), re.search(r"guid:\s*([0-9a-f]+)", text)
    return {"fileID": fid.group(1) if fid else None, "guid": guid.group(1) if guid else None}


def variant_facts(ctx):
    """Source GUID and the m_Mass modifications of the variant file; None when the variant or Crate.prefab is missing
    or cannot be read as Unity YAML."""
    base, variant = _disk(ctx, ctx.handles["basePath"]), _disk(ctx, variant_path(ctx))
    if not (os.path.isfile(base) and os.path.isfile(base + ".meta") and os.path.isfile(variant)):
        return None
    try:
        return _variant_facts(base, variant)
    except Exception:
        return None


def _variant_facts(base, variant):
    base_guid = V.uyaml.meta_guid(base)
    base_docs = V.uyaml.load(base)
    roots = [d for d in V.uyaml.by_type(base_docs, "Transform") if _ref(d.get("m_Father"))["fileID"] == "0"]
    root_go = _ref(roots[0].get("m_GameObject"))["fileID"] if len(roots) == 1 else None
    bodies = [d.file_id for d in V.uyaml.by_type(base_docs, "Rigidbody")
              if root_go and _ref(d.get("m_GameObject"))["fileID"] == root_go]
    docs = V.uyaml.load(variant)
    instances = V.uyaml.by_type(docs, "PrefabInstance")
    tops = [i for i in instances if _ref(i.get("m_Modification", "m_TransformParent"))["fileID"] == "0"]
    source = _ref(tops[0].get("m_SourcePrefab"))["guid"] if len(tops) == 1 else None
    mods = [m for i in tops for m in i.get("m_Modification", "m_Modifications", default=[]) or []
            if isinstance(m, dict)]
    mass = [m.get("value") for m in mods if m.get("propertyPath") == "m_Mass" and
            _ref(m.get("target"))["guid"] == base_guid and bodies and _ref(m.get("target"))["fileID"] == str(bodies[0])]
    return {"source": source, "instances": len(instances), "mass": mass, "rootRigidbody": bodies}


def _instances(ctx, prefab):
    """Rows of prefab_find_instances for `prefab` (instance roots, inactive ones too); [] on a business error."""
    envelope = ctx.probe.c.read_raw("prefab_find_instances", prefabPath=prefab, includeInactive=True, limit=500)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return []
    result = envelope.get("result", envelope)
    return (result.get("instances") or []) if isinstance(result, dict) else []


def _pose(state):
    return {"position": V.floats((state or {}).get("position")), "rotation": V.floats((state or {}).get("rotation")),
            "scale": V.floats((state or {}).get("scale"))}


def _same_pose(now, before):
    q_now, q_before = V.quat_from_euler(*now["rotation"]) if len(now["rotation"]) == 3 else None, \
        V.quat_from_euler(*before["rotation"]) if len(before["rotation"]) == 3 else None
    return (V.close_tuple(now["position"], before["position"], POSE_TOLERANCE) and
            V.close_tuple(now["scale"], before["scale"], POSE_TOLERANCE) and
            q_now is not None and q_before is not None and V.quat_close(q_now, q_before, ROTATION_TOLERANCE_DEG))


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    facts = variant_facts(ctx)
    base_guid = c0.get("baseGuid")
    card.check("V1", bool(facts) and base_guid is not None and facts["source"] == base_guid, expected=base_guid,
               actual=facts)
    masses = V.floats((facts or {}).get("mass") or [])
    card.check("V2", len(masses) == 1 and abs(masses[0] - HEAVY_MASS) < 1e-3, expected=HEAVY_MASS,
               actual=(facts or {}).get("mass"))

    disk = _disk(ctx, h["basePath"])
    digest = _safe_digest(disk)
    guid = V.uyaml.meta_guid(disk) if os.path.isfile(disk + ".meta") else None
    card.check("V3", digest is not None and digest == ctx.c0_digests.get("base") and guid == base_guid,
               actual={"contentSame": digest is not None and digest == ctx.c0_digests.get("base"), "guid": guid})

    children = c0.get("children") or []
    marked = replaced(c0)
    if marked is None:
        for aid in ("V4", "V5", "V6", "V7", "V9"):
            card.unobservable(aid, "C0 does not single out one Crate near a world rotation Y of 75 degrees")
    else:
        index, old, old_state = marked
        card.check("V4", p.go(old) is None, actual=(p.go(old) or {}).get("path"))

        diffs = []
        for name in ("root", "depot"):
            handle = ctx.root if name == "root" else h["depot"]
            ok, diff = V.same(c0.get(name), V.go_state(p, handle))
            if not ok:
                diffs.append({name: diff})
        ok, diff = V.same(c0.get("row"), V.go_state(p, h["row"], with_children=False))
        if not ok:
            diffs.append({"row": diff})
        kept = [(ref, state) for i, (ref, state) in enumerate(children) if i != index]
        for ref, state in kept:
            ok, diff = V.same(state, V.go_state(p, ref))
            if not ok:
                diffs.append({str(_key(ref)): diff})
        before = [_key(ref) for ref, _state in kept]
        row = p.go(h["row"]) or {}
        now_order = [_key(kid) for kid in row.get("children") or [] if _key(kid) in set(before)]
        touched = [rec for rec in ctx.records if rec.get("kind") == "property_changed" and
                   rec.get("targetType") == "Rigidbody" and _key(rec) in set(before)]
        card.check("V5", not diffs and now_order == before and not touched,
                   actual={"diffs": diffs[:5], "order": now_order, "massEdits": touched[:5]})

        kids = row.get("children") or []
        known = {_key(ref) for ref, _state in children}
        added = [(i, kid) for i, kid in enumerate(kids) if _key(kid) not in known]
        new = added[0][1] if len(added) == 1 else None
        position = added[0][0] if len(added) == 1 else None
        rows = _instances(ctx, variant_path(ctx))
        linked = new is not None and any(V.same_id(item, new) for item in rows)
        card.check("V6", new is not None and new.get("name") == "Crate" and linked and position == index,
                   expected={"index": index, "instanceOf": variant_path(ctx)},
                   actual={"added": [(i, kid.get("name")) for i, kid in added], "linked": linked})
        now_state = V.go_state(p, new) if new is not None else None
        card.check("V7", now_state is not None and _same_pose(_pose(now_state), _pose(old_state)),
                   expected=_pose(old_state), actual=_pose(now_state))

        reported = (new is not None and V.same_id(V.answer(ctx, "newInstanceId"), new) and
                    V.as_int(V.answer(ctx, "siblingIndex")) == position and
                    V.norm_path(V.answer(ctx, "variantPath")) == variant_path(ctx) and
                    isinstance(V.answer(ctx, "declaredComplete"), bool))
        card.check("V9", reported, actual=ctx.answer)

    bad = ctx.violations()
    card.check("V8", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("V10", not direct, actual={"directWrites": direct[:10]})


def allowed_change(ctx, rec):
    """Edits of a prefab asset are journaled as scene-less records named after the asset's root object."""
    if rec.get("type") != "scene_changes" or rec.get("scenePath") or rec.get("assetPath"):
        return False
    roots = {os.path.splitext(os.path.basename(path))[0] for path in (ctx.handles["basePath"], variant_path(ctx))}
    return str(rec.get("path") or "").split("/")[0] in roots

# Producer:Betsy
