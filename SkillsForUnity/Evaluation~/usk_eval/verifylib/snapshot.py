"""Canonical state summaries (contract 5.2, 5.8): C0 digests of read-only discovery tasks and the build fingerprint.

structure / hierarchy_signature / asset_listing / asset_inventory / component_values read through a Probe or the
disk; canonical_digest (usk_eval.store) turns any of them into the sha256 that goes to state.json, so the run state never
stores what a task inventoried.
"""
import json
import os

from ..store import canonical_digest, sha256_bytes
from .checks import local_trs, object_id

RUN_TOKEN = "<RUN>"


def structure(probe, root_iids, poses=False):
    """Canonical subtree summary, depth first from each root: id (checks.object_id), entityId and instanceId as the
    editor reports them, name, active, tag, layer, path, components as [type, enabled] and the ids of the children in
    sibling order; with poses=True each node also carries `local`, its local position, rotation and scale
    (checks.local_trs), so a moved, turned or rescaled node differs too. A root is an int, an entityId text or a result
    dict. A missing object keeps its node with empty fields, so a deleted object differs from its snapshot instead of
    aborting the comparison."""
    if isinstance(root_iids, (str, int, dict)):
        root_iids = [root_iids]
    out = []
    seen = set()
    stack = list(reversed(list(root_iids)))
    while stack:
        ref = stack.pop()
        ident = object_id(ref)
        if ident is not None:
            if str(ident) in seen:
                continue
            seen.add(str(ident))
        info = probe.go(ref) or {}
        kids = [object_id(child) for child in info.get("children") or []]
        node = {"id": object_id(info) if info else ident, "entityId": info.get("entityId"),
                "instanceId": info.get("instanceId"), "name": info.get("name"), "active": info.get("isActive"),
                "tag": info.get("tag"), "layer": info.get("layer"), "path": info.get("path"),
                "components": [[c.get("type"), c.get("enabled")] for c in probe.components(ref)],
                "children": kids}
        if poses:
            node["local"] = local_trs(probe, ref) if info else None
        out.append(node)
        stack.extend(reversed([kid for kid in kids if kid is not None]))
    return out


def _round(vec):
    """Four decimals, and no negative zero: two builds of one seed must serialise alike."""
    if not isinstance(vec, dict):
        return vec
    return {k: round(float(v), 4) + 0.0 if isinstance(v, (int, float)) and not isinstance(v, bool) else v
            for k, v in vec.items()}


def _signature(probe, ref, depth, limit):
    info = probe.go(ref) or {}
    node = {"name": info.get("name"), "active": info.get("isActive"), "tag": info.get("tag"),
            "layer": info.get("layer"), "components": info.get("components"),
            "pos": _round(info.get("position")), "rot": _round(info.get("rotation")),
            "scale": _round(info.get("scale"))}
    if depth < limit:
        node["children"] = [_signature(probe, c, depth + 1, limit) for c in info.get("children") or []]
    return node


def hierarchy_signature(probe, root_iid, limit=8):
    """Normalised hierarchy without ids: names, order, components, TRS, flags, down to `limit` levels. The root is an
    int, an entityId text or a result dict."""
    return _signature(probe, root_iid, 0, limit)


def asset_listing(project_path, asset_root):
    """Relative paths of the files under the run folder, without .meta files and the run scene (Eval_*)."""
    root = os.path.join(project_path, asset_root)
    out = []
    for base, _dirs, files in os.walk(root):
        for name in files:
            if name.endswith(".meta") or name.startswith("Eval_"):
                continue
            out.append(os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/"))
    return sorted(out)


def _sha256(path):
    with open(path, "rb") as handle:
        return sha256_bytes(handle.read())


def asset_inventory(project_path, asset_root, hashed=None):
    """Every folder and file under the run folder, .meta files included, as sorted [relative path, sha256] rows: a
    folder's path ends in "/" and carries None, a file the `hashed(rel)` predicate turns down carries None too (a file
    whose content the task changes on purpose and judges elsewhere). The run scene (Eval_*) and its .meta are left out.
    Unlike asset_listing it sees an empty folder, a .meta change (labels, GUID, import settings) and an edited file."""
    root = os.path.join(project_path, asset_root)
    out = []
    for base, dirs, files in os.walk(root):
        dirs.sort()
        for name in dirs:
            out.append([os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/") + "/", None])
        for name in sorted(files):
            if name.startswith("Eval_"):
                continue
            rel = os.path.relpath(os.path.join(base, name), root).replace(os.sep, "/")
            out.append([rel, _sha256(os.path.join(base, name)) if hashed is None or hashed(rel) else None])
    return sorted(out)


def component_values(probe, ref, types=None, skip=()):
    """{component type: {propertyPath: value}} of an object, as component_get_serialized_properties reads them: every
    component type it carries, or only `types`, minus `skip`; a type that occurs twice is read once (the first one).
    {} for a missing object."""
    out = {}
    for comp in probe.components(ref):
        kind = comp.get("type")
        if not kind or kind in out or kind in skip or (types is not None and kind not in types):
            continue
        out[kind] = probe.ser(ref, kind)
    return out


def _root_ref(ctx):
    """The object root as a Probe reference: the canonical id the runner stored (scope.root_id), else the legacy
    instanceId, else the root looked up by path. The last case is a state written before rootId existed on Unity
    6000.4+, where every instanceId is 0."""
    scope = ctx.scope
    for stored in (getattr(scope, "root_id", None), scope.root_iid):
        if object_id(stored) is not None:
            return stored
    return ctx.probe.go_path(ctx.R)


def fingerprint(ctx):
    """sha256 of the build's initial state (hierarchy and asset listing) with the run id masked; the same seed must
    give the same value in every build (selftest gate)."""
    state = {"hierarchy": hierarchy_signature(ctx.probe, _root_ref(ctx)),
             "assets": asset_listing(ctx.project_path, ctx.A)}
    masked = json.dumps(state, default=str).replace(ctx.scope.run_id, RUN_TOKEN)
    return canonical_digest(json.loads(masked))

# Producer:Betsy
