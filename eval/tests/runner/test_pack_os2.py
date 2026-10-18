"""Offline OS2 tests using the shared harness and a task-local UGUI layout model.

This models serialized properties and deterministic layout rebuilds, not Unity rendering, native serialization,
Undo or production journal callbacks. Every scratch project lives under eval/runs and is deleted by cleanup.
"""
import json
import os
import re
import tempfile
import unittest
from unittest import mock

from . import pack_offline
from .pack_fake_editor import REPO, RECT_DEFAULT, _fail, _numbers, _g9, q_from_euler
from .pack_offline import Offline, OpsFakeEditor, OpsTaskTests, detail, load_modules
from usk_eval import pack, verifylib as V
from usk_eval.rest import SOLVER_AGENT

SEEDS = (9930, 88117)
SOURCE_ROOT = os.path.join(REPO, "SkillsForUnity", "Editor", "Skills")
SURFACE = {
    "ui_create_canvas": "UISkills.cs", "ui_create_panel": "UISkills.cs", "ui_create_button": "UISkills.cs",
    "ui_create_batch": "UISkills.cs", "ui_layout_children": "UISkills.cs", "ui_set_rect_transform": "UISkills.cs",
    "ui_add_layout_element": "UISkills.cs", "ui_set_text": "UISkills.cs",
    "gameobject_create": "GameObjectSkills.cs", "gameobject_get_info": "GameObjectSkills.cs",
    "gameobject_set_parent": "GameObjectSkills.cs", "gameobject_set_sibling_index": "GameObjectSkills.cs",
    "gameobject_delete": "GameObjectSkills.cs", "gameobject_set_active": "GameObjectSkills.cs",
    "component_add": "ComponentSkills.cs", "component_remove": "ComponentSkills.cs",
    "component_list": "ComponentSkills.cs", "component_get_serialized_properties": "ComponentSkills.cs",
    "component_set_property": "ComponentSkills.cs", "component_set_serialized_property": "ComponentSkills.cs",
    "component_set_enabled": "ComponentSkills.cs", "asset_create_folder": "AssetSkills.cs",
    "asset_set_labels": "AssetImportSkills.cs", "scene_save": "SceneSkills.cs",
}


def declarations():
    out = {}
    for skill, filename in SURFACE.items():
        with open(os.path.join(SOURCE_ROOT, filename), encoding="utf-8-sig") as handle:
            source = handle.read()
        offset = source.index('[UnitySkill("' + skill + '"')
        found = re.search(r"public\s+static\s+object\s+\w+\s*\((.*?)\)\s*\{", source[offset:], re.S)
        if not found:
            raise AssertionError("C# declaration not found: " + skill)
        params = re.sub(r"\[SkillParam\(.*?\)\]", "", found.group(1), flags=re.S)
        out[skill] = set(re.findall(r"\b(?:string|int|bool|float)\??\s+(\w+)", params))
    return out


def short(kind):
    return str(kind).rsplit(".", 1)[-1]


