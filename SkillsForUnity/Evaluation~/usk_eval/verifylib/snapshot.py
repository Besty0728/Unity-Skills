"""Canonical state summaries (contract 5.2, 5.8): C0 digests of read-only discovery tasks and the build fingerprint.

structure / hierarchy_signature / asset_listing read through a Probe or the disk; canonical_digest (usk_eval.store)
turns any of them into the sha256 that goes to state.json, so the run state never stores what a task inventoried.
"""
import json
import os

from ..store import canonical_digest
from .checks import object_id

RUN_TOKEN = "<RUN>"


def structure(probe, root_iids):
    """Canonical subtree summary, depth first from each root: id (checks.object_id), entityId and instanceId as the
    editor reports them, name, active, tag, layer, path, components as [type, enabled] and the ids of the children in
    sibling order. A root is an int, an entityId text or a result dict. A missing object keeps its node with empty
    fields, so a deleted object differs from its snapshot instead of aborting the comparison."""
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
        out.append({"id": object_id(info) if info else ident, "entityId": info.get("entityId"),
                    "instanceId": info.get("instanceId"), "name": info.get("name"), "active": info.get("isActive"),
                    "tag": info.get("tag"), "layer": info.get("layer"), "path": info.get("path"),
                    "components": [[c.get("type"), c.get("enabled")] for c in probe.components(ref)],
                    "children": kids})
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
