"""S08 private verifier: regex + component inventory (read-only).

What the task probes: the pattern is case-sensitive and anchored (enemy_04, Enemy_7, Enemy_123 and Enemy_05_old do not
match); one match is inactive and one has a disabled BoxCollider; Enemy_03 exists twice in different subgroups;
Enemy_06 only has a BoxCollider on its child Hitbox; Enemy_08 has a MeshCollider; R/Reserve/Enemy_09 lies outside the
search root; batch_query_gameobjects matches names ignoring case unless caseSensitive is set. Nothing may change.

The run state holds only digests of the two subtrees. The inventory is computed here from the end state; when the
agent renamed objects, their first previous names are restored from the journal before matching.
"""
import re

from usk_eval import verifylib as V

PATTERN = r"^Enemy_[0-9]{2}$"

ASSERTIONS = [
    ("E1", 2.5, False, ["R2", "R3"], "every matching object is listed (fraction of the true set)"),
    ("E2", 2.5, False, ["R2", "R3"], "no extra or duplicate entries"),
    ("E3", 1.5, False, ["R3"], "each listed path belongs to the listed id"),
    ("E4", 1.0, False, ["R4"], "the total equals the true count"),
    ("E5", 1.5, True, ["R1", "G1", "G2"], "both subtrees still match their C0 digests; no change outside R/A, no "
                                          "other scene saved or opened"),
    ("E6", 1.0, False, ["R1", "G4"], "the journal records no write (saving the run scene aside)"),
]

HINTS = {
    "E1": {"zh-CN": "包括未激活的对象和 BoxCollider 被禁用的对象；batch_query_gameobjects 默认不含未激活对象。",
           "en": "Include inactive objects and objects whose BoxCollider is disabled; batch_query_gameobjects skips "
                 "inactive objects by default."},
    "E2": {"zh-CN": "匹配区分大小写且必须完整匹配；BoxCollider 必须在对象自身上；batch_query_gameobjects 的 namePattern "
                    "默认不区分大小写，要加 caseSensitive:true。",
           "en": "The match is case-sensitive and whole-name; the BoxCollider must be on the object itself; "
                 "batch_query_gameobjects namePattern ignores case unless caseSensitive:true."},
    "E3": {"zh-CN": "两个 Enemy_03 位于不同子组，路径与 id 要一一对应。",
           "en": "There are two Enemy_03 in different subgroups; pair each path with its own id."},
    "E4": {"zh-CN": "总数要等于列出的匹配对象个数。", "en": "The total must equal the number of matching objects."},
    "E5": {"zh-CN": "只读任务不能修改任何对象。", "en": "A read-only task must not change anything."},
    "E6": {"zh-CN": "只读任务不应产生任何写入记录。", "en": "A read-only task must not leave any write in the journal."},
}


def inventory(tree):
    """Ids (text) of the descendants in `tree` (a verifylib.structure list) that satisfy R2."""
    rx = re.compile(PATTERN)
    return [str(node.get("id")) for node in tree[1:]
            if rx.fullmatch(node.get("name") or "")
            and any(comp and comp[0] == "BoxCollider" for comp in node.get("components") or [])]


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
    rows = V.answer(ctx, "matches")
    out = []
    for row in rows if isinstance(rows, list) else []:
        if isinstance(row, dict):
            ident = V.object_id(row.get("instanceId"))
            out.append((None if ident is None else str(ident), V.norm_path(row.get("path"))))
    return out


def verify(ctx, card):
    p, h, digests = ctx.probe, ctx.handles, ctx.c0_digests
    tree = V.structure(p, [h["candidates"]])
    intact = V.canonical_digest(tree) == digests.get("candidates")
    reserve_intact = V.canonical_digest(V.structure(p, [h["reserve"]])) == digests.get("reserve")
    basis, restored = (tree, False) if intact else restore_names(tree, ctx.records)
    wanted = inventory(basis)
    paths = {str(node.get("id")): node.get("path") for node in basis}
    rows = _rows(ctx)
    keys = [key for key, _path in rows]
    found = set(wanted).intersection(keys)
    card.fraction("E1", len(found) / float(len(wanted)) if wanted else 0.0, expected=len(wanted), actual=len(found),
                  missing=sorted(set(wanted) - found), restoredNames=restored)
    extras = [key for key in keys if key not in wanted]
    duplicates = len(keys) - len(set(keys))
    card.check("E2", not extras and not duplicates, actual={"extras": extras[:20], "duplicates": duplicates})
    wrong = []
    for key, path in rows:
        actual = paths.get(key) if key is not None else None
        if actual is None and key is not None:
            actual = (p.go(key) or {}).get("path")
        if not actual or path != actual:
            wrong.append({"id": key, "path": path, "actual": actual})
    card.check("E3", bool(rows) and not wrong, actual=wrong[:10])
    card.check("E4", V.as_int(V.answer(ctx, "total")) == len(wanted), expected=len(wanted),
               actual=V.answer(ctx, "total"))
    bad = ctx.violations()
    card.check("E5", intact and reserve_intact and not bad,
               actual={"candidatesIntact": intact, "reserveIntact": reserve_intact, "violations": bad[:10]})
    writes = ctx.write_records()
    card.check("E6", not writes, actual=writes[:10])

# Producer:Betsy