class MenuEditor(OpsFakeEditor):
    """Additional UI skills, qualified component names and serialized layout inputs; no sockets or threads."""

    TEXT_CARRIER = "ugui"
    SIGNATURES = None

    def __init__(self, *args, **kwargs):
        OpsFakeEditor.__init__(self, *args, **kwargs)
        self.set_text_setup(self.TEXT_CARRIER, settings=True)
        if MenuEditor.SIGNATURES is None:
            MenuEditor.SIGNATURES = declarations()

    def call(self, skill, args, agent):
        if skill in self.SIGNATURES:
            unknown = set(args) - self.SIGNATURES[skill]
            if unknown:
                raise AssertionError("undeclared %s keyword(s): %s" % (skill, sorted(unknown)))
        self._rebuild()
        result = OpsFakeEditor.call(self, skill, args, agent)
        self._rebuild()
        return result

    def component(self, obj, kind):
        return OpsFakeEditor.component(self, obj, short(kind))

    def _skill_component_list(self, agent, args):
        result = OpsFakeEditor._skill_component_list(self, agent, args)
        ui = {"Image", "Button", "CanvasScaler", "GraphicRaycaster", "VerticalLayoutGroup", "HorizontalLayoutGroup",
              "GridLayoutGroup", "LayoutElement", "ContentSizeFitter", "Text"}
        for row in result["components"]:
            row["fullType"] = (("UnityEngine.UI." if row["type"] in ui else "UnityEngine.") + row["type"]
                               if row["type"] != "TextMeshProUGUI" else "TMPro.TextMeshProUGUI")
        return result

    def _new_canvas(self, agent, name):
        obj = OpsFakeEditor._new_canvas(self, agent, name)
        obj.fields["Canvas"] = {"m_RenderMode": 0, "m_AdditionalShaderChannelsFlag": 0}
        obj.fields["CanvasScaler"] = {"m_UiScaleMode": 0, "m_ReferenceResolution": (800.0, 600.0),
                                      "m_ScreenMatchMode": 0, "m_MatchWidthOrHeight": 0.0}
        obj.fields["GraphicRaycaster"] = {"m_IgnoreReversedGraphics": True}
        obj.fields["RectTransform"]["m_SizeDelta"] = (960.0, 540.0)
        return obj

    def _parent(self, agent, args):
        if args.get("parentEntityId"):
            return self.find({"entityId": args["parentEntityId"]})
        return self._canvas_for(agent, args.get("parent"))

    def _skill_ui_create_panel(self, agent, args):
        holder = self._parent(agent, args)
        obj = self._ui(agent, args.get("name", "Panel"), holder, [["CanvasRenderer", None], ["Image", True]],
                       m_AnchorMin=(0.0, 0.0), m_AnchorMax=(1.0, 1.0), m_SizeDelta=(0.0, 0.0))
        obj.fields["Image"] = {"m_Color": (1.0, 1.0, 1.0, 0.5)}
        return dict(self._created(obj), success=True)

    def _skill_ui_create_button(self, agent, args):
        args = dict(args, parent=self._parent(agent, args).name)
        result = OpsFakeEditor._skill_ui_create_button(self, agent, args)
        obj = self.find(result)
        obj.fields["Button"] = {"m_Interactable": True}
        obj.fields["Image"] = {"m_Color": (1.0, 1.0, 1.0, 1.0)}
        return result

    def _skill_ui_create_batch(self, agent, args):
        rows = []
        for item in json.loads(args["items"]):
            if item.pop("type").lower() != "button":
                raise AssertionError("Only button creation is modeled")
            if set(item) - self.SIGNATURES["ui_create_button"]:
                raise AssertionError("Undeclared button item field")
            rows.append(self._skill_ui_create_button(agent, item))
        return {"success": True, "results": rows, "successCount": len(rows), "failCount": 0, "totalItems": len(rows)}

    def _add(self, agent, obj, kind):
        if self.component(obj, kind):
            return
        if kind in ("RectTransform", "CanvasRenderer"):
            if kind == "RectTransform":
                obj.components = [pair for pair in obj.components if pair[0] != "Transform"]
                obj.fields[kind] = dict(RECT_DEFAULT)
            obj.components.append([kind, None])
        else:
            obj.components.append([kind, kind not in ("CanvasRenderer", "Transform")])
        if kind == "BoxCollider":
            obj.fields[kind] = {"m_Size": (1.0, 1.0, 1.0), "m_Center": (0.0, 0.0, 0.0), "m_IsTrigger": False}
        elif kind in ("VerticalLayoutGroup", "HorizontalLayoutGroup"):
            obj.fields[kind] = {"m_Spacing": 0.0, "m_ChildAlignment": 0, "m_ChildControlWidth": True,
                                "m_ChildControlHeight": True, "m_ChildForceExpandWidth": True,
                                "m_ChildForceExpandHeight": True, "m_ChildScaleWidth": False,
                                "m_ChildScaleHeight": False, "m_Padding.m_Left": 0, "m_Padding.m_Right": 0,
                                "m_Padding.m_Top": 0, "m_Padding.m_Bottom": 0}
        elif kind == "LayoutElement":
            obj.fields[kind] = {"m_MinWidth": -1.0, "m_MinHeight": -1.0, "m_PreferredWidth": -1.0,
                                "m_PreferredHeight": -1.0, "m_FlexibleWidth": -1.0, "m_FlexibleHeight": -1.0,
                                "m_IgnoreLayout": False, "m_LayoutPriority": 1}
        elif kind == "ContentSizeFitter":
            obj.fields[kind] = {"m_HorizontalFit": 0, "m_VerticalFit": 0}
        elif kind in ("Text", "TextMeshProUGUI", "TextMesh"):
            obj.fields[kind] = {"m_text" if kind == "TextMeshProUGUI" else "m_Text": ""}
            if kind == "TextMesh":
                self._add(agent, obj, "MeshRenderer")
        elif kind == "Canvas":
            obj.fields[kind] = {"m_RenderMode": 0, "m_AdditionalShaderChannelsFlag": 0}
        self._change(agent, self._identity(obj, kind="components_changed"))

    def _skill_component_add(self, agent, args):
        obj, kind = self.find(args), short(args["componentType"])
        self._add(agent, obj, kind)
        return {"success": True, "component": kind}

    def _skill_component_remove(self, agent, args):
        result = OpsFakeEditor._skill_component_remove(self, agent, dict(args, componentType=short(args["componentType"])))
        self.find(args).fields.pop(short(args["componentType"]), None)
        return result

    def _serialized(self, obj, kind):
        kind = short(kind)
        if not self.component(obj, kind) and not (kind == "Transform" and self.component(obj, "RectTransform")):
            raise _fail("COMPONENT_NOT_FOUND", "Component missing: " + kind)
        if kind in ("Transform", "RectTransform"):
            values = {"m_LocalPosition": _g9(obj.pos), "m_LocalRotation": _g9(obj.rot), "m_LocalScale": _g9(obj.scale)}
            if kind == "RectTransform":
                values.update({key: _g9(val) if isinstance(val, tuple) else str(val)
                               for key, val in obj.fields[kind].items()})
            return values
        if kind == "Button":
            values = self._button_serialized(obj)
            values["m_Interactable"] = str(obj.fields[kind]["m_Interactable"])
            return values
        if kind == "Text" and "Text" in obj.fields:
            return {key: self.format_value(val) for key, val in obj.fields[kind].items()}
        if kind in obj.fields:
            return {key: _g9(val) if isinstance(val, tuple) else self.format_value(val)
                    for key, val in obj.fields[kind].items()}
        return {}

    @staticmethod
    def _parse(value, old):
        if isinstance(old, bool):
            return str(value).lower() == "true"
        if isinstance(old, tuple):
            return _numbers(value, len(old))
        if isinstance(old, int):
            return int(value)
        if isinstance(old, float):
            return float(value)
        return str(value)

    def _skill_component_set_serialized_property(self, agent, args):
        obj, kind, field = self.find(args), short(args["componentType"]), args["propertyPath"]
        if kind == "Transform":
            return OpsFakeEditor._skill_component_set_serialized_property(self, agent, dict(args, componentType=kind))
        fields = obj.fields.get(kind, {})
        if field not in fields:
            raise _fail("PROPERTY_NOT_FOUND", kind + "." + field)
        fields[field] = self._parse(args["value"], fields[field])
        self._change(agent, self._identity(obj, kind="property_changed", targetType=kind, property=field,
                                           previousValue="", value=str(fields[field])))
        return {"success": True}

    def _skill_component_set_property(self, agent, args):
        obj, kind, field = self.find(args), short(args["componentType"]), args["propertyName"]
        names = {"referenceResolution": "m_ReferenceResolution", "uiScaleMode": "m_UiScaleMode",
                 "screenMatchMode": "m_ScreenMatchMode", "matchWidthOrHeight": "m_MatchWidthOrHeight",
                 "additionalShaderChannels": "m_AdditionalShaderChannelsFlag", "size": "m_Size", "text": "m_Text"}
        value = args["value"]
        enums = {"ScaleWithScreenSize": "1", "MatchWidthOrHeight": "0", "TexCoord1,Normal,Tangent": "25"}
        return self._skill_component_set_serialized_property(agent, dict(V.address(obj.eid), componentType=kind,
            propertyPath=names[field], value=enums.get(value, value)))

    def _rect_position(self, obj):
        fields = obj.fields["RectTransform"]
        parent = obj.parent
        if parent and "RectTransform" in parent.fields:
            parent_fields = parent.fields["RectTransform"]
            size = parent_fields["m_SizeDelta"]
            pivot = parent_fields["m_Pivot"]
            anchor = tuple(a + (b - a) * p for a, b, p in zip(fields["m_AnchorMin"], fields["m_AnchorMax"],
                                                            fields["m_Pivot"]))
            obj.pos = tuple((a - p) * s + v for a, p, s, v in zip(anchor, pivot, size,
                                                                 fields["m_AnchoredPosition"])) + (obj.pos[2],)

    def _skill_ui_set_rect_transform(self, agent, args):
        result = OpsFakeEditor._skill_ui_set_rect_transform(self, agent, args)
        obj = self.find(args)
        self._rect_position(obj)
        obj.pos = tuple(float(args.get(key, old)) for key, old in zip(("localPosX", "localPosY", "localPosZ"), obj.pos))
        if args.get("anchoredPosZ") is not None and args.get("localPosZ") is None:
            obj.pos = obj.pos[:2] + (float(args["anchoredPosZ"]),)
        if any(key in args for key in ("localPosX", "localPosY")):
            rect = obj.fields["RectTransform"]
            old = rect["m_AnchoredPosition"]
            self._rect_position(obj)
            delta = tuple(float(args[key]) - obj.pos[i] if key in args else 0
                          for i, key in enumerate(("localPosX", "localPosY")))
            rect["m_AnchoredPosition"] = tuple(a + b for a, b in zip(old, delta))
            self._rect_position(obj)
        obj.rot = q_from_euler(tuple(float(args.get(key, 0)) for key in ("localRotX", "localRotY", "localRotZ")))
        obj.scale = tuple(float(args.get(key, old)) for key, old in zip(("localScaleX", "localScaleY", "localScaleZ"), obj.scale))
        return result

    def _skill_ui_layout_children(self, agent, args):
        obj = self.find(args)
        for kind in ("VerticalLayoutGroup", "HorizontalLayoutGroup", "GridLayoutGroup"):
            if self.component(obj, kind):
                self._skill_component_remove(agent, dict(V.address(obj.eid), componentType=kind))
        axis = args.get("layoutType", "Vertical")
        kind = axis + "LayoutGroup"
        self._add(agent, obj, kind)
        fields = obj.fields[kind]
        fields["m_Spacing"] = float(args.get("spacing", 10))
        for key in ("Left", "Right", "Top", "Bottom"):
            fields["m_Padding.m_" + key] = int(args.get("padding" + key, 0))
        for key in ("Width", "Height"):
            fields["m_ChildForceExpand" + key] = bool(args.get("childForceExpand" + key, False))
        self._add(agent, obj, "ContentSizeFitter")
        obj.fields["ContentSizeFitter"]["m_VerticalFit" if axis == "Vertical" else "m_HorizontalFit"] = 2
        return {"success": True, "parent": obj.name, "layoutType": axis, "childCount": len(obj.children)}

    def _skill_ui_add_layout_element(self, agent, args):
        obj = self.find(args)
        self._add(agent, obj, "LayoutElement")
        fields = obj.fields["LayoutElement"]
        for key, val in args.items():
            name = "m_" + key[0].upper() + key[1:]
            if name in fields:
                fields[name] = self._parse(val, fields[name])
        self._change(agent, self._identity(obj, kind="property_changed", targetType="LayoutElement"))
        return dict(fields, success=True)

    def _rebuild(self):
        for obj in list(self.objects.values()):
            for kind in ("VerticalLayoutGroup", "HorizontalLayoutGroup"):
                comp = self.component(obj, kind)
                if not comp or not comp[1]:
                    continue
                fields = obj.fields[kind]
                children = [child for child in obj.children if child.active and "RectTransform" in child.fields and
                            not child.fields.get("LayoutElement", {}).get("m_IgnoreLayout", False)]
                sizes = []
                for child in children:
                    values = child.fields.get("LayoutElement", {})
                    size = tuple(max(0, values.get("m_Min" + axis, -1), values.get("m_Preferred" + axis, -1))
                                 for axis in ("Width", "Height"))
                    sizes.append(size)
                left, right, top, bottom = [fields["m_Padding.m_" + key] for key in ("Left", "Right", "Top", "Bottom")]
                spacing = fields["m_Spacing"]
                width = (max([size[0] for size in sizes] or [0]) if kind == "VerticalLayoutGroup" else
                         sum(size[0] for size in sizes) + max(0, len(sizes) - 1) * spacing) + left + right
                height = (sum(size[1] for size in sizes) + max(0, len(sizes) - 1) * spacing if kind == "VerticalLayoutGroup"
                          else max([size[1] for size in sizes] or [0])) + top + bottom
                fitter = self.component(obj, "ContentSizeFitter")
                if fitter and fitter[1]:
                    fit = obj.fields["ContentSizeFitter"]
                    old = obj.fields["RectTransform"]["m_SizeDelta"]
                    obj.fields["RectTransform"]["m_SizeDelta"] = (width if fit["m_HorizontalFit"] == 2 else old[0],
                                                                  height if fit["m_VerticalFit"] == 2 else old[1])
                cursor = top if kind == "VerticalLayoutGroup" else left
                for child, size in zip(children, sizes):
                    rect = child.fields["RectTransform"]
                    old = rect["m_SizeDelta"]
                    rect["m_SizeDelta"] = tuple(size[i] if fields["m_ChildControl" + axis] else old[i]
                                               for i, axis in enumerate(("Width", "Height")))
                    rect["m_AnchorMin"] = rect["m_AnchorMax"] = (0.0, 1.0)
                    rect["m_AnchoredPosition"] = (left + size[0] / 2, -cursor - size[1] / 2) if kind == "VerticalLayoutGroup" else (
                        cursor + size[0] / 2, -top - size[1] / 2)
                    cursor += size[1 if kind == "VerticalLayoutGroup" else 0] + spacing
                    self._rect_position(child)

    def _skill_asset_set_labels(self, agent, args):
        path = self.disk(args["assetPath"]) + ".meta"
        with open(path, "a", encoding="utf-8") as handle:
            handle.write("labels:\n- " + args["labels"] + "\n")
        self._imported(agent, [args["assetPath"]])
        return {"success": True, "assetPath": args["assetPath"], "labels": args["labels"].split(",")}


