"""OC1 private verifier: a Muzzle node inside Turret.prefab under its nested Barrel instance.

What the task probes: prefab asset edits take a root-relative gameObjectPath (Mount/Barrel), not a scene path; the
Muzzle must be an added GameObject on the nested Barrel instance inside Turret.prefab, so adding it to the scene
instances, into Tank.prefab (which is also where prefab_apply on a Tank instance writes) or into Barrel.prefab leaves
the wrong asset changed; one Tank instance carries a Barrel rotation override and an added AudioSource that must stay.

"Unchanged" covers values, not only structure: Turret.prefab's other nodes keep their component values (A4), the
component added on an instance keeps its values (A5), the objects under the container keep their local poses and the
asset root keeps its folders, files and .meta files byte for byte, prefab files aside (A7; those are A1-A4's).
"""
import json
import os
import random

from usk_eval import verifylib as V

MUZZLE_Y = (0.0, 0.1, 0.15)
MUZZLE_Z = (1.0, 1.25, 1.5)
MUZZLE_ROT = ((0, 0, 0), (90, 0, 0), (0, 90, 0))
MUZZLE_SCALE = (1.0, 0.5)
POSE_SALT = 0x4D55
NODE_KEYS = ("activeSelf", "layer", "tag", "siblingIndex")
COUNT_KEYS = ("propertyOverrides", "addedComponents", "removedComponents", "addedGameObjects")
ENTRY_KEYS = ("kind", "gameObjectPath", "component", "componentIndex", "property", "name")
TRANSFORM_ONLY = ["UnityEngine.Transform"]

