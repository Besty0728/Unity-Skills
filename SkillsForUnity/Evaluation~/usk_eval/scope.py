"""Scope evidence over flattened journal records (contract 2.9, 5.7, 7.1; ported from v1 eval/harness/journal.py).

Records are what usk_eval.journal.flatten produces. A record that cannot be tied to the run's object root R, asset
root A or run scene counts against the attempt: nothing here turns missing evidence into an exemption.
"""

FILE_KINDS = ("imported", "deleted", "moved")
SCENE_KINDS = ("scene_saved", "scene_opened")


def under(path, root):
    if not isinstance(path, str) or not isinstance(root, str) or not path or not root:
        return False
    path = path.replace("\\", "/")
    root = root.replace("\\", "/").rstrip("/")
    return path == root or path.startswith(root + "/")


def _strip_meta(path):
    if not isinstance(path, str):
        return ""
    return path[:-5] if path.endswith(".meta") else path


def _same_path(path, other):
    if not isinstance(path, str) or not isinstance(other, str) or not path or not other:
        return False
    return path.replace("\\", "/") == other.replace("\\", "/")


def _file_paths(rec):
    """Asset paths a file record touches; a move touches both ends. A .meta file sits next to its asset, so it is
    under a root exactly when the asset is."""
    paths = [rec.get("assetPath")]
    if rec.get("kind") == "moved":
        paths.append(rec.get("from"))
    return [p for p in paths if p]


def adopted_paths(records, object_root):
    """Paths of objects that were created or edited outside R and then moved under R: their earlier
    records are transient work on the subject's own object, not edits of anything outside R."""
    out = set()
    for rec in records:
        if rec.get("kind") != "gameobject_reparented" or not rec.get("fromPath"):
            continue
        if under(rec.get("toPath"), object_root) and not under(rec["fromPath"], object_root):
            out.add(rec["fromPath"])
    return out


# ---------------------------------------------------------------- identity table (eval/design/scope-verifier-fix)


def _identity(rec):
    """entityId (6000.4+: a ulong string), else a non-zero instanceId; zero means no object on every Unity version."""
    for key in ("entityId", "instanceId"):
        value = rec.get(key)
        if isinstance(value, bool) or value in (None, "", 0, "0"):
            continue
        return str(value)
    return None


def _observed_location(rec):
    """Trusted location evidence for later pathless destruction records: a create proves where the attempt
    introduced the identity, a reparent proves its new path. Other records may mention both, but prove neither."""
    kind = rec.get("kind")
    if kind == "gameobject_created":
        return rec.get("path")
    if kind == "gameobject_reparented":
        return rec.get("toPath")
    return None


def _location_in_scope(location, object_root, run_scene):
    if not location or not under(location.get("path"), object_root):
        return False
    scene = location.get("scenePath")
    return not (run_scene and scene and not _same_path(scene, run_scene))


# ---------------------------------------------------------------- scope


def _outside_scope(rec, identity, locations, adopted, object_root, asset_root, run_scene):
    kind = rec.get("kind")
    if kind in FILE_KINDS:
        path = rec.get("assetPath")
        return not (under(path, asset_root) and (kind != "moved" or under(rec.get("from") or path, asset_root)))
    if kind in SCENE_KINDS:
        return not _same_path(rec.get("path"), run_scene)
    if kind == "omitted":
        return True                      # paths were cut off by the plugin's cap: nothing to prove they are in scope
    if rec.get("type") != "scene_changes":
        return False                     # undo_redo: its effects arrive as scene_changes records of their own
    asset = rec.get("assetPath")
    if asset:
        return not under(asset, asset_root)
    paths = [p for p in (rec.get("path"), rec.get("fromPath"), rec.get("toPath"), rec.get("newParentPath"))
             if p and p not in adopted]
    if not paths and kind == "gameobject_destroyed":
        return not _location_in_scope(locations.get(identity) if identity else None, object_root, run_scene)
    if not all(under(p, object_root) for p in paths):
        return True
    if not paths and not adopted.intersection(p for p in (rec.get("path"), rec.get("fromPath")) if p):
        return True
    return bool(run_scene and rec.get("scenePath") and not _same_path(rec.get("scenePath"), run_scene))


