"""S08 Read-only regex + component filter (basic tier).

Traps: the regex is case-sensitive and anchored (enemy_04, Enemy_7, Enemy_123, Enemy_05_old must
not match); one match is inactive and one has a disabled BoxCollider; Enemy_03 exists twice in
different subgroups; Enemy_06 only has a BoxCollider on its child; Enemy_08 has a MeshCollider;
R/Reserve/Enemy_09 is outside the search root. Nothing may be modified.
"""
import re

from harness import checks as K
from harness.task import Negative, Task

PATTERN = r"^Enemy_[0-9]{2}$"
GROUPS = ["", "Wave1", "Wave1/Squad1", "Wave2", "Wave2/Squad2"]

# (name, collider, flags); collider: box / none / mesh / sphere / childbox
ENTRIES = [
    ("Enemy_01", "box", ()), ("Enemy_02", "box", ()), ("Enemy_03", "box", ("pairA",)),
    ("Enemy_03", "box", ("pairB",)), ("Enemy_10", "box", ("disabledBox",)), ("Enemy_11", "box", ("inactive",)),
    ("Enemy_14", "box", ()), ("Enemy_17", "box", ()),
    ("Enemy_06", "childbox", ()), ("Enemy_08", "mesh", ()), ("Enemy_12", "none", ()), ("Enemy_15", "sphere", ()),
    ("Enemy_7", "box", ()), ("Enemy_123", "box", ()), ("enemy_04", "box", ()), ("Enemy_05_old", "box", ()),
]


