"""OC6 private checks: connected extraction, inbound Transform identities and complete preservation."""
import math

from usk_eval import verifylib as V

ASSERTIONS = [
    ("A1", 1.25, False, ["R1"], "the new prefab has the original hierarchy and component content"),
    ("A2", 1.25, False, ["R2"], "all four locations are instance roots of the required prefab asset"),
    ("A3", 1.5, False, ["R3"], "all four roots retain world position, rotation and lossy scale"),
    ("A4", 0.75, False, ["R3"], "the gate roots retain their original sibling indexes"),
    ("A5", 2.0, False, ["R4"], "all three assigned Transform references resolve to the original gate's Hinge location"),
    ("A6", 1.0, True, ["R2", "R5"], "gate content and all other scene objects, values and overrides are unchanged"),
    ("A7", 0.75, True, ["R6"], "all pre-existing assets and folder metadata are byte-identical"),
    ("A8", 0.5, True, ["R7"], "no surplus object or asset remains anywhere under R/A"),
    ("A9", 0.25, False, ["R8", "G8"], "the answer identifies the prefab and all inspected reference owners"),
    ("A10", 0.5, True, ["G1", "G2"], "no out-of-scope change or other-scene operation"),
    ("A11", 0.25, False, ["G4"], "no scene or asset direct writes and no C# file operations"),
]


def layout(ctx):
    rows = (getattr(ctx, "c0", {}) or {}).get("nodes") or []
    if not rows:
        rows = [[row["id"], row["path"], row["children"]] for row in ctx.task.read_scene(ctx)]
    nodes = {str(ident): {"id": ident, "path": path, "children": kids} for ident, path, kids in rows}
    paths = {row["path"]: row for row in nodes.values()}
    gates = []
    for ident, asset in ctx.handles["bindings"]:
        row = nodes[str(ident)]
        parent = paths[row["path"].rpartition("/")[0]]
        gates.append(dict(row, asset=asset, parent=parent["id"],
                          index=next(i for i, kid in enumerate(parent["children"]) if V.same_id(kid, ident))))
    links = []
    for ident, field, obj in ctx.handles["edges"]:
        links.append({"id": ident, "path": nodes[str(ident)]["path"], "field": field,
                      "childPath": nodes[str(obj)]["path"]})
    return gates, links


def content(rows):
    out = []
    for row in rows:
        parts = []
        for comp in row["parts"]:
            props = dict(comp["props"])
            if not row["path"] and comp["type"] == "UnityEngine.Transform":
                props = {k: v for k, v in props.items() if k not in
                         ("m_LocalPosition", "m_LocalRotation", "m_LocalScale") and not k.startswith("m_Local")}
            parts.append(dict(comp, props=props))
        out.append({"path": row["path"], "name": row["name"] if row["path"] else "",
                    "index": row["index"] if row["path"] else 0, "active": row["active"],
                    "tag": row["tag"], "layer": row["layer"], "parts": parts})
    return sorted(out, key=lambda row: row["path"])


def scene_content(rows, path):
    by_path = {row["path"]: row for row in rows}
    out = []
    for row in rows:
        if not V.under(row["path"], path):
            continue
        parent = by_path.get(row["parent"], {})
        kids = parent.get("children") or []
        index = next((i for i, kid in enumerate(kids) if V.same_id(kid, row["id"])), -1)
        out.append(dict(row, path=row["path"][len(path):].lstrip("/"), index=index))
    return content(out)


def _qmul(a, b):
    x, y, z, w = a
    i, j, k, r = b
    return [w*i+x*r+y*k-z*j, w*j-x*k+y*r+z*i, w*k+x*j-y*i+z*r, w*r-x*i-y*j-z*k]


def _matrix(trs):
    x, y, z, w = trs["rotation"]
    sx, sy, sz = trs["scale"]
    px, py, pz = trs["position"]
    return [[(1-2*(y*y+z*z))*sx, 2*(x*y-z*w)*sy, 2*(x*z+y*w)*sz, px],
            [2*(x*y+z*w)*sx, (1-2*(x*x+z*z))*sy, 2*(y*z-x*w)*sz, py],
            [2*(x*z-y*w)*sx, 2*(y*z+x*w)*sy, (1-2*(x*x+y*y))*sz, pz], [0, 0, 0, 1]]


def world_pose(ctx, path, leaf=None):
    chain = []
    node = ctx.probe.go_path(path)
    if not node:
        return None
    chain.append(leaf or V.local_trs(ctx.probe, node))
    parent = node.get("parentPath")
    while parent:
        node = ctx.probe.go_path(parent)
        if not node:
            return None
        chain.append(V.local_trs(ctx.probe, node))
        parent = node.get("parentPath")
    mat = [[float(i == j) for j in range(4)] for i in range(4)]
    quat = [0, 0, 0, 1]
    for trs in reversed(chain):
        if [len(trs[k]) for k in ("position", "rotation", "scale")] != [3, 4, 3]:
            return None
        q = trs["rotation"]
        norm = math.sqrt(sum(v*v for v in q))
        if not norm or not math.isfinite(norm):
            return None
        trs = dict(trs, rotation=[v/norm for v in q])
        other = _matrix(trs)
        mat = [[sum(mat[i][k]*other[k][j] for k in range(4)) for j in range(4)] for i in range(4)]
        quat = _qmul(quat, trs["rotation"])
    return {"position": [mat[i][3] for i in range(3)], "rotation": quat,
            "scale": [math.sqrt(sum(mat[i][j]**2 for i in range(3))) for j in range(3)]}


