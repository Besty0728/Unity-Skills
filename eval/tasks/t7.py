"""T7 Large-set incremental repair (composite tier).

R/Yard holds six rows with 113 objects; 73 of them fully match ^Crate_[A-F][0-9]{2}$ (case-sensitive).
Every match without a BoxCollider gets one, every match with a Rigidbody gets mass 4.5, nothing else.
Traps: 9 matches are inactive (6 self, 3 through an inactive parent) and batch_query_gameobjects skips
them by default; its namePattern is case-insensitive (crate_A01 / Crate_a01 / CRATE_A01 decoys);
gameobject_find truncates at 50; R/Archive/Crate_A01 matches but is outside the Yard; two Crate_B07 in
different rows defeat name-based targeting; 6 matches carry a Sphere/Mesh collider instead (keep it,
still add a BoxCollider) and 2 carry a disabled BoxCollider (leave it alone).
"""
import json
import re

from harness import checks as K
from harness.task import Negative, Task

PATTERN = r"^Crate_[A-F][0-9]{2}$"
MASS = 4.5
ROWS = "ABCDEF"
# Matches outside the stack / duplicate / inactive specials: (collider, rigidbody mass or None).
# collider: box | box_off | none | sphere | mesh
REGULAR = ([("none", None)] * 6 + [("none", 1.0), ("none", 4.0), ("none", MASS), ("none", MASS)] +
           [("sphere", 5.0), ("sphere", MASS), ("mesh", 45.0), ("mesh", None), ("box_off", 4.25)] +
           [("box", 0.45), ("box", 3.5)] + [("box", MASS)] * 14 + [("box", None)] * 31)
# (collider, mass, activeSelf)
INACTIVE = [("none", None, False), ("sphere", 2.0, False), ("box_off", 9.0, False), ("box", MASS, False),
            ("box", None, False), ("none", 1.0, False)]
STACK = [("none", None), ("mesh", 7.0), ("box", MASS)]
DECOYS = ["crate_A01", "Crate_a01", "CRATE_A01", "Crate_G01", "Crate_A1", "Crate_A012", "Crate_A01_old",
          "CrateA01", "Crate-A01", "Crate_AA1", "Crate_F7x", "Crate_D05 (1)"]
FILLERS = ["Pallet", "Barrel", "Sign"]
OTHER_COLLIDERS = ("SphereCollider", "MeshCollider")


