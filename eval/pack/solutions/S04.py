"""S04 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _answer(ctx, ref):
    info = ctx.probe.go(ref) or {}
    return {"disabledInstanceId": _number(V.object_id(info) or V.object_id(ref)), "disabledPath": info.get("path"),
            "declaredComplete": True}


def _nodes(ctx):
    return ctx.task.snapshot(ctx)["data"]["nodes"]


def _children_of_markers(ctx, nodes):
    table = {str(V.object_id(ref)): state for ref, state in nodes}
    return [(ref, table.get(str(V.object_id(ref))) or {})
            for ref in (table.get(str(V.object_id(ctx.handles["markers"]))) or {}).get("children") or []]


def _set_active(ctx, ref, active):
    ctx.c.write("gameobject_set_active", active=active, **V.address(ref))


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    res = ctx.probe.query({"parentPath": ctx.handles["markersPath"], "componentType": "AudioSource",
                           "includeInactive": True, "caseSensitive": True})
    hits = [obj for obj in res.get("objects") or [] if obj.get("name") == "Marker"]
    ref = V.object_id(hits[0])
    _set_active(ctx, ref, False)
    return _answer(ctx, ref)


def all_markers(ctx):
    nodes = _nodes(ctx)
    for ref, state in _children_of_markers(ctx, nodes):
        if state.get("name") == "Marker":
            _set_active(ctx, ref, False)
    return _answer(ctx, ctx.private.audio_marker(nodes, ctx.handles["markers"]))


def reactivate_hidden(ctx):
    nodes = _nodes(ctx)
    answer = reference(ctx)
    for ref, state in _children_of_markers(ctx, nodes):
        if state.get("name") == "Marker" and state.get("isActive") is False:
            _set_active(ctx, ref, True)
    return answer


def rename(ctx):
    nodes = _nodes(ctx)
    reference(ctx)
    ref = ctx.private.audio_marker(nodes, ctx.handles["markers"])
    ctx.c.write("gameobject_rename", newName="Marker_Audio", **V.address(ref))
    return _answer(ctx, ref)


def archive(ctx):
    nodes = _nodes(ctx)
    ref = next(ref for ref, state in _children_of_markers(ctx, nodes) if state.get("name") == "Marker_Archive")
    _set_active(ctx, ref, False)
    return _answer(ctx, ref)


def other_group(ctx):
    backup = ctx.probe.go(ctx.handles["backup"]) or {}
    ref = V.object_id(next(child for child in backup.get("children") or [] if child.get("name") == "Marker"))
    _set_active(ctx, ref, False)
    return _answer(ctx, ref)


def wrong_report(ctx):
    nodes = _nodes(ctx)
    reference(ctx)
    ref = next(ref for ref, state in _children_of_markers(ctx, nodes)
               if state.get("name") == "Marker" and state.get("isActive") is True
               and "AudioSource" not in (state.get("components") or []))
    return _answer(ctx, ref)


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayMarker")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "markers.txt"), b"disabled\n")
    return answer


def find_with_component(ctx):
    res = ctx.c.read("gameobject_find", name="Marker", component="AudioSource", limit=500)
    path = ctx.handles["markersPath"] + "/Marker"
    hits = [obj for obj in res.get("objects") or [] if obj.get("name") == "Marker" and obj.get("path") == path]
    ref = V.object_id(hits[0])
    _set_active(ctx, ref, False)
    return _answer(ctx, ref)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("all_markers", all_markers, {"C2"}),
    Negative("reactivate_hidden", reactivate_hidden, {"C2"}, "the Marker that was inactive is switched on"),
    Negative("rename", rename, {"C4", "C6"}),
    Negative("archive", archive, {"C1", "C3", "C5"}, "Marker_Archive also has an AudioSource"),
    Negative("other_group", other_group, {"C1", "C3", "C5"}, "R/Hall/Backup/Marker, what a name lookup may hit"),
    Negative("wrong_report", wrong_report, {"C5"}),
    Negative("outside_root", outside_root, {"C6"}),
    Negative("direct_file", direct_file, {"C7"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("find_with_component", find_with_component), ("save_run_scene", save_run_scene)]

# Producer:Betsy
