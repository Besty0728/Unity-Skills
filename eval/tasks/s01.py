"""S01+ Crate cube with a new URP material (basic tier).

Traps: world vs local placement under an offset parent, a same-named decoy material at another
path that must not be reused or edited, the URP Lit shader, saving the material as an asset.
"""
from harness import checks as K
from harness.task import Negative, Task

CRATE_POS = (0.0, 0.5, 0.0)
LEGACY_COLOR = (0.3, 0.6, 0.2, 1.0)


class S01(Task):
    id = "S01"
    title = "Crate cube with a new material"
    tier = "basic"
    timeout_s = 600
    budget_usd = 15.0

    def assertions(self):
        return [
            ("A1", 1.5, False, "exactly one new Crate under Props with Transform/MeshFilter/MeshRenderer/BoxCollider"),
            ("A2", 1.5, False, "world position (0, 0.5, 0)"),
            ("A3", 0.5, False, "identity rotation and unit local scale"),
            ("A4", 0.5, False, "mesh is the built-in Cube"),
            ("A5", 1.5, False, "material asset at A/Materials/CrateMaterial.mat uses the pipeline's default Lit shader"),
            ("A6", 2.0, False, "Crate has exactly one slot and slot 0 is that asset"),
            ("A7", 1.0, True, "decoy legacy material and the Control object unchanged"),
            ("A8", 1.0, True, "no side effects outside R/A and no scene save"),
            ("A9", 0.5, False, "answer fields match the actual state"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, p, rng = ctx.c, ctx.probe, ctx.rng
        props_pos = (rng.choice([3, 4, -4, 5]), 0, rng.choice([-2, 2, -3]))
        props = c.write("gameobject_create", name="Props", parentInstanceId=ctx.scope.root_iid,
                        x=props_pos[0], y=props_pos[1], z=props_pos[2])
        legacy = ctx.asset("Legacy", "CrateMaterial.mat")
        c.write("material_create", name="CrateMaterial", savePath=legacy)
        c.write("material_set_color", path=legacy, propertyName="_BaseColor", r=LEGACY_COLOR[0], g=LEGACY_COLOR[1],
                b=LEGACY_COLOR[2], a=LEGACY_COLOR[3])
        control = c.write("gameobject_create", name="Control", primitiveType="Sphere",
                          parentInstanceId=props["instanceId"], x=1.5, y=0.5, z=0)
        c.write("material_assign", instanceId=control["instanceId"], materialPath=legacy)
        ctx.facts.update({
            "propsIid": props["instanceId"], "propsPath": ctx.path("Props"), "controlIid": control["instanceId"],
            "legacyPath": legacy, "materialPath": ctx.asset("Materials", "CrateMaterial.mat"),
        })

    def after_fixture(self, ctx):
        p = ctx.probe
        ctx.facts["propsChildrenC0"] = [ch["instanceId"] for ch in p.children(ctx.facts["propsIid"])]
        ctx.facts["legacyC0"] = K.material_state(p, ctx.facts["legacyPath"])
        ctx.facts["controlC0"] = K.go_state(p, ctx.facts["controlIid"])
        ctx.facts["controlMatC0"] = (p.material(iid=ctx.facts["controlIid"]) or {}).get("materialPath")

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("在 `{props}` 下新建名为 `Crate` 的立方体，世界坐标 (0, 0.5, 0)，旋转为 0，本地缩放为 1。"
                "新建材质 `CrateMaterial`，使用本工程渲染管线的默认 Lit 着色器，保存为 `{mat}`，并赋给 Crate。"
                "不要修改其他对象和资产。").format(props=ctx.facts["propsPath"], mat=ctx.facts["materialPath"])

    def answer_schema(self):
        return '{"objectPath": "Crate 的完整层级路径", "objectInstanceId": 整数, "materialPath": "材质资产路径", ' \
               '"declaredComplete": true 或 false}'

    # ------------------------------------------------------------------ verification

    def _crate(self, ctx):
        added = K.new_children(ctx.probe, ctx.facts["propsIid"], ctx.facts["propsChildrenC0"])
        crates = [a for a in added if a.get("name") == "Crate"]
        reported = K.as_int(K.answer(ctx, "objectInstanceId"))
        target = next((a for a in crates if a.get("instanceId") == reported), crates[0] if crates else None)
        return added, crates, target

    def verify(self, ctx, card):
        p = ctx.probe
        added, crates, target = self._crate(ctx)
        iid = target["instanceId"] if target else None
        info = p.go(iid) if iid else None
        comps = sorted((info or {}).get("components") or [])
        card.check("A1", len(added) == 1 and len(crates) == 1 and
                   comps == sorted(["Transform", "MeshFilter", "MeshRenderer", "BoxCollider"]),
                   expected="1 new Crate", actual={"added": [a.get("name") for a in added], "components": comps})
        card.check("A2", bool(info) and K.close_tuple(info.get("position"), CRATE_POS),
                   expected=CRATE_POS, actual=(info or {}).get("position"))
        trs = K.local_trs(p, iid) if iid else {}
        card.check("A3", bool(trs) and K.identity_rotation(trs.get("rotation")) and
                   K.close_tuple(trs.get("scale"), (1, 1, 1)), actual=trs)
        mesh = ctx.c.read("model_get_mesh_info", instanceId=iid) if iid else {}
        card.check("A4", mesh.get("name") == "Cube" and mesh.get("vertexCount") == 24,
                   actual={k: mesh.get(k) for k in ("name", "vertexCount")})
        path = ctx.facts["materialPath"]
        asset = p.asset(path)
        mat = p.material(path=path) if asset else None
        want_shader = (ctx.env or {}).get("defaultShader")
        card.check("A5", bool(asset) and asset.get("type") == "Material" and (mat or {}).get("shader") == want_shader,
                   expected={"type": "Material", "shader": want_shader},
                   actual={"asset": asset, "shader": (mat or {}).get("shader")})
        slot_path = (p.material(iid=iid) or {}).get("materialPath") if iid else None
        ser = p.ser(iid, "MeshRenderer") if iid else {}
        card.check("A6", slot_path == path and (ser or {}).get("m_Materials.Array.size") == "1",
                   expected=path, actual={"slot0": slot_path, "slots": (ser or {}).get("m_Materials.Array.size")})
        legacy_now = K.material_state(p, ctx.facts["legacyPath"])
        ok_legacy, diff_legacy = K.same(ctx.facts["legacyC0"], legacy_now)
        control_now = K.go_state(p, ctx.facts["controlIid"])
        ok_control, diff_control = K.same(ctx.facts["controlC0"], control_now)
        control_mat = (p.material(iid=ctx.facts["controlIid"]) or {}).get("materialPath")
        card.check("A7", ok_legacy and ok_control and control_mat == ctx.facts["controlMatC0"],
                   actual={"legacyDiff": diff_legacy, "controlDiff": diff_control, "controlMaterial": control_mat})
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("A8", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})
        ans_ok = (target is not None and K.as_int(K.answer(ctx, "objectInstanceId")) == iid and
                  K.norm_path(K.answer(ctx, "objectPath")) == (info or {}).get("path") and
                  K.norm_path(K.answer(ctx, "materialPath")) == (slot_path or "") and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("A9", ans_ok, actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iid):
        info = ctx.probe.go(iid) or {}
        slot = (ctx.probe.material(iid=iid) or {}).get("materialPath")
        return {"objectPath": info.get("path"), "objectInstanceId": iid, "materialPath": slot, "declaredComplete": True}

    def _create_crate(self, ctx, world=True, parent=True):
        args = dict(name="Crate", primitiveType="Cube", x=CRATE_POS[0], y=CRATE_POS[1], z=CRATE_POS[2])
        if parent:
            args["parentInstanceId"] = ctx.facts["propsIid"]
        if world:
            args["space"] = "world"
        return ctx.c.write("gameobject_create", **args)["instanceId"]

    def reference(self, ctx):
        res = ctx.c.batch([
            {"skill": "gameobject_create", "args": {"name": "Crate", "primitiveType": "Cube",
                                                    "parentInstanceId": ctx.facts["propsIid"],
                                                    "x": 0, "y": 0.5, "z": 0, "space": "world"}},
            {"skill": "material_create", "args": {"name": "CrateMaterial", "savePath": ctx.facts["materialPath"]}},
            {"skill": "material_assign", "args": {"instanceId": {"$ref": "$0.instanceId"},
                                                  "materialPath": ctx.facts["materialPath"]}},
        ])
        iid = res["results"][0]["result"]["instanceId"]
        return self._answer(ctx, iid)

    def negatives(self):
        def local_coords(ctx):
            iid = self._create_crate(ctx, world=False)
            ctx.c.write("material_create", name="CrateMaterial", savePath=ctx.facts["materialPath"])
            ctx.c.write("material_assign", instanceId=iid, materialPath=ctx.facts["materialPath"])
            return self._answer(ctx, iid)

        def reuse_legacy(ctx):
            iid = self._create_crate(ctx)
            ctx.c.write("material_assign", instanceId=iid, materialPath=ctx.facts["legacyPath"])
            return self._answer(ctx, iid)

        def not_assigned(ctx):
            iid = self._create_crate(ctx)
            ctx.c.write("material_create", name="CrateMaterial", savePath=ctx.facts["materialPath"])
            return self._answer(ctx, iid)

        def nested_path(ctx):
            iid = self._create_crate(ctx)
            path = ctx.asset("Materials", "Nested", "CrateMaterial.mat")
            ctx.c.write("material_create", name="CrateMaterial", savePath=path)
            ctx.c.write("material_assign", instanceId=iid, materialPath=path)
            return self._answer(ctx, iid)

        def two_crates(ctx):
            ans = self.reference(ctx)
            self._create_crate(ctx)
            return ans

        def edit_legacy(ctx):
            ans = self.reference(ctx)
            ctx.c.write("material_set_color", path=ctx.facts["legacyPath"], propertyName="_BaseColor",
                        r=1, g=0, b=0, a=1)
            return ans

        def standard_shader(ctx):
            iid = self._create_crate(ctx)
            ctx.c.write("material_create", name="CrateMaterial", shaderName="Standard",
                        savePath=ctx.facts["materialPath"])
            ctx.c.write("material_assign", instanceId=iid, materialPath=ctx.facts["materialPath"])
            return self._answer(ctx, iid)

        def wrong_report(ctx):
            ans = self.reference(ctx)
            ans["objectInstanceId"] = ctx.facts["propsIid"]
            return ans

        def outside_root(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_create", name="StrayHelper")
            return ans

        return [
            Negative("local_coords", local_coords, {"A2"}),
            Negative("reuse_legacy", reuse_legacy, {"A5", "A6"}),
            Negative("not_assigned", not_assigned, {"A6"}),
            Negative("nested_path", nested_path, {"A5", "A6"}),
            Negative("two_crates", two_crates, {"A1"}),
            Negative("edit_legacy", edit_legacy, {"A7"}),
            Negative("standard_shader", standard_shader, {"A5"}),
            Negative("wrong_report", wrong_report, {"A9"}),
            Negative("outside_root", outside_root, {"A8"}),
        ]

    def alternatives(self):
        def root_then_move(ctx):
            iid = self._create_crate(ctx, world=True, parent=False)
            ctx.c.write("gameobject_set_parent", childInstanceId=iid, parentInstanceId=ctx.facts["propsIid"])
            ctx.c.write("gameobject_set_transform", instanceId=iid, posX=0, posY=0.5, posZ=0)
            ctx.c.write("material_create", name="CrateMaterial", savePath=ctx.asset("Materials"))
            ctx.c.write("component_set_property", instanceId=iid, componentType="MeshRenderer",
                        propertyName="sharedMaterial", assetPath=ctx.facts["materialPath"])
            return self._answer(ctx, iid)

        return [("root_then_move", root_then_move)]


TASK = S01()

# Producer:Betsy