def scope_violations(records, object_root, asset_root, run_scene=None, extra_allowed=None):
    """Records that change anything outside R, A or the run scene; extra_allowed(rec) exempts a record.

    G2: saving or opening the run scene is allowed, any other scene is a violation. A gameobject_destroyed without
    a path (the plugin's catalog did not know the object) is judged by the last location the journal observed for
    its identity, and only a create or reparent inside R (in the run scene) counts: unknown identities fail closed.
    An identity leaves the table when its object is destroyed, so a reused instance id inherits nothing.
    """
    bad = []
    adopted = adopted_paths(records, object_root)
    locations = {}
    for rec in records:
        identity = _identity(rec)
        observed = _observed_location(rec)
        if identity and observed:
            previous = locations.get(identity) or {}
            locations[identity] = {"path": observed, "scenePath": rec.get("scenePath") or previous.get("scenePath")}
        if not (extra_allowed and extra_allowed(rec)) and \
                _outside_scope(rec, identity, locations, adopted, object_root, asset_root, run_scene):
            bad.append(rec)
        if rec.get("kind") == "gameobject_destroyed" and identity:
            locations.pop(identity, None)
    return bad


# ---------------------------------------------------------------- evidence classes (contract 2.9, 5.7)


def non_rest(records, asset_root, run_scene=None, allowed=None):
    """Editor-sourced records the attempt cannot be blamed for or excused by (contract 2.9, choice 35).

    Exempt: Play Mode records, file records wholly under A (Unity importing what the agent wrote to disk; the G4
    assertion judges those through direct_writes), saving or opening the run scene, and allowed(rec).
    """
    out = []
    for rec in records:
        if rec.get("source") != "editor" or rec.get("isPlaying"):
            continue
        kind = rec.get("kind")
        if kind in FILE_KINDS:
            paths = _file_paths(rec)
            if paths and all(under(p, asset_root) for p in paths):
                continue
        elif kind in SCENE_KINDS and _same_path(rec.get("path"), run_scene):
            continue
        if allowed is not None and allowed(rec):
            continue
        out.append(rec)
    return out


def _save_imports(records, run_scene):
    """Positions of the run scene file's imports that directly follow a REST save of that scene: Unity importing what
    the skill just wrote. A save opens a window over the file records right after it and any other record closes it.
    Refresh-window imports are never explained this way: they are what the runner's refresh found on disk."""
    explained = set()
    open_window = False
    for index, rec in enumerate(records):
        kind = rec.get("kind")
        if kind == "scene_saved":
            open_window = rec.get("source") == "rest" and _same_path(rec.get("path"), run_scene)
        elif kind in FILE_KINDS:
            if open_window and kind == "imported" and rec.get("origin") != "refresh" \
                    and _same_path(_strip_meta(rec.get("assetPath")), run_scene):
                explained.add(index)
        else:
            open_window = False
    return explained


def direct_writes(records, asset_root, run_scene=None):
    """File records touching A that the runner's refresh picked up or that arrived from the editor itself:
    evidence of files written around the skills (the G4 assertion of ops tasks).

    With run_scene, the import of that scene file (and its .meta) right after a REST save of the scene is the save's
    own doing and does not count, whichever source the plugin attributed the import to."""
    records = list(records)
    explained = _save_imports(records, run_scene) if run_scene else ()
    return [rec for index, rec in enumerate(records)
            if index not in explained and rec.get("kind") in FILE_KINDS
            and any(under(p, asset_root) for p in _file_paths(rec))
            and (rec.get("origin") == "refresh" or rec.get("source") == "editor")]


def write_records(records, run_scene, runner_agent=None):
    """Every record that counts as a write by a read-only task's agent.

    Not counted: Play Mode records, the runner's own records of the attempt window (editor_stop and the like; its
    refresh-window records stay, they are the agent's direct file writes), saving the run scene and importing
    the run scene file (.unity, .unity.meta).
    """
    out = []
    for rec in records:
        if rec.get("isPlaying"):
            continue
        if runner_agent and rec.get("agent") == runner_agent and rec.get("origin") != "refresh":
            continue
        kind = rec.get("kind")
        if kind == "scene_saved" and _same_path(rec.get("path"), run_scene):
            continue
        if kind == "imported" and _same_path(_strip_meta(rec.get("assetPath")), run_scene):
            continue
        out.append(rec)
    return out

# Producer:Betsy
