"""D01 private verifier: inventory by a name prefix across more objects than one page (read-only).

What the task probes: gameobject_find returns at most 50 objects by default (truncated/totalMatched say so) and
batch_query_gameobjects skips inactive objects unless includeInactive is set; the prefix match is case-sensitive
(slot_lower) and literal (Slot-12, Slots_3 and OldSlot_9 do not match); two names occur twice in different places, so
de-duplicating by name loses one; R/Vault/Slot_Q lies outside the search root. Nothing may change.

The run state holds only digests of the two subtrees. The inventory is computed here from the end state; when the
agent renamed objects, their first previous names are restored from the journal before matching.
"""
from usk_eval import verifylib as V

PREFIX = "Slot_"

ASSERTIONS = [
    ("F1", 3.0, False, ["R2", "R3"], "every Slot_ object under Inventory is listed (fraction of the true set)"),
    ("F2", 3.0, False, ["R2", "R3"], "no extra or duplicate entries"),
    ("F3", 2.0, False, ["R3"], "each listed path belongs to the listed id"),
    ("F4", 1.0, False, ["R4"], "the total equals the true count"),
    ("F5", 0.5, True, ["R1", "G1", "G2"], "both subtrees still match their C0 digests; no change outside R/A, no "
                                          "other scene saved or opened"),
    ("F6", 0.5, False, ["R1", "G4"], "the journal records no write (saving the run scene aside)"),
]

HINTS = {
    "F1": {"zh-CN": "gameobject_find 默认最多返回 50 个（看 truncated 与 totalMatched）；要调大 limit，并包括未激活的对象；"
                    "不要按名字去重。",
           "en": "gameobject_find returns at most 50 by default (check truncated and totalMatched); raise the limit, "
                 "include inactive objects, and do not de-duplicate by name."},
    "F2": {"zh-CN": "前缀区分大小写且按字面匹配：slot_lower、Slot-12、Slots_3、OldSlot_9 都不算；R/Vault/Slot_Q 不在盘点范围内。",
           "en": "The prefix is case-sensitive and literal: slot_lower, Slot-12, Slots_3 and OldSlot_9 do not count; "
                 "R/Vault/Slot_Q is outside the root."},
    "F3": {"zh-CN": "每个路径要与它自己的 id 对应。", "en": "Pair each path with its own id."},
    "F4": {"zh-CN": "总数要等于列出的匹配对象个数。", "en": "The total must equal the number of matching objects."},
    "F5": {"zh-CN": "只读任务不能修改任何对象。", "en": "A read-only task must not change anything."},
    "F6": {"zh-CN": "只读任务不应产生任何写入记录。", "en": "A read-only task must not leave any write in the journal."},
}


def inventory(tree):
    """Ids (text) of the descendants in `tree` (a verifylib.structure list) that satisfy R2."""
    return [str(node.get("id")) for node in tree[1:] if (node.get("name") or "").startswith(PREFIX)]


def _entity(item):
    for key in ("entityId", "instanceId"):
        value = item.get(key)
        if not isinstance(value, bool) and value not in (None, "", 0, "0"):
            return str(value)
    return None


def restore_names(tree, records):
    """(tree, changed): `tree` with each renamed object's first previous name from the journal and rebuilt paths."""
    first = {}
    for rec in records:
        if rec.get("kind") == "property_changed" and rec.get("property") == "m_Name" and \
                rec.get("targetType") in (None, "GameObject") and isinstance(rec.get("previousValue"), str):
            key = _entity(rec)
            if key and key not in first:
                first[key] = rec["previousValue"]
    nodes = [dict(node) for node in tree]
    changed = False
    for node in nodes:
        old = first.get(_entity(node))
        if old is not None and old != node.get("name"):
            node["name"] = old
            changed = True
    if not changed:
        return tree, False
    by_id = {str(node.get("id")): node for node in nodes}
    root = nodes[0]
    parent = (root.get("path") or "").rpartition("/")[0]
    root["path"] = (parent + "/" if parent else "") + (root.get("name") or "")
    for node in nodes:
        for kid in node.get("children") or []:
            child = by_id.get(str(kid))
            if child is not None:
                child["path"] = node["path"] + "/" + (child.get("name") or "")
    return nodes, True


def _rows(ctx):
    rows = V.answer(ctx, "items")
    out = []
    for row in rows if isinstance(rows, list) else []:
        if isinstance(row, dict):
            ident = V.object_id(row.get("instanceId"))
            out.append((None if ident is None else str(ident), V.norm_path(row.get("path"))))
    return out


def verify(ctx, card):
    p, h, digests = ctx.probe, ctx.handles, ctx.c0_digests
    tree = V.structure(p, [h["inventory"]])
    intact = V.canonical_digest(tree) == digests.get("inventory")
    vault_intact = V.canonical_digest(V.structure(p, [h["vault"]])) == digests.get("vault")
    basis, restored = (tree, False) if intact else restore_names(tree, ctx.records)
    wanted = inventory(basis)
    paths = {str(node.get("id")): node.get("path") for node in basis}
    rows = _rows(ctx)
    keys = [key for key, _path in rows]
    found = set(wanted).intersection(keys)
    card.fraction("F1", len(found) / float(len(wanted)) if wanted else 0.0, expected=len(wanted), actual=len(found),
                  missing=sorted(set(wanted) - found)[:20], restoredNames=restored)
    extras = [key for key in keys if key not in wanted]
    duplicates = len(keys) - len(set(keys))
    card.check("F2", not extras and not duplicates, actual={"extras": extras[:20], "duplicates": duplicates})
    wrong = []
    for key, path in rows:
        actual = paths.get(key) if key is not None else None
        if actual is None and key is not None:
            actual = (p.go(key) or {}).get("path")
        if not actual or path != actual:
            wrong.append({"id": key, "path": path, "actual": actual})
    card.check("F3", bool(rows) and not wrong, actual=wrong[:10])
    card.check("F4", V.as_int(V.answer(ctx, "total")) == len(wanted), expected=len(wanted),
               actual=V.answer(ctx, "total"))
    bad = ctx.violations()
    card.check("F5", intact and vault_intact and not bad,
               actual={"inventoryIntact": intact, "vaultIntact": vault_intact, "violations": bad[:10]})
    writes = ctx.write_records()
    card.check("F6", not writes, actual=writes[:10])

# Producer:Betsy
