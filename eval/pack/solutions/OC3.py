"""OC3 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os

from usk_eval.task_api import Negative

BATCH_LIMIT = 50


def _rows(ctx):
    return ctx.private.plan(ctx)


def _items(rows):
    return [{"prefabPath": row["prefabPath"], "componentType": "Loot", "componentIndex": row["componentIndex"],
             "propertyPath": "table", "assetPath": row["tablePath"]} for row in rows]


def _apply(ctx, rows):
    items = _items(rows)
    for start in range(0, len(items), BATCH_LIMIT):
        ctx.c.write("prefab_set_serialized_property_batch", items=json.dumps(items[start:start + BATCH_LIMIT]))


def _answer(ctx):
    """The state as it is after the solver acted: every Loot component of the base prefabs with the table it really
    references, the variants, and the GUID the Common-tier prefabs reference."""
    rows = ctx.private.current(ctx)
    common = next((row["guid"] for row in rows if row["tier"] == "Common" and row["componentIndex"] == 0
                   and row["guid"]), "")
    return {"wired": [{"prefabPath": row["prefabPath"], "componentIndex": row["componentIndex"],
                       "tablePath": row["tablePath"] or ""} for row in rows],
            "variants": ctx.private.variants(ctx), "commonTableGuid": common, "declaredComplete": True}


def _other_common(ctx):
    """The LootTable_Common asset outside the loot folder (the one an asset search by name also finds)."""
    wanted = ctx.handles["tables"]["Common"]
    for path in ctx.private.guid_paths(ctx).values():
        if path.endswith("/LootTable_Common.asset") and path != wanted:
            return path
    raise RuntimeError("no second LootTable_Common asset under A")


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    _apply(ctx, _rows(ctx))
    return _answer(ctx)


def by_name(ctx):
    other = _other_common(ctx)
    wanted = ctx.handles["tables"]["Common"]
    rows = [dict(row, tablePath=other if row["tablePath"] == wanted else row["tablePath"]) for row in _rows(ctx)]
    _apply(ctx, rows)
    return _answer(ctx)


def variants_too(ctx):
    rows = _rows(ctx)
    tables = ctx.handles["tables"]
    for path in ctx.private.variants(ctx):
        tier = ctx.private.tier_of(ctx.handles["enemies"], path)
        rows.append({"prefabPath": path, "componentIndex": 0, "tablePath": tables[tier]})
    _apply(ctx, rows)
    return _answer(ctx)


def variant_instead_of_base(ctx):
    """One base prefab is skipped and its table written into the variant made from it."""
    rows = _rows(ctx)
    variant = ctx.private.variants(ctx)[0]
    docs = ctx.private.docs(ctx, variant)
    source_guid = docs and next((d.get("m_SourcePrefab", "guid") for d in docs if d.type_name == "PrefabInstance"),
                                None)
    base = next(path for guid, path in _prefab_guids(ctx).items() if guid == source_guid)
    kept = [row for row in rows if row["prefabPath"] != base]
    table = next(row["tablePath"] for row in rows if row["prefabPath"] == base and row["componentIndex"] == 0)
    kept.append({"prefabPath": variant, "componentIndex": 0, "tablePath": table})
    _apply(ctx, kept)
    return _answer(ctx)


def _prefab_guids(ctx):
    out = {}
    for path in ctx.handles["prefabs"]:
        guid = ctx.probe.guid(path)
        if guid:
            out[guid] = path
    return out


def skip_prewired(ctx):
    empty = {(row["prefabPath"], row["componentIndex"]) for row in ctx.private.current(ctx) if not row["guid"]}
    _apply(ctx, [row for row in _rows(ctx) if (row["prefabPath"], row["componentIndex"]) in empty])
    return _answer(ctx)


def both_loots_tier(ctx):
    tables = ctx.handles["tables"]
    rows = [dict(row, tablePath=tables[ctx.private.tier_of(ctx.handles["enemies"], row["prefabPath"])])
            for row in _rows(ctx)]
    _apply(ctx, rows)
    return _answer(ctx)


def outermost_tier(ctx):
    enemies, tables = ctx.handles["enemies"], ctx.handles["tables"]
    rows = []
    for row in _rows(ctx):
        if row["componentIndex"] == 0:
            segments = row["prefabPath"][len(enemies) + 1:].split("/")[:-1]
            tier = next(segment for segment in segments if segment in ctx.private.TIERS)
            row = dict(row, tablePath=tables[tier])
        rows.append(row)
    _apply(ctx, rows)
    return _answer(ctx)


def edit_dropchance(ctx):
    answer = reference(ctx)
    row = next(row for row in _rows(ctx) if row["componentIndex"] == 0)
    ctx.c.write("prefab_set_serialized_property", prefabPath=row["prefabPath"], componentType="Loot",
                propertyPath="dropChance", value="0.1")
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    rows = answer["wired"]
    first = next(row for row in rows if row["componentIndex"] == 0)
    second = next(row for row in rows if row["componentIndex"] == 0 and row["tablePath"] != first["tablePath"])
    first["tablePath"], second["tablePath"] = second["tablePath"], first["tablePath"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayLoot")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("notes.txt"), b"wired\n")
    return answer


def single_calls(ctx):
    for item in _items(_rows(ctx)):
        ctx.c.write("prefab_set_serialized_property", **item)
    return _answer(ctx)


def legacy_mixed(ctx):
    """prefab_set_property for the first Loot of each prefab, the indexed API for the further ones."""
    for row in _rows(ctx):
        if row["componentIndex"] == 0:
            ctx.c.write("prefab_set_property", prefabPath=row["prefabPath"], componentType="Loot",
                        propertyName="table", assetReferencePath=row["tablePath"])
        else:
            ctx.c.write("prefab_set_serialized_property", **_items([row])[0])
    return _answer(ctx)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("by_name", by_name, {"B1", "B5"}, "the other LootTable_Common asset is referenced for the Common tier"),
    Negative("variants_too", variants_too, {"B3"}, "the variant prefabs are written as well"),
    Negative("variant_instead_of_base", variant_instead_of_base, {"B1", "B3"},
             "a variant is written instead of its base"),
    Negative("skip_prewired", skip_prewired, {"B1"}, "wrong existing references are left in place"),
    Negative("both_loots_tier", both_loots_tier, {"B2"}, "the second Loot gets the tier table"),
    Negative("outermost_tier", outermost_tier, {"B1"}, "the first tier folder from the top instead of the nearest"),
    Negative("edit_dropchance", edit_dropchance, {"B4"}),
    Negative("wrong_report", wrong_report, {"B5"}),
    Negative("outside_root", outside_root, {"B6"}),
    Negative("direct_file", direct_file, {"B4", "B7"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("single_calls", single_calls), ("legacy_mixed", legacy_mixed), ("save_run_scene", save_run_scene)]

# Producer:Betsy
