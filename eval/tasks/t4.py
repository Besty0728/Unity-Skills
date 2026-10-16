"""T4 Multi-slot material: recolor one car's body without touching the shared material, emissive slot 1.

Traps: Car and CarTwin share BodyPaint in slot 0 (recoloring it or editing through the renderer
changes the twin), material_assign only ever writes slot 0, URP's main color is _BaseColor (_Color
is a hidden legacy property), and emission needs both the keyword and HDR color.
"""
import os

from harness import checks as K
from harness.task import Negative, Task
from harness.verify import parse_ref

BODY_PAINT = (0.2, 0.2, 0.2, 1.0)
TRIM = (0.9, 0.9, 0.85, 1.0)
RED = (0.8, 0.1, 0.1, 1.0)
GLOW_HDR = (2.0, 1.0, 0.0)


def _token(rng):
    return "".join(rng.choice("ABCDEFGHJKMNPQRSTUVWXYZ") for _ in range(3))


class T4(Task):
    id = "T4"
    title = "Multi-slot material with a shared-material trap"
    tier = "composite"
    timeout_s = 1800
    budget_usd = 40.0

    def assertions(self):
        return [
            ("M1", 1.0, False, "Car still has exactly 2 material slots"),
            ("M2", 1.5, False, "Car slot 0 is a new saved asset under A: URP Lit, _BaseColor (0.8, 0.1, 0.1, 1)"),
            ("M3", 1.5, True, "BodyPaint asset unchanged"),
            ("M4", 0.5, False, "Trim and GlowOld assets unchanged"),
            ("M5", 1.0, False, "CarTwin keeps [BodyPaint, Trim]; both cars' transforms/components unchanged"),
            ("M6", 1.5, False, "Car slot 1 is Glow_<k>, the only Glow_<k> material in the project"),
            ("M7", 1.0, False, "A/Materials/Glow_<k>.mat exists and uses URP Lit"),
            ("M8", 0.5, False, "Glow emission enabled (_EMISSION on, giFlags not EmissiveIsBlack)"),
            ("M9", 0.5, False, "Glow _EmissionColor rgb == (2, 1, 0)"),
            ("M10", 0.5, True, "no side effects outside R/A and no scene save"),
            ("M11", 0.5, False, "answer matches the actual state"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        k = _token(rng)
        body, trim, old = ctx.asset("Shared", "BodyPaint.mat"), ctx.asset("Shared", "Trim.mat"), \
            ctx.asset("Legacy", "GlowOld.mat")
        for name, path, color in (("BodyPaint", body, BODY_PAINT), ("Trim", trim, TRIM), ("GlowOld", old, None)):
            c.write("material_create", name=name, savePath=path)
            if color:
                c.write("material_set_color", path=path, propertyName="_BaseColor", r=color[0], g=color[1],
                        b=color[2], a=color[3])
        c.write("material_set_emission", path=old, r=1, g=0.5, b=0, intensity=1.5)
        showroom = c.write("gameobject_create", name="Showroom", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        cars = {}
        for name, x in (("Car", -1.5), ("CarTwin", 1.5)):
            iid = c.write("gameobject_create", name=name, primitiveType="Cube", parentInstanceId=showroom,
                          x=x, y=0.5, z=0)["instanceId"]
            self._set_slots(ctx, iid, [body, trim])
            cars[name] = iid
        ctx.facts.update({
            "k": k, "carIid": cars["Car"], "twinIid": cars["CarTwin"], "showroomPath": ctx.path("Showroom"),
            "bodyPath": body, "trimPath": trim, "oldPath": old,
            "glowName": "Glow_%s" % k, "glowPath": ctx.asset("Materials", "Glow_%s.mat" % k),
        })

    @staticmethod
    def _set_slots(ctx, iid, paths):
        ctx.c.write("component_set_serialized_property", instanceId=iid, componentType="MeshRenderer",
                    propertyPath="m_Materials.Array.size", value=str(len(paths)))
        for i, path in enumerate(paths):
            ctx.c.write("component_set_serialized_property", instanceId=iid, componentType="MeshRenderer",
                        propertyPath="m_Materials.Array.data[%d]" % i, assetPath=path)

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["assetsC0"] = {key: K.material_state(p, f[key]) for key in ("bodyPath", "trimPath", "oldPath")}
        f["carC0"] = K.go_state(p, f["carIid"])
        f["twinC0"] = {"go": K.go_state(p, f["twinIid"]), "slots": self._slots(p, f["twinIid"])}

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("`{room}` 下的 `Car` 和 `CarTwin` 两个立方体各有两个材质槽：槽 0 是共享的车身漆 `{body}`，槽 1 是 `{trim}`。"
                "① 把 `Car` 的车身颜色（槽 0，URP 主颜色）改为 RGBA (0.8, 0.1, 0.1, 1)；`CarTwin` 的外观、`{body}` 和 `{trim}` 都不能变，"
                "新材质需保存为 A 下的资产（路径自定）。"
                "② 新建 URP Lit 材质 `{glow}`，保存为 `{glowpath}`，开启自发光，HDR 自发光颜色为 (2, 1, 0)，"
                "用它替换 `Car` 的槽 1。`Car` 仍保持两个材质槽。不要修改其他对象和资产。").format(
            room=f["showroomPath"], body=f["bodyPath"], trim=f["trimPath"], glow=f["glowName"], glowpath=f["glowPath"])

    def answer_schema(self):
        return ('{"bodyMaterialPath": "Car 槽 0 材质的资产路径", "glowMaterialPath": "Car 槽 1 材质的资产路径", '
                '"declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    @staticmethod
    def _slots(p, iid):
        ser = p.ser(iid, "MeshRenderer") or {}
        size = K.as_int(ser.get("m_Materials.Array.size"))
        refs = [parse_ref(ser.get("m_Materials.Array.data[%d]" % i)) for i in range(size or 0)]
        return {"size": size, "names": [r[2] if r else None for r in refs],
                "slot0": (p.material(iid=iid) or {}).get("materialPath")}

    def _glow_copies(self, ctx):
        res = ctx.c.read("asset_find", searchFilter="%s t:Material" % ctx.facts["glowName"], limit=200)
        paths = [a.get("path") for a in res.get("assets") or []]
        return sorted(x for x in paths if x and os.path.splitext(os.path.basename(x))[0] == ctx.facts["glowName"])

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        car = self._slots(p, f["carIid"])
        card.check("M1", car["size"] == 2, expected=2, actual=car)

        fixture_paths = {f["bodyPath"], f["trimPath"], f["oldPath"], f["glowPath"]}
        slot0 = car["slot0"] or ""
        state0 = K.material_state(p, slot0) if slot0 else None
        color0 = K.material_color(p, state0, "_BaseColor")
        want_shader = (ctx.env or {}).get("defaultShader")
        card.check("M2", bool(state0) and slot0 not in fixture_paths and K.norm_path(slot0).startswith(ctx.A + "/")
                   and state0.get("shader") == want_shader and K.close_tuple(color0, RED),
                   expected={"shader": want_shader, "_BaseColor": RED},
                   actual={"slot0": slot0, "shader": (state0 or {}).get("shader"), "_BaseColor": color0})

        now = {key: K.material_state(p, f[key]) for key in ("bodyPath", "trimPath", "oldPath")}
        ok_body, diff_body = K.same(f["assetsC0"]["bodyPath"], now["bodyPath"])
        card.check("M3", ok_body, actual=diff_body)
        ok_trim, diff_trim = K.same(f["assetsC0"]["trimPath"], now["trimPath"])
        ok_old, diff_old = K.same(f["assetsC0"]["oldPath"], now["oldPath"])
        card.check("M4", ok_trim and ok_old, actual={"trim": diff_trim, "glowOld": diff_old})

        twin = {"go": K.go_state(p, f["twinIid"]), "slots": self._slots(p, f["twinIid"])}
        ok_twin, diff_twin = K.same(f["twinC0"], twin)
        ok_car, diff_car = K.same(f["carC0"], K.go_state(p, f["carIid"]))
        card.check("M5", ok_twin and ok_car, actual={"twin": diff_twin, "car": diff_car})

        copies = self._glow_copies(ctx)
        slot1 = car["names"][1] if len(car["names"]) > 1 else None
        card.check("M6", slot1 == f["glowName"] and copies == [f["glowPath"]],
                   expected={"slot1": f["glowName"], "copies": [f["glowPath"]]},
                   actual={"slot1": slot1, "copies": copies})

        glow = p.material(path=f["glowPath"]) if p.asset(f["glowPath"]) else None
        card.check("M7", bool(glow) and glow.get("shader") == want_shader,
                   expected=want_shader, actual=(glow or {}).get("shader"))
        keywords = (glow or {}).get("keywords") or []
        gi = (glow or {}).get("giFlags")
        card.check("M8", "_EMISSION" in keywords and gi not in (None, "EmissiveIsBlack", 4, "4"),
                   actual={"keywords": keywords, "giFlags": gi})
        emission = K.material_color(p, K.material_state(p, f["glowPath"]), "_EmissionColor") if glow else None
        card.check("M9", emission is not None and K.close_tuple(emission[:3], GLOW_HDR, tol=0.01),
                   expected=GLOW_HDR, actual=emission)

        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("M10", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

        ans_ok = (K.norm_path(K.answer(ctx, "bodyMaterialPath")) == slot0 and slot0 and
                  os.path.splitext(os.path.basename(K.norm_path(K.answer(ctx, "glowMaterialPath"))))[0] == slot1 and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("M11", bool(ans_ok), actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, glow_path=None):
        f = ctx.facts
        car = self._slots(ctx.probe, f["carIid"])
        slot1 = car["names"][1] if len(car["names"]) > 1 else None
        if glow_path is None:
            glow_path = f["glowPath"] if slot1 == f["glowName"] else f["trimPath"]
        return {"bodyMaterialPath": car["slot0"], "glowMaterialPath": glow_path, "declaredComplete": True}

    def _body(self, ctx, prop="_BaseColor", duplicate=False):
        c, f = ctx.c, ctx.facts
        path = ctx.asset("Materials", "CarBody_%s.mat" % f["k"])
        if duplicate:
            path = c.write("material_duplicate", sourcePath=f["bodyPath"], newName="CarBody_%s" % f["k"],
                           savePath=ctx.asset("Materials")).get("path") or path
        else:
            c.write("material_create", name="CarBody_%s" % f["k"], savePath=path)
        c.write("material_set_color", path=path, propertyName=prop, r=RED[0], g=RED[1], b=RED[2], a=RED[3])
        return path

    def _glow(self, ctx, path=None):
        path = path or ctx.facts["glowPath"]
        ctx.c.write("material_create", name=ctx.facts["glowName"], savePath=path)
        ctx.c.write("material_set_emission", path=path, r=1, g=0.5, b=0, intensity=2)
        return path

    def _slot(self, ctx, iid, index, path):
        ctx.c.write("component_set_serialized_property", instanceId=iid, componentType="MeshRenderer",
                    propertyPath="m_Materials.Array.data[%d]" % index, assetPath=path)

    def reference(self, ctx):
        f = ctx.facts
        body = self._body(ctx)
        glow = self._glow(ctx)
        self._slot(ctx, f["carIid"], 0, body)
        self._slot(ctx, f["carIid"], 1, glow)
        return self._answer(ctx)

    def negatives(self):
        def recolor_bodypaint(ctx):
            f = ctx.facts
            ctx.c.write("material_set_color", path=f["bodyPath"], propertyName="_BaseColor", r=RED[0], g=RED[1],
                        b=RED[2], a=RED[3])
            self._slot(ctx, f["carIid"], 1, self._glow(ctx))
            return self._answer(ctx)

        def glow_slot0(ctx):
            f = ctx.facts
            self._slot(ctx, f["carIid"], 0, self._body(ctx))
            ctx.c.write("material_assign", instanceId=f["carIid"], materialPath=self._glow(ctx))
            return self._answer(ctx)

        def three_slots(ctx):
            f = ctx.facts
            body, glow = self._body(ctx), self._glow(ctx)
            T4._set_slots(ctx, f["carIid"], [body, glow, f["trimPath"]])
            return self._answer(ctx)

        def emission_off(ctx):
            # URP re-derives _EMISSION from giFlags, so "emission off" is EmissiveIsBlack.
            ans = self.reference(ctx)
            ctx.c.write("material_set_gi_flags", path=ctx.facts["glowPath"], flags="EmissiveIsBlack")
            return ans

        def glow_wrong_path(ctx):
            f = ctx.facts
            wrong = ctx.asset("Glow_%s.mat" % f["k"])
            self._slot(ctx, f["carIid"], 0, self._body(ctx))
            self._slot(ctx, f["carIid"], 1, self._glow(ctx, wrong))
            return self._answer(ctx, wrong)

        def twin_edited(ctx):
            ans = self.reference(ctx)
            self._slot(ctx, ctx.facts["twinIid"], 0, ans["bodyMaterialPath"])
            return ans

        def only_legacy_color(ctx):
            f = ctx.facts
            self._slot(ctx, f["carIid"], 0, self._body(ctx, prop="_Color", duplicate=True))
            self._slot(ctx, f["carIid"], 1, self._glow(ctx))
            return self._answer(ctx)

        def wrong_report(ctx):
            ans = self.reference(ctx)
            ans["bodyMaterialPath"] = ctx.facts["bodyPath"]
            return ans

        return [
            Negative("recolor_bodypaint", recolor_bodypaint, {"M2", "M3"}),
            Negative("glow_slot0", glow_slot0, {"M2", "M6"}),
            Negative("three_slots", three_slots, {"M1"}),
            Negative("emission_off", emission_off, {"M8"}),
            Negative("glow_wrong_path", glow_wrong_path, {"M6", "M7", "M8", "M9"}),
            Negative("twin_edited", twin_edited, {"M5"}),
            Negative("only_legacy_color", only_legacy_color, {"M2"}),
            Negative("wrong_report", wrong_report, {"M11"}),
        ]

    def alternatives(self):
        def duplicate_and_hdr(ctx):
            c, f = ctx.c, ctx.facts
            body = self._body(ctx, duplicate=True)
            c.write("material_create", name=f["glowName"], savePath=ctx.asset("Materials"))
            c.write("material_set_emission", path=f["glowPath"], r=2, g=1, b=0, intensity=1)
            self._slot(ctx, f["carIid"], 1, f["glowPath"])
            self._slot(ctx, f["carIid"], 0, body)
            return self._answer(ctx)

        return [("duplicate_and_hdr", duplicate_and_hdr)]


TASK = T4()

# Producer:Betsy