ASSERTIONS = [
    ("A1", 2.0, False, ["R1"], "Turret.prefab holds exactly one Muzzle, a direct child of the nested Barrel node, "
                               "attached to the nested Barrel instance in the asset file"),
    ("A2", 1.5, False, ["R2"], "Muzzle's local pose in Turret.prefab is the required one and Transform is its only "
                               "component"),
    ("A3", 1.5, True, ["R3"], "Barrel.prefab and Tank.prefab are unchanged"),
    ("A4", 1.0, False, ["R3"], "Turret.prefab's other nodes are unchanged, their component values included, and "
                               "Barrel is still its only nested prefab instance"),
    ("A5", 1.5, False, ["R4"], "every scene instance keeps exactly its previous override list, counts, Barrel pose and "
                               "the values of the component added on it"),
    ("A6", 1.0, False, ["R4"], "each of the three instances shows Barrel/Muzzle with the required local pose"),
    ("A7", 0.5, True, ["R5"], "nothing else changed under the container (the poses of the container, the bays and "
                              "the instance roots included) and no folder, file or .meta file under the asset root "
                              "was added, removed or changed, prefab content aside"),
    ("A8", 0.5, False, ["R6", "G8"], "the answer reports the prefab path, the Muzzle node path, the scene Muzzles, "
                                     "the instance with overrides and its override count"),
    ("A9", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("A10", 0.25, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "A1": {"zh-CN": "要在 Turret.prefab 资产里、以预制体根为基准的路径（如 Mount/Barrel）下新建 Muzzle；在场景实例或其他预制体里加都不算。",
           "en": "Create Muzzle inside the Turret.prefab asset under the root-relative node path (e.g. Mount/Barrel); "
                 "adding it on a scene instance or in another prefab does not count."},
    "A2": {"zh-CN": "Muzzle 的位置、欧拉角和缩放都是相对父节点的本地值；不要给它加任何组件。",
           "en": "Muzzle's position, Euler angles and scale are local to its parent node; add no component to it."},
    "A3": {"zh-CN": "Barrel.prefab 与 Tank.prefab 不得改动：在 Tank 实例上 apply 或编辑 Barrel.prefab 都会改错文件。",
           "en": "Barrel.prefab and Tank.prefab must stay as they are: applying a Tank instance or editing Barrel.prefab "
                 "changes the wrong file."},
    "A4": {"zh-CN": "Turret.prefab 里只允许新增 Muzzle，其他节点（包括其组件的取值）和嵌套的 Barrel 实例保持不变。",
           "en": "Only Muzzle may be added to Turret.prefab; its other nodes (their component values included) and the "
                 "nested Barrel instance stay as they were."},
    "A5": {"zh-CN": "每个实例的覆盖列表必须与开始前相同：不要在实例上添加 Muzzle，也不要 revert 或 apply 实例；实例上新增的组件也要保持原值。",
           "en": "Every instance's override list must equal what it was: add no Muzzle on an instance and neither "
                 "revert nor apply an instance; the component added on an instance keeps its values."},
    "A6": {"zh-CN": "改好资产后，3 个实例都应在各自的 Barrel 下继承到位姿正确的 Muzzle。",
           "en": "Once the asset is right, all 3 instances inherit a correctly posed Muzzle under their Barrel."},
    "A7": {"zh-CN": "不要在容器下新增或移动对象，也不要新增、删除或修改资产根下的文件夹、文件或 .meta 文件。",
           "en": "Add or move no object under the container, and add, delete or change no folder, file or .meta file "
                 "under the asset root."},
    "A8": {"zh-CN": "答案中的路径、id 与覆盖总数要与最终状态一致，并写明 declaredComplete。",
           "en": "Report paths, ids and the override total as they really are, and declaredComplete."},
    "A9": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "A10": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def muzzle_pose(seed):
    rng = random.Random(seed ^ POSE_SALT)
    return {"position": (0.0, rng.choice(MUZZLE_Y), rng.choice(MUZZLE_Z)), "rotation": rng.choice(MUZZLE_ROT),
            "scale": rng.choice(MUZZLE_SCALE)}


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _int(text):
    try:
        return int(str(text))
    except (TypeError, ValueError):
        return None


def _full(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def docs(ctx, asset_path):
    try:
        return V.uyaml.load(_full(ctx, asset_path))
    except OSError:
        return []


def yaml_digest(docs_list):
    return V.canonical_digest([[d.class_id, d.file_id, d.stripped, d.type_name, d.body] for d in docs_list])


def prefab_nodes(ctx, prefab_path):
    """Same shape as the public module's prefab_nodes: root-relative path, parent, flags, sibling index and local
    pose per node; the root carries only its flags."""
    res = _read(ctx, "prefab_get_hierarchy", prefabPath=prefab_path, limit=500) or {}
    out = []
    for node in res.get("nodes") or []:
        item = {"path": node.get("gameObjectPath"), "parent": node.get("parentPath")}
        item.update({key: node.get(key) for key in NODE_KEYS})
        if item["path"]:
            item.update({"name": node.get("gameObject"), "pos": V.floats(node.get("localPosition")),
                         "rot": V.floats(node.get("localEulerAngles")), "scale": V.floats(node.get("localScale"))})
        out.append(item)
    return sorted(out, key=lambda item: item["path"] or "")


def unhashed_prefab(rel):
    """Same asset_inventory predicate as the public module: prefab content is A1-A4's, the rest A7's."""
    return not rel.endswith(".prefab")


def prefab_components(ctx, prefab_path):
    """Same rows as the public module's prefab_components: [node path, [[type, index, enabled, properties]]] for every
    node but Muzzle, a Transform without its properties; None when the asset cannot be read."""
    res = _read(ctx, "prefab_get_hierarchy", prefabPath=prefab_path, limit=500)
    if res is None:
        return None
    out = []
    for node in res.get("nodes") or []:
        path = node.get("gameObjectPath") or ""
        if path.split("/")[-1] == "Muzzle":
            continue
        listed = _read(ctx, "prefab_list_components", prefabPath=prefab_path, gameObjectPath=path,
                       includeProperties=True) or {}
        comps = []
        for comp in listed.get("components") or []:
            if not isinstance(comp, dict):
                continue
            kind = str(comp.get("componentType"))
            props = None if kind.endswith("Transform") else sorted(
                [prop.get("propertyPath"), prop.get("value")] for prop in comp.get("properties") or []
                if isinstance(prop, dict))
            comps.append([kind, comp.get("componentIndex"), comp.get("enabled"), props])
        out.append([path, comps])
    return sorted(out, key=lambda row: row[0])


def added_values(ctx, instance_path):
    """Same rows as the public module's added_values: [node path, type, [[propertyPath, value]]] of every component
    added on the instance; None when unreadable."""
    res = _read(ctx, "prefab_get_overrides", path=instance_path, includeDetails=True) if instance_path else None
    if not res:
        return None
    out = []
    for entry in res.get("overrides") or []:
        if not isinstance(entry, dict) or entry.get("kind") != "addedComponent":
            continue
        rel = entry.get("gameObjectPath") or ""
        node = ctx.probe.go_path(instance_path + ("/" + rel if rel else ""))
        values = V.component_values(ctx.probe, node, types=(entry.get("component"),)) if node else {}
        out.append([rel, entry.get("component"), sorted([key, value] for key, value in
                                                        (values.get(entry.get("component")) or {}).items())])
    return sorted(out, key=lambda row: (row[0], str(row[1])))


def overrides(ctx, instance_path):
    """Counts and entries of prefab_get_overrides, as the public module stored them in C0; None when unreadable."""
    res = _read(ctx, "prefab_get_overrides", path=instance_path, includeDetails=True) if instance_path else None
    if not res:
        return None
    counts = {key: res.get(key) for key in COUNT_KEYS}
    entries = [{key: entry.get(key) for key in ENTRY_KEYS} for entry in res.get("overrides") or []
               if isinstance(entry, dict)]
    return {"counts": counts, "entries": sorted(json.dumps(entry, sort_keys=True) for entry in entries)}


def descend(probe, ref, names):
    node = probe.go(ref)
    for name in names:
        kids = [kid for kid in (node or {}).get("children") or [] if kid.get("name") == name]
        if len(kids) != 1:
            return None
        node = probe.go(kids[0])
    return node


def muzzles_under(probe, ref):
    """Every object called Muzzle in the subtree of ref (what an agent reports for an instance), top-down."""
    out, queue = [], [ref]
    while queue:
        node = probe.go(queue.pop(0))
        if not node:
            continue
        if node.get("name") == "Muzzle":
            out.append(node)
        queue.extend(node.get("children") or [])
    return out


def _attached(docs_list, name, barrel_guid):
    """True when exactly one GameObject called `name` is in the file and its Transform's parent is a stripped
    Transform belonging to the PrefabInstance whose source prefab is barrel_guid (an added GameObject on the nested
    instance, not an inherited one)."""
    by_id = V.uyaml.index(docs_list)
    objects = [d for d in V.uyaml.by_type(docs_list, "GameObject") if d.get("m_Name") == name]
    if len(objects) != 1:
        return False
    transform = None
    for entry in objects[0].get("m_Component") or []:
        ref = entry.get("component") if isinstance(entry, dict) else None
        doc = by_id.get(_int(ref.get("fileID"))) if isinstance(ref, dict) else None
        if doc is not None and doc.type_name == "Transform":
            transform = doc
    if transform is None:
        return False
    father = by_id.get(_int(transform.get("m_Father", "fileID")))
    nested = {d.file_id for d in V.uyaml.by_type(docs_list, "PrefabInstance")
              if barrel_guid and d.get("m_SourcePrefab", "guid") == barrel_guid}
    return father is not None and bool(father.stripped) and father.type_name == "Transform" and \
        _int(father.get("m_PrefabInstance", "fileID")) in nested


def _without(nodes, name):
    """A structure() list without the subtrees rooted at nodes called `name`."""
    parent_of = {}
    for node in nodes:
        for kid in node.get("children") or []:
            parent_of[str(kid)] = str(node.get("id"))
    named = {str(node.get("id")) for node in nodes if node.get("name") == name}

    def gone(ident):
        seen = set()
        while ident is not None and ident not in seen:
            if ident in named:
                return True
            seen.add(ident)
            ident = parent_of.get(ident)
        return False

    out = []
    for node in nodes:
        if gone(str(node.get("id"))):
            continue
        copy = dict(node)
        copy["children"] = [kid for kid in node.get("children") or [] if not gone(str(kid))]
        out.append(copy)
    return out


def _scene_poses(nodes, roots):
    """A structure() list whose local poses stay only where the scene owns them: outside the instances and on their
    roots (an instance root's position and rotation are its own). Inside an instance a pose follows the asset (an
    asset edit there is A4's), and a change the instance makes there is an override (A5's)."""
    parent_of = {}
    for node in nodes:
        for kid in node.get("children") or []:
            parent_of[str(kid)] = str(node.get("id"))
    tops = {str(V.object_id(ref)) for ref in roots}
    out = []
    for node in nodes:
        current, seen, inside = parent_of.get(str(node.get("id"))), set(), False
        while current is not None and current not in seen:
            if current in tops:
                inside = True
                break
            seen.add(current)
            current = parent_of.get(current)
        copy = dict(node)
        if inside:
            copy["local"] = None
        out.append(copy)
    return out


def _muzzle_rel(ctx, ref):
    h = ctx.handles
    is_tank = any(V.same_id(ref, tank) for tank in h["tanks"])
    return ((h["turretNode"] + "/") if is_tank else "") + h["barrelNode"]


def _answer_ok(ctx, node_path, present, carrier_path, carrier_total):
    """The answer names the real prefab and node paths, lists exactly the Muzzle objects that exist under the three
    instances (wherever they sit), and reports the instance with overrides with its current override total."""
    if not isinstance(ctx.answer, dict):
        return False
    rows = V.answer(ctx, "sceneMuzzles")
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        return False
    reported = {(V.norm_path(row.get("path")), str(V.object_id(row.get("instanceId")))) for row in rows}
    actual = {(node.get("path"), str(V.object_id(node))) for node in present}
    return (V.norm_path(V.answer(ctx, "turretPrefabPath")) == ctx.handles["turret"] and
            V.norm_path(V.answer(ctx, "muzzleNodePath")) == node_path and
            reported == actual and len(rows) == len(actual) and carrier_path is not None and
            V.norm_path(V.answer(ctx, "overrideInstancePath")) == carrier_path and
            V.as_int(V.answer(ctx, "overrideCount")) == carrier_total and
            isinstance(V.answer(ctx, "declaredComplete"), bool))


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    pose = muzzle_pose(ctx.seed)
    want_rot = V.quat_from_euler(*pose["rotation"])
    want_scale = (pose["scale"],) * 3
    node_path = h["barrelNode"] + "/Muzzle"
    turret_docs = docs(ctx, h["turret"])

    nodes = prefab_nodes(ctx, h["turret"])
    muzzles = [n for n in nodes if (n.get("path") or "").split("/")[-1] == "Muzzle"]
    placed = [n for n in muzzles if n["path"] == node_path and n.get("parent") == h["barrelNode"]]
    attached = _attached(turret_docs, "Muzzle", c0.get("barrelGuid"))
    card.check("A1", len(muzzles) == 1 and len(placed) == 1 and attached,
               expected={"path": node_path, "parent": h["barrelNode"], "attachedToNestedBarrel": True},
               actual={"muzzles": [n["path"] for n in muzzles], "attachedToNestedBarrel": attached})

    info = _read(ctx, "prefab_get_info", prefabPath=h["turret"], gameObjectPath=node_path) or {}
    listed = _read(ctx, "prefab_list_components", prefabPath=h["turret"], gameObjectPath=node_path) or {}
    types = [c.get("componentType") for c in listed.get("components") or [] if isinstance(c, dict)]
    euler = V.floats(info.get("localEulerAngles"))
    pose_ok = bool(info) and V.close_tuple(info.get("localPosition"), pose["position"]) and len(euler) == 3 and \
        V.quat_close(V.quat_from_euler(*euler), want_rot, 0.5) and V.close_tuple(info.get("localScale"), want_scale)
    card.check("A2", pose_ok and types == TRANSFORM_ONLY,
               expected={"localPosition": pose["position"], "localEulerAngles": pose["rotation"],
                         "localScale": want_scale, "components": TRANSFORM_ONLY},
               actual={"localPosition": info.get("localPosition"), "localEulerAngles": info.get("localEulerAngles"),
                       "localScale": info.get("localScale"), "components": types})

    before = c0.get("prefabDocs") or {}
    digests = {name: yaml_digest(docs(ctx, h[name])) for name in ("barrel", "tank")}
    card.check("A3", all(digests[name] == before.get(h[name]) for name in ("barrel", "tank")),
               actual={name: digests[name] == before.get(h[name]) for name in digests})

    rest = [n for n in nodes if (n.get("path") or "").split("/")[-1] != "Muzzle"]
    instances = V.uyaml.by_type(turret_docs, "PrefabInstance")
    nested_ok = len(instances) == 1 and instances[0].get("m_SourcePrefab", "guid") == c0.get("barrelGuid")
    same_nodes = bool(nodes) and V.canonical_digest(rest) == V.canonical_digest(c0.get("turretNodes") or [])
    components = prefab_components(ctx, h["turret"])
    same_values, value_diff = V.same(c0.get("turretComponents"), components)
    card.check("A4", same_nodes and nested_ok and components is not None and same_values,
               actual={"otherNodesSame": same_nodes, "nestedInstances": len(instances), "nestedBarrel": nested_ok,
                       "componentValues": value_diff})

    diffs, pose_diffs, found, missing = {}, {}, [], []
    carrier_path, carrier_total = None, None
    for ref in h["tanks"] + h["turrets"]:
        key = str(V.object_id(ref))
        info = p.go(ref) or {}
        now = overrides(ctx, info.get("path")) if info else None
        was = (c0.get("overrides") or {}).get(key)
        if was and sum(V.as_int(v) or 0 for v in (was.get("counts") or {}).values()) > 0:
            carrier_path = info.get("path")
            carrier_total = sum(V.as_int(v) or 0 for v in (now or {}).get("counts", {}).values()) if now else None
        if not now or not was or now["entries"] != was["entries"] or now["counts"] != was["counts"]:
            diffs[key] = {"before": was, "after": now}
        added = added_values(ctx, info.get("path")) if info else None
        if added != (c0.get("addedValues") or {}).get(key):
            diffs.setdefault(key, {})["addedComponentValues"] = {"before": (c0.get("addedValues") or {}).get(key),
                                                                 "after": added}
        rel = _muzzle_rel(ctx, ref)
        trs = V.local_trs(p, descend(p, ref, rel.split("/")))
        same, diff = V.same((c0.get("barrelPose") or {}).get(key), trs)
        if not same:
            pose_diffs[key] = diff
        muzzle = descend(p, ref, (rel + "/Muzzle").split("/"))
        trs = V.local_trs(p, muzzle) if muzzle else {}
        ok = bool(muzzle) and V.close_tuple(trs.get("position"), pose["position"]) and \
            V.quat_close(trs.get("rotation"), want_rot, 0.5) and V.close_tuple(trs.get("scale"), want_scale)
        (found if ok else missing).append(muzzle if ok else {"instance": key, "path": (muzzle or {}).get("path"),
                                                             "trs": trs})
    card.check("A5", not diffs and not pose_diffs, actual={"overrideDiffs": diffs, "barrelPoseDiffs": pose_diffs})
    card.check("A6", not missing and len(found) == len(h["tanks"]) + len(h["turrets"]),
               expected={"count": len(h["tanks"]) + len(h["turrets"]), "localPosition": pose["position"],
                         "localEulerAngles": pose["rotation"], "localScale": want_scale},
               actual={"found": [node.get("path") for node in found], "missing": missing})

    tree = V.structure(p, [h["container"]], poses=True)
    roots = h["tanks"] + h["turrets"]
    same_tree, tree_diff = V.same(_scene_poses(_without(c0.get("container") or [], "Muzzle"), roots),
                                  _scene_poses(_without(tree, "Muzzle"), roots))
    assets = [tuple(row) for row in V.asset_inventory(ctx.project_path, ctx.A, hashed=unhashed_prefab)]
    before_assets = [tuple(row) for row in c0.get("assets") or []]
    card.check("A7", same_tree and assets == before_assets,
               actual={"treeSame": same_tree, "treeDiff": tree_diff,
                       "added": [row[0] for row in assets if row not in before_assets][:10],
                       "removed": [row[0] for row in before_assets if row not in assets][:10]})
    present = [node for ref in h["tanks"] + h["turrets"] for node in muzzles_under(p, ref)]
    card.check("A8", _answer_ok(ctx, node_path, present, carrier_path, carrier_total), actual=ctx.answer)
    bad = ctx.violations()
    card.check("A9", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("A10", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
