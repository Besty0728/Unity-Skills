"""T10 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os
import re

from usk_eval.task_api import Negative


def _list(ctx, folder):
    res = ctx.c.read("batch_query_assets", folder=folder, typeFilter="Material", maxResults=500)
    return sorted(a["path"] for a in res.get("assets") or [] if (a.get("path") or "").endswith(".mat"))


def _plan(ctx, sources=None, reverse=False):
    """{source: destination} for `sources` (default: every material under Incoming) against what Materials holds now."""
    h = ctx.handles
    sources = _list(ctx, h["incoming"]) if sources is None else sources
    return ctx.private.destinations(sources, _list(ctx, h["materials"]), h["materials"], reverse=reverse)


def _folders(ctx, plan):
    need = sorted({dest.rsplit("/", 1)[0] for dest in plan.values()})
    missing = [folder for folder in need if not ctx.probe.asset(folder)]
    if missing:
        ctx.c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": folder} for folder in missing]))


def _move(ctx, plan):
    _folders(ctx, plan)
    ctx.c.write("asset_move_batch", items=json.dumps([{"sourcePath": s, "destinationPath": d}
                                                     for s, d in sorted(plan.items())]))


def _prune(ctx):
    """Delete the Incoming folders that hold nothing any more, deepest first."""
    root = os.path.join(ctx.project_path, *ctx.handles["incoming"].split("/"))
    deleted = []
    for base, dirs, files in sorted(os.walk(root), key=lambda walk: -walk[0].count(os.sep)):
        live = [name for name in dirs if os.path.isdir(os.path.join(base, name))]
        if not live and not [name for name in files if not name.endswith(".meta") and not name.startswith(".")]:
            rel = os.path.relpath(base, ctx.project_path).replace(os.sep, "/")
            ctx.c.write("asset_delete", assetPath=rel)
            deleted.append(rel)
    return deleted


def _answer(plan):
    return {"moves": [{"from": s, "to": d} for s, d in sorted(plan.items())], "declaredComplete": True}


def _grip(ctx):
    return ctx.asset("Incoming", "Misc", "Grip.physicMaterial")


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    plan = _plan(ctx)
    _move(ctx, plan)
    _prune(ctx)
    return _answer(plan)


def copy_delete(ctx):
    plan = _plan(ctx)
    _folders(ctx, plan)
    for source, dest in sorted(plan.items()):
        copy = ctx.c.write("asset_duplicate", assetPath=source).get("copy")
        ctx.c.write("asset_move", sourcePath=copy, destinationPath=dest)
        ctx.c.write("asset_delete", assetPath=source)
    _prune(ctx)
    return _answer(plan)


def recreate_clash(ctx):
    plan = _plan(ctx)
    clash = {s: d for s, d in plan.items() if os.path.basename(s) != os.path.basename(d)}
    _move(ctx, {s: d for s, d in plan.items() if s not in clash})
    _folders(ctx, clash)
    for source, dest in sorted(clash.items()):
        copy = ctx.c.write("asset_duplicate", assetPath=source).get("copy")
        ctx.c.write("asset_move", sourcePath=copy, destinationPath=dest)
        ctx.c.write("asset_delete", assetPath=source)
    _prune(ctx)
    return _answer(plan)


def overwrite_bolt(ctx):
    ctx.c.write("asset_delete", assetPath=ctx.asset("Materials", "B", "Bolt.mat"))
    plan = _plan(ctx)
    _move(ctx, plan)
    _prune(ctx)
    return _answer(plan)


def move_grip(ctx):
    plan = _plan(ctx)
    _move(ctx, plan)
    folder = ctx.asset("Materials", "G")
    if not ctx.probe.asset(folder):
        ctx.c.write("asset_create_folder", folderPath=folder)
    ctx.c.write("asset_move", sourcePath=_grip(ctx), destinationPath=folder + "/Grip.physicMaterial")
    _prune(ctx)
    return _answer(plan)


def leave_empty(ctx):
    plan = _plan(ctx)
    _move(ctx, plan)
    return _answer(plan)


def delete_incoming(ctx):
    plan = _plan(ctx)
    _move(ctx, plan)
    ctx.c.write("asset_delete", assetPath=ctx.handles["incoming"])
    return _answer(plan)


def wrong_order(ctx):
    plan = _plan(ctx, reverse=True)
    _move(ctx, plan)
    _prune(ctx)
    return _answer(plan)


def move_elsewhere(ctx):
    plan = _plan(ctx)
    _move(ctx, plan)
    extra = _plan(ctx, [ctx.asset("Elsewhere", "Brick.mat")])
    _move(ctx, extra)
    _prune(ctx)
    merged = dict(plan)
    merged.update(extra)
    return _answer(merged)


def bad_suffix(ctx):
    plan = {s: re.sub(r"_(\d+)\.mat$", r"-\1.mat", d) for s, d in _plan(ctx).items()}
    _move(ctx, plan)
    _prune(ctx)
    return _answer(plan)


def shallow_scan(ctx):
    incoming = ctx.handles["incoming"]
    shallow = [path for path in _list(ctx, incoming) if path[len(incoming) + 1:].count("/") == 1]
    plan = _plan(ctx, shallow)
    _move(ctx, plan)
    _prune(ctx)
    return _answer(plan)


def wrong_report(ctx):
    answer = reference(ctx)
    rows = answer["moves"]
    rows[0]["to"], rows[1]["to"] = rows[1]["to"], rows[0]["to"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayHelper")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "moves.txt"), b"moved\n")
    return answer


def one_by_one(ctx):
    # asset_find listing, single moves, the emptied top-level folders deleted in one call each
    incoming = ctx.handles["incoming"]
    found = ctx.c.read("asset_find", searchFilter="t:Material", limit=5000)
    sources = [a["path"] for a in found.get("assets") or []
               if (a.get("path") or "").startswith(incoming + "/") and a["path"].endswith(".mat")]
    plan = _plan(ctx, sources)
    for source, dest in sorted(plan.items()):
        folder = dest.rsplit("/", 1)[0]
        if not ctx.probe.asset(folder):
            ctx.c.write("asset_create_folder", folderPath=folder)
        ctx.c.write("asset_move", sourcePath=source, destinationPath=dest)
    for name in ("Batch01", "Batch02", "Vendor"):
        ctx.c.write("asset_delete", assetPath="%s/%s" % (incoming, name))
    return _answer(plan)


def rename_then_move(ctx):
    plan = _plan(ctx)
    _folders(ctx, plan)
    for source, dest in sorted(plan.items()):
        current = source
        if os.path.basename(dest) != os.path.basename(source):
            current = source.rsplit("/", 1)[0] + "/" + os.path.basename(dest)
            ctx.c.write("asset_move", sourcePath=source, destinationPath=current)
        ctx.c.write("asset_move", sourcePath=current, destinationPath=dest)
    _prune(ctx)
    return _answer(plan)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("copy_delete", copy_delete, {"H1", "H2", "H3", "H4", "H8"},
             "duplicate, move the copy, delete the original"),
    Negative("recreate_clash", recreate_clash, {"H2", "H3", "H4", "H8"}, "only the clashing names are duplicated"),
    Negative("overwrite_bolt", overwrite_bolt, {"H2", "H3", "H4", "H5"},
             "the existing Materials/B/Bolt.mat is deleted"),
    Negative("move_grip", move_grip, {"H6", "H7"}, "the physic material is moved too and the folders emptied"),
    Negative("leave_empty", leave_empty, {"H7"}),
    Negative("delete_incoming", delete_incoming, {"H6", "H7"}, "the whole Incoming tree, Grip included, is deleted"),
    Negative("wrong_order", wrong_order, {"H1", "H3"}, "descending instead of ascending ordinal order"),
    Negative("move_elsewhere", move_elsewhere, {"H5"}, "Elsewhere/Brick.mat is moved as well"),
    Negative("bad_suffix", bad_suffix, {"H3"}, "-2, -3 instead of _2, _3"),
    Negative("shallow_scan", shallow_scan, {"H1", "H3", "H7"}, "Vendor/Old is never looked at"),
    Negative("wrong_report", wrong_report, {"H8"}),
    Negative("outside_root", outside_root, {"H9"}),
    Negative("direct_file", direct_file, {"H10"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("one_by_one", one_by_one), ("rename_then_move", rename_then_move),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
