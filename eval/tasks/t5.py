"""T5 Prefab variant replacement: swap one same-name instance for a variant instance in place.

Traps: three same-name Crate instances told apart only by world rotation, a sibling instance with
overrides that must not be applied to the base prefab, gameobject_duplicate losing the prefab link,
new instances landing at the end of the parent, a rotated parent (world vs local), and the variant's
mass that belongs in the asset rather than as an instance override.
"""
import hashlib
import os
import re

from harness import checks as K
from harness import uyaml as Y
from harness.task import Negative, Task
from harness.verify import floats, quat_close

ROW_POS = (3.0, 0.0, 2.0)
ROW_ROT_Y = 15.0
TARGET_ROT = 75.0
HEAVY_MASS = 50.0


def _ref(value):
    """{fileID, guid} from a flow mapping; uyaml returns the first line only when Unity wraps one."""
    if isinstance(value, dict):
        return {"fileID": str(value.get("fileID")), "guid": value.get("guid")}
    text = str(value or "")
    fid, guid = re.search(r"fileID:\s*(-?\d+)", text), re.search(r"guid:\s*([0-9a-f]+)", text)
    return {"fileID": fid.group(1) if fid else None, "guid": guid.group(1) if guid else None}


def _file_hash(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


class T5(Task):
    id = "T5"
    title = "Prefab variant replacement keeping pose and sibling index"
    tier = "composite"
    timeout_s = 1800
    budget_usd = 40.0

    def assertions(self):
        return [
            ("V1", 1.0, False, "A/Prefabs/Crate_Heavy.prefab exists and is a variant of Crate.prefab"),
            ("V2", 1.5, False, "the variant asset overrides the root Rigidbody mass to 50"),
            ("V3", 1.5, True, "Crate.prefab unchanged (file bytes and GUID)"),
            ("V4", 0.5, False, "the old 75-degree Crate is gone"),
            ("V5", 1.0, False, "the other crates (values, mass) and pallets unchanged"),
            ("V6", 1.5, False, "one new Crate_Heavy instance named Crate at the old sibling index under Row"),
            ("V7", 1.5, False, "new instance has the old local position/rotation/scale (same world pose)"),
            ("V8", 1.0, True, "no side effects outside R/A and no scene save"),
            ("V9", 0.5, False, "answer matches the actual state"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        root = ctx.scope.root_iid
        base = ctx.asset("Prefabs", "Crate.prefab")
        src = c.write("gameobject_create", name="Crate", primitiveType="Cube", parentInstanceId=root)["instanceId"]
        c.write("component_add", instanceId=src, componentType="Rigidbody")
        c.write("component_set_property", instanceId=src, componentType="Rigidbody", propertyName="mass", value="5")
        c.write("gameobject_create", name="Label", parentInstanceId=src, y=0.7)
        c.write("prefab_create", instanceId=src, savePath=base)
        c.write("gameobject_delete", instanceId=src)

        depot = c.write("gameobject_create", name="Depot", parentInstanceId=root)["instanceId"]
        row = c.write("gameobject_create", name="Row", parentInstanceId=depot, x=ROW_POS[0], y=ROW_POS[1],
                      z=ROW_POS[2], rotY=ROW_ROT_Y)["instanceId"]
        # Crates sit at sibling indices 1, 2, 4; the target is at 1 or 2 so "append at end" is always wrong,
        # and the crate carrying overrides is never the target.
        target_index = rng.choice([1, 2])
        others = [i for i in (1, 2, 4) if i != target_index]
        override_index = rng.choice(others)
        rest_rots = [30.0, 120.0]
        rng.shuffle(rest_rots)
        rot_by_index = {target_index: TARGET_ROT, others[0]: rest_rots[0], others[1]: rest_rots[1]}
        scales = [0.8, 1.1, 0.95]
        rng.shuffle(scales)
        scale_by_index = dict(zip((1, 2, 4), scales))
        scale_by_index[override_index] = 1.2
        children = []
        for index in range(5):
            x = -4.0 + 2.0 * index
            if index in (0, 3):
                name = "Pallet" if index == 0 else "Pallet2"
                iid = c.write("gameobject_create", name=name, primitiveType="Cube", parentInstanceId=row,
                              x=x, y=-0.4, z=0, scaleY=0.2)["instanceId"]
            else:
                iid = c.write("prefab_instantiate", prefabPath=base, parentInstanceId=row, x=x, y=0.5, z=0,
                              name="Crate")["instanceId"]
                s = scale_by_index[index]
                c.write("gameobject_set_transform", instanceId=iid, rotX=0, rotY=rot_by_index[index], rotZ=0,
                        scaleX=s, scaleY=s, scaleZ=s)
                if index == override_index:
                    c.write("component_set_property", instanceId=iid, componentType="Rigidbody",
                            propertyName="mass", value="9")
            children.append(iid)
        ctx.facts.update({
            "basePath": base, "variantPath": ctx.asset("Prefabs", "Crate_Heavy.prefab"), "rowIid": row,
            "rowPath": ctx.path("Depot", "Row"), "rowChildren": children, "targetIndex": target_index,
            "targetIid": children[target_index], "overrideIid": children[override_index],
            "keepIids": [children[i] for i in range(5) if i != target_index],
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        disk = os.path.join(ctx.project_path, f["basePath"])
        f["baseHash"] = _file_hash(disk)
        f["baseGuid"] = Y.meta_guid(disk)
        f["targetTrs"] = K.local_trs(p, f["targetIid"])
        f["keepC0"] = {str(iid): self._keep_state(p, iid) for iid in f["keepIids"]}

    @staticmethod
    def _keep_state(p, iid):
        state = K.go_state(p, iid)
        if state and "Rigidbody" in (state.get("components") or []):
            state["mass"] = (p.ser(iid, "Rigidbody") or {}).get("m_Mass")
        return state

    def allowed_change(self, ctx, rec):
        # Edits inside the A/Prefabs assets (prefab_set_property, prefab_apply) are journaled as scene-less
        # changes whose path is the asset's own hierarchy ("Crate_Heavy/...") without an assetPath; changes
        # to Crate.prefab are judged by V3 (file hash) instead.
        path = rec.get("path") or ""
        return (rec.get("type") == "scene_changes" and not rec.get("scenePath") and not rec.get("assetPath")
                and path.split("/")[0] in ("Crate", "Crate_Heavy"))

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("`{row}` 下有三个同名的 `Crate`，都是 `{base}` 的实例。"
                "① 以 `{base}` 为基础创建预制体变体 `{variant}`，在变体资产中把根物体 Rigidbody 的质量设为 50。"
                "② 把世界旋转 Y 约为 75° 的那个 Crate 换成 `Crate_Heavy` 的实例：新实例的世界位置、旋转、缩放与旧的一致，"
                "父物体和同级顺序（sibling index）不变，名字仍为 `Crate`，然后删除旧的。"
                "不得把其他 Crate 的覆盖应用（Apply）到预制体，`{base}` 不能有任何改动。不要修改其他对象和资产。").format(
            row=f["rowPath"], base=f["basePath"], variant=f["variantPath"])

    def answer_schema(self):
        return ('{"variantPath": "变体资产路径", "newInstanceId": 整数, "siblingIndex": 整数, '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _variant_facts(self, ctx):
        f = ctx.facts
        disk = os.path.join(ctx.project_path, f["variantPath"])
        if not os.path.exists(disk):
            return None
        base_docs = Y.load(os.path.join(ctx.project_path, f["basePath"]))
        transforms = [d for d in Y.by_type(base_docs, "Transform") if _ref(d.get("m_Father"))["fileID"] == "0"]
        root_go = _ref(transforms[0].get("m_GameObject"))["fileID"] if len(transforms) == 1 else None
        rb = [d.file_id for d in Y.by_type(base_docs, "Rigidbody") if root_go and
              _ref(d.get("m_GameObject"))["fileID"] == root_go]
        docs = Y.load(disk)
        instances = Y.by_type(docs, "PrefabInstance")
        roots = [i for i in instances if _ref(i.get("m_Modification", "m_TransformParent"))["fileID"] == "0"]
        source = _ref(roots[0].get("m_SourcePrefab"))["guid"] if len(roots) == 1 else None
        mods = [m for i in roots for m in i.get("m_Modification", "m_Modifications", default=[]) or []
                if isinstance(m, dict)]
        mass = [m.get("value") for m in mods if m.get("propertyPath") == "m_Mass" and
                _ref(m.get("target"))["guid"] == f["baseGuid"] and
                rb and _ref(m.get("target"))["fileID"] == str(rb[0])]
        return {"source": source, "instances": len(instances), "mass": mass, "rootRigidbody": rb}

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        variant = self._variant_facts(ctx)
        card.check("V1", bool(variant) and variant["source"] == f["baseGuid"], expected=f["baseGuid"],
                   actual=variant)
        masses = floats((variant or {}).get("mass") or [])
        card.check("V2", len(masses) == 1 and abs(masses[0] - HEAVY_MASS) < 1e-3, expected=HEAVY_MASS,
                   actual=(variant or {}).get("mass"))

        disk = os.path.join(ctx.project_path, f["basePath"])
        hash_now = _file_hash(disk) if os.path.exists(disk) else None
        guid_now = Y.meta_guid(disk) if os.path.exists(disk) else None
        card.check("V3", hash_now == f["baseHash"] and guid_now == f["baseGuid"],
                   actual={"hashSame": hash_now == f["baseHash"], "guid": guid_now})

        card.check("V4", p.go(f["targetIid"]) is None, actual=p.go(f["targetIid"]))

        diffs = {}
        for key, before in f["keepC0"].items():
            ok, diff = K.same(before, self._keep_state(p, int(key)))
            if not ok:
                diffs[key] = diff
        card.check("V5", not diffs, actual=diffs)

        kids = p.children(f["rowIid"])
        added = [(i, ch) for i, ch in enumerate(kids) if ch.get("instanceId") not in set(f["rowChildren"])]
        new = added[0][1] if len(added) == 1 else None
        new_iid = new.get("instanceId") if new else None
        source = None
        if new_iid:
            try:
                source = ctx.c.read("prefab_get_overrides", instanceId=new_iid).get("prefabPath")
            except Exception:
                source = None
        index = added[0][0] if len(added) == 1 else None
        card.check("V6", bool(new) and new.get("name") == "Crate" and source == f["variantPath"] and
                   index == f["targetIndex"],
                   expected={"index": f["targetIndex"], "source": f["variantPath"]},
                   actual={"added": [(i, ch.get("name")) for i, ch in added], "source": source})

        trs = K.local_trs(p, new_iid) if new_iid else {}
        old = f["targetTrs"]
        card.check("V7", bool(trs) and K.close_tuple(trs.get("position"), old["position"]) and
                   quat_close(trs.get("rotation"), old["rotation"], 0.1) and
                   K.close_tuple(trs.get("scale"), old["scale"]), expected=old, actual=trs)

        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("V8", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

        ans_ok = (new_iid is not None and K.as_int(K.answer(ctx, "newInstanceId")) == new_iid and
                  K.as_int(K.answer(ctx, "siblingIndex")) == index and
                  K.norm_path(K.answer(ctx, "variantPath")) == f["variantPath"] and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("V9", ans_ok, actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iid):
        kids = [ch.get("instanceId") for ch in ctx.probe.children(ctx.facts["rowIid"])]
        return {"variantPath": ctx.facts["variantPath"], "newInstanceId": iid,
                "siblingIndex": kids.index(iid) if iid in kids else None, "declaredComplete": True}

    def _variant(self, ctx, mass=True):
        f = ctx.facts
        ctx.c.write("prefab_create_variant", sourcePrefabPath=f["basePath"], variantPath=f["variantPath"])
        if mass:
            ctx.c.write("prefab_set_property", prefabPath=f["variantPath"], componentType="Rigidbody",
                        propertyName="mass", value=str(int(HEAVY_MASS)))

    def _replace(self, ctx, rotate=True, sibling=True, delete=True, duplicate=False):
        c, f = ctx.c, ctx.facts
        old = f["targetIid"]
        info = ctx.probe.go(old)
        trs = f["targetTrs"]
        pos = trs["position"]
        if duplicate:
            iid = c.write("gameobject_duplicate", instanceId=old)["copyInstanceId"]
            c.write("gameobject_rename", instanceId=iid, newName="Crate")
        else:
            iid = c.write("prefab_instantiate", prefabPath=f["variantPath"], parentInstanceId=f["rowIid"],
                          x=pos[0], y=pos[1], z=pos[2], name="Crate")["instanceId"]
            rot = floats(info.get("rotation"))
            args = {"scaleX": trs["scale"][0], "scaleY": trs["scale"][1], "scaleZ": trs["scale"][2]}
            if rotate:
                args.update(rotX=rot[0], rotY=rot[1], rotZ=rot[2])
            c.write("gameobject_set_transform", instanceId=iid, **args)
        if delete:
            c.write("gameobject_delete", instanceId=old)
        if sibling:
            c.write("gameobject_set_sibling_index", instanceId=iid, index=f["targetIndex"])
        return iid

    def reference(self, ctx):
        self._variant(ctx)
        return self._answer(ctx, self._replace(ctx))

    def negatives(self):
        def mass_override(ctx):
            self._variant(ctx, mass=False)
            iid = self._replace(ctx)
            ctx.c.write("component_set_property", instanceId=iid, componentType="Rigidbody", propertyName="mass",
                        value=str(int(HEAVY_MASS)))
            return self._answer(ctx, iid)

        def apply_overrides(ctx):
            ans = self.reference(ctx)
            ctx.c.write("prefab_apply", instanceId=ctx.facts["overrideIid"])
            return ans

        def new_at_end(ctx):
            self._variant(ctx)
            return self._answer(ctx, self._replace(ctx, sibling=False))

        def no_rotation(ctx):
            self._variant(ctx)
            return self._answer(ctx, self._replace(ctx, rotate=False))

        def duplicate_instead(ctx):
            self._variant(ctx)
            return self._answer(ctx, self._replace(ctx, duplicate=True))

        def old_not_deleted(ctx):
            self._variant(ctx)
            return self._answer(ctx, self._replace(ctx, delete=False))

        def wrong_report(ctx):
            ans = self.reference(ctx)
            ans["siblingIndex"] = 4
            return ans

        return [
            Negative("mass_override", mass_override, {"V2"}),
            Negative("apply_overrides", apply_overrides, {"V3", "V5"}),
            Negative("new_at_end", new_at_end, {"V6"}),
            Negative("no_rotation", no_rotation, {"V7"}),
            Negative("duplicate_instead", duplicate_instead, {"V6"}),
            Negative("old_not_deleted", old_not_deleted, {"V4"}),
            Negative("wrong_report", wrong_report, {"V9"}),
        ]

    def alternatives(self):
        def world_then_parent(ctx):
            c, f = ctx.c, ctx.facts
            c.write("prefab_create_variant", sourcePrefabPath=f["basePath"], variantPath=f["variantPath"])
            c.write("prefab_set_property", prefabPath=f["variantPath"], componentType="Rigidbody",
                    propertyName="mass", value="50")
            info = ctx.probe.go(f["targetIid"])
            pos, rot = floats(info.get("position")), floats(info.get("rotation"))
            scale = f["targetTrs"]["scale"]
            iid = c.write("prefab_instantiate", prefabPath=f["variantPath"], x=pos[0], y=pos[1], z=pos[2])["instanceId"]
            c.write("gameobject_set_transform", instanceId=iid, rotX=rot[0], rotY=rot[1], rotZ=rot[2],
                    scaleX=scale[0], scaleY=scale[1], scaleZ=scale[2])
            c.write("gameobject_set_parent", childInstanceId=iid, parentInstanceId=f["rowIid"])
            c.write("gameobject_set_sibling_index", instanceId=iid, index=f["targetIndex"])
            c.write("gameobject_rename", instanceId=iid, newName="Crate")
            c.write("gameobject_delete", instanceId=f["targetIid"])
            return self._answer(ctx, iid)

        return [("world_then_parent", world_then_parent)]


TASK = T5()

# Producer:Betsy
