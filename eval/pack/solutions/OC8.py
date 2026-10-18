"""Reference routes and isolated incorrect final states for OC8."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

TAG = "ProjectSettings/TagManager.asset"


def _names(ctx):
    return ctx.task.layer_names(ctx)


def _layer(ctx, ref, name, recursive=False):
    result = ctx.c.write("gameobject_set_layer_batch", items=json.dumps([
        dict(V.address(ref), layer=name, recursive=recursive)]))
    if result.get("failCount"):
        raise ValueError("layer batch failed")


def _set_name(ctx, index, name):
    ctx.c.write("scriptableobject_set_serialized_property", assetPath=TAG,
                propertyPath="layers.Array.data[%d]" % index, value=name)


def _pair(ctx, a, b, enabled):
    ctx.c.write("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=enabled)


def _solve(ctx, route="flat", allocation="low", mutate=None, reverse=False):
    before = _names(ctx)
    labels = ctx.handles["names"]
    rows = V.structure(ctx.probe, ctx.root)
    paths = [[row["id"], row["path"]] for row in rows]
    picks = ctx.private.selection(paths, ctx.handles["site"], labels)
    free = [i for i in range(8, 32) if not before[i]]
    if allocation == "high":
        free.reverse()
    slots = free[:2]
    if allocation == "occupied":
        slots[0] = next(i for i in range(8, 32) if before[i] and before[i] not in labels)
    for index, name in zip(slots, labels[:2]):
        _set_name(ctx, index, name)
    names = _names(ctx)
    for a, b, enabled in ctx.private.requests(names, labels):
        _pair(ctx, b if reverse else a, a if reverse else b, enabled)

    chosen = [row for row in rows if picks[str(row["id"])]]
    if route == "flat":
        for row in chosen:
            _layer(ctx, row["id"], picks[str(row["id"])])
    else:
        roots = [row for row in chosen if any(row["name"].endswith("__" + name) for name in labels[:2])]
        roots.sort(key=lambda row: row["path"].count("/"))
        for row in roots:
            _layer(ctx, row["id"], picks[str(row["id"])], recursive=route != "roots")
        if route == "restore":
            for row in rows:
                if "KeepLayer" in row["path"].split("/"):
                    _layer(ctx, row["id"], row["layer"])
    if mutate:
        mutate(ctx, before, rows, picks)
    return {"layers": ctx.private.changed_names(before, _names(ctx)), "declaredComplete": True}


def reference(ctx):
    return _solve(ctx)


def recursive_restore_other_slots(ctx):
    return _solve(ctx, route="restore", allocation="high", reverse=True)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


def occupied_slot(ctx):
    return _solve(ctx, allocation="occupied")


def name_typo(ctx):
    def edit(c, _before, _rows, _picks):
        name = c.handles["names"][1]
        _set_name(c, c.private.slot(_names(c), name), name + "x")
    return _solve(ctx, mutate=edit)


def fourth_pair(ctx):
    def edit(c, _before, _rows, _picks):
        labels = c.handles["names"]
        names = _names(c)
        a, b = (c.private.slot(names, name) for name in labels[2:])
        enabled = c.read("physics_get_layer_collision", layer1=a, layer2=b)["collisionEnabled"]
        _pair(c, a, b, not enabled)
    return _solve(ctx, mutate=edit)


def inverted_enabled(ctx):
    def edit(c, _before, _rows, _picks):
        a, b, enabled = c.private.requests(_names(c), c.handles["names"])[1]
        _pair(c, a, b, not enabled)
    return _solve(ctx, mutate=edit)


def compact_indices(ctx):
    def edit(c, _before, _rows, _picks):
        names, labels = _names(c), c.handles["names"]
        required = c.private.requests(names, labels)
        for a, b, enabled in required:
            _pair(c, a, b, not enabled)
        compact = c.read("project_get_layers")["layers"]
        candidates = [(compact.index(labels[0]), compact.index(labels[1])),
                      (compact.index(labels[0]), compact.index(labels[2])),
                      (compact.index(labels[1]), compact.index(labels[3]))]
        allowed = {(a, b) for a, b, _enabled in required}
        a, b = next(tuple(sorted(pair)) for pair in candidates if tuple(sorted(pair)) not in allowed)
        value = c.read("physics_get_layer_collision", layer1=a, layer2=b)["collisionEnabled"]
        _pair(c, a, b, not value)
    return _solve(ctx, mutate=edit)


def roots_only(ctx):
    return _solve(ctx, route="roots")


def recursive_keep_too(ctx):
    return _solve(ctx, route="recursive")


def restore_keep_uniform(ctx):
    def edit(c, _before, rows, _picks):
        row = next(row for row in rows if row["name"] == "KeepLayer")
        _layer(c, row["id"], row["layer"], recursive=True)
    return _solve(ctx, mutate=edit)


def skip_inactive(ctx):
    def edit(c, _before, rows, picks):
        for row in rows:
            if picks[str(row["id"])] and not row["active"]:
                _layer(c, row["id"], row["layer"])
    return _solve(ctx, mutate=edit)


def outermost_wins(ctx):
    def edit(c, _before, rows, picks):
        selected = [row for row in rows if picks[str(row["id"])]]
        roots = [row for row in selected if any(row["name"].endswith("__" + label)
                                                for label in c.handles["names"][:2])]
        for row in selected:
            root = min((r for r in roots if V.under(row["path"], r["path"])), key=lambda r: len(r["path"]))
            _layer(c, row["id"], picks[str(root["id"])])
    return _solve(ctx, mutate=edit)


def prefix_marker(ctx):
    def edit(c, _before, rows, picks):
        for row in rows:
            if picks[str(row["id"])] and any(p.startswith("KeepLayer") for p in row["path"].split("/")):
                _layer(c, row["id"], row["layer"])
    return _solve(ctx, mutate=edit)


def outside_rule(ctx):
    def edit(c, _before, rows, picks):
        row = next(row for row in rows if not picks[str(row["id"])])
        _layer(c, row["id"], c.handles["names"][0])
    return _solve(ctx, mutate=edit)


def tag_added(ctx):
    def edit(c, _before, _rows, _picks):
        c.c.write("project_add_tag", tagName=c.handles["names"][0])
    return _solve(ctx, mutate=edit)


def gravity_changed(ctx):
    def edit(c, _before, _rows, _picks):
        g = c.read("physics_get_gravity")
        c.c.write("physics_set_gravity", x=g["x"], y=g["y"] + 1.0, z=g["z"])
    return _solve(ctx, mutate=edit)


def extra_layer(ctx):
    def edit(c, _before, _rows, _picks):
        names = _names(c)
        index = next(i for i in range(8, 32) if not names[i])
        _set_name(c, index, c.handles["names"][0] + "_extra")
    return _solve(ctx, mutate=edit)


def bystander_value(ctx):
    def edit(c, _before, rows, picks):
        row = next(row for row in rows if not picks[str(row["id"])] and
                   c.probe.component_count(row["id"], "Rigidbody"))
        c.c.write("component_set_property", componentType="Rigidbody", propertyName="mass", value="19",
                  **V.address(row["id"]))
    return _solve(ctx, mutate=edit)


def bystander_transform(ctx):
    def edit(c, _before, rows, picks):
        row = next(row for row in rows if not picks[str(row["id"])])
        c.c.write("gameobject_set_transform", localPosY=42.0, **V.address(row["id"]))
    return _solve(ctx, mutate=edit)


def extra_object(ctx):
    def edit(c, _before, _rows, _picks):
        c.c.write("gameobject_create", name="Extra", **V.address(c.root, "parent"))
    return _solve(ctx, mutate=edit)


def extra_folder(ctx):
    def edit(c, _before, _rows, _picks):
        c.c.write("asset_create_folder", folderPath=c.asset("Extra"))
    return _solve(ctx, mutate=edit)


def folder_label(ctx):
    def edit(c, _before, _rows, _picks):
        c.c.write("asset_set_labels", assetPath=c.handles["folders"][0], labels="processed")
    return _solve(ctx, mutate=edit)


def outside_root(ctx):
    def edit(c, _before, _rows, _picks):
        c.c.write("gameobject_create", name="Outside")
    return _solve(ctx, mutate=edit)


def direct_file(ctx):
    def edit(c, _before, _rows, _picks):
        with open(os.path.join(c.project_path, *c.asset("note.txt").split("/")), "w", encoding="utf-8") as stream:
            stream.write("done\n")
    return _solve(ctx, mutate=edit)


def inaccurate_report(ctx):
    answer = reference(ctx)
    answer["layers"][0]["name"] += "x"
    return answer


NEGATIVES = [
    Negative("occupied_slot", occupied_slot, {"A1", "A2"}),
    Negative("name_typo", name_typo, {"A1", "A2", "A3", "A4", "A6"}),
    Negative("fourth_pair", fourth_pair, {"A6"}),
    Negative("inverted_enabled", inverted_enabled, {"A3"}),
    Negative("compact_indices", compact_indices, {"A3", "A6"}),
    Negative("roots_only", roots_only, {"A4"}),
    Negative("recursive_keep_too", recursive_keep_too, {"A5"}),
    Negative("restore_keep_uniform", restore_keep_uniform, {"A5"}),
    Negative("skip_inactive", skip_inactive, {"A4"}),
    Negative("outermost_wins", outermost_wins, {"A4"}),
    Negative("prefix_marker", prefix_marker, {"A4"}),
    Negative("outside_rule", outside_rule, {"A5"}),
    Negative("tag_added", tag_added, {"A6"}),
    Negative("gravity_changed", gravity_changed, {"A6"}),
    Negative("extra_layer", extra_layer, {"A2"}),
    Negative("bystander_value", bystander_value, {"A7"}),
    Negative("bystander_transform", bystander_transform, {"A7"}),
    Negative("extra_object", extra_object, {"A7"}),
    Negative("extra_folder", extra_folder, {"A7"}),
    Negative("folder_label", folder_label, {"A7"}),
    Negative("outside_root", outside_root, {"A9"}),
    Negative("direct_file", direct_file, {"A7", "A10"}),
    Negative("inaccurate_report", inaccurate_report, {"A8"}),
]
ALTERNATIVES = [("recursive_restore_other_slots", recursive_restore_other_slots), ("save_run_scene", save_run_scene)]

# Producer:Betsy
