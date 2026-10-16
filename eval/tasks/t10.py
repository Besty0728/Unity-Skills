"""T10 Asset reorganisation keeping GUIDs (composite tier).

A/Incoming/{Batch01, Batch02, Vendor/Old, Misc} holds 35 URP materials plus Misc/Grip.physicMaterial.
Every material moves to A/Materials/<upper-case first letter>/ keeping its file name, a taken name gets
_2, _3, ... in ordinal order of the original full path, GUIDs and renderer references survive, emptied
Incoming folders are deleted. Traps: Brick in Batch01 and Batch02, Tile in three folders (order decides
the suffixes), the pre-existing A/Materials/B/Bolt.mat against Vendor/Old/Bolt.mat, lower-case initials,
the non-material Grip that keeps Misc and Incoming alive, the decoy A/Elsewhere/Brick.mat, and
copy+delete (new GUIDs, broken renderer slots) instead of a move.
"""
import json
import os

from harness import checks as K
from harness import uyaml
from harness.task import Negative, Task
from harness.verify import parse_ref

POOL = ["Asphalt", "Bark", "Basalt", "Canvas", "Chrome", "Clay", "Copper", "Denim", "Ember", "Felt", "Flint",
        "Glass", "Granite", "Gravel", "Iron", "Ivory", "Jade", "Leather", "Linen", "Marble", "Nickel", "Oak",
        "Onyx", "Paper", "Pine", "Quartz", "Rubber", "Sand", "Slate", "Steel", "Teak", "Velvet", "Walnut", "Wool",
        "Zinc", "moss", "rust", "cedar", "lava", "pearl", "fern", "ash", "umber"]
# folder -> fixed names, plus how many seeded pool names it gets (29 in total)
FOLDERS = [("Batch01", ["Brick"], 9), ("Batch02", ["Brick", "Tile"], 8), ("Vendor/Old", ["Bolt", "Tile"], 6),
           ("Misc", ["Tile"], 6)]
EMPTIED = ["Batch01", "Batch02", "Vendor/Old", "Vendor"]