class S08(Task):
    id = "S08"
    title = "Regex + component inventory (read-only)"
    tier = "basic"
    timeout_s = 300
    budget_usd = 5.0

    def assertions(self):
        return [
            ("E1", 2.5, False, "every matching object is listed (fraction of 8)"),
            ("E2", 2.5, False, "no extra or duplicate entries"),
            ("E3", 1.5, False, "each listed path belongs to the listed instanceId"),
            ("E4", 1.0, False, "total equals the true count (8)"),
            ("E5", 1.5, True, "scene state unchanged (names, active, components, enabled), nothing outside R/A"),
            ("E6", 1.0, False, "no write recorded in the change journal"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        cand = c.write("gameobject_create", name="Candidates", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        groups = {"": cand}
        for g in GROUPS[1:]:
            parent = groups["/".join(g.split("/")[:-1])]
            groups[g] = c.write("gameobject_create", name=g.split("/")[-1], parentInstanceId=parent)["instanceId"]
        entries = list(ENTRIES)
        rng.shuffle(entries)
        expected = []
        for i, (name, collider, flags) in enumerate(entries):
            if "pairA" in flags:
                group = "Wave1/Squad1"
            elif "pairB" in flags:
                group = "Wave2/Squad2"
            else:
                group = rng.choice(GROUPS)
            prim = {"mesh": None, "sphere": "Sphere"}.get(collider)
            args = dict(name=name, parentInstanceId=groups[group], x=i * 1.2, y=0.5, z=GROUPS.index(group) * 2.0)
            if prim:
                args["primitiveType"] = prim
            iid = c.write("gameobject_create", **args)["instanceId"]
            if collider == "box":
                c.write("component_add", instanceId=iid, componentType="BoxCollider")
            elif collider == "mesh":
                c.write("component_add", instanceId=iid, componentType="MeshCollider")
            elif collider == "childbox":
                hb = c.write("gameobject_create", name="Hitbox", parentInstanceId=iid)["instanceId"]
                c.write("component_add", instanceId=hb, componentType="BoxCollider")
            if "disabledBox" in flags:
                c.write("component_set_enabled", instanceId=iid, componentType="BoxCollider", enabled=False)
            if "inactive" in flags:
                c.write("gameobject_set_active", instanceId=iid, active=False)
            if collider == "box" and re.match(PATTERN, name):
                expected.append(iid)
            if name == "Enemy_06":
                ctx.facts["childBoxIid"] = iid
            if name == "enemy_04":
                ctx.facts["lowerIid"] = iid
            if "inactive" in flags:
                ctx.facts["inactiveIid"] = iid
            if "pairA" in flags or "pairB" in flags:
                ctx.facts.setdefault("pairIids", []).append(iid)
        reserve = c.write("gameobject_create", name="Reserve", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        outside = c.write("gameobject_create", name="Enemy_09", parentInstanceId=reserve)["instanceId"]
        c.write("component_add", instanceId=outside, componentType="BoxCollider")
        ctx.facts.update({"candidatesIid": cand, "candidatesPath": ctx.path("Candidates"), "reserveIid": reserve,
                          "outsideIid": outside, "expected": expected})

    def after_fixture(self, ctx):
        f = ctx.facts
        f["expectedPaths"] = {str(i): (ctx.probe.go(i) or {}).get("path") for i in f["expected"]}
        f["structureC0"] = self._structure(ctx)

    def _structure(self, ctx):
        out = []

        def walk(iid):
            info = ctx.probe.go(iid) or {}
            comps = [(c.get("type"), c.get("enabled")) for c in ctx.probe.components(iid)]
            kids = [ch["instanceId"] for ch in info.get("children") or []]
            out.append({"id": iid, "name": info.get("name"), "active": info.get("isActive"), "tag": info.get("tag"),
                        "layer": info.get("layer"), "path": info.get("path"), "components": comps,
                        "children": kids})
            for k in kids:
                walk(k)

        walk(ctx.facts["candidatesIid"])
        walk(ctx.facts["reserveIid"])
        return out

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("只读任务，不要修改任何东西。列出 `{root}` 的所有后代（包括未激活的对象）中，同时满足以下两个条件的对象："
                "① 名称完整匹配正则 `{pattern}`（区分大小写）；② 对象自身带有 BoxCollider 组件（组件启用与否都算）。"
                "给出每个对象的完整层级路径和 instanceId，以及总数。").format(root=ctx.facts["candidatesPath"],
                                                               pattern=PATTERN)

    def answer_schema(self):
        return ('{"total": 整数, "matches": [{"path": "完整层级路径", "instanceId": 整数}], '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _entries(self, ctx):
        rows = K.answer(ctx, "matches") or []
        out = []
        for row in rows if isinstance(rows, list) else []:
            if isinstance(row, dict):
                out.append((K.as_int(row.get("instanceId")), K.norm_path(row.get("path"))))
        return out

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        expected = set(f["expected"])
        rows = self._entries(ctx)
        ids = [i for i, _ in rows]
        found = expected.intersection(ids)
        card.fraction("E1", len(found) / float(len(expected)), expected=len(expected), actual=len(found),
                      missing=sorted(expected - found))
        extras = [i for i in ids if i not in expected]
        dupes = len(ids) - len(set(ids))
        card.check("E2", not extras and not dupes, actual={"extras": extras, "duplicates": dupes})
        wrong = []
        for iid, path in rows:
            actual = f["expectedPaths"].get(str(iid)) or ((p.go(iid) or {}).get("path") if iid is not None else None)
            if not actual or path != actual:
                wrong.append({"instanceId": iid, "path": path, "actual": actual})
        card.check("E3", bool(rows) and not wrong, actual=wrong[:10])
        card.check("E4", K.as_int(K.answer(ctx, "total")) == len(expected), expected=len(expected),
                   actual=K.answer(ctx, "total"))
        ok, diff = K.same(f["structureC0"], self._structure(ctx))
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("E5", ok and not bad and not saves,
                   actual={"diff": diff, "violations": bad[:10], "sceneSaves": saves[:5]})
        card.check("E6", not ctx.records, actual=ctx.records[:10])

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iids):
        rows = [{"path": (ctx.probe.go(i) or {}).get("path"), "instanceId": i} for i in iids]
        return {"total": len(rows), "matches": rows, "declaredComplete": True}

    def _query(self, ctx):
        res = ctx.probe.query({"componentType": "BoxCollider", "includeInactive": True,
                               "namePattern": PATTERN})
        root = ctx.facts["candidatesPath"] + "/"
        return [o for o in res.get("objects") or []
                if re.match(PATTERN, o.get("name") or "") and (o.get("path") or "").startswith(root)]

    def reference(self, ctx):
        return self._answer(ctx, [o["instanceId"] for o in self._query(ctx)])

    def negatives(self):
        def include_lowercase(ctx):
            ans = self.reference(ctx)
            return self._answer(ctx, [r["instanceId"] for r in ans["matches"]] + [ctx.facts["lowerIid"]])

        def skip_inactive(ctx):
            ans = self.reference(ctx)
            return self._answer(ctx, [r["instanceId"] for r in ans["matches"]
                                      if r["instanceId"] != ctx.facts["inactiveIid"]])

        def swap_ids(ctx):
            ans = self.reference(ctx)
            a, b = ctx.facts["pairIids"]
            for row in ans["matches"]:
                row["instanceId"] = {a: b, b: a}.get(row["instanceId"], row["instanceId"])
            return ans

        def wrong_total(ctx):
            ans = self.reference(ctx)
            ans["total"] = len(ans["matches"]) + 1
            return ans

        def writes(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_rename", instanceId=ctx.facts["outsideIid"], newName="Enemy_09_checked")
            return ans

        def include_child_collider(ctx):
            ans = self.reference(ctx)
            return self._answer(ctx, [r["instanceId"] for r in ans["matches"]] + [ctx.facts["childBoxIid"]])

        return [
            Negative("include_lowercase", include_lowercase, {"E2", "E4"}),
            Negative("skip_inactive", skip_inactive, {"E1", "E4"}),
            Negative("swap_ids", swap_ids, {"E3"}),
            Negative("wrong_total", wrong_total, {"E4"}),
            Negative("writes", writes, {"E5", "E6"}),
            Negative("include_child_collider", include_child_collider, {"E2", "E4"}),
        ]

    def alternatives(self):
        def find_regex(ctx):
            res = ctx.c.read("gameobject_find", name=PATTERN, useRegex=True, component="BoxCollider", limit=500)
            root = ctx.facts["candidatesPath"] + "/"
            iids = [o["instanceId"] for o in res.get("objects") or [] if (o.get("path") or "").startswith(root)]
            # gameobject_find skips inactive objects; pick those up with an explicit inactive query
            inactive = ctx.probe.query({"componentType": "BoxCollider", "active": False, "namePattern": PATTERN})
            iids += [o["instanceId"] for o in inactive.get("objects") or []
                     if re.match(PATTERN, o.get("name") or "") and (o.get("path") or "").startswith(root)
                     and o["instanceId"] not in iids]
            return self._answer(ctx, iids)

        return [("find_regex", find_regex)]


TASK = S08()

# Producer:Betsy
