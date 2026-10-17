"""OC3 private verifier: Loot.table references on the enemy prefabs, judged from the asset files on disk.

What the task probes: the tier comes from the nearest Common/Rare/Elite folder (one tier folder sits inside another);
two LootTable_Common assets exist and only the one in the loot folder counts (references are compared by GUID, not by
name); one prefab carries two Loot components, addressed by componentIndex; prefab variants must stay untouched and
inherit from their base; some tables are already referenced, right or wrong; a batch write cannot exceed 50 items.
"""
import hashlib
import os

from usk_eval import verifylib as V

TIERS = ("Common", "Rare", "Elite")
MASKED = {"MonoBehaviour": ("table",), "Transform": ("m_RootOrder", "m_LocalEulerAnglesHint")}
FILE_KINDS = ("imported", "deleted", "moved")
SCRIPT_KEYS = ("lootScript", "tableScript")

ASSERTIONS = [
    ("B1", 3.5, False, ["R1", "R2"], "the first Loot component of every base prefab references the table of its "
                                     "tier, by GUID (fraction of the base prefabs)"),
    ("B2", 1.0, False, ["R3"], "every further Loot component references the Bonus table"),
    ("B3", 1.5, True, ["R4"], "no variant prefab file changed"),
    ("B4", 1.5, True, ["R5"], "nothing but Loot.table changed: other prefab content, the prefabs without Loot, the "
                              "LootTable assets, the two scripts and the file set are as before"),
    ("B5", 1.0, False, ["R6", "G8"], "the answer lists the Loot components with the tables they really reference, "
                                     "the variants and the GUID of the loot folder's Common table"),
    ("B6", 0.75, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("B7", 0.75, False, ["G4"], "no file under A was written around the skills and no script was touched"),
]

HINTS = {
    "B1": {"zh-CN": "等级取最近的 Common/Rare/Elite 父文件夹；table 必须引用题目给出路径上的那个资产（按 GUID 判定），同名的其他资产不算；已引用错误资产的也要改正。",
           "en": "The tier is the nearest Common/Rare/Elite parent folder; table must reference the asset at the "
                 "given path (judged by GUID), not a same-named asset elsewhere; wrong existing references must be "
                 "fixed too."},
    "B2": {"zh-CN": "带两个 Loot 组件的预制体：componentIndex 0 按等级，其余引用 Bonus 表。",
           "en": "On the prefab with two Loot components, componentIndex 0 follows the tier and the rest reference "
                 "the Bonus table."},
    "B3": {"zh-CN": "变体预制体不要写入任何值，它们从基础预制体继承 table。",
           "en": "Write nothing into variant prefabs; they inherit table from their base prefab."},
    "B4": {"zh-CN": "只改 Loot 组件的 table 字段；dropChance、没有 Loot 的预制体、各个 LootTable 资产、两个脚本和文件集合都要保持不变。",
           "en": "Change only the table field of Loot components; dropChance, the prefabs without Loot, the LootTable "
                 "assets, the two scripts and the set of files stay as they were."},
    "B5": {"zh-CN": "答案要列出每个基础预制体 Loot 组件实际引用的资产路径、所有变体路径和正确的 Common 表 GUID，并写明 declaredComplete。",
           "en": "List the table each base prefab's Loot component really references, every variant path and the "
                 "right Common table GUID, and declaredComplete."},
    "B6": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "B7": {"zh-CN": "不要绕过技能直接在 A 下写文件，也不要修改脚本。",
           "en": "Do not write files under A directly and do not touch the scripts."},
}


def tier_of(enemies, prefab_path):
    """Nearest Common/Rare/Elite folder above the prefab, or None."""
    if not isinstance(prefab_path, str) or not prefab_path.startswith(enemies + "/"):
        return None
    for segment in reversed(prefab_path[len(enemies) + 1:].split("/")[:-1]):
        if segment in TIERS:
            return segment
    return None


def _int(text):
    try:
        return int(str(text))
    except (TypeError, ValueError):
        return None


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def script_hashes(ctx):
    """{script path: sha256 of its text as script_read returns it}, None for a script that cannot be read."""
    out = {}
    for key in SCRIPT_KEYS:
        path = ctx.handles[key]
        content = (_read(ctx, "script_read", scriptPath=path) or {}).get("content")
        out[path] = hashlib.sha256(content.encode("utf-8")).hexdigest() if isinstance(content, str) else None
    return out


def _full(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def docs(ctx, asset_path):
    try:
        return V.uyaml.load(_full(ctx, asset_path))
    except OSError:
        return []


def yaml_digest(docs_list):
    return V.canonical_digest([[d.class_id, d.file_id, d.stripped, d.type_name, d.body] for d in docs_list])


def masked_digest(docs_list):
    """Same masking as the public module: Loot.table and Unity's resave-volatile Transform keys left out."""
    rows = []
    for d in docs_list:
        body = dict(d.body) if isinstance(d.body, dict) else d.body
        for key in MASKED.get(d.type_name, ()):
            if isinstance(body, dict):
                body.pop(key, None)
        rows.append([d.class_id, d.file_id, d.stripped, d.type_name, body])
    return V.canonical_digest(rows)


def is_variant(docs_list):
    return bool(V.uyaml.by_type(docs_list, "PrefabInstance"))


def loot_slots(docs_list, loot_guid):
    """Loot components of a prefab file in component order per GameObject: {object, index, guid, fileId}, where guid
    and fileId describe what `table` references (None for an empty reference)."""
    by_id = V.uyaml.index(docs_list)
    out = []
    for go in V.uyaml.by_type(docs_list, "GameObject"):
        index = 0
        for entry in go.get("m_Component") or []:
            ref = entry.get("component") if isinstance(entry, dict) else None
            doc = by_id.get(_int(ref.get("fileID"))) if isinstance(ref, dict) else None
            if doc is None or doc.type_name != "MonoBehaviour" or doc.get("m_Script", "guid") != loot_guid:
                continue
            table = doc.get("table")
            file_id = _int(table.get("fileID")) if isinstance(table, dict) else None
            out.append({"object": go.get("m_Name"), "index": index,
                        "guid": table.get("guid") if isinstance(table, dict) and file_id else None,
                        "fileId": file_id})
            index += 1
    return out


def loot_guid(ctx):
    return ctx.probe.guid(ctx.handles["lootScript"])


def _meta_guid(ctx, asset_path):
    try:
        return V.uyaml.meta_guid(_full(ctx, asset_path))
    except OSError:
        return None


def table_guids(ctx):
    """{tier: GUID} of the loot folder's tables as the .meta files say now (None when a table is gone)."""
    return {tier: _meta_guid(ctx, path) for tier, path in ctx.handles["tables"].items()}


def guid_paths(ctx):
    """{GUID: asset path} of every .asset under A."""
    out = {}
    for rel in V.asset_listing(ctx.project_path, ctx.A):
        if rel.endswith(".asset"):
            path = ctx.A + "/" + rel
            guid = _meta_guid(ctx, path)
            if guid:
                out[guid] = path
    return out


def current(ctx, guid=None):
    """State of every Loot component of the non-variant prefabs, from the files: {prefabPath, componentIndex, tier,
    guid, tablePath}; tablePath is None for an empty or unknown reference."""
    guid = guid or loot_guid(ctx)
    paths = guid_paths(ctx)
    out = []
    for path in ctx.handles["prefabs"]:
        docs_list = docs(ctx, path)
        if not docs_list or is_variant(docs_list):
            continue
        for slot in loot_slots(docs_list, guid):
            out.append({"prefabPath": path, "componentIndex": slot["index"], "tier": tier_of(ctx.handles["enemies"], path),
                        "guid": slot["guid"], "tablePath": paths.get(slot["guid"]) if slot["guid"] else None})
    return out


def variants(ctx):
    return sorted(path for path in ctx.handles["prefabs"] if is_variant(docs(ctx, path)))


def plan(ctx):
    """Per Loot component of the non-variant prefabs, the table it must reference: {prefabPath, componentIndex,
    tablePath}."""
    tables = ctx.handles["tables"]
    out = []
    for row in current(ctx):
        table = tables.get(row["tier"]) if row["componentIndex"] == 0 else tables.get("Bonus")
        out.append({"prefabPath": row["prefabPath"], "componentIndex": row["componentIndex"], "tablePath": table})
    return out


def _answer_ok(ctx, rows_now, variant_paths, common_guid):
    if not isinstance(ctx.answer, dict):
        return False
    rows = V.answer(ctx, "wired")
    listed = V.answer(ctx, "variants")
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows) or not isinstance(listed, list):
        return False
    reported = {}
    for row in rows:
        key = (V.norm_path(row.get("prefabPath")), V.as_int(row.get("componentIndex")))
        reported[key] = V.norm_path(row.get("tablePath"))
    actual = {(row["prefabPath"], row["componentIndex"]): row["tablePath"] or "" for row in rows_now}
    return (len(rows) == len(reported) == len(actual) and set(reported) == set(actual) and
            all(reported[key] == actual[key] for key in actual) and
            sorted(V.norm_path(path) for path in listed) == variant_paths and bool(common_guid) and
            V.answer(ctx, "commonTableGuid") == common_guid and
            isinstance(V.answer(ctx, "declaredComplete"), bool))


def _script_records(ctx):
    out = []
    for rec in ctx.records:
        if rec.get("isPlaying") or rec.get("kind") not in FILE_KINDS:
            continue
        for path in (rec.get("assetPath"), rec.get("from")):
            text = str(path or "")
            if text.endswith(".meta"):
                text = text[:-5]
            if text.endswith(".cs") and V.under(text, ctx.A):
                out.append(rec)
                break
    return out


def verify(ctx, card):
    h, c0 = ctx.handles, ctx.c0
    guid = c0.get("lootGuid")
    counts = c0.get("lootCounts") or {}
    full, masked = c0.get("full") or {}, c0.get("masked") or {}
    bases = sorted(path for path, n in counts.items() if (V.as_int(n) or 0) >= 1)
    others = sorted(path for path, n in counts.items() if (V.as_int(n) or 0) == 0)
    guids = table_guids(ctx)
    loaded = {path: docs(ctx, path) for path in counts}
    slots = {path: loot_slots(loaded[path], guid) for path in bases}

    right, wrong = 0, []
    for path in bases:
        want = guids.get(tier_of(h["enemies"], path))
        firsts = [slot for slot in slots[path] if slot["index"] == 0]
        if firsts and want and all(slot["guid"] == want for slot in firsts):
            right += 1
        else:
            wrong.append({"prefab": path, "tier": tier_of(h["enemies"], path),
                          "got": [slot["guid"] for slot in firsts]})
    card.fraction("B1", right / float(len(bases)) if bases else 0.0, expected=len(bases), actual=right,
                  wrong=wrong[:15])

    extras = [(path, slot) for path in bases for slot in slots[path] if slot["index"] >= 1]
    want_extra = sum(max(0, (V.as_int(n) or 0) - 1) for n in counts.values())
    bonus = guids.get("Bonus")
    card.check("B2", len(extras) == want_extra and bool(bonus) and all(slot["guid"] == bonus for _p, slot in extras),
               expected={"count": want_extra, "guid": bonus},
               actual=[{"prefab": path, "index": slot["index"], "guid": slot["guid"]} for path, slot in extras])

    variant_paths, variant_changed = [], []
    for path in others:
        if is_variant(loaded[path]):
            variant_paths.append(path)
            if yaml_digest(loaded[path]) != full.get(path):
                variant_changed.append(path)
    card.check("B3", not variant_changed, actual={"variants": variant_paths, "changed": variant_changed})

    changed = [path for path in bases if masked_digest(loaded[path]) != masked.get(path)]
    changed += [path for path in others if path not in variant_paths and yaml_digest(loaded[path]) != full.get(path)]
    changed += [path for path in sorted(full) if path.endswith(".asset") and yaml_digest(docs(ctx, path)) != full[path]]
    assets = V.asset_listing(ctx.project_path, ctx.A)
    before_assets = c0.get("assets") or []
    same_guids = guids == (c0.get("tableGuids") or {})
    hashes = script_hashes(ctx)
    same_scripts = all(hashes.values()) and hashes == (c0.get("scriptHashes") or {})
    card.check("B4", not changed and same_guids and same_scripts and assets == before_assets,
               actual={"changed": changed[:15], "tableGuidsSame": same_guids, "scriptsSame": same_scripts,
                       "added": sorted(set(assets) - set(before_assets))[:10],
                       "removed": sorted(set(before_assets) - set(assets))[:10]})

    rows_now = current(ctx, guid)
    card.check("B5", _answer_ok(ctx, rows_now, sorted(variant_paths), guids.get("Common")), actual=ctx.answer)
    bad = ctx.violations()
    card.check("B6", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    scripts = _script_records(ctx)
    card.check("B7", not direct and not scripts, actual={"directWrites": direct[:10], "scriptRecords": scripts[:10]})

# Producer:Betsy
