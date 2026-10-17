"""S08 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import re

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

PATTERN = r"^Enemy_[0-9]{2}$"


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _tree(ctx):
    return V.structure(ctx.probe, [ctx.handles["candidates"]])


def _answer(ctx, refs):
    paths = {str(node.get("id")): node.get("path") for node in _tree(ctx)}
    rows = [{"path": paths.get(str(V.object_id(ref))) or (ctx.probe.go(ref) or {}).get("path"),
             "instanceId": _number(V.object_id(ref))} for ref in refs]
    return {"total": len(rows), "matches": rows, "declaredComplete": True}


def _named(ctx, name):
    return [node.get("id") for node in _tree(ctx)[1:] if node.get("name") == name]


def _under_root(ctx, obj):
    return (obj.get("path") or "").startswith(ctx.handles["candidatesPath"] + "/")


def _query(ctx):
    res = ctx.probe.query({"componentType": "BoxCollider", "includeInactive": True, "namePattern": PATTERN,
                           "caseSensitive": True})
    return [V.object_id(obj) for obj in res.get("objects") or []
            if re.match(PATTERN, obj.get("name") or "") and _under_root(ctx, obj)]


def reference(ctx):
    return _answer(ctx, _query(ctx))


def include_lowercase(ctx):
    return _answer(ctx, _query(ctx) + _named(ctx, "enemy_04"))


def skip_inactive(ctx):
    hidden = {str(node.get("id")) for node in _tree(ctx) if node.get("active") is False}
    return _answer(ctx, [ref for ref in _query(ctx) if str(ref) not in hidden])


def swap_ids(ctx):
    answer = reference(ctx)
    first, second = (_number(ref) for ref in _named(ctx, "Enemy_03"))
    for row in answer["matches"]:
        row["instanceId"] = {first: second, second: first}.get(row["instanceId"], row["instanceId"])
    return answer


def wrong_total(ctx):
    answer = reference(ctx)
    answer["total"] = len(answer["matches"]) + 1
    return answer


def writes(ctx):
    answer = reference(ctx)
    reserve = ctx.probe.go(ctx.handles["reserve"]) or {}
    enemy = next(child for child in reserve.get("children") or [] if child.get("name") == "Enemy_09")
    ctx.c.write("gameobject_rename", newName="Enemy_09_checked", **V.address(enemy))
    return answer


def include_child_collider(ctx):
    return _answer(ctx, _query(ctx) + _named(ctx, "Enemy_06"))


def rename_listed(ctx):
    answer = reference(ctx)
    ref = _named(ctx, "Enemy_01")[0]
    ctx.c.write("gameobject_rename", newName="Enemy_01_seen", **V.address(ref))
    return answer


def find_regex(ctx):
    res = ctx.c.read("gameobject_find", name=PATTERN, useRegex=True, component="BoxCollider", limit=500)
    refs = [V.object_id(obj) for obj in res.get("objects") or []
            if re.match(PATTERN, obj.get("name") or "") and _under_root(ctx, obj)]
    inactive = ctx.probe.query({"componentType": "BoxCollider", "active": False, "namePattern": PATTERN,
                                "caseSensitive": True})
    seen = {str(ref) for ref in refs}
    refs += [V.object_id(obj) for obj in inactive.get("objects") or []
             if re.match(PATTERN, obj.get("name") or "") and _under_root(ctx, obj)
             and str(V.object_id(obj)) not in seen]
    return _answer(ctx, refs)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("include_lowercase", include_lowercase, {"E2", "E4"}, "enemy_04 matches only ignoring case"),
    Negative("skip_inactive", skip_inactive, {"E1", "E4"}),
    Negative("swap_ids", swap_ids, {"E3"}, "the two Enemy_03 swap ids"),
    Negative("wrong_total", wrong_total, {"E4"}),
    Negative("writes", writes, {"E5", "E6"}, "an object outside the search root is renamed"),
    Negative("include_child_collider", include_child_collider, {"E2", "E4"}, "Enemy_06 only has a child collider"),
    Negative("rename_listed", rename_listed, {"E5", "E6"}, "a listed object is renamed after the inventory"),
]

ALTERNATIVES = [("find_regex", find_regex), ("save_run_scene", save_run_scene)]

# Producer:Betsy