def _read(ctx, skill, **args):
    try:
        result = ctx.read(skill, **args)
        return result if isinstance(result, dict) and not result.get("error") else {}
    except Exception:
        return {}


def verify(ctx, card):
    # Independent checks keep a missing asset or malformed solution from aborting the verifier.
    try:
        gates, links = layout(ctx)
        rows = ctx.task.read_scene(ctx)
        current = {row["path"]: row for row in rows}
        records = {g["asset"]: ctx.task.read_asset(ctx, g["asset"]) for g in gates}
    except Exception as exc:
        gates, links, rows, current, records = [], [], [], {}, {}
        reason = str(exc)
    else:
        reason = None
    wanted = ctx.handles.get("file")
    saved = ctx.task.read_asset(ctx, wanted)
    baseline = records.get(gates[0]["asset"]) if gates else []
    card.check("A1", bool(saved and baseline) and saved[0].get("assetType") == "Regular" and
               V.same(content(saved), content(baseline), 0)[0], note=reason)
    found = _read(ctx, "prefab_find_instances", prefabPath=wanted, includeInactive=True, limit=4096)
    instances = found.get("instances") or []
    membership = [any(V.same_id(row, (current.get(g["path"]) or {}).get("id")) for row in instances) for g in gates]
    card.check("A2", len(gates) == 4 and all(membership) and not found.get("truncated"), actual=membership)
    poses, order, contents = [], [], []
    for gate in gates:
        asset = records.get(gate["asset"]) or []
        root = next((n for n in asset if n["path"] == ""), {})
        try:
            before = root.get("local")
            after = world_pose(ctx, gate["path"])
            poses.append(bool(before and after) and V.vec_close(before["position"], after["position"], 0.001) and
                         V.quat_close(before["rotation"], after["rotation"], 0.1) and
                         V.vec_close(before["scale"], after["scale"], 0.001))
        except Exception:
            poses.append(False)
        children = ctx.probe.children(gate["parent"])
        slot = next((i for i, kid in enumerate(children) if V.same_id(kid, (current.get(gate["path"]) or {}).get("id"))), None)
        order.append(slot == gate["index"])
        contents.append(bool(asset) and V.same(scene_content(rows, gate["path"]), content(asset), 0)[0])
    card.check("A3", len(poses) == 4 and all(poses), actual=poses)
    card.check("A4", len(order) == 4 and all(order), actual=order)
    connected = []
    for link in links:
        response = _read(ctx, "component_get_serialized_properties", componentType="UnityEngine.MeshRenderer",
                         includeHidden=True, includeChildren=True, limit=4096, **V.address(link["id"]))
        field = next((r for r in response.get("properties") or [] if r.get("propertyPath") == link["field"]), {})
        value = V.parse_ref(field.get("value"))
        owner = ctx.probe.go(ctx.probe.owner(field.get("value"))) or {}
        connected.append(field.get("referenceState") == "assigned" and value is not None and
                         value[1] == "UnityEngine.Transform" and owner.get("path") == link["childPath"])
    card.check("A5", len(connected) == 3 and all(connected), actual=connected)
    roots = {str(row["id"]) for row in rows if any(row["path"] == g["path"] for g in gates)}
    fields = {(str(link["id"]), "UnityEngine.MeshRenderer", link["field"]) for link in links}
    final_ids = {str(row["id"]): row for row in rows}
    changed = []
    for ident, digest in (ctx.c0_digests.get("scene") or {}).items():
        row = final_ids.get(ident)
        if row is None or V.canonical_digest(ctx.task.omit_fields(row, roots, fields)) != digest:
            changed.append(ident)
    card.check("A6", len(contents) == 4 and all(contents) and not changed, actual={"gates": contents, "objects": changed})
    try:
        files = ctx.task.read_files(ctx)
    except Exception:
        files = {}
    changed_files = [path for path, digest in (ctx.c0_digests.get("files") or {}).items() if files.get(path) != digest]
    meta = ctx.task.read_metadata(ctx, (ctx.c0_digests.get("metadata") or {}).keys())
    changed_meta = [path for path, digest in (ctx.c0_digests.get("metadata") or {}).items()
                    if V.canonical_digest(meta.get(path)) != digest]
    card.check("A7", not changed_files and not changed_meta and bool(ctx.c0_digests.get("files")),
               actual={"files": changed_files, "metadata": changed_meta})
    allowed_paths = set(ctx.c0.get("paths") or []) | {wanted, str(wanted) + ".meta"}
    extra_files = sorted(set(files) - allowed_paths)
    paths = [path for ident, path, kids in ctx.c0.get("nodes") or []]
    surplus = [path for path in set(row["path"] for row in rows)
               if sum(row["path"] == path for row in rows) > paths.count(path)]
    card.check("A8", not extra_files and not surplus, actual={"objects": surplus, "assets": extra_files})
    owners = V.answer(ctx, "inspectedOwners")
    card.check("A9", isinstance(owners, list) and len(owners) == 3 and
               all(isinstance(path, str) for path in owners) and sorted(owners) == sorted(link["path"] for link in links) and
               V.answer(ctx, "prefabPath") == wanted and isinstance(V.answer(ctx, "declaredComplete"), bool),
               actual=ctx.answer)
    card.check("A10", not ctx.violations(), actual=ctx.violations()[:10])
    scripts = [r for r in ctx.records if any(str(r.get(k) or "").lower().endswith(".cs") for k in
                                            ("assetPath", "from"))]
    card.check("A11", not ctx.direct_writes() and not scripts,
               actual={"writes": ctx.direct_writes()[:10], "scripts": scripts[:10]})

# Producer:Betsy