class PackageTextEditor(MenuEditor):
    TEXT_CARRIER = "package"


class LegacyTextEditor(MenuEditor):
    TEXT_CARRIER = None


class OS2Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "OS2"
    EDITOR = MenuEditor
    SEED = SEEDS[0]
    FAILS_UNTOUCHED = {"A1", "A2", "A3", "A4", "A5", "A6", "A7", "A9"}

    def setUp(self):
        self.network_guard = mock.patch("socket.socket", side_effect=AssertionError("Network is forbidden"))
        self.network_guard.start()
        self.addCleanup(self.network_guard.stop)

    def run_for(self, **kwargs):
        scratch = os.path.join(REPO, "eval", "runs")
        created = not os.path.isdir(scratch)
        os.makedirs(scratch, exist_ok=True)
        if created:
            self.addCleanup(os.rmdir, scratch)
        original = tempfile.mkdtemp
        with mock.patch.object(pack_offline.tempfile, "mkdtemp", side_effect=lambda **args: original(dir=scratch, **args)):
            return Offline(self, self.TASK_ID, kwargs.pop("editor_class", self.EDITOR),
                           kwargs.pop("seed", self.SEED), **kwargs)

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("OS2", "ops", "standard", 1, 20))
        self.assertEqual(pack.normalize_requires(task.requires),
                         {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.ugui"]})

    def test_no_prompt_forbids_saving_the_run_scene(self):
        # The prompts explicitly permit the run scene to be saved.
        for lang, phrase in (("en", "Saving this run's scene is allowed"), ("zh-CN", "允许保存本轮场景")):
            with open(os.path.join(pack_offline.PACK_ROOT, "tasks", "OS2", "prompt." + lang + ".md"), encoding="utf-8") as handle:
                self.assertIn(phrase, handle.read())

    def test_all_variants_both_seeds_ids_and_text_carriers(self):
        solutions = load_modules("OS2")[2]
        for seed in SEEDS:
            for ids in ("legacy", "6000.4"):
                for editor in (MenuEditor, PackageTextEditor, LegacyTextEditor):
                    untouched = self.run_for(seed=seed, entity_mode=ids, editor_class=editor)
                    self.assertEqual(set(untouched.verify(None).failed()), self.FAILS_UNTOUCHED)
                    cases = [("reference", solutions.reference, set())]
                    cases += [(item.name, item.apply, item.expect_failed) for item in solutions.NEGATIVES]
                    cases += [(name, fn, set()) for name, fn in solutions.ALTERNATIVES]
                    for name, fn, failed in cases:
                        with self.subTest(seed=seed, ids=ids, carrier=editor.TEXT_CARRIER, case=name):
                            run = self.run_for(seed=seed, entity_mode=ids, editor_class=editor)
                            card = run.verify(run.solve(fn))
                            self.assertEqual(set(card.failed()), failed, detail(card))
                            self.assertEqual(card.strict, not failed)

    def test_seed_rearranges_names_structure_and_dimensions(self):
        first, repeat, other = self.run_for(), self.run_for(), self.run_for(seed=SEEDS[1])
        self.assertEqual(first.fingerprint, repeat.fingerprint)
        self.assertNotEqual(first.fingerprint, other.fingerprint)
        self.assertEqual(first.snap["data"], {})
        a = V.structure(first.build_ctx.probe, [first.scope.root_id])
        b = V.structure(other.build_ctx.probe, [other.scope.root_id])
        self.assertNotEqual(sorted(len(row["children"]) for row in a), sorted(len(row["children"]) for row in b))
        self.assertNotEqual(first.handles["canvasPath"].split("/")[1:], other.handles["canvasPath"].split("/")[1:])
        self.assertNotEqual(first.task.prompt_vars(first.build_ctx)["canvas_units"],
                            other.task.prompt_vars(other.build_ctx)["canvas_units"])
        for run in (first, other):
            self.assertEqual(run.build_ctx.probe.children(run.handles["canvas"]), [])
            self.assertTrue(all(re.fullmatch("[0-9a-f]{64}", digest)
                                for digest in run.snap["digests"]["objects"].values()))

    def test_layout_rebuild_overwrites_size_delta_and_needs_elements(self):
        run = self.run_for()
        run.solve(run.solutions.size_delta_only)
        refs = run.private.locate(run.build_ctx)
        rect = run.build_ctx.probe.ser(refs[1], "UnityEngine.RectTransform")
        self.assertNotEqual(V.floats(rect["m_SizeDelta"]), (run.private.spec(run.build_ctx)["buttonWidth"],
                                                          run.private.spec(run.build_ctx)["buttonHeight"]))
        self.assertEqual(set(run.verify(run.private.actual_report(run.build_ctx)).failed()), {"A4"})

    def test_wrong_solutions_missing_objects_and_malformed_reports_do_not_raise(self):
        for which in ("canvas", "panel", "button", "label"):
            with self.subTest(missing=which):
                run = self.run_for()
                result = run.solve(run.solutions.reference)
                nodes = run.private.locate(run.build_ctx)
                refs = {"canvas": run.handles["canvas"], "panel": nodes[0], "button": nodes[1],
                        "label": V.object_id(run.build_ctx.probe.children(nodes[1])[0])}
                run.editor.client(SOLVER_AGENT).write("gameobject_delete", **V.address(refs[which]))
                self.assertFalse(run.verify(result).strict)
        for answer in (None, [], {"panelPath": 7, "buttonPaths": None}, {"panelPath": {}, "buttonPaths": []}):
            run = self.run_for()
            run.solve(run.solutions.reference)
            self.assertEqual(set(run.verify(answer).failed()), {"A9"})

    def test_every_original_component_value_and_folder_metadata_is_protected(self):
        for variant in ("extra_object", "extra_folder", "bystander_property", "bystander_depth", "folder_labels"):
            run = self.run_for()
            self.assertEqual(run.failed(getattr(run.solutions, variant)), {"A8"})
        for asset in ("folder", "scene"):
            run = self.run_for()
            result = run.solve(run.solutions.reference)
            asset_path = run.handles["folders"][0] if asset == "folder" else run.scope.scene_path
            path = run.editor.disk(asset_path) + ".meta"
            with open(path, "a", encoding="utf-8") as handle:
                handle.write("userData: changed-importer-data\n")
            self.assertEqual(set(run.verify(result).failed()), {"A8"})

    def test_main_checks_ignore_report_and_tolerance_is_absolute(self):
        run = self.run_for()
        run.solve(run.solutions.reference)
        panel = run.private.locate(run.build_ctx)[0]
        ctx = run.build_ctx
        wanted = run.private.spec(ctx)["width"]
        ctx.c.write("ui_set_rect_transform", sizeDeltaX=wanted + 0.0005, **V.address(panel))
        self.assertTrue(run.verify(run.private.actual_report(ctx)).strict)
        ctx.c.write("ui_set_rect_transform", sizeDeltaX=wanted + 0.002, **V.address(panel))
        self.assertEqual(set(run.verify(run.private.actual_report(ctx)).failed()), {"A2"})
        untouched = self.run_for()
        answer = untouched.private.actual_report(untouched.build_ctx)
        self.assertEqual(set(untouched.verify(answer).failed()), self.FAILS_UNTOUCHED - {"A9"})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
