"""D01 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
from usk_eval import verifylib as V
from usk_eval.task_api import Negative

PREFIX = "Slot_"


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _tree(ctx):
    return V.structure(ctx.probe, [ctx.handles["inventory"]])


def _answer(ctx, refs):
    paths = {str(node.get("id")): node.get("path") for node in _tree(ctx)}
    rows = [{"path": paths.get(str(V.object_id(ref))) or (ctx.probe.go(ref) or {}).get("path"),
             "instanceId": _number(V.object_id(ref))} for ref in refs]
    return {"total": len(rows), "items": rows, "declaredComplete": True}


def _under_root(ctx, obj):
    return (obj.get("path") or "").startswith(ctx.handles["inventoryPath"] + "/")


def _query(ctx, **extra):
    query = {"includeInactive": True, "namePattern": "^" + PREFIX, "caseSensitive": True, "limit": 500}
    query.update(extra)
    res = ctx.probe.query(query, sample_limit=500)
    return [obj for obj in res.get("objects") or [] if (obj.get("name") or "").startswith(PREFIX)
            and _under_root(ctx, obj)]


def _vault_slot(ctx):
    vault = ctx.probe.go(ctx.handles["vault"]) or {}
    return V.object_id(next(child for child in vault.get("children") or [] if child.get("name") == "Slot_Q"))


def reference(ctx):
    return _answer(ctx, [V.object_id(obj) for obj in _query(ctx)])


def first_50(ctx):
    res = ctx.c.read("gameobject_find", name="^" + PREFIX, useRegex=True)
    return _answer(ctx, [V.object_id(obj) for obj in res.get("objects") or [] if _under_root(ctx, obj)])


def include_lower(ctx):
    lower = [node.get("id") for node in _tree(ctx)[1:] if node.get("name") == "slot_lower"]
    return _answer(ctx, [V.object_id(obj) for obj in _query(ctx)] + lower)


def skip_inactive(ctx):
    return _answer(ctx, [V.object_id(obj) for obj in _query(ctx, includeInactive=False)])


def include_vault(ctx):
    return _answer(ctx, [V.object_id(obj) for obj in _query(ctx)] + [_vault_slot(ctx)])


def dedupe_names(ctx):
    seen, refs = set(), []
    for obj in _query(ctx):
        if obj.get("name") not in seen:
            seen.add(obj.get("name"))
            refs.append(V.object_id(obj))
    return _answer(ctx, refs)


def swap_ids(ctx):
    answer = reference(ctx)
    first, second = answer["items"][0], answer["items"][1]
    first["instanceId"], second["instanceId"] = second["instanceId"], first["instanceId"]
    return answer


def writes(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Slot_Q_seen", **V.address(_vault_slot(ctx)))
    return answer


def rename_listed(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Seen_Slot", **V.address(answer["items"][0]["instanceId"]))
    return answer


def find_paged(ctx):
    res = ctx.c.read("gameobject_find", name="^" + PREFIX, useRegex=True, limit=500)
    refs = [V.object_id(obj) for obj in res.get("objects") or [] if _under_root(ctx, obj)]
    seen = {str(ref) for ref in refs}
    refs += [V.object_id(obj) for obj in _query(ctx, active=False) if str(V.object_id(obj)) not in seen]
    return _answer(ctx, refs)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("first_50", first_50, {"F1", "F4"}, "gameobject_find's default limit cuts the list"),
    Negative("include_lower", include_lower, {"F2", "F4"}, "slot_lower matches only ignoring case"),
    Negative("skip_inactive", skip_inactive, {"F1", "F4"}),
    Negative("include_vault", include_vault, {"F2", "F4"}, "R/Vault/Slot_Q is outside the root"),
    Negative("dedupe_names", dedupe_names, {"F1", "F4"}, "two names occur twice"),
    Negative("swap_ids", swap_ids, {"F3"}),
    Negative("writes", writes, {"F5", "F6"}, "an object outside the search root is renamed"),
    Negative("rename_listed", rename_listed, {"F5", "F6"}, "a listed object is renamed after the inventory"),
]

ALTERNATIVES = [("find_paged", find_paged), ("save_run_scene", save_run_scene)]

# Producer:Betsy