class T10(Task):
    id = "T10"
    title = "Asset reorganisation keeping GUIDs"
    tier = "composite"
    timeout_s = 1800
    budget_usd = 40.0

    def assertions(self):
        return [
            ("H1", 2.0, False, "each material without a name clash sits at A/Materials/<L>/<name>.mat with its GUID (fraction)"),
            ("H2", 1.5, True, "the GUID set of all .mat files under A is unchanged (moved, not copied or recreated)"),
            ("H3", 1.0, False, "clashing names got _2/_3 in ordinal order of the original path, with their GUIDs"),
            ("H4", 1.5, False, "every renderer slot still references the same Material object"),
            ("H5", 0.5, False, "pre-existing Materials/B/Bolt.mat and decoy Elsewhere/Brick.mat untouched"),
            ("H6", 1.0, False, "Misc/Grip.physicMaterial stays where it was with its GUID"),
            ("H7", 1.0, False, "emptied folders deleted; Misc and Incoming kept"),
            ("H8", 0.5, False, "reported moves equal the actual mapping and declaredComplete is true"),
            ("H9", 1.0, True, "no side effects outside R/A and no scene save"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        incoming, materials = ctx.asset("Incoming"), ctx.asset("Materials")
        names = rng.sample(POOL, 29)
        sources, cursor = [], 0
        for folder, fixed, n in FOLDERS:
            batch = fixed + names[cursor:cursor + n]
            cursor += n
            rng.shuffle(batch)
            sources += ["%s/%s/%s.mat" % (incoming, folder, name) for name in batch]
        assert len(sources) == 35
        folders = [incoming] + ["%s/%s" % (incoming, f) for f in ("Batch01", "Batch02", "Vendor", "Vendor/Old", "Misc")]
        folders += [materials, materials + "/B", ctx.asset("Elsewhere")]
        c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": f} for f in folders]))
        bolt, decoy = materials + "/B/Bolt.mat", ctx.asset("Elsewhere", "Brick.mat")
        made = [{"name": os.path.basename(p)[:-4], "savePath": p} for p in sources + [bolt, decoy]]
        c.write("material_create_batch", items=json.dumps(made))
        c.write("material_set_colors_batch", propertyName="_BaseColor", items=json.dumps([
            {"path": m["savePath"], "r": round(rng.random(), 3), "g": round(rng.random(), 3), "b": round(rng.random(), 3)}
            for m in made]))
        grip = "%s/Misc/Grip.physicMaterial" % incoming
        c.write("physics_create_material", name="Grip", savePath="%s/Misc" % incoming, dynamicFriction=0.7,
                staticFriction=0.8)

        # 12 renderers over 10 incoming materials; some in slot 1, one beside the decoy, one beside old Bolt
        by_name = {}
        for p in sources:
            by_name.setdefault(os.path.basename(p), []).append(p)
        must = by_name["Brick.mat"] + by_name["Tile.mat"][:1] + by_name["Bolt.mat"]
        rest = [p for p in sources if p not in must]
        refs = must + rng.sample(rest, 10 - len(must))
        rng.shuffle(refs)
        slots = [[refs[i]] for i in range(9)] + [[decoy, refs[9]], [bolt, refs[0]], [refs[3], refs[7]]]
        props = c.write("gameobject_create", name="Props", parentInstanceId=ctx.scope.root_iid)
        cubes = c.write("gameobject_create_batch", items=json.dumps([
            {"name": "Prop_%02d" % i, "primitiveType": "Cube", "parentInstanceId": props["instanceId"], "x": i * 1.5}
            for i in range(len(slots))]))
        iids = [int(r["instanceId"]) for r in cubes["results"]]
        c.write("material_assign_batch", items=json.dumps([{"instanceId": i, "materialPath": s[0]}
                                                          for i, s in zip(iids, slots)]))
        second = []
        for i, s in zip(iids, slots):
            if len(s) > 1:
                second += [{"instanceId": i, "componentType": "MeshRenderer", "propertyPath": "m_Materials.Array.size",
                            "value": "2"},
                           {"instanceId": i, "componentType": "MeshRenderer",
                            "propertyPath": "m_Materials.Array.data[1]", "assetPath": s[1], "objectType": "Material"}]
        c.write("component_set_serialized_property_batch", items=json.dumps(second))
        ctx.facts.update({"incoming": incoming, "materials": materials, "sources": sources, "bolt": bolt,
                          "decoy": decoy, "grip": grip, "renderers": iids, "expected": self._expected(ctx, sources)})

    def _expected(self, ctx, sources):
        taken = {ctx.facts.get("bolt") or ctx.asset("Materials", "B", "Bolt.mat")}
        out = {}
        for src in sorted(sources):     # Python str order is ordinal
            stem = os.path.basename(src)[:-4]
            folder = "%s/%s" % (ctx.asset("Materials"), stem[0].upper())
            dest, n = "%s/%s.mat" % (folder, stem), 1
            while dest in taken:
                n += 1
                dest = "%s/%s_%d.mat" % (folder, stem, n)
            taken.add(dest)
            out[src] = dest
        return out

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["guid0"] = {s: p.guid(s) for s in f["sources"] + [f["bolt"], f["decoy"], f["grip"]]}
        f["matGuids0"] = sorted(self._disk_guids(ctx))
        f["state0"] = {s: K.material_state(p, s) for s in (f["bolt"], f["decoy"])}
        f["slots0"] = {i: self._slots(ctx, i) for i in f["renderers"]}

    def _abs(self, ctx, path):
        return os.path.join(ctx.project_path, path)

    def _disk_guids(self, ctx):
        out = []
        for base, _dirs, files in os.walk(self._abs(ctx, ctx.A)):
            for name in files:
                if name.endswith(".mat"):
                    out.append(uyaml.meta_guid(os.path.join(base, name)))
        return out

    @staticmethod
    def _slots(ctx, iid):
        ser = ctx.probe.ser(iid, "MeshRenderer") or {}
        size = K.as_int(ser.get("m_Materials.Array.size")) or 0
        return [ser.get("m_Materials.Array.data[%d]" % k) for k in range(size)]

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("整理 `{inc}` 下（含所有子文件夹）的全部材质（.mat）：\n"
                "1. 每个材质移动到 `{mat}/<文件名首字母的大写>/`，文件名保持不变（例如 `moss.mat` 进 `{mat}/M/moss.mat`）；"
                "目标文件夹不存在就新建。\n"
                "2. 如果目标位置已有同名文件（无论是本来就存在的还是本次刚移进去的），依次改用 `<原名>_2.mat`、`<原名>_3.mat`……"
                "中第一个空闲的名字；所有材质按原完整路径的序数（ordinal，逐字符码值）升序依次处理。\n"
                "3. 必须保持每个材质的 GUID 不变，场景中引用这些材质的渲染器不能断引用；不要复制后删除，也不要覆盖已有文件。\n"
                "4. 非材质文件留在原处；`{mat}` 下原有的资产和 `{inc}` 以外的资产都不要动。\n"
                "5. 移动后变空的 `{inc}` 子文件夹要删除（`{inc}` 本身变空也删除，不空则保留）。\n"
                "报告每个材质的原路径和新路径。").format(inc=ctx.facts["incoming"], mat=ctx.facts["materials"])

    def answer_schema(self):
        return ('{"moves": [{"from": "原资产路径", "to": "新资产路径"}], "deletedFolders": ["删除的文件夹路径"], '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        expected, guid0 = f["expected"], f["guid0"]
        clash = {s for s, d in expected.items() if os.path.basename(d)[:-4] != os.path.basename(s)[:-4]}
        plain = [s for s in expected if s not in clash]
        ok_plain = [s for s in plain if p.guid(expected[s]) == guid0[s]]
        card.fraction("H1", len(ok_plain) / float(len(plain)), expected=len(plain),
                      actual={"ok": len(ok_plain), "wrong": [(s, expected[s]) for s in plain if s not in ok_plain][:6]})
        now = sorted(self._disk_guids(ctx))
        card.check("H2", now == f["matGuids0"], actual={"lost": sorted(set(f["matGuids0"]) - set(now))[:6],
                                                       "new": sorted(set(now) - set(f["matGuids0"]))[:6]})
        bad_clash = [(s, expected[s], p.guid(expected[s])) for s in sorted(clash) if p.guid(expected[s]) != guid0[s]]
        card.check("H3", len(clash) == 4 and not bad_clash, actual={"clashes": len(clash), "wrong": bad_clash})
        bad_slots = []
        for iid, before in f["slots0"].items():
            after = self._slots(ctx, iid)
            ids0 = [(parse_ref(v) or (None,))[0] for v in before]
            ids1 = [(parse_ref(v) or (None,))[0] for v in after]
            if ids1 != ids0 or None in ids1:
                bad_slots.append({"renderer": iid, "before": before, "after": after})
        card.check("H4", not bad_slots, actual=bad_slots[:4])
        untouched = [s for s in (f["bolt"], f["decoy"])
                     if p.guid(s) != guid0[s] or not K.same(f["state0"][s], K.material_state(p, s))[0]]
        card.check("H5", not untouched, actual=untouched)
        card.check("H6", p.guid(f["grip"]) == guid0[f["grip"]], actual=p.guid(f["grip"]))
        inc = f["incoming"]
        gone = ["%s/%s" % (inc, d) for d in EMPTIED]
        kept = [inc, inc + "/Misc"]
        still = [d for d in gone if p.asset(d) or os.path.isdir(self._abs(ctx, d))]
        lost = [d for d in kept if not p.asset(d)]
        card.check("H7", not still and not lost, actual={"notDeleted": still, "missing": lost})
        moves = K.answer(ctx, "moves") or []
        reported = sorted((K.norm_path(m.get("from")), K.norm_path(m.get("to"))) for m in moves if isinstance(m, dict))
        actual = sorted(self._actual_moves(ctx))
        card.check("H8", reported == actual and K.answer(ctx, "declaredComplete") is True,
                   expected=actual[:5], actual={"reported": reported[:5], "count": len(reported),
                                                "declaredComplete": K.answer(ctx, "declaredComplete")})
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("H9", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

    def _actual_moves(self, ctx):
        """(original path, current path) for every C0 material whose GUID now resolves elsewhere."""
        f = ctx.facts
        where = {}
        for base, _dirs, files in os.walk(self._abs(ctx, ctx.A)):
            for name in files:
                if name.endswith(".mat"):
                    full = os.path.join(base, name)
                    rel = os.path.relpath(full, ctx.project_path).replace(os.sep, "/")
                    where[uyaml.meta_guid(full)] = rel
        out = []
        for src in f["sources"] + [f["bolt"], f["decoy"]]:
            cur = where.get(f["guid0"][src])
            if cur and cur != src:
                out.append((src, cur))
        return out

    # ------------------------------------------------------------------ solutions

    def _list(self, ctx, folder):
        res = ctx.c.read("batch_query_assets", folder=folder, typeFilter="Material", maxResults=500)
        return sorted(a["path"] for a in res.get("assets") or [] if (a.get("path") or "").endswith(".mat"))

    def _plan(self, ctx, sources, reverse=False, taken=None):
        mat = ctx.facts["materials"]
        taken = set(taken if taken is not None else self._list(ctx, mat))
        plan = []
        for src in sorted(sources, reverse=reverse):
            stem = os.path.basename(src)[:-4]
            folder = "%s/%s" % (mat, stem[0].upper())
            dest, n = "%s/%s.mat" % (folder, stem), 1
            while dest in taken:
                n += 1
                dest = "%s/%s_%d.mat" % (folder, stem, n)
            taken.add(dest)
            plan.append((src, dest))
        return plan

    def _folders(self, ctx, plan):
        need = sorted({d.rsplit("/", 1)[0] for _s, d in plan})
        missing = [d for d in need if not ctx.probe.asset(d)]
        if missing:
            ctx.c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": d} for d in missing]))

    def _move(self, ctx, plan):
        self._folders(ctx, plan)
        ctx.c.write("asset_move_batch", items=json.dumps([{"sourcePath": s, "destinationPath": d} for s, d in plan]))

    def _prune(self, ctx):
        """Delete Incoming folders that hold nothing any more, deepest first."""
        root = self._abs(ctx, ctx.facts["incoming"])
        deleted = []
        for base, dirs, files in sorted(os.walk(root), key=lambda w: -w[0].count(os.sep)):
            live = [d for d in dirs if os.path.isdir(os.path.join(base, d))]
            if not live and not [n for n in files if not n.endswith(".meta")]:
                rel = os.path.relpath(base, ctx.project_path).replace(os.sep, "/")
                ctx.c.write("asset_delete", assetPath=rel)
                deleted.append(rel)
        return deleted

    def _answer(self, plan, deleted):
        return {"moves": [{"from": s, "to": d} for s, d in plan], "deletedFolders": deleted, "declaredComplete": True}

    def reference(self, ctx):
        plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
        self._move(ctx, plan)
        return self._answer(plan, self._prune(ctx))

    def negatives(self):
        def copy_delete(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._folders(ctx, plan)
            for src, dest in plan:
                copy = ctx.c.write("asset_duplicate", assetPath=src).get("copy")
                ctx.c.write("asset_move", sourcePath=copy, destinationPath=dest)
                ctx.c.write("asset_delete", assetPath=src)
            return self._answer(plan, self._prune(ctx))

        def overwrite_bolt(ctx):
            ctx.c.write("asset_delete", assetPath=ctx.facts["bolt"])
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._move(ctx, plan)
            return self._answer(plan, self._prune(ctx))

        def move_grip(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._move(ctx, plan)
            grip_to = ctx.asset("Materials", "G", "Grip.physicMaterial")
            if not ctx.probe.asset(ctx.asset("Materials", "G")):
                ctx.c.write("asset_create_folder", folderPath=ctx.asset("Materials", "G"))
            ctx.c.write("asset_move", sourcePath=ctx.facts["grip"], destinationPath=grip_to)
            return self._answer(plan, self._prune(ctx))

        def leave_empty(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._move(ctx, plan)
            return self._answer(plan, [])

        def delete_incoming(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._move(ctx, plan)
            ctx.c.write("asset_delete", assetPath=ctx.facts["incoming"])
            return self._answer(plan, [ctx.facts["incoming"]])

        def wrong_order(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]), reverse=True)
            self._move(ctx, plan)
            return self._answer(plan, self._prune(ctx))

        def move_elsewhere(ctx):
            plan = self._plan(ctx, self._list(ctx, ctx.facts["incoming"]))
            self._move(ctx, plan)
            extra = self._plan(ctx, [ctx.facts["decoy"]])
            self._move(ctx, extra)
            return self._answer(plan + extra, self._prune(ctx))

        return [
            Negative("copy_delete", copy_delete, {"H1", "H2", "H3", "H4", "H8"}),
            Negative("overwrite_bolt", overwrite_bolt, {"H2", "H3", "H4", "H5"}),
            Negative("move_grip", move_grip, {"H6", "H7"}),
            Negative("leave_empty", leave_empty, {"H7"}),
            Negative("delete_incoming", delete_incoming, {"H6", "H7"}),
            Negative("wrong_order", wrong_order, {"H1", "H3"}),
            Negative("move_elsewhere", move_elsewhere, {"H5"}),
        ]

    def alternatives(self):
        def one_by_one(ctx):
            # asset_find listing, single moves, delete the emptied Vendor tree in one call
            inc = ctx.facts["incoming"] + "/"
            res = ctx.c.read("asset_find", searchFilter="t:Material", limit=5000)
            sources = [a["path"] for a in res.get("assets") or []
                       if (a.get("path") or "").startswith(inc) and a["path"].endswith(".mat")]
            plan = self._plan(ctx, sources)
            for src, dest in plan:
                folder = dest.rsplit("/", 1)[0]
                if not ctx.probe.asset(folder):
                    ctx.c.write("asset_create_folder", folderPath=folder)
                ctx.c.write("asset_move", sourcePath=src, destinationPath=dest)
            deleted = []
            for sub in ("Batch01", "Batch02", "Vendor"):
                ctx.c.write("asset_delete", assetPath=inc + sub)
                deleted.append(inc + sub)
            return self._answer(plan, deleted)

        return [("one_by_one", one_by_one)]


TASK = T10()

# Producer:Betsy
