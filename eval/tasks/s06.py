"""S06' Backslash save path, assign to one renderer, shared material untouched (basic tier).

Traps: the save directory is given Windows-style with backslashes and does not exist yet;
DisplayCube and ControlCube share PanelBase.mat, so colouring "DisplayCube's material" before the
swap edits the shared asset; URP Lit's main colour is _BaseColor (_Color is a hidden legacy prop).
"""
import os

from harness import checks as K
from harness.task import Negative, Task

GRAY = (0.5, 0.5, 0.5, 1.0)
BLUE = (0.1, 0.4, 0.8, 1.0)


class S06(Task):
    id = "S06"
    title = "Nested material from a backslash path"
    tier = "basic"
    timeout_s = 420
    budget_usd = 6.0

    def assertions(self):
        return [
            ("D1", 2.0, False, "A/Materials/Nested/BluePanel.mat exists as a Material, folders registered"),
            ("D2", 2.5, False, "the new material uses URP Lit with _BaseColor (0.1, 0.4, 0.8, 1)"),
            ("D3", 2.0, False, "DisplayCube has one slot and slot 0 is A/Materials/Nested/BluePanel.mat"),
            ("D4", 2.0, True, "PanelBase.mat unchanged and ControlCube still uses it"),
            ("D5", 0.5, False, "answer fields match the actual state"),
            ("D6", 1.0, False, "no folder/file with a literal backslash, nothing outside R/A, no scene save"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        base = ctx.asset("Shared", "PanelBase.mat")
        c.write("asset_create_folder", folderPath=ctx.asset("Shared"))
        c.write("material_create", name="PanelBase", savePath=base)
        c.write("material_set_color", path=base, propertyName="_BaseColor", r=GRAY[0], g=GRAY[1], b=GRAY[2],
                a=GRAY[3])
        display = c.write("gameobject_create", name="Display", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        names = ["DisplayCube", "ControlCube"]
        rng.shuffle(names)
        iids = {}
        for i, name in enumerate(names):
            iids[name] = c.write("gameobject_create", name=name, primitiveType="Cube", parentInstanceId=display,
                                 x=i * 2 - 1, y=0.5, z=0)["instanceId"]
            c.write("material_assign", instanceId=iids[name], materialPath=base)
        ctx.facts.update({
            "basePath": base, "displayIid": iids["DisplayCube"], "controlIid": iids["ControlCube"],
            "displayPath": ctx.path("Display", "DisplayCube"), "controlPath": ctx.path("Display", "ControlCube"),
            "saveDirWin": ctx.asset("Materials", "Nested").replace("/", "\\"),
            "materialPath": ctx.asset("Materials", "Nested", "BluePanel.mat"),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["baseC0"] = K.material_state(p, f["basePath"])
        f["controlC0"] = K.go_state(p, f["controlIid"])
        f["displayC0"] = K.go_state(p, f["displayIid"])

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("新建材质 `BluePanel`，保存目录为 `{dir}`（Windows 风格路径，目录尚不存在）。主颜色设为 RGBA (0.1, 0.4, 0.8, 1)。"
                "只把它赋给 `{display}`；`{control}` 的外观和它们原来共用的材质 `{base}` 都不能改变。"
                ).format(dir=f["saveDirWin"], display=f["displayPath"], control=f["controlPath"], base=f["basePath"])

    def answer_schema(self):
        return ('{"materialPath": "新材质的资产路径（用正斜杠）", "colorProperty": "写入主颜色的着色器属性名", '
                '"readback": [r, g, b, a] 写入后读回的颜色, "declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _candidate(self, ctx):
        """The new material: the expected asset if present, else whatever non-PanelBase material sits in
        DisplayCube's slot 0 (saved elsewhere or in memory), so D2 grades colour/shader independently of D1/D3."""
        p, f = ctx.probe, ctx.facts
        if p.asset(f["materialPath"]):
            return K.material_state(p, f["materialPath"]), f["materialPath"]
        info = p.material(iid=f["displayIid"]) or {}
        if info.get("materialPath") == f["basePath"]:
            return None, None
        props = info.get("properties") or {}
        state = {"shader": info.get("shader"),
                 "colors": {c["name"]: c.get("value") for c in props.get("colors") or []}}
        return state, info.get("materialPath")

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        path = f["materialPath"]
        asset = p.asset(path)
        folders = [p.asset(ctx.asset("Materials")), p.asset(ctx.asset("Materials", "Nested"))]
        card.check("D1", bool(asset) and asset.get("type") == "Material" and all(folders),
                   actual={"asset": asset, "folders": folders})
        state, _cand_path = self._candidate(ctx)
        want_shader = (ctx.env or {}).get("defaultShader")
        color = K.material_color(p, state, "_BaseColor") if state else None
        card.check("D2", bool(state) and state.get("shader") == want_shader and K.close_tuple(color, BLUE),
                   expected={"shader": want_shader, "_BaseColor": BLUE},
                   actual={"shader": (state or {}).get("shader"), "_BaseColor": color})
        slot0 = (p.material(iid=f["displayIid"]) or {}).get("materialPath")
        ser = p.ser(f["displayIid"], "MeshRenderer") or {}
        card.check("D3", slot0 == path and ser.get("m_Materials.Array.size") == "1",
                   expected=path, actual={"slot0": slot0, "slots": ser.get("m_Materials.Array.size")})
        ok_base, diff_base = K.same(f["baseC0"], K.material_state(p, f["basePath"]))
        ok_ctrl, diff_ctrl = K.same(f["controlC0"], K.go_state(p, f["controlIid"]))
        ctrl_mat = (p.material(iid=f["controlIid"]) or {}).get("materialPath")
        ctrl_ser = p.ser(f["controlIid"], "MeshRenderer") or {}
        card.check("D4", ok_base and ok_ctrl and ctrl_mat == f["basePath"] and
                   ctrl_ser.get("m_Materials.Array.size") == "1",
                   actual={"baseDiff": diff_base, "controlDiff": diff_ctrl, "controlMaterial": ctrl_mat})
        card.check("D5", self._answer_ok(ctx, slot0), actual=ctx.answer)
        slashed = self._backslash_entries(ctx)
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("D6", not slashed and not bad and not saves,
                   actual={"backslashEntries": slashed[:10], "violations": bad[:10], "sceneSaves": saves[:5]})

    def _answer_ok(self, ctx, slot0):
        p = ctx.probe
        reported = K.norm_path(K.answer(ctx, "materialPath"))
        if reported != (slot0 or ""):
            return False
        if reported:
            state = K.material_state(p, reported)
        else:
            info = p.material(iid=ctx.facts["displayIid"]) or {}
            state = {"colors": {c["name"]: c.get("value")
                                for c in (info.get("properties") or {}).get("colors") or []}}
        actual = K.material_color(p, state, K.answer(ctx, "colorProperty") or "")
        return K.close_tuple(K.answer(ctx, "readback"), actual, 1e-2) and \
            K.answer(ctx, "declaredComplete") in (True, False)

    def _backslash_entries(self, ctx):
        out = []
        for base, dirs, files in os.walk(os.path.join(ctx.project_path, ctx.A)):
            for name in dirs + files:
                if "\\" in name:
                    out.append(os.path.relpath(os.path.join(base, name), ctx.project_path))
        return out

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, path, prop="_BaseColor"):
        p = ctx.probe
        if path:
            color = K.material_color(p, K.material_state(p, path), prop)
        else:
            info = p.material(iid=ctx.facts["displayIid"]) or {}
            color = K.material_color(p, {"colors": {c["name"]: c.get("value") for c in
                                                 (info.get("properties") or {}).get("colors") or []}}, prop)
        return {"materialPath": path or "", "colorProperty": prop, "readback": list(color or []),
                "declaredComplete": True}

    def _make(self, ctx, save, prop="_BaseColor"):
        c, f = ctx.c, ctx.facts
        res = c.write("material_create", name="BluePanel", savePath=save)
        path = res.get("path")
        c.write("material_set_color", path=path, propertyName=prop, r=BLUE[0], g=BLUE[1], b=BLUE[2], a=BLUE[3])
        c.write("material_assign", instanceId=f["displayIid"], materialPath=path)
        return path

    def reference(self, ctx):
        return self._answer(ctx, self._make(ctx, ctx.facts["saveDirWin"]))

    def negatives(self):
        def color_on_go(ctx):
            c, f = ctx.c, ctx.facts
            path = c.write("material_create", name="BluePanel", savePath=f["saveDirWin"]).get("path")
            c.write("material_set_color", instanceId=f["displayIid"], propertyName="_BaseColor",
                    r=BLUE[0], g=BLUE[1], b=BLUE[2], a=BLUE[3])
            c.write("material_assign", instanceId=f["displayIid"], materialPath=path)
            return self._answer(ctx, path)

        def only_legacy_color(ctx):
            return self._answer(ctx, self._make(ctx, ctx.facts["saveDirWin"], prop="_Color"), prop="_Color")

        def both_cubes(ctx):
            ans = self.reference(ctx)
            ctx.c.write("material_assign", instanceId=ctx.facts["controlIid"], materialPath=ans["materialPath"])
            return ans

        def wrong_folder(ctx):
            return self._answer(ctx, self._make(ctx, ctx.asset("Materials")))

        def flat_name(ctx):
            # backslashes kept inside a single folder name would be a literal-backslash folder; the
            # nearest legal-but-wrong outcome is a sibling folder named after the joined path
            return self._answer(ctx, self._make(ctx, ctx.asset("MaterialsNested")))

        def wrong_report(ctx):
            ans = self.reference(ctx)
            ans["readback"] = [0.5, 0.5, 0.5, 1.0]
            return ans

        def outside_root(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_create", name="StrayPanel", primitiveType="Cube")
            return ans

        return [
            Negative("color_on_go", color_on_go, {"D2", "D4"}),
            Negative("only__Color", only_legacy_color, {"D2"}),
            Negative("both_cubes", both_cubes, {"D4"}),
            Negative("wrong_folder", wrong_folder, {"D1", "D3"}),
            Negative("flat_name", flat_name, {"D1", "D3"}),
            Negative("wrong_report", wrong_report, {"D5"}),
            Negative("outside_root", outside_root, {"D6"}),
        ]

    def alternatives(self):
        def forward_slashes(ctx):
            return self._answer(ctx, self._make(ctx, ctx.asset("Materials", "Nested", "BluePanel.mat")))

        def duplicate_base(ctx):
            c, f = ctx.c, ctx.facts
            c.write("asset_create_folder", folderPath=ctx.asset("Materials"))
            c.write("asset_create_folder", folderPath=ctx.asset("Materials", "Nested"))
            path = c.write("material_duplicate", sourcePath=f["basePath"], newName="BluePanel",
                           savePath=ctx.asset("Materials", "Nested")).get("path")
            c.write("material_set_color", path=path, propertyName="_BaseColor", r=BLUE[0], g=BLUE[1], b=BLUE[2],
                    a=BLUE[3])
            c.write("material_assign", instanceId=f["displayIid"], materialPath=path)
            return self._answer(ctx, path)

        return [("forward_slashes", forward_slashes), ("duplicate_base", duplicate_base)]


TASK = S06()

# Producer:Betsy
