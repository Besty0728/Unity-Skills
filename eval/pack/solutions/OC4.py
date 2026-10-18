"""OC4 reference route, exact-failure negatives and alternative routes through public REST skills."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

CHUNK = 50


def query(ctx, include=True, limit=100, sample=100):
    result = ctx.c.read("batch_query_gameobjects", queryJson=json.dumps({"componentType": "MeshRenderer",
                        "includeInactive": include, "limit": limit}), sampleLimit=sample)
    rows = [row for row in result.get("objects") or [] if V.under(row.get("path"), ctx.handles["groupPath"])]
    return result, rows


def inventory(ctx, route="query"):
    if route == "tree":
        rows, pending = [], [ctx.handles["group"]]
        while pending:
            info = ctx.probe.go(pending.pop()) or {}
            pending.extend(info.get("children") or [])
            if "MeshRenderer" in (info.get("components") or []):
                rows.append(info)
        return rows
    if route == "find":
        result = ctx.c.read("gameobject_find", component="MeshRenderer")
        if result.get("truncated"):
            result = ctx.c.read("gameobject_find", component="MeshRenderer", limit=result["totalMatched"])
        return [row for row in result.get("objects") or [] if V.under(row.get("path"), ctx.handles["groupPath"])]
    result, rows = query(ctx)
    if result.get("truncated") or len(result.get("objects") or []) < result.get("count", 0):
        cap = max(result.get("totalMatched", 0), result.get("count", 0), 240)
        _result, rows = query(ctx, limit=cap, sample=cap)
    return rows


def audit(ctx, rows):
    names = {os.path.splitext(os.path.basename(path))[0]: path for path in ctx.handles["materials"]}
    shaders = {path: (ctx.probe.material(path=path) or {}).get("shader") for path in ctx.handles["materials"]}
    counts = {key: 0 for key in ("standardSlots", "missingSlots", "emptySlots", "otherSlots")}
    counts.update(renderers=len(rows), slots=0, declaredComplete=True)
    entries = []
    for row in rows:
        ref = V.object_id(row)
        result = ctx.c.read("component_get_serialized_properties", componentType="MeshRenderer",
                            includeChildren=True, includeHidden=True, limit=5000, **V.address(ref))
        for prop in result.get("properties") or []:
            path = prop.get("propertyPath") or ""
            if not ctx.private.SLOT.match(path):
                continue
            state = prop.get("referenceState")
            parsed = V.parse_ref(prop.get("value"))
            if state == "missing":
                kind = "missingSlots"
            elif state == "none":
                kind = "emptySlots"
            elif parsed and parsed[2] in names:
                kind = "standardSlots" if shaders[names[parsed[2]]] == "Standard" else "otherSlots"
            else:
                raise ValueError("cannot classify material slot")
            counts[kind] += 1
            counts["slots"] += 1
            entries.append({"id": ref, "path": row["path"], "propertyPath": path, "kind": kind})
    return counts, entries


def write_entries(ctx, entries, destination=None, singles=False):
    destination = destination or ctx.handles["material"]
    items = [{"path": row["path"], "componentType": "MeshRenderer", "propertyPath": row["propertyPath"],
              "assetPath": destination, "objectType": "Material"} for row in entries]
    if singles:
        for item in items:
            ctx.c.write("component_set_serialized_property", **item)
    else:
        for start in range(0, len(items), CHUNK):
            part = items[start:start + CHUNK]
            result = ctx.c.write("component_set_serialized_property_batch", items=json.dumps(part))
            if result.get("failCount") or result.get("successCount") != len(part):
                raise ValueError("material replacement batch failed")


def repair(ctx, route="query", select=None, singles=False, destination=None):
    rows = inventory(ctx, route)
    counts, entries = audit(ctx, rows)
    replacements = [row for row in entries if row["kind"] in ("standardSlots", "missingSlots")]
    if select:
        replacements = [row for row in replacements if select(row)]
    write_entries(ctx, replacements, destination=destination, singles=singles)
    return counts, entries


def reference(ctx):
    return repair(ctx)[0]


def only_active_branches(ctx):
    rows = inventory(ctx)
    counts, entries = audit(ctx, rows)
    _result, active = query(ctx, include=False, limit=1000, sample=1000)
    ids = {str(V.object_id(row)) for row in active}
    write_entries(ctx, [row for row in entries if row["kind"] in ("standardSlots", "missingSlots")
                        and str(row["id"]) in ids])
    return counts


def first_query_limit(ctx):
    counts, entries = audit(ctx, inventory(ctx))
    result, rows = query(ctx)
    if not result.get("truncated"):
        raise ValueError("the fixture did not exceed the query limit")
    ids = {str(V.object_id(row)) for row in rows}
    write_entries(ctx, [row for row in entries if row["kind"] in ("standardSlots", "missingSlots")
                        and str(row["id"]) in ids])
    return counts


def first_query_sample(ctx):
    counts, entries = audit(ctx, inventory(ctx))
    _result, rows = query(ctx, limit=1000, sample=20)
    ids = {str(V.object_id(row)) for row in rows}
    write_entries(ctx, [row for row in entries if row["kind"] in ("standardSlots", "missingSlots")
                        and str(row["id"]) in ids])
    return counts


def material_assign_only(ctx):
    counts, entries = audit(ctx, inventory(ctx))
    for row in entries:
        if row["kind"] in ("standardSlots", "missingSlots") and row["propertyPath"].endswith("[0]"):
            ctx.c.write("material_assign", materialPath=ctx.handles["material"], **V.address(row["id"]))
    return counts


def conflate_null(ctx):
    counts, entries = repair(ctx)
    write_entries(ctx, [row for row in entries if row["kind"] == "emptySlots"])
    return counts


def skip_missing(ctx):
    return repair(ctx, select=lambda row: row["kind"] == "standardSlots")[0]


def replace_valid_slots(ctx):
    counts, entries = repair(ctx)
    write_entries(ctx, [row for row in entries if row["kind"] == "otherSlots"])
    return counts


def same_shader_other_asset(ctx):
    path = next(path for path in ctx.handles["materials"] if path != ctx.handles["material"]
                and (ctx.probe.material(path=path) or {}).get("shader") != "Standard")
    return repair(ctx, destination=path)[0]


def resize_array(ctx):
    counts, entries = repair(ctx)
    ref = entries[0]["id"]
    size = sum(1 for row in entries if V.same_id(row["id"], ref))
    ctx.c.write("component_set_serialized_property", componentType="MeshRenderer",
                propertyPath="m_Materials.Array.size", value=str(size + 1), **V.address(ref))
    return counts


def edit_standard_shader(ctx):
    path = next(path for path in ctx.handles["materials"] if (ctx.probe.material(path=path) or {}).get("shader") == "Standard")
    counts = reference(ctx)
    ctx.c.write("material_set_shader", path=path, shaderName="Universal Render Pipeline/Lit")
    return counts


def inaccurate_counts(ctx):
    counts = reference(ctx)
    counts["standardSlots"] += 1
    return counts


def outside_root(ctx):
    counts = reference(ctx)
    ctx.c.write("gameobject_create", name="AuditHelper")
    return counts


def direct_file(ctx):
    counts = reference(ctx)
    with open(os.path.join(ctx.project_path, *ctx.asset("audit.txt").split("/")), "wb") as handle:
        handle.write(b"audited\n")
    return counts


def hierarchy_single_writes(ctx):
    return repair(ctx, route="tree", singles=True)[0]


def find_and_batch(ctx):
    return repair(ctx, route="find")[0]


def save_run_scene(ctx):
    counts = reference(ctx)
    ctx.c.write("scene_save")
    return counts


NEGATIVES = [
    Negative("only_active_branches", only_active_branches, {"A1", "A2"}, "inactive branches are never processed"),
    Negative("first_query_limit", first_query_limit, {"A1", "A2"}, "queryJson.limit truncation is ignored"),
    Negative("first_query_sample", first_query_sample, {"A1", "A2"}, "the sample is mistaken for every match"),
    Negative("material_assign_only", material_assign_only, {"A1", "A2"}, "only slot zero is written"),
    Negative("conflate_null", conflate_null, {"A3"}, "empty slots are treated as missing references"),
    Negative("skip_missing", skip_missing, {"A2"}, "only Standard materials are repaired"),
    Negative("replace_valid_slots", replace_valid_slots, {"A4"}, "valid assigned slots are overwritten"),
    Negative("same_shader_other_asset", same_shader_other_asset, {"A1", "A2"}, "a different URP material replaces Fallback"),
    Negative("resize_array", resize_array, {"A5"}, "an extra slot is appended"),
    Negative("edit_standard_shader", edit_standard_shader, {"A6"}, "the Standard asset is edited after reference repair"),
    Negative("inaccurate_counts", inaccurate_counts, {"A7"}),
    Negative("outside_root", outside_root, {"A8"}),
    Negative("direct_file", direct_file, {"A9"}, "a new file under A bypasses the skills"),
]

ALTERNATIVES = [("hierarchy_single_writes", hierarchy_single_writes), ("find_and_batch", find_and_batch),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
