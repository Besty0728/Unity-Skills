"""D01 Cross-page inventory of 63 objects (basic tier, read-only).

Traps: gameobject_find stops at limit=50 without saying so and skips inactive objects; the prefix
match is case-sensitive (slot_lower) and literal (Slot-12, Slots_3, OldSlot_9 are not matches);
two names occur twice in different racks (deduplicating by name loses one); R/Vault/Slot_Q lies
outside the search root.
"""
import json

from harness import checks as K
from harness.task import Negative, Task

PREFIX = "Slot_"
SLOT_COUNT = 63
INACTIVE = 7
DECOYS = ["slot_lower", "OldSlot_9", "Slot-12", "Slots_3"]


class D01(Task):
    id = "D01"
    title = "Inventory of 63 objects across pages"
    tier = "basic"
    timeout_s = 420
    budget_usd = 5.0

    def assertions(self):
        return [
            ("F1", 3.0, False, "every Slot_ object under Inventory is listed (fraction of 63)"),
            ("F2", 3.0, False, "no extra or duplicate entries"),
            ("F3", 2.0, False, "each listed path belongs to the listed instanceId"),
            ("F4", 1.0, False, "total equals 63"),
            ("F5", 0.5, True, "scene state unchanged, nothing outside R/A, no scene save"),
            ("F6", 0.5, False, "no write recorded in the change journal"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        inv = c.write("gameobject_create", name="Inventory", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        leaves = []
        for s in range(3):
            sec = c.write("gameobject_create", name="Section_%s" % "ABC"[s], parentInstanceId=inv)["instanceId"]
            for r in range(2):
                rack = c.write("gameobject_create", name="Rack_%d" % (r + 1), parentInstanceId=sec)["instanceId"]
                tray = c.write("gameobject_create", name="Tray", parentInstanceId=rack)["instanceId"]
                box = c.write("gameobject_create", name="Box", parentInstanceId=tray)["instanceId"]
                leaves += [rack, tray, box]
        numbers = rng.sample(range(1, 400), SLOT_COUNT - 2 - 12)
        lettered = ["%s%d" % (rng.choice("ABCDEFGH"), n) for n in rng.sample(range(1, 90), 12)]
        suffixes = [str(n) for n in numbers] + lettered
        pairs = rng.sample(suffixes, 2)
        names = ["Slot_" + s for s in suffixes + pairs]
        homes = [rng.choice(leaves) for _ in names]
        for i, name in enumerate(names[-2:]):
            twin = names.index(name)
            while homes[len(names) - 2 + i] == homes[twin]:
                homes[len(names) - 2 + i] = rng.choice(leaves)
        items = [{"name": n, "parentInstanceId": h, "x": i % 9, "y": 0, "z": i // 9}
                 for i, (n, h) in enumerate(zip(names, homes))]
        decoy_items = [{"name": n, "parentInstanceId": rng.choice(leaves)} for n in DECOYS]
        order = list(range(len(items) + len(decoy_items)))
        rng.shuffle(order)
        everything = items + decoy_items
        created = {}
        for start in range(0, len(order), 50):
            chunk = [everything[k] for k in order[start:start + 50]]
            res = c.write("gameobject_create_batch", items=json.dumps(chunk))
            for k, row in zip(order[start:start + 50], res.get("results") or []):
                created[k] = int(row["instanceId"])
        slots = [created[k] for k in range(len(items))]
        inactive = rng.sample(slots, INACTIVE)
        for iid in inactive:
            c.write("gameobject_set_active", instanceId=iid, active=False)
        vault = c.write("gameobject_create", name="Vault", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        outside = c.write("gameobject_create", name="Slot_Q", parentInstanceId=vault)["instanceId"]
        ctx.facts.update({
            "inventoryIid": inv, "inventoryPath": ctx.path("Inventory"), "vaultIid": vault, "outsideIid": outside,
            "expected": slots, "inactive": inactive, "lowerIid": created[len(items)],
            "pairNames": ["Slot_" + s for s in pairs],
        })

    def after_fixture(self, ctx):
        f = ctx.facts
        f["structureC0"] = self._structure(ctx)
        paths = {str(row["id"]): row["path"] for row in f["structureC0"]}
        f["expectedPaths"] = {str(i): paths.get(str(i)) for i in f["expected"]}

    def _structure(self, ctx):
        out = []

        def walk(iid):
            info = ctx.probe.go(iid) or {}
            kids = [ch["instanceId"] for ch in info.get("children") or []]
            out.append({"id": iid, "name": info.get("name"), "active": info.get("isActive"), "path": info.get("path"),
                        "components": info.get("components"), "children": kids})
            for k in kids:
                walk(k)

        walk(ctx.facts["inventoryIid"])
        walk(ctx.facts["vaultIid"])
        return out

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("只读任务，不要修改任何东西。盘点 `{root}` 的所有后代（包括未激活的对象）中名称以 `Slot_` 开头"
                "（区分大小写）的对象，给出每个对象的完整层级路径和 instanceId，以及总数。"
                ).format(root=ctx.facts["inventoryPath"])

    def answer_schema(self):
        return ('{"total": 整数, "items": [{"path": "完整层级路径", "instanceId": 整数}], '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _entries(self, ctx):
        rows = K.answer(ctx, "items") or []
        return [(K.as_int(r.get("instanceId")), K.norm_path(r.get("path")))
                for r in (rows if isinstance(rows, list) else []) if isinstance(r, dict)]

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        expected = set(f["expected"])
        rows = self._entries(ctx)
        ids = [i for i, _ in rows]
        found = expected.intersection(ids)
        card.fraction("F1", len(found) / float(len(expected)), expected=len(expected), actual=len(found),
                      missing=sorted(expected - found)[:20])
        extras = [i for i in ids if i not in expected]
        dupes = len(ids) - len(set(ids))
        card.check("F2", not extras and not dupes, actual={"extras": extras[:20], "duplicates": dupes})
        wrong = []
        for iid, path in rows:
            actual = f["expectedPaths"].get(str(iid)) or ((p.go(iid) or {}).get("path") if iid is not None else None)
            if not actual or path != actual:
                wrong.append({"instanceId": iid, "path": path, "actual": actual})
        card.check("F3", bool(rows) and not wrong, actual=wrong[:10])
        card.check("F4", K.as_int(K.answer(ctx, "total")) == len(expected), expected=len(expected),
                   actual=K.answer(ctx, "total"))
        ok, diff = K.same(f["structureC0"], self._structure(ctx))
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("F5", ok and not bad and not saves,
                   actual={"diff": diff, "violations": bad[:10], "sceneSaves": saves[:5]})
        card.check("F6", not ctx.records, actual=ctx.records[:10])

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iids):
        paths = {}
        for iid in iids:
            paths[iid] = ctx.facts["expectedPaths"].get(str(iid)) or (ctx.probe.go(iid) or {}).get("path")
        rows = [{"path": paths[i], "instanceId": i} for i in iids]
        return {"total": len(rows), "items": rows, "declaredComplete": True}

    def _query(self, ctx, **extra):
        query = {"includeInactive": True, "namePattern": "^" + PREFIX, "limit": 500}
        query.update(extra)
        res = ctx.probe.query(query, sample_limit=500)
        root = ctx.facts["inventoryPath"] + "/"
        return [o for o in res.get("objects") or []
                if (o.get("name") or "").startswith(PREFIX) and (o.get("path") or "").startswith(root)]

    def reference(self, ctx):
        return self._answer(ctx, [o["instanceId"] for o in self._query(ctx)])

    def negatives(self):
        def first_50(ctx):
            res = ctx.c.read("gameobject_find", name="^" + PREFIX, useRegex=True)
            root = ctx.facts["inventoryPath"] + "/"
            return self._answer(ctx, [o["instanceId"] for o in res.get("objects") or []
                                      if (o.get("path") or "").startswith(root)])

        def include_lower(ctx):
            ans = self.reference(ctx)
            return self._answer(ctx, [r["instanceId"] for r in ans["items"]] + [ctx.facts["lowerIid"]])

        def skip_inactive(ctx):
            return self._answer(ctx, [o["instanceId"] for o in self._query(ctx, includeInactive=False)])

        def include_vault(ctx):
            ans = self.reference(ctx)
            return self._answer(ctx, [r["instanceId"] for r in ans["items"]] + [ctx.facts["outsideIid"]])

        def dedupe_names(ctx):
            seen, iids = set(), []
            for o in self._query(ctx):
                if o.get("name") not in seen:
                    seen.add(o.get("name"))
                    iids.append(o["instanceId"])
            return self._answer(ctx, iids)

        def swap_ids(ctx):
            ans = self.reference(ctx)
            a, b = ans["items"][0], ans["items"][1]
            a["instanceId"], b["instanceId"] = b["instanceId"], a["instanceId"]
            return ans

        def writes(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_rename", instanceId=ctx.facts["outsideIid"], newName="Slot_Q_seen")
            return ans

        return [
            Negative("first_50", first_50, {"F1", "F4"}),
            Negative("include_lower", include_lower, {"F2", "F4"}),
            Negative("skip_inactive", skip_inactive, {"F1", "F4"}),
            Negative("include_vault", include_vault, {"F2", "F4"}),
            Negative("dedupe_names", dedupe_names, {"F1", "F4"}),
            Negative("swap_ids", swap_ids, {"F3"}),
            Negative("writes", writes, {"F5", "F6"}),
        ]

    def alternatives(self):
        def find_paged(ctx):
            root = ctx.facts["inventoryPath"] + "/"
            res = ctx.c.read("gameobject_find", name="^" + PREFIX, useRegex=True, limit=500)
            iids = [o["instanceId"] for o in res.get("objects") or [] if (o.get("path") or "").startswith(root)]
            iids += [o["instanceId"] for o in self._query(ctx, active=False) if o["instanceId"] not in iids]
            return self._answer(ctx, iids)

        return [("find_paged", find_paged)]


TASK = D01()

# Producer:Betsy