class T7(Task):
    id = "T7"
    title = "Large-set incremental repair"
    tier = "composite"
    timeout_s = 1800
    budget_usd = 40.0

    def assertions(self):
        return [
            ("G1", 2.0, False, "every match that had no BoxCollider now has exactly one, enabled (fraction)"),
            ("G2", 1.0, False, "matches that already had a BoxCollider keep exactly one with the same enabled state"),
            ("G3", 0.5, False, "matches keep their other components (Sphere/Mesh colliders, renderers) unchanged"),
            ("G4", 2.0, False, "every match whose Rigidbody mass was not 4.5 now has mass 4.5 (fraction)"),
            ("G5", 1.0, False, "no Rigidbody added or removed; matches already at 4.5 unchanged"),
            ("G6", 1.0, True, "decoys, R/Archive/Crate_A01, fillers and groups unchanged"),
            ("G7", 0.5, False, "hierarchy unchanged: same objects, names, paths, active flags, sibling order"),
            ("G8", 0.5, False, "addedBoxCollider lists exactly the objects that gained a BoxCollider"),
            ("G9", 0.5, False, "fixedMass lists exactly the objects whose Rigidbody mass changed"),
            ("G10", 0.5, False, "matchedCount is 73 and declaredComplete is true"),
            ("G11", 0.5, True, "no side effects outside R/A and no scene save"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        yard = ctx.path("Yard")
        row_path = {r: yard + "/Row_" + r for r in ROWS}
        stack_path = row_path["C"] + "/Stack"
        specs = [{"collider": col, "mass": mass, "active": True} for col, mass in REGULAR]
        specs += [{"collider": col, "mass": mass, "active": act} for col, mass, act in INACTIVE]
        assert len(specs) == 68
        rng.shuffle(specs)
        # 68 regular matches over rows A..F; B and E also host a Crate_B07, C also hosts the stack
        per_row = {"A": 13, "B": 11, "C": 9, "D": 12, "E": 11, "F": 12}
        rows = {r: [] for r in ROWS}
        cursor = 0
        for r in ROWS:
            pool = [n for n in range(1, 40) if not (r == "B" and n == 7)]
            extra = 3 if r == "C" else 0
            numbers = rng.sample(pool, per_row[r] + extra)
            for n in numbers[:per_row[r]]:
                spec = dict(specs[cursor], name="Crate_%s%02d" % (r, n), role="target")
                cursor += 1
                rows[r].append(spec)
            if r == "C":
                stack = [dict(collider=col, mass=mass, active=True, role="target", name="Crate_C%02d" % n)
                         for (col, mass), n in zip(STACK, numbers[per_row[r]:])]
        rows["B"].append({"collider": "none", "mass": None, "active": True, "role": "target", "name": "Crate_B07"})
        rows["E"].append({"collider": "none", "mass": None, "active": True, "role": "target", "name": "Crate_B07"})
        decoy_rows = [ROWS[i // 2] for i in range(len(DECOYS))]
        rng.shuffle(decoy_rows)
        for name, r in zip(DECOYS, decoy_rows):
            rows[r].append({"collider": "none", "mass": 2.0, "active": True, "role": "decoy", "name": name})
        for r in ROWS:
            for i, base in enumerate(FILLERS):
                rows[r].append({"collider": "box" if i == 0 else "none", "mass": 1.0 if i == 1 else None,
                                "active": True, "role": "filler", "name": "%s_%s%d" % (base, r, rng.randint(1, 9))})
        rows["C"].append({"group": True, "name": "Stack", "role": "group", "active": False})
        for r in ROWS:
            rng.shuffle(rows[r])

        items = [{"name": "Yard", "parentInstanceId": ctx.scope.root_iid},
                 {"name": "Archive", "parentInstanceId": ctx.scope.root_iid, "x": 30}]
        plan = [None, None]
        for ri, r in enumerate(ROWS):
            items.append({"name": "Row_" + r, "parentPath": yard, "z": ri * 3})
            plan.append({"group": True, "role": "group", "active": True})
        for r in ROWS:
            for i, spec in enumerate(rows[r]):
                items.append(self._item(spec, row_path[r], i))
                plan.append(spec)
        for i, spec in enumerate(stack):
            items.append(self._item(spec, stack_path, i))
            plan.append(spec)
        archive = {"collider": "none", "mass": 2.0, "active": True, "role": "archive", "name": "Crate_A01"}
        items.append(self._item(archive, ctx.path("Archive"), 0))
        plan.append(archive)
        res = c.write("gameobject_create_batch", items=json.dumps(items))
        iids = [int(r["instanceId"]) for r in res["results"]]
        assert len(iids) == len(items)
        yard_iid, archive_iid = iids[0], iids[1]

        add, mass_set, disable, inactive = [], [], [], []
        objects = {}
        for iid, item, spec in zip(iids, items, plan):
            if spec is None:
                objects[iid] = {"role": "group", "name": item["name"]}
                continue
            objects[iid] = {"role": spec["role"], "name": item["name"]}
            if spec.get("group"):
                if not spec["active"]:
                    inactive.append(iid)
                continue
            if spec["collider"] == "mesh":
                add.append({"instanceId": iid, "componentType": "MeshCollider"})
            if spec["collider"] == "box_off":
                disable.append({"instanceId": iid, "componentType": "BoxCollider", "propertyName": "enabled",
                                "value": False})
            if spec["mass"] is not None:
                add.append({"instanceId": iid, "componentType": "Rigidbody"})
                mass_set.append({"instanceId": iid, "componentType": "Rigidbody", "propertyName": "mass",
                                 "value": spec["mass"]})
            if not spec["active"]:
                inactive.append(iid)
        c.write("component_add_batch", items=json.dumps(add))
        c.write("component_set_property_batch", items=json.dumps(mass_set + disable))
        c.write("gameobject_set_active_batch", items=json.dumps([{"instanceId": i, "active": False}
                                                                  for i in inactive]))
        targets = [i for i, o in objects.items() if o["role"] == "target"]
        assert len(targets) == 73
        ctx.facts.update({"yardIid": yard_iid, "yardPath": yard, "archiveIid": archive_iid,
                          "objects": objects, "targets": sorted(targets)})

    @staticmethod
    def _item(spec, parent_path, index):
        item = {"name": spec["name"], "parentPath": parent_path, "x": index * 1.5}
        if spec.get("collider") in ("box", "box_off"):
            item["primitiveType"] = "Cube"
        elif spec.get("collider") == "sphere":
            item["primitiveType"] = "Sphere"
        return item

    def after_fixture(self, ctx):
        ctx.facts["c0"] = self._snapshot(ctx)

    def _snapshot(self, ctx):
        p = ctx.probe
        res = p.query({"includeInactive": True, "limit": 500})
        under = ctx.R + "/"
        hierarchy = {o["instanceId"]: {"name": o.get("name"), "path": o.get("path"), "active": o.get("activeSelf")}
                     for o in res.get("objects") or [] if (o.get("path") or "").startswith(under)}
        comps, mass = {}, {}
        for iid in hierarchy:
            listed = p.components(iid)
            comps[iid] = [(x.get("type"), x.get("enabled")) for x in listed]
            if any(t == "Rigidbody" for t, _e in comps[iid]):
                mass[iid] = float((p.ser(iid, "Rigidbody") or {}).get("m_Mass") or "nan")
        groups = [i for i, o in ctx.facts["objects"].items() if o["role"] == "group"] + [ctx.scope.root_iid]
        order = {g: [ch["instanceId"] for ch in p.children(g)] for g in groups}
        return {"hierarchy": hierarchy, "comps": comps, "mass": mass, "order": order}

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("`{yard}` 下有六排货物（约 110 个对象，含未激活的对象和未激活父节点下的对象）。"
                "对 `{yard}` 的所有子孙对象中名字完整匹配正则 `{pat}`（区分大小写）的每一个对象做增量修复：\n"
                "1. 如果它自身没有 BoxCollider 组件，给它添加一个 BoxCollider；已有 BoxCollider 的（不论是否启用）不要改动它，"
                "也不要移除它已有的其他碰撞体或组件。\n"
                "2. 如果它自身有 Rigidbody，把质量 mass 设为 4.5；没有 Rigidbody 的不要添加。\n"
                "不要修改不匹配的对象和 `{yard}` 之外的对象，不要改变任何对象的激活状态、名字、父子关系或顺序。"
                "报告匹配的对象总数、新加了 BoxCollider 的对象 instanceId 列表、修改了质量的对象 instanceId 列表"
                "（质量本来就是 4.5 的不算修改）。").format(yard=ctx.facts["yardPath"], pat=PATTERN)

    def answer_schema(self):
        return ('{"matchedCount": 整数, "addedBoxCollider": [instanceId 整数...], "fixedMass": [instanceId 整数...], '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    @staticmethod
    def _count(comps, type_name):
        return sum(1 for t, _e in comps if t == type_name)

    @staticmethod
    def _box_enabled(comps):
        return [e for t, e in comps if t == "BoxCollider"]

    @staticmethod
    def _rest(comps):
        return [(t, e) for t, e in comps if t not in ("BoxCollider", "Rigidbody")]

    def verify(self, ctx, card):
        c0, now = ctx.facts["c0"], self._snapshot(ctx)
        objects, targets = ctx.facts["objects"], ctx.facts["targets"]
        comps0, comps1 = c0["comps"], now["comps"]

        def comps(iid):
            return comps1.get(iid, [])

        lacking = [i for i in targets if self._count(comps0[i], "BoxCollider") == 0]
        boxed = [i for i in targets if i not in lacking]
        fixed_box = [i for i in lacking if self._box_enabled(comps(i)) == [True]]
        card.fraction("G1", len(fixed_box) / float(len(lacking)), expected=len(lacking),
                      actual={"ok": len(fixed_box), "missing": [i for i in lacking if i not in fixed_box][:10]})
        bad_boxed = [i for i in boxed if self._box_enabled(comps(i)) != self._box_enabled(comps0[i])]
        card.check("G2", not bad_boxed, actual={"changed": bad_boxed[:10]})
        bad_rest = [i for i in targets if self._rest(comps(i)) != self._rest(comps0[i])]
        card.check("G3", not bad_rest, actual={"changed": [(i, comps(i)) for i in bad_rest[:5]]})

        mass0, mass1 = c0["mass"], now["mass"]
        wrong = [i for i in targets if i in mass0 and abs(mass0[i] - MASS) > 1e-4]
        fixed_mass = [i for i in wrong if self._count(comps(i), "Rigidbody") == 1 and abs(mass1.get(i, 0) - MASS) <= 1e-4]
        card.fraction("G4", len(fixed_mass) / float(len(wrong)), expected=len(wrong),
                      actual={"ok": len(fixed_mass), "missing": [i for i in wrong if i not in fixed_mass][:10]})
        bad_rb = [i for i in targets if self._count(comps(i), "Rigidbody") != self._count(comps0[i], "Rigidbody") or
                  (i in mass0 and i not in wrong and abs(mass1.get(i, 0) - MASS) > 1e-4)]
        card.check("G5", not bad_rb, actual={"changed": [(i, comps(i), mass1.get(i)) for i in bad_rb[:5]]})

        others = [i for i in comps0 if i not in set(targets)]
        bad_others = [i for i in others if comps(i) != comps0[i] or
                      (i in mass0 and abs(mass1.get(i, 0) - mass0[i]) > 1e-4)]
        card.check("G6", not bad_others, actual={"changed": [(objects.get(i, {}).get("name"), comps(i), mass1.get(i))
                                                            for i in bad_others[:5]]})

        h0, h1 = c0["hierarchy"], now["hierarchy"]
        diff_h = [i for i in set(h0) | set(h1) if h0.get(i) != h1.get(i)]
        diff_order = [g for g in c0["order"] if c0["order"][g] != now["order"].get(g)]
        card.check("G7", not diff_h and not diff_order,
                   actual={"changed": [(h0.get(i), h1.get(i)) for i in diff_h[:5]], "reordered": diff_order[:5]})

        gained = sorted(i for i in comps1 if self._count(comps1[i], "BoxCollider") > self._count(comps0.get(i, []),
                                                                                                 "BoxCollider"))
        changed_mass = sorted(i for i in mass0 if i in mass1 and abs(mass1[i] - mass0[i]) > 1e-4)
        rep_added = sorted(x for x in (K.as_int(v) for v in K.answer(ctx, "addedBoxCollider") or []))
        rep_mass = sorted(x for x in (K.as_int(v) for v in K.answer(ctx, "fixedMass") or []))
        card.check("G8", rep_added == gained, expected=gained, actual=rep_added)
        card.check("G9", rep_mass == changed_mass, expected=changed_mass, actual=rep_mass)
        card.check("G10", K.as_int(K.answer(ctx, "matchedCount")) == len(targets) and
                   K.answer(ctx, "declaredComplete") is True,
                   actual={k: K.answer(ctx, k) for k in ("matchedCount", "declaredComplete")})
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("G11", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

    # ------------------------------------------------------------------ solutions

    def _scan(self, ctx, include_inactive=True, case_sensitive=True, yard_only=True, query=None):
        """Matches with their component types and Rigidbody mass, read through the normal skills."""
        p = ctx.probe
        q = query or {"namePattern": PATTERN, "includeInactive": include_inactive, "limit": 500}
        res = p.query(q)
        rx = re.compile(PATTERN if case_sensitive else "(?i)" + PATTERN)
        prefix = ctx.facts["yardPath"] + "/" if yard_only else ctx.R + "/"
        out = []
        for o in res.get("objects") or []:
            if not rx.match(o.get("name") or "") or not (o.get("path") or "").startswith(prefix):
                continue
            types = [x.get("type") for x in p.components(o["instanceId"])]
            mass = None
            if "Rigidbody" in types:
                mass = float((p.ser(o["instanceId"], "Rigidbody") or {}).get("m_Mass"))
            out.append({"iid": o["instanceId"], "name": o["name"], "types": types, "mass": mass})
        return out

    @staticmethod
    def _plan(found):
        add = [f for f in found if "BoxCollider" not in f["types"]]
        fix = [f for f in found if f["mass"] is not None and abs(f["mass"] - MASS) > 1e-4]
        return add, fix

    def _apply(self, ctx, found, by_name=False):
        add, fix = self._plan(found)

        def key(f):
            return {"name": f["name"]} if by_name else {"instanceId": f["iid"]}

        if add:
            ctx.c.write("component_add_batch", items=json.dumps([dict(key(f), componentType="BoxCollider")
                                                                for f in add]))
        if fix:
            ctx.c.write("component_set_property_batch", items=json.dumps([
                dict(key(f), componentType="Rigidbody", propertyName="mass", value=MASS) for f in fix]))
        return {"matchedCount": len(found), "addedBoxCollider": [f["iid"] for f in add],
                "fixedMass": [f["iid"] for f in fix], "declaredComplete": True}

    def reference(self, ctx):
        return self._apply(ctx, self._scan(ctx))

    def negatives(self):
        def skip_inactive(ctx):
            return self._apply(ctx, self._scan(ctx, include_inactive=False))

        def case_insensitive(ctx):
            return self._apply(ctx, self._scan(ctx, case_sensitive=False))

        def include_archive(ctx):
            return self._apply(ctx, self._scan(ctx, yard_only=False))

        def by_name(ctx):
            return self._apply(ctx, self._scan(ctx), by_name=True)

        def enable_disabled(ctx):
            found = self._scan(ctx)
            ans = self._apply(ctx, found)
            for f in found:
                listed = ctx.probe.components(f["iid"])
                if any(x.get("type") == "BoxCollider" and x.get("enabled") is False for x in listed):
                    ctx.c.write("component_set_enabled", instanceId=f["iid"], componentType="BoxCollider",
                                enabled=True)
            return ans

        def replace_colliders(ctx):
            found = self._scan(ctx)
            for f in found:
                for t in OTHER_COLLIDERS:
                    if t in f["types"] and "BoxCollider" not in f["types"]:
                        ctx.c.write("component_remove", instanceId=f["iid"], componentType=t)
            return self._apply(ctx, found)

        def add_rigidbody(ctx):
            found = self._scan(ctx)
            ans = self._apply(ctx, found)
            bare = [f for f in found if f["mass"] is None]
            ctx.c.write("component_add_batch", items=json.dumps([{"instanceId": f["iid"], "componentType": "Rigidbody"}
                                                                for f in bare]))
            ctx.c.write("component_set_property_batch", items=json.dumps([
                {"instanceId": f["iid"], "componentType": "Rigidbody", "propertyName": "mass", "value": MASS}
                for f in bare]))
            return ans

        def reactivate(ctx):
            found = self._scan(ctx)
            dormant = [i for i, o in ctx.facts["c0"]["hierarchy"].items() if o["active"] is False]
            ctx.c.write("gameobject_set_active_batch", items=json.dumps([{"instanceId": i, "active": True}
                                                                        for i in dormant]))
            return self._apply(ctx, found)

        return [
            Negative("skip_inactive", skip_inactive, {"G1", "G4", "G10"}),
            Negative("case_insensitive", case_insensitive, {"G6", "G10"}),
            Negative("include_archive", include_archive, {"G6", "G10"}),
            Negative("by_name", by_name, {"G1", "G8"}),
            Negative("enable_disabled", enable_disabled, {"G2"}),
            Negative("replace_colliders", replace_colliders, {"G3"}),
            Negative("add_rigidbody", add_rigidbody, {"G5"}),
            Negative("reactivate", reactivate, {"G7"}),
        ]

    def alternatives(self):
        def substring_scan_serialized(ctx):
            # substring query + local regex, one-step-per-object batches, mass via the serialized path
            found = self._scan(ctx, query={"name": "Crate_", "includeInactive": True, "limit": 500})
            add, fix = self._plan(found)
            steps = [{"skill": "component_add", "args": {"instanceId": f["iid"], "componentType": "BoxCollider"}}
                     for f in add]
            for start in range(0, len(steps), 50):
                ctx.c.batch(steps[start:start + 50])
            if fix:
                ctx.c.write("component_set_serialized_property_batch", items=json.dumps([
                    {"instanceId": f["iid"], "componentType": "Rigidbody", "propertyPath": "m_Mass", "value": "4.5"}
                    for f in fix]))
            return {"matchedCount": len(found), "addedBoxCollider": [f["iid"] for f in add],
                    "fixedMass": [f["iid"] for f in fix], "declaredComplete": True}

        return [("substring_scan_serialized", substring_scan_serialized)]


TASK = T7()

# Producer:Betsy
