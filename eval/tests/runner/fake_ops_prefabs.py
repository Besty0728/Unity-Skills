"""In-memory prefab model for the offline tests of the challenge tasks OC1, OC3 and OC5 (no Unity, no HTTP).

PrefabFakeEditor extends pack_offline.OpsFakeEditor with what those tasks touch:
- components that carry serialized values (Comp), listed the way SerializedPropertySkillUtility.ListProperties reads
  them (a vector's composite entry, then its leaves; a RectOffset's children) and written into the asset YAML;
- RectTransforms whose localPosition x/y follows from anchors, pivot, anchoredPosition and the parent rect, as Unity
  drives it: prefab_set_transform writes localPosition before the anchors, and new anchors keep anchoredPosition;
- prefab assets as object trees saved as Unity-like YAML: GameObject, Transform/RectTransform, component and
  MonoBehaviour documents; a nested instance or a variant is a PrefabInstance document with m_Modifications,
  m_AddedGameObjects, m_AddedComponents and m_RemovedComponents and the stripped documents they point at; fileIDs stay
  stable across saves, a nested object's fileID is derived from its PrefabInstance's as Unity does;
- scene and nested instances whose objects link to their source object; overrides are what an instance differs from
  its source (the plugin's phantom filter for free, m_Name left out); an asset edit is carried into every instance and
  every asset that nests or derives from it, whose files stay untouched as in Unity; the root of an instance keeps its
  default overrides (position, rotation, and for a RectTransform root its anchors, pivot, size and anchoredPosition);
  prefab_apply writes into the outermost asset, prefab_revert_overrides keeps the default overrides;
- scripts with namespaces, ScriptableObjects with m_Script, prefab variants, labels kept in .meta files and GUIDs that
  change when an asset is deleted and created anew.
The live selftest against a real editor remains the gate: this catches mistakes in the task code before it.
"""
import hashlib
import json
import os
import re

from .pack_fake_editor import (CANVAS_CENTER, CLASS_RE, Obj, RECT_DEFAULT, _fail, _g9, euler_from_q, q_from_euler,
                               q_normalize)
from .pack_offline import OpsFakeEditor
from usk_eval.verifylib import checks  # noqa: E402

MASK = (1 << 63) - 1
F, I, B, E, S, V2, V3, Q, C, R = "float", "int", "bool", "enum", "str", "vec2", "vec3", "quat", "color", "ref"
LEAVES = {V2: "xy", V3: "xyz", Q: "xyzw", C: "rgba"}
LAYERS = {"Default": 0, "TransparentFX": 1, "Ignore Raycast": 2, "Water": 4, "UI": 5}
RECT_FIELDS = ("m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta", "m_Pivot")
TRANSFORMS = ("Transform", "RectTransform")
ENUMS = {"m_RenderMode": {"ScreenSpaceOverlay": 0, "ScreenSpaceCamera": 1, "WorldSpace": 2}}
SCHEMA = {
    "MeshFilter": [("m_Mesh", R, None)],
    "MeshRenderer": [("m_Enabled", B, True), ("m_CastShadows", E, 1), ("m_ReceiveShadows", B, True),
                     ("m_Materials.Array.data[0]", R, ("builtin", "Default-Material"))],
    "BoxCollider": [("m_Enabled", B, True), ("m_IsTrigger", B, False), ("m_Size", V3, (1.0, 1.0, 1.0)),
                    ("m_Center", V3, (0.0, 0.0, 0.0))],
    "SphereCollider": [("m_Enabled", B, True), ("m_IsTrigger", B, False), ("m_Radius", F, 0.5),
                       ("m_Center", V3, (0.0, 0.0, 0.0))],
    "CapsuleCollider": [("m_Enabled", B, True), ("m_IsTrigger", B, False), ("m_Radius", F, 0.5), ("m_Height", F, 2.0),
                        ("m_Direction", E, 1), ("m_Center", V3, (0.0, 0.0, 0.0))],
    "AudioSource": [("m_Enabled", B, True), ("m_audioClip", R, None), ("m_PlayOnAwake", B, True), ("m_Volume", F, 1.0),
                    ("m_Pitch", F, 1.0), ("Loop", B, False), ("Mute", B, False), ("Priority", I, 128)],
    "Light": [("m_Enabled", B, True), ("m_Type", E, 2), ("m_Color", C, (1.0, 1.0, 1.0, 1.0)), ("m_Intensity", F, 1.0),
              ("m_Range", F, 10.0), ("m_Shadows.m_Type", E, 0)],
    "CanvasRenderer": [("m_CullTransparentMesh", B, True)],
    "Image": [("m_Enabled", B, True), ("m_Material", R, None), ("m_Color", C, (1.0, 1.0, 1.0, 1.0)),
              ("m_RaycastTarget", B, True), ("m_Maskable", B, True), ("m_Sprite", R, None), ("m_Type", E, 0),
              ("m_PreserveAspect", B, False), ("m_FillCenter", B, True)],
    "Canvas": [("m_Enabled", B, True), ("m_RenderMode", E, 0), ("m_Camera", R, None), ("m_PlaneDistance", F, 100.0),
               ("m_PixelPerfect", B, False), ("m_ReceivesEvents", B, True), ("m_OverrideSorting", B, False),
               ("m_OverridePixelPerfect", B, False), ("m_SortingBucketNormalizedSize", F, 0.0),
               ("m_AdditionalShaderChannelsFlag", I, 0), ("m_SortingLayerID", I, 0), ("m_SortingOrder", I, 0),
               ("m_TargetDisplay", I, 0)],
    "CanvasScaler": [("m_Enabled", B, True), ("m_UiScaleMode", E, 0), ("m_ReferencePixelsPerUnit", F, 100.0),
                     ("m_ScaleFactor", F, 1.0), ("m_ReferenceResolution", V2, (800.0, 600.0)),
                     ("m_ScreenMatchMode", E, 0), ("m_MatchWidthOrHeight", F, 0.0), ("m_PhysicalUnit", E, 3),
                     ("m_FallbackScreenDPI", F, 96.0), ("m_DefaultSpriteDPI", F, 96.0),
                     ("m_DynamicPixelsPerUnit", F, 1.0)],
    "GraphicRaycaster": [("m_Enabled", B, True), ("m_IgnoreReversedGraphics", B, True), ("m_BlockingObjects", E, 0),
                         ("m_BlockingMask", I, -1)],
    "HorizontalLayoutGroup": [("m_Enabled", B, True), ("m_Padding.m_Left", I, 0), ("m_Padding.m_Right", I, 0),
                              ("m_Padding.m_Top", I, 0), ("m_Padding.m_Bottom", I, 0), ("m_ChildAlignment", E, 0),
                              ("m_Spacing", F, 0.0), ("m_ChildForceExpandWidth", B, True),
                              ("m_ChildForceExpandHeight", B, True), ("m_ChildControlWidth", B, True),
                              ("m_ChildControlHeight", B, True), ("m_ChildScaleWidth", B, False),
                              ("m_ChildScaleHeight", B, False), ("m_ReverseArrangement", B, False)],
}
CLASS_IDS = {"GameObject": 1, "Transform": 4, "RectTransform": 224, "MeshFilter": 33, "MeshRenderer": 23,
             "BoxCollider": 65, "SphereCollider": 135, "CapsuleCollider": 136, "AudioSource": 82, "Light": 108,
             "Canvas": 223, "CanvasRenderer": 222}
UI_SCRIPTS = {"Image": "fe87c0e1cc204ed48ad3b37840f39efc", "CanvasScaler": "0cd44c1031e13a943bb63640046fad76",
              "GraphicRaycaster": "dc42784cf147c0c48a680349fa168899",
              "HorizontalLayoutGroup": "30649d3a9faa99c48a7b1166b86bf2a0"}
FULL = {"Image": "UnityEngine.UI.Image", "CanvasScaler": "UnityEngine.UI.CanvasScaler",
        "GraphicRaycaster": "UnityEngine.UI.GraphicRaycaster",
        "HorizontalLayoutGroup": "UnityEngine.UI.HorizontalLayoutGroup"}
BUILTIN = {"Cube": (10202, "UnityEngine.Mesh", "e"), "Sphere": (10207, "UnityEngine.Mesh", "e"),
           "Cylinder": (10206, "UnityEngine.Mesh", "e"), "Capsule": (10208, "UnityEngine.Mesh", "e"),
           "Default-Material": (10303, "UnityEngine.Material", "f")}
HEADER = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
NAMESPACE_RE = re.compile(r"namespace\s+([\w.]+)")
FLOAT_TOL = 1e-6


class Comp(list):
    """[type, enabled] as the base fake reads a component, plus its serialized values (field -> value; vectors as
    tuples), the link to the component it comes from (asset path, fileID) and its own fileID inside an asset."""

    def __init__(self, kind, enabled=None, values=None, src=None, fid=None):
        list.__init__(self, [kind, enabled])
        self.values = dict(values or {})
        self.src = src
        self.fid = fid

    def copy(self, src=None):
        return Comp(self[0], self[1], self.values, src if src is not None else self.src)


class Asset(object):
    """A prefab asset: its object tree (nodes are Obj, not registered in the scene), GUID and fileID sequence."""

    def __init__(self, path, guid, root, variant=False):
        self.path, self.guid, self.root, self.variant = path, guid, root, variant
        self.seq = 0
        self.nodes, self.comps = {}, {}


class Diff(object):
    """What an instance subtree differs from its source: property leaves per source object, names, removed and added
    components and added GameObjects (with their sibling index)."""

    def __init__(self):
        self.props = []          # (source GameObject fileID, source component fileID or None, leaf, kind, value)
        self.names = {}          # source GameObject fileID -> the instance's name where it differs
        self.removed = []        # (source GameObject fileID, source component fileID)
        self.added_comps = []    # (source GameObject fileID, Comp)
        self.added_gos = []      # (source GameObject fileID, sibling index, node)


def _nums(text):
    return [float(v) for v in checks.NUM_RE.findall(str(text))]


def _yf(value):
    return "%.9g" % (float(value) + 0.0)


def _same(kind, a, b):
    if kind in (F,) and isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(float(a) - float(b)) <= FLOAT_TOL
    return a == b


def _derive(inst_fid, src_fid):
    return (inst_fid ^ src_fid) & MASK or 1


class PrefabFakeEditor(OpsFakeEditor):
    extra_reads = frozenset((
        "prefab_get_hierarchy", "prefab_get_info", "prefab_list_components", "prefab_get_serialized_properties",
        "prefab_get_overrides", "prefab_find_instances", "script_read", "script_list", "scriptableobject_list_types",
        "asset_get_labels"))

    def __init__(self, *args, **kwargs):
        self.guids, self.generations, self.labels = {}, {}, {}
        self.assets, self.namespaces = {}, {}
        OpsFakeEditor.__init__(self, *args, **kwargs)

    # ------------------------------------------------------------ GUIDs, .meta files and labels

    def _guid(self, asset_path):
        """A GUID per asset; deleting the asset and creating it anew gives a new one, as in Unity."""
        if asset_path not in self.guids:
            seed = "%s#%d" % (asset_path, self.generations.get(asset_path, 0))
            self.guids[asset_path] = hashlib.md5(seed.encode("utf-8")).hexdigest()
        return self.guids[asset_path]

    def _write_meta(self, asset_path):
        labels = self.labels.get(asset_path) or []
        text = "fileFormatVersion: 2\nguid: %s\n" % self._guid(asset_path)
        if labels:
            text += "labels:\n" + "".join("- %s\n" % label for label in labels)
        with open(self.disk(asset_path) + ".meta", "w", encoding="utf-8") as handle:
            handle.write(text)

    def _write_file(self, asset_path, text):
        path = self.disk(asset_path)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        self._write_meta(asset_path)
        self.known.add(asset_path)

    def _skill_asset_get_info(self, agent, args):
        res = OpsFakeEditor._skill_asset_get_info(self, agent, args)
        res["labels"] = list(self.labels.get(args["assetPath"]) or [])
        return res

    def _skill_asset_get_labels(self, agent, args):
        return {"success": True, "assetPath": args["assetPath"],
                "labels": list(self.labels.get(args["assetPath"]) or [])}

    def _skill_asset_set_labels(self, agent, args):
        path = args["assetPath"]
        if path not in self.known or not os.path.exists(self.disk(path)):
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)
        labels = [label.strip() for label in str(args.get("labels") or "").split(",") if label.strip()]
        self.labels[path] = labels
        self._write_meta(path)
        self._imported(agent, [path])
        return {"success": True, "assetPath": path, "labels": labels}

    def _skill_asset_create_folder_batch(self, agent, args):
        results = [self._skill_asset_create_folder(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_asset_delete(self, agent, args):
        path = args["assetPath"]
        if path in self.assets:
            del self.assets[path]
            self.prefabs.pop(path, None)
            for node in self.walk():
                if node.src is not None and node.src[0] == path:
                    node.src = None
                    for comp in node.components:
                        comp.src = None
        res = OpsFakeEditor._skill_asset_delete(self, agent, args)
        for gone in [known for known in list(self.guids) if known == path or known.startswith(path + "/")]:
            self.generations[gone] = self.generations.get(gone, 0) + 1
            del self.guids[gone]
            self.labels.pop(gone, None)
        return res

    # ------------------------------------------------------------ component model

    def _attrs(self, node):
        for name in ("rect", "src", "fid", "inst_fid"):
            if not hasattr(node, name):
                setattr(node, name, None)
        return node

    def _schema(self, kind):
        if kind in SCHEMA:
            return SCHEMA[kind]
        spec = self.classes.get(kind)
        if spec is None:
            return None
        out = [("m_Enabled", B, True), ("m_Script", R, ("script", spec["path"]))]
        for ftype, name, default, _old in spec["fields"]:
            kind_of = {"float": F, "int": I, "bool": B, "string": S}.get(ftype, R)
            out.append((name, kind_of, None if kind_of == R else default))
        return out

    def _short(self, kind):
        text = str(kind or "")
        short = text.rpartition(".")[2]
        if text in SCHEMA or text in self.classes or text in TRANSFORMS:
            return text
        if short in SCHEMA or short in self.classes or short in TRANSFORMS:
            return short
        lowered = {name.lower(): name for name in list(SCHEMA) + list(self.classes) + list(TRANSFORMS)}
        return lowered.get(short.lower(), short)

    def _full(self, kind):
        if kind in FULL:
            return FULL[kind]
        if kind in self.classes:
            space = self.namespaces.get(kind)
            return "%s.%s" % (space, kind) if space else kind
        return "UnityEngine." + kind

    def _comp(self, kind, **values):
        kind = self._short(kind)
        if kind in TRANSFORMS:
            return Comp(kind)
        schema = self._schema(kind)
        if schema is None:
            raise _fail("COMPONENT_TYPE_NOT_FOUND", "Component type not found: %s" % kind)
        enabled = True if any(name == "m_Enabled" for name, _k, _d in schema) else None
        vals = {name: default for name, _k, default in schema if name != "m_Enabled"}
        vals.update(values)
        return Comp(kind, enabled, vals)

    def _enrich(self, obj):
        """Turns the base fake's [type, enabled] pairs into Comp with default values."""
        self._attrs(obj)
        comps = []
        for entry in obj.components:
            if isinstance(entry, Comp):
                comps.append(entry)
                continue
            kind, enabled = entry[0], entry[1]
            extra = {}
            if kind == "MeshFilter" and obj.mesh:
                extra["m_Mesh"] = ("builtin", obj.mesh)
            comp = self._comp(kind, **extra)
            if enabled is not None and comp[1] is not None:
                comp[1] = enabled
            comps.append(comp)
        obj.components = comps
        return obj

    def _create(self, agent, item, parent):
        return self._enrich(OpsFakeEditor._create(self, agent, item, parent))

    def _find_comp(self, node, type_name, index=0, required=True):
        short = self._short(type_name)
        matches = [c for c in node.components if c[0] == short]
        if not matches and short == "Transform":
            matches = [c for c in node.components if c[0] == "RectTransform"]
        index = int(index or 0)
        if 0 <= index < len(matches):
            return matches[index]
        if required:
            raise _fail("COMPONENT_NOT_FOUND", "%s has no %s #%d" % (node.name, type_name, index))
        return None

    def _index_of(self, node, comp):
        """componentIndex: the position among the components of exactly this type (by identity: two Loot
        components with equal values are still two)."""
        return next(index for index, other in enumerate(c for c in node.components if c[0] == comp[0])
                    if other is comp)

    # ------------------------------------------------------------ values: leaves, listing, parsing

    def _field_kind(self, comp, name):
        for field, kind, _default in self._schema(comp[0]) or ():
            if field == name:
                return kind
        return None

    def _go_leaves(self, node):
        return {"m_IsActive": (B, bool(node.active)), "m_Layer": (I, LAYERS.get(node.layer, 0)),
                "m_TagString": (S, node.tag)}

    def _leaves(self, node, comp):
        out = {}
        if comp[0] in TRANSFORMS:
            for axis, value in zip("xyzw", node.rot):
                out["m_LocalRotation." + axis] = (F, value)
            for axis, value in zip("xyz", node.pos):
                if node.rect is None or axis == "z":
                    out["m_LocalPosition." + axis] = (F, value)
            for axis, value in zip("xyz", node.scale):
                out["m_LocalScale." + axis] = (F, value)
            if node.rect is not None:
                for name in RECT_FIELDS:
                    for axis, value in zip("xy", node.rect[name]):
                        out["%s.%s" % (name, axis)] = (F, value)
            return out
        if comp[1] is not None:
            out["m_Enabled"] = (B, comp[1])
        for name, kind, _default in self._schema(comp[0]):
            if name == "m_Enabled":
                continue
            value = comp.values.get(name)
            if kind in LEAVES:
                for axis, item in zip(LEAVES[kind], value):
                    out["%s.%s" % (name, axis)] = (F, item)
            else:
                out[name] = (kind, value)
        return out

    def _set_leaf(self, node, comp, leaf, value):
        if comp is None:
            if leaf == "m_IsActive":
                node.active = bool(value)
            elif leaf == "m_Layer":
                node.layer = next((name for name, number in LAYERS.items() if number == value), "Default")
            elif leaf == "m_TagString":
                node.tag = value
            return
        if leaf == "m_Enabled":
            comp[1] = bool(value)
            return
        name, _dot, axis = leaf.rpartition(".")
        if comp[0] in TRANSFORMS:
            fields = {"m_LocalRotation": "rot", "m_LocalPosition": "pos", "m_LocalScale": "scale"}
            if name in fields:
                current = list(getattr(node, fields[name]))
                current["xyzw".index(axis)] = float(value)
                setattr(node, fields[name], tuple(current))
            elif node.rect is not None and name in RECT_FIELDS:
                current = list(node.rect[name])
                current["xy".index(axis)] = float(value)
                node.rect[name] = tuple(current)
            return
        if self._field_kind(comp, leaf) is not None:
            comp.values[leaf] = value
            return
        kind = self._field_kind(comp, name)
        if kind in LEAVES:
            current = list(comp.values[name])
            current[LEAVES[kind].index(axis)] = float(value)
            comp.values[name] = tuple(current)

    def _ref_text(self, value):
        if value is None:
            return "null"
        tag = value[0]
        if tag == "asset":
            path = value[1]
            kind = "UnityEngine.GameObject" if path in self.assets else (
                self._full(self.sos[path]["type"]) if path in self.sos else "UnityEngine.Object")
            return "%s:%s:%s" % (self.asset_id(path), kind, os.path.splitext(os.path.basename(path))[0])
        if tag == "builtin":
            number, kind, _space = BUILTIN[value[1]]
            return "%d:%s:%s" % (number, kind, value[1])
        if tag == "script":
            return "%s:UnityEditor.MonoScript:%s" % (self.asset_id(value[1]),
                                                     os.path.splitext(os.path.basename(value[1]))[0])
        return "%s:UnityEditor.MonoScript:%s" % (int(UI_SCRIPTS[value[1]][:7], 16), value[1])

    def _ref_yaml(self, value):
        if value is None:
            return "{fileID: 0}"
        tag = value[0]
        if tag == "asset":
            path = value[1]
            if path in self.assets:
                return "{fileID: 100100000, guid: %s, type: 3}" % self._guid(path)
            if path in self.materials:
                return "{fileID: 2100000, guid: %s, type: 2}" % self._guid(path)
            return "{fileID: 11400000, guid: %s, type: 2}" % self._guid(path)
        if tag == "builtin":
            number, _kind, space = BUILTIN[value[1]]
            return "{fileID: %d, guid: 0000000000000000%s000000000000000, type: 0}" % (number, space)
        if tag == "script":
            return "{fileID: 11500000, guid: %s, type: 3}" % self._guid(value[1])
        return "{fileID: 11500000, guid: %s, type: 3}" % UI_SCRIPTS[value[1]]

    def _describe(self, kind, value):
        """SerializedPropertySkillUtility.DescribeValue."""
        if kind == F:
            return _yf(value)
        if kind in (I, E):
            return str(int(value))
        if kind == B:
            return "True" if value else "False"
        if kind == R:
            return self._ref_text(value)
        if kind in LEAVES:
            return _g9(value)
        return "" if value is None else str(value)

    def _scalar(self, kind, value):
        """A value as PropertyModification.value / the YAML spell it."""
        if kind == F:
            return _yf(value)
        if kind in (I, E):
            return str(int(value))
        if kind == B:
            return "1" if value else "0"
        if kind == R:
            return ""
        return "" if value is None else str(value)

    def _props(self, node, comp):
        """ListProperties of one component: composite entries, then their leaves."""
        out = []

        def add(path, text):
            out.append({"propertyPath": path, "value": text})

        if comp[0] in TRANSFORMS:
            pairs = [("m_LocalRotation", node.rot), ("m_LocalPosition", node.pos), ("m_LocalScale", node.scale)]
            if node.rect is not None:
                pairs += [(name, node.rect[name]) for name in RECT_FIELDS]
            for name, value in pairs:
                add(name, _g9(value))
                for axis, item in zip("xyzw", value):
                    add("%s.%s" % (name, axis), _yf(item))
            return out
        if comp[1] is not None:
            add("m_Enabled", "True" if comp[1] else "False")
        parents = set()
        for name, kind, _default in self._schema(comp[0]):
            if name == "m_Enabled":
                continue
            value = comp.values.get(name)
            if ".Array.data[" in name:
                base = name.split(".Array.")[0]
                if base not in parents:
                    parents.add(base)
                    add(base, "Generic")
                    add(base + ".Array.size", "1")
            elif "." in name:
                base = name.split(".")[0]
                if base not in parents:
                    parents.add(base)
                    add(base, "Generic")
            add(name, self._describe(kind, value))
            if kind in LEAVES:
                for axis, item in zip(LEAVES[kind], value):
                    add("%s.%s" % (name, axis), _yf(item))
        return out

    def _parse(self, kind, text, field=None):
        if kind == F:
            numbers = _nums(text)
            if len(numbers) != 1:
                raise _fail("INVALID_VALUE", "not a number: %r" % (text,))
            return numbers[0]
        if kind in (I, E):
            names = ENUMS.get(field) or {}
            if str(text) in names:
                return names[str(text)]
            numbers = _nums(text)
            if len(numbers) != 1:
                raise _fail("INVALID_VALUE", "not a number: %r" % (text,))
            return int(round(numbers[0]))
        if kind == B:
            return str(text).strip().lower() in ("true", "1")
        if kind in LEAVES:
            numbers = _nums(text)
            if len(numbers) != len(LEAVES[kind]):
                raise _fail("INVALID_VALUE", "%s needs %d numbers: %r" % (field, len(LEAVES[kind]), text))
            return tuple(numbers)
        return "" if text is None else str(text)

    def _set_property(self, node, comp, path, args):
        """component_set_serialized_property / prefab_set_serialized_property on one component."""
        reference = args.get("assetPath") or args.get("assetReferencePath")
        if comp[0] in TRANSFORMS:
            base, _dot, axis = path.partition(".")
            vector = {"m_LocalPosition": V3, "m_LocalRotation": Q, "m_LocalScale": V3}.get(base)
            if vector is None and node.rect is not None and base in RECT_FIELDS:
                vector = V2
            if vector is None:
                raise _fail("PROPERTY_NOT_FOUND", "%s has no property %s" % (comp[0], path))
            if axis:
                self._set_leaf(node, comp, path, self._parse(F, args.get("value")))
                if base == "m_LocalPosition" and node.rect is not None and axis in "xy":
                    self._sync_rect(node)
            else:
                value = self._parse(vector, args.get("value"), base)
                if base == "m_LocalPosition":
                    self._set_local_position(node, value)
                elif base == "m_LocalRotation":
                    node.rot = q_normalize(value)
                elif base == "m_LocalScale":
                    node.scale = value
                else:
                    node.rect[base] = value
            self._relayout(node)
            return
        if path == "m_Enabled":
            comp[1] = self._parse(B, args.get("value"))
            return
        kind = self._field_kind(comp, path)
        if kind is not None:
            if kind == R:
                if not reference:
                    raise _fail("INVALID_VALUE", "%s is a reference; give assetPath" % path)
                if reference not in self.sos and reference not in self.assets and reference not in self.materials:
                    raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % reference)
                comp.values[path] = ("asset", reference)
            else:
                comp.values[path] = self._parse(kind, args.get("value"), path)
            return
        base, _dot, axis = path.rpartition(".")
        kind = self._field_kind(comp, base)
        if kind in LEAVES and axis in LEAVES[kind]:
            self._set_leaf(node, comp, path, self._parse(F, args.get("value")))
            return
        raise _fail("PROPERTY_NOT_FOUND", "%s has no property %s" % (comp[0], path))

    def _property_path(self, comp, name):
        """A C# property name as its serialized field: volume -> m_Volume, sortingOrder -> m_SortingOrder."""
        if self._field_kind(comp, name) is not None:
            return name
        candidate = "m_" + name[:1].upper() + name[1:]
        if self._field_kind(comp, candidate) is not None:
            return candidate
        raise _fail("PROPERTY_NOT_FOUND", "%s.%s is not modelled" % (comp[0], name))

    # ------------------------------------------------------------ RectTransform layout

    def _rect_size(self, node):
        rect = node.rect
        parent = node.parent
        pw, ph = self._rect_size(parent) if parent is not None and parent.rect is not None else (0.0, 0.0)
        return (rect["m_SizeDelta"][0] + (rect["m_AnchorMax"][0] - rect["m_AnchorMin"][0]) * pw,
                rect["m_SizeDelta"][1] + (rect["m_AnchorMax"][1] - rect["m_AnchorMin"][1]) * ph)

    def _anchor_ref(self, node):
        """The point anchoredPosition is measured from, in the parent's local space."""
        rect, parent = node.rect, node.parent
        if parent is not None and parent.rect is not None:
            size, ppivot = self._rect_size(parent), parent.rect["m_Pivot"]
        else:
            size, ppivot = (0.0, 0.0), (0.5, 0.5)
        out = []
        for axis in (0, 1):
            low = (rect["m_AnchorMin"][axis] - ppivot[axis]) * size[axis]
            high = (rect["m_AnchorMax"][axis] - ppivot[axis]) * size[axis]
            out.append(low + (high - low) * rect["m_Pivot"][axis])
        return out

    def _relayout(self, node):
        for item in self.walk([node]):
            if getattr(item, "rect", None) is not None:
                ref = self._anchor_ref(item)
                item.pos = (ref[0] + item.rect["m_AnchoredPosition"][0], ref[1] + item.rect["m_AnchoredPosition"][1],
                            item.pos[2])

    def _sync_rect(self, node):
        """anchoredPosition from the current localPosition (localPosition was written)."""
        ref = self._anchor_ref(node)
        node.rect["m_AnchoredPosition"] = (node.pos[0] - ref[0], node.pos[1] - ref[1])

    def _set_local_position(self, node, value):
        node.pos = tuple(float(v) for v in value)
        if node.rect is not None:
            self._sync_rect(node)

    # ------------------------------------------------------------ scene skills with the component model

    def _skill_component_add(self, agent, args):
        obj = self.find(args)
        comp = self._comp(args["componentType"])
        obj.components.append(comp)
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {"success": True, "gameObject": obj.name, "component": comp[0]}

    def _skill_component_list(self, agent, args):
        obj = self.find(args)
        out = []
        for comp in obj.components:
            row = {"type": comp[0], "fullType": self._full(comp[0])}
            if comp[1] is not None:
                row["enabled"] = comp[1]
            out.append(row)
        return {"success": True, "gameObject": obj.name, "count": len(out), "components": out}

    def _skill_component_get_serialized_properties(self, agent, args):
        obj = self.find(args)
        comp = self._find_comp(obj, args["componentType"], args.get("componentIndex", 0))
        return {"success": True, "componentType": self._full(comp[0]), "properties": self._props(obj, comp)}

    def _skill_component_set_serialized_property(self, agent, args):
        obj = self.find(args)
        comp = self._find_comp(obj, args["componentType"], args.get("componentIndex", 0))
        self._set_property(obj, comp, args["propertyPath"], args)
        self._change(agent, self._identity(obj, kind="property_changed", targetType=comp[0],
                                           property=args["propertyPath"], previousValue="",
                                           value=str(args.get("value"))))
        return {"success": True}

    def _skill_component_set_property(self, agent, args):
        obj = self.find(args)
        comp = self._find_comp(obj, args["componentType"])
        name = args["propertyName"]
        if comp[0] in TRANSFORMS:
            values = _nums(args.get("value"))
            if name == "localEulerAngles":
                obj.rot = q_from_euler(values)
            elif name == "localPosition":
                self._set_local_position(obj, values)
            elif name == "localScale":
                obj.scale = tuple(values)
            else:
                raise _fail("PROPERTY_NOT_FOUND", "Transform.%s is not modelled" % name)
            self._relayout(obj)
        else:
            self._set_property(obj, comp, self._property_path(comp, name), args)
        self._change(agent, self._identity(obj, kind="property_changed", targetType=comp[0], property=name,
                                           previousValue="", value=str(args.get("value"))))
        return {"success": True}

    def _skill_gameobject_set_transform(self, agent, args):
        res = OpsFakeEditor._skill_gameobject_set_transform(self, agent, args)
        obj = self.find(args)
        if obj.rect is not None:
            self._sync_rect(obj)
        return res

    def _skill_gameobject_set_parent(self, agent, args):
        res = OpsFakeEditor._skill_gameobject_set_parent(self, agent, args)
        child = self.find(args, "child")
        if child.rect is not None:
            self._sync_rect(child)
        return res

    def _skill_gameobject_delete_batch(self, agent, args):
        results = [self._skill_gameobject_delete(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    # ------------------------------------------------------------ UGUI

    def _ui(self, agent, name, parent, components, **rect):
        obj = self._create(agent, {"name": name}, parent)
        obj.layer = "UI"
        comps = [Comp("RectTransform")]
        for entry in components:
            comps.append(entry if isinstance(entry, Comp) else self._comp(entry if isinstance(entry, str) else entry[0]))
        obj.components = comps
        obj.rect = dict(RECT_DEFAULT, **rect)
        self._relayout(obj)
        return obj

    def _new_canvas(self, agent, name, mode=0):
        canvas = self._comp("Canvas", m_RenderMode=mode)
        obj = self._ui(agent, name, None, [canvas, "CanvasScaler", "GraphicRaycaster"])
        obj.pos = CANVAS_CENTER if mode != 2 else (0.0, 0.0, 0.0)
        obj.rect["m_AnchoredPosition"] = obj.pos[:2]
        return obj

    def _skill_ui_create_canvas(self, agent, args):
        mode = ENUMS["m_RenderMode"].get(args.get("renderMode") or "ScreenSpaceOverlay", 0)
        canvas = self._new_canvas(agent, args.get("name") or "Canvas", mode)
        return {"success": True, "name": canvas.name, "entityId": canvas.eid, "instanceId": canvas.iid,
                "renderMode": args.get("renderMode") or "ScreenSpaceOverlay"}

    def _skill_ui_create_panel(self, agent, args):
        holder = self._canvas_for(agent, args.get("parent"))
        color = tuple(float(args.get(key, default)) for key, default in (("r", 1), ("g", 1), ("b", 1), ("a", 0.4)))
        panel = self._ui(agent, args.get("name") or "Panel", holder, ["CanvasRenderer", self._comp("Image", m_Color=color)],
                         m_AnchorMin=(0.0, 0.0), m_AnchorMax=(1.0, 1.0), m_SizeDelta=(0.0, 0.0))
        return {"success": True, "name": panel.name, "entityId": panel.eid, "instanceId": panel.iid,
                "parent": holder.name}

    def _skill_ui_create_image(self, agent, args):
        holder = self._canvas_for(agent, args.get("parent"))
        size = (float(args.get("width", 100)), float(args.get("height", 100)))
        image = self._ui(agent, args.get("name") or "Image", holder, ["CanvasRenderer", "Image"], m_SizeDelta=size)
        return {"success": True, "name": image.name, "entityId": image.eid, "instanceId": image.iid,
                "parent": holder.name}

    def _skill_ui_set_rect_transform(self, agent, args):
        obj = self.find(args)
        if obj.rect is None:
            raise _fail("NO_RECT_TRANSFORM", "No RectTransform on %s" % obj.name)
        for x_key, y_key, prop in (("anchorMinX", "anchorMinY", "m_AnchorMin"), ("anchorMaxX", "anchorMaxY",
                                                                                "m_AnchorMax"),
                                   ("pivotX", "pivotY", "m_Pivot"), ("sizeDeltaX", "sizeDeltaY", "m_SizeDelta"),
                                   ("anchoredPosX", "anchoredPosY", "m_AnchoredPosition")):
            old = obj.rect[prop]
            obj.rect[prop] = (float(args[x_key]) if args.get(x_key) is not None else old[0],
                              float(args[y_key]) if args.get(y_key) is not None else old[1])
        if args.get("anchoredPosZ") is not None:
            obj.pos = (obj.pos[0], obj.pos[1], float(args["anchoredPosZ"]))
        obj.scale = tuple(float(args[key]) if args.get(key) is not None else value
                          for key, value in zip(("localScaleX", "localScaleY", "localScaleZ"), obj.scale))
        self._relayout(obj)
        self._change(agent, self._identity(obj, kind="property_changed", targetType="RectTransform",
                                           property="m_AnchoredPosition", previousValue="", value=""))
        return {"success": True, "name": obj.name}

    def _skill_ui_set_rect_transform_batch(self, agent, args):
        results = [self._skill_ui_set_rect_transform(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    # ------------------------------------------------------------ scripts and ScriptableObjects

    def _compile(self):
        OpsFakeEditor._compile(self)
        for _path, source in sorted(self.scripts.items()):
            space = NAMESPACE_RE.search(source)
            for match in CLASS_RE.finditer(source):
                self.namespaces[match.group(1)] = space.group(1) if space else None

    def _skill_script_create_batch(self, agent, args):
        results = [self._skill_script_create(agent, item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_script_read(self, agent, args):
        path = args["scriptPath"]
        if path not in self.scripts:
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        return {"success": True, "path": path, "content": self.scripts[path]}

    def _skill_script_list(self, agent, args):
        folder, wanted = (args.get("folder") or "Assets").rstrip("/"), str(args.get("filter") or "").lower()
        rows = [{"name": os.path.splitext(os.path.basename(path))[0], "path": path} for path in sorted(self.scripts)
                if (path + "/").startswith(folder + "/") and wanted in os.path.basename(path).lower()]
        limit = int(args.get("limit") or 100)
        return {"success": True, "count": min(len(rows), limit), "scripts": rows[:limit]}

    def _skill_scriptableobject_list_types(self, agent, args):
        wanted = str(args.get("filter") or "").lower()
        rows = [{"name": name, "fullName": self._full(name)} for name, spec in sorted(self.classes.items())
                if spec.get("base") == "ScriptableObject" and wanted in name.lower()]
        return {"success": True, "count": len(rows), "types": rows[:int(args.get("limit") or 50)]}

    def _skill_scriptableobject_set_batch(self, agent, args):
        fields = json.loads(args["fields"]) if isinstance(args["fields"], str) else args["fields"]
        for name, value in fields.items():
            self._set_so(agent, args["assetPath"], name, {"value": value})
        return {"success": True, "assetPath": args["assetPath"], "count": len(fields)}

    def _write_so(self, path):
        record = self.sos[path]
        spec = self.classes[record["type"]]
        lines = ["--- !u!114 &11400000", "MonoBehaviour:", "  m_ObjectHideFlags: 0",
                 "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
                 "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: 0}", "  m_Enabled: 1",
                 "  m_EditorHideFlags: 0", "  m_Script: {fileID: 11500000, guid: %s, type: 3}" % self._guid(spec["path"]),
                 "  m_Name: %s" % os.path.splitext(os.path.basename(path))[0], "  m_EditorClassIdentifier: "]
        for name, value in record["fields"].items():
            if isinstance(value, tuple) or value is None:
                lines.append("  %s: %s" % (name, self._ref_yaml(value)))
            elif isinstance(value, bool):
                lines.append("  %s: %d" % (name, int(value)))
            elif isinstance(value, float):
                lines.append("  %s: %s" % (name, _yf(value)))
            else:
                lines.append("  %s: %s" % (name, value))
        self._write_file(path, HEADER + "\n".join(lines) + "\n")

    # ------------------------------------------------------------ prefab assets: trees, links, fileIDs

    def _asset(self, path):
        asset = self.assets.get(path)
        if asset is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % path)
        return asset

    def _alloc(self, asset):
        asset.seq += 1
        return int(hashlib.md5(("%s/%d" % (asset.path, asset.seq)).encode("utf-8")).hexdigest()[:15], 16) or 1

    def _blank(self, scene, name, parent):
        if scene:
            eid, iid = self._new_ids()
        else:
            eid, iid = None, 0
        node = self._attrs(Obj(eid, iid, name, parent))
        node.components = []
        if scene:
            self.objects[eid] = node
            self.owners[self.transform_id(node)] = eid
        return node

    def _copy_tree(self, node, parent, scene, link=None):
        """A copy of a subtree; link=path makes every copied object and component a link to the one it copies in the
        asset at that path (an instance), else the copies keep the links of the originals."""
        copy = self._blank(scene, node.name, parent)
        copy.active, copy.tag, copy.layer = node.active, node.tag, node.layer
        copy.pos, copy.rot, copy.scale = node.pos, node.rot, node.scale
        copy.rect = dict(node.rect) if node.rect is not None else None
        copy.mesh, copy.materials = node.mesh, list(node.materials)
        copy.src = (link, node.fid) if link else node.src
        copy.components = [comp.copy((link, comp.fid) if link else None) for comp in node.components]
        copy.children = [self._copy_tree(child, copy, scene, link) for child in node.children]
        return copy

    def _is_root_of_instance(self, node):
        if node.src is None:
            return False
        source = self.assets.get(node.src[0])
        return source is not None and node.src[1] == source.root.fid

    def _instance_root(self, node):
        """The root of the instance a linked object belongs to (its nearest ancestor that links to a source root)."""
        current = node
        while current is not None:
            if current.src is not None and current.src[0] == node.src[0] and self._is_root_of_instance(current):
                return current
            current = current.parent
        return None

    def _outermost(self, obj):
        """PrefabUtility.GetOutermostPrefabInstanceRoot: the topmost ancestor-or-self that roots an instance."""
        found, current = None, obj
        while current is not None:
            if self._is_root_of_instance(current):
                found = current
            current = current.parent
        return found

    def _rel(self, root, node):
        parts = []
        while node is not None and node is not root:
            parts.append(node.name)
            node = node.parent
        return "/".join(reversed(parts))

    def _resolve(self, asset, rel):
        node = asset.root
        for name in [part for part in str(rel or "").split("/") if part]:
            node = next((child for child in node.children if child.name == name), None)
            if node is None:
                raise _fail("GAMEOBJECT_NOT_FOUND", "No node %s in %s" % (rel, asset.path))
        return node

    def _settle(self, asset):
        """fileIDs for objects that have none (own objects get fresh ones, linked ones derive from their instance),
        the fileID index, and the RectTransform layout."""
        for node in self.walk([asset.root]):
            if node.src is None:
                if node.fid is None:
                    node.fid = self._alloc(asset)
                for comp in node.components:
                    if comp.fid is None:
                        comp.fid = self._alloc(asset)
                continue
            root = self._instance_root(node)
            if root.inst_fid is None:
                root.inst_fid = self._alloc(asset)
            node.fid = _derive(root.inst_fid, node.src[1])
            for comp in node.components:
                if comp.src is not None:
                    comp.fid = _derive(root.inst_fid, comp.src[1])
                elif comp.fid is None:
                    comp.fid = self._alloc(asset)
        asset.nodes, asset.comps = {}, {}
        for node in self.walk([asset.root]):
            asset.nodes[node.fid] = node
            for comp in node.components:
                asset.comps[comp.fid] = (node, comp)
        self._relayout(asset.root)

    def _link(self, scene_node, asset_node, path):
        scene_node.src = (path, asset_node.fid)
        for comp, mine in zip(scene_node.components, asset_node.components):
            comp.src = (path, mine.fid)
        for child, mine in zip(scene_node.children, asset_node.children):
            self._link(child, mine, path)

    def _save(self, agent, asset):
        self._settle(asset)
        self._write_file(asset.path, self._yaml(asset))
        self._imported(agent, [asset.path])

    # ------------------------------------------------------------ overrides: diff, resync, closure

    def _diff(self, root):
        source = self.assets[root.src[0]]
        diff = Diff()

        def visit(node, mine):
            if node is not root and node.name != mine.name:
                diff.names[mine.fid] = node.name
            theirs = self._go_leaves(mine)
            for leaf, (kind, value) in self._go_leaves(node).items():
                if not _same(kind, value, theirs[leaf][1]):
                    diff.props.append((mine.fid, None, leaf, kind, value))
            by_fid = {comp.fid: comp for comp in mine.components}
            seen = set()
            for comp in node.components:
                if comp.src is not None and comp.src[0] == source.path and comp.src[1] in by_fid and \
                        comp.src[1] not in seen:
                    other = by_fid[comp.src[1]]
                    seen.add(other.fid)
                    theirs_c = self._leaves(mine, other)
                    for leaf, (kind, value) in self._leaves(node, comp).items():
                        if leaf not in theirs_c or not _same(kind, value, theirs_c[leaf][1]):
                            diff.props.append((mine.fid, other.fid, leaf, kind, value))
                else:
                    diff.added_comps.append((mine.fid, comp))
            for other in mine.components:
                if other.fid not in seen:
                    diff.removed.append((mine.fid, other.fid))
            kids = {child.fid: child for child in mine.children}
            for index, child in enumerate(node.children):
                if child.src is not None and child.src[0] == source.path and child.src[1] in kids:
                    visit(child, kids[child.src[1]])
                else:
                    diff.added_gos.append((mine.fid, index, child))

        visit(root, source.nodes[root.src[1]])
        return diff

    def _resync(self, agent, root, diff, scene):
        """Rebuilds the instance `root` from its source as it is now, plus the overrides `diff` it had; objects that
        still exist keep their identity, the root keeps its name and default overrides (Unity never inherits them)."""
        source = self.assets[root.src[0]]
        old = {}

        def collect(node):
            if node.src is not None and node.src[0] == source.path:
                old[node.src[1]] = node
                for child in node.children:
                    collect(child)

        collect(root)
        before = self.walk([root])
        kept = (root.pos, root.rot, dict(root.rect) if root.rect is not None else None)
        added, extra, props = {}, {}, {}
        for gfid, index, sub in diff.added_gos:
            added.setdefault(gfid, []).append((index, sub))
        for gfid, comp in diff.added_comps:
            extra.setdefault(gfid, []).append(comp)
        for gfid, cfid, leaf, _kind, value in diff.props:
            props.setdefault(gfid, []).append((cfid, leaf, value))
        removed = set(diff.removed)
        fresh = []

        def build(mine, parent):
            node = old.pop(mine.fid, None)
            if node is None:
                node = self._blank(scene, mine.name, parent)
                fresh.append(node)
            node.parent = parent
            node.src = (source.path, mine.fid)
            if node is not root:
                node.name = diff.names.get(mine.fid, mine.name)
            node.active, node.tag, node.layer = mine.active, mine.tag, mine.layer
            node.pos, node.rot, node.scale = mine.pos, mine.rot, mine.scale
            node.rect = dict(mine.rect) if mine.rect is not None else None
            node.mesh, node.materials = mine.mesh, list(mine.materials)
            existing = {comp.src[1]: comp for comp in node.components
                        if comp.src is not None and comp.src[0] == source.path}
            comps = []
            for other in mine.components:
                if (mine.fid, other.fid) in removed:
                    continue
                comp = existing.get(other.fid) or Comp(other[0])
                comp[0], comp[1], comp.values, comp.src = other[0], other[1], dict(other.values), (source.path,
                                                                                                   other.fid)
                comps.append(comp)
            node.components = comps + extra.get(mine.fid, [])
            for cfid, leaf, value in props.get(mine.fid, []):
                target = None if cfid is None else next((c for c in node.components
                                                         if c.src == (source.path, cfid)), None)
                if cfid is None or target is not None:
                    self._set_leaf(node, target, leaf, value)
            children = [build(child, node) for child in mine.children]
            for index, sub in sorted(added.get(mine.fid, []), key=lambda item: item[0]):
                sub.parent = node
                children.insert(min(index, len(children)), sub)
            node.children = children
            return node

        build(source.nodes[root.src[1]], root.parent)
        root.pos, root.rot = kept[0], kept[1]
        if kept[2] is not None and root.rect is not None:
            root.rect = kept[2]
        self._relayout(root)
        if scene:
            for node in fresh:
                self._change(agent, self._identity(node, kind="gameobject_created"))
            now = {id(node) for node in self.walk([root])}
            for node in before:
                if id(node) not in now:
                    self.objects.pop(node.eid, None)
                    self._change(agent, {"kind": "gameobject_destroyed", "name": node.name, "entityId": node.eid,
                                         "instanceId": node.iid, "path": self.path(root) + "/" + node.name,
                                         "scenePath": self.scene_path})

    def _instances_of(self, path):
        """(container asset or None for the scene, root) of every instance of the asset at `path`."""
        source = self.assets[path]
        key = (path, source.root.fid)
        out = []
        for asset in self.assets.values():
            if asset.path != path:
                out += [(asset, node) for node in self.walk([asset.root]) if node.src == key]
        out += [(None, node) for node in self.walk() if node.src == key]
        return out

    def _closure(self, path):
        """Every instance an edit of the asset at `path` reaches, in the order they must follow it."""
        order, queue, seen = [], [path], {path}
        while queue:
            current = queue.pop(0)
            for container, root in self._instances_of(current):
                order.append((container, root))
                if container is not None and container.path not in seen:
                    seen.add(container.path)
                    queue.append(container.path)
        return order

    def _asset_edit(self, agent, path, fn, skip=None):
        """fn(asset) edits the asset at `path` (loaded, edited, saved, as PrefabAssetEditor does); the change then
        reaches every instance in the scene and in other assets, whose files stay as they are."""
        asset = self._asset(path)
        plan = [(container, root, self._diff(root)) for container, root in self._closure(path) if root is not skip]
        result = fn(asset)
        self._save(agent, asset)
        for container, root, diff in plan:
            self._resync(agent, root, diff, scene=container is None)
            if container is not None:
                self._settle(container)
        return result

    # ------------------------------------------------------------ prefab skills: create, instantiate, variants

    @staticmethod
    def _prefab_path(path):
        return path if path.endswith(".prefab") else path + ".prefab"

    def _skill_prefab_create(self, agent, args):
        obj = self.find(args)
        path = self._prefab_path(args["savePath"])
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        root = self._copy_tree(obj, None, scene=False)
        root.name = os.path.splitext(os.path.basename(path))[0]
        asset = Asset(path, self._guid(path), root, variant=self._is_root_of_instance(obj))
        self.assets[path] = asset
        self.prefabs[path] = {"name": root.name, "mesh": obj.mesh}
        self._settle(asset)
        self._link(obj, root, path)
        self._save(agent, asset)
        return {"success": True, "prefabPath": path, "name": root.name, "connected": True}

    def _instantiate(self, agent, path, item):
        asset = self._asset(path)
        parent = self.find(item, "parent", required=False)
        obj = self._copy_tree(asset.root, parent, scene=True, link=path)
        (parent.children if parent is not None else self.roots).append(obj)
        obj.pos = (float(item.get("x") or 0), float(item.get("y") or 0), float(item.get("z") or 0))
        if obj.rect is not None:
            self._sync_rect(obj)
        if any(item.get(key) for key in ("rotX", "rotY", "rotZ")):
            self._set_world(obj, rot=q_from_euler([float(item.get(key) or 0) for key in ("rotX", "rotY", "rotZ")]))
        if any(item.get(key) not in (None, 1) for key in ("scaleX", "scaleY", "scaleZ")):
            obj.scale = tuple(float(item.get(key, 1)) for key in ("scaleX", "scaleY", "scaleZ"))
        if item.get("name"):
            obj.name = item["name"]
        self._relayout(obj)
        self._change(agent, self._identity(obj, kind="gameobject_created"))
        pos = self.world(obj)[0]
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
                "position": dict(zip("xyz", pos)), "localPosition": dict(zip("xyz", obj.pos))}

    def _skill_prefab_instantiate(self, agent, args):
        return self._instantiate(agent, args["prefabPath"], args)

    def _skill_prefab_instantiate_batch(self, agent, args):
        results = [self._instantiate(agent, item["prefabPath"], item) for item in json.loads(args["items"])]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_prefab_create_variant(self, agent, args):
        source = self._asset(args["sourcePrefabPath"])
        path = self._prefab_path(args["variantPath"])
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        root = self._copy_tree(source.root, None, scene=False, link=source.path)
        root.name = os.path.splitext(os.path.basename(path))[0]
        asset = Asset(path, self._guid(path), root, variant=True)
        self.assets[path] = asset
        self.prefabs[path] = {"name": root.name, "mesh": root.mesh}
        self._save(agent, asset)
        return {"success": True, "sourcePath": source.path, "variantPath": path, "name": root.name, "isVariant": True}

    def _skill_prefab_find_instances(self, agent, args):
        path = args["prefabPath"]
        self._asset(path)
        rows = [{"name": node.name, "path": self.path(node), "entityId": node.eid, "instanceId": node.iid,
                 "active": self.active_in_hierarchy(node), "nested": self._outermost(node) is not node}
                for container, node in self._instances_of(path) if container is None and
                (args.get("includeInactive") or self.active_in_hierarchy(node))]
        limit = int(args.get("limit") or 50)
        return {"success": True, "prefabPath": path, "count": min(len(rows), limit), "totalMatched": len(rows),
                "truncated": len(rows) > limit, "instances": rows[:limit]}

    # ------------------------------------------------------------ prefab skills: overrides, apply, revert

    def _instance_target(self, args):
        root = self._outermost(self.find(args))
        if root is None:
            raise _fail("NOT_A_PREFAB_INSTANCE", "Not a prefab instance")
        return root

    def _override_rows(self, root, diff):
        source = self.assets[root.src[0]]
        by_src = {}

        def index(node):
            if node.src is not None and node.src[0] == source.path:
                by_src[node.src[1]] = node
                for child in node.children:
                    index(child)

        index(root)
        rows = []
        for gfid, cfid, leaf, kind, value in diff.props:
            node = by_src[gfid]
            comp = None if cfid is None else next(c for c in node.components if c.src == (source.path, cfid))
            row = {"kind": "property", "gameObjectPath": self._rel(root, node),
                   "component": "GameObject" if comp is None else comp[0],
                   "componentIndex": 0 if comp is None else self._index_of(node, comp), "property": leaf,
                   "value": self._scalar(kind, value)}
            if kind == R and value is not None:
                row["objectReference"] = self._ref_text(value)
            rows.append(row)
        for gfid, comp in diff.added_comps:
            node = by_src[gfid]
            rows.append({"kind": "addedComponent", "gameObjectPath": self._rel(root, node), "component": comp[0],
                         "componentIndex": self._index_of(node, comp)})
        for gfid, cfid in diff.removed:
            rows.append({"kind": "removedComponent", "gameObjectPath": self._rel(root, by_src[gfid]),
                         "component": source.comps[cfid][1][0]})
        for _gfid, _index, sub in diff.added_gos:
            rows.append({"kind": "addedGameObject", "gameObjectPath": self._rel(root, sub), "component": "GameObject",
                         "name": sub.name})
        return rows

    def _skill_prefab_get_overrides(self, agent, args):
        root = self._instance_target(args)
        diff = self._diff(root)
        rows = self._override_rows(root, diff)
        counts = {"propertyOverrides": len(diff.props), "addedComponents": len(diff.added_comps),
                  "removedComponents": len(diff.removed), "addedGameObjects": len(diff.added_gos)}
        res = dict(counts, success=True, prefabPath=root.src[0], hasOverrides=any(counts.values()))
        if args.get("includeDetails"):
            limit = int(args.get("limit") if args.get("limit") is not None else 50)
            res.update({"count": min(len(rows), limit), "totalMatched": len(rows), "truncated": len(rows) > limit,
                        "overrides": rows[:limit]})
        return res

    def _push(self, asset, root):
        """ApplyPrefabInstance: the instance's state written into its asset, the root's name and default overrides
        left out; added objects and components become the asset's own, and the instance links to them."""

        def visit(node, mine, top):
            if not top:
                mine.name = node.name
                mine.pos, mine.rot = node.pos, node.rot
                mine.rect = dict(node.rect) if node.rect is not None else None
            mine.scale = node.scale
            mine.active, mine.tag, mine.layer = node.active, node.tag, node.layer
            by_fid = {comp.fid: comp for comp in mine.components}
            kept = []
            for comp in node.components:
                if comp.src is not None and comp.src[0] == asset.path and comp.src[1] in by_fid:
                    other = by_fid[comp.src[1]]
                    other[1], other.values = comp[1], dict(comp.values)
                    kept.append(other)
                else:
                    other = comp.copy(None)
                    other.fid = self._alloc(asset)
                    comp.src = (asset.path, other.fid)
                    kept.append(other)
            mine.components = kept
            kids = {child.fid: child for child in mine.children}
            children = []
            for child in node.children:
                if child.src is not None and child.src[0] == asset.path and child.src[1] in kids:
                    visit(child, kids[child.src[1]], False)
                    children.append(kids[child.src[1]])
                else:
                    copy = self._copy_tree(child, mine, scene=False)
                    for item in self.walk([copy]):
                        item.src, item.fid = None, None
                        for comp in item.components:
                            comp.src, comp.fid = None, None
                    for item in self.walk([copy]):
                        item.fid = self._alloc(asset)
                        for comp in item.components:
                            comp.fid = self._alloc(asset)
                    self._link(child, copy, asset.path)
                    children.append(copy)
            mine.children = children

        visit(root, asset.nodes[root.src[1]], True)

    def _skill_prefab_apply(self, agent, args):
        root = self._instance_target(args)
        path = root.src[0]
        self._asset_edit(agent, path, lambda asset: self._push(asset, root), skip=root)
        self._change(agent, self._identity(root, kind="prefab_applied"))
        return {"success": True, "appliedTo": path}

    def _skill_prefab_apply_overrides(self, agent, args):
        return self._skill_prefab_apply(agent, args)

    def _skill_prefab_revert_overrides(self, agent, args):
        root = self._instance_target(args)
        self._resync(agent, root, Diff(), scene=True)
        self._change(agent, self._identity(root, kind="prefab_reverted"))
        return {"success": True, "reverted": root.name}

    # ------------------------------------------------------------ prefab asset reads

    def _describe_node(self, asset, node):
        out = {"gameObject": node.name, "gameObjectPath": self._rel(asset.root, node),
               "parentPath": None if node is asset.root else self._rel(asset.root, node.parent),
               "activeSelf": bool(node.active), "layer": LAYERS.get(node.layer, 0), "tag": node.tag,
               "siblingIndex": node.parent.children.index(node) if node.parent is not None else 0,
               "childCount": len(node.children), "localPosition": dict(zip("xyz", node.pos)),
               "localEulerAngles": dict(zip("xyz", euler_from_q(node.rot))), "localScale": dict(zip("xyz", node.scale)),
               "prefabAssetType": "Variant" if asset.variant else "Regular"}
        if node.rect is not None:
            out.update({"anchoredPosition": dict(zip("xy", node.rect["m_AnchoredPosition"])),
                        "anchorMin": dict(zip("xy", node.rect["m_AnchorMin"])),
                        "anchorMax": dict(zip("xy", node.rect["m_AnchorMax"])),
                        "pivot": dict(zip("xy", node.rect["m_Pivot"])),
                        "sizeDelta": dict(zip("xy", node.rect["m_SizeDelta"]))})
        return out

    def _skill_prefab_get_hierarchy(self, agent, args):
        asset = self._asset(args["prefabPath"])
        limit = int(args.get("limit") or 200)
        nodes = self.walk([asset.root])
        return {"success": True, "prefabPath": asset.path, "total": len(nodes), "truncated": len(nodes) > limit,
                "nodes": [self._describe_node(asset, node) for node in nodes[:limit]]}

    def _skill_prefab_get_info(self, agent, args):
        asset = self._asset(args["prefabPath"])
        return dict(self._describe_node(asset, self._resolve(asset, args.get("gameObjectPath"))), success=True)

    def _skill_prefab_list_components(self, agent, args):
        asset = self._asset(args["prefabPath"])
        node = self._resolve(asset, args.get("gameObjectPath"))
        out = []
        for comp in node.components:
            row = {"componentType": self._full(comp[0]), "componentIndex": self._index_of(node, comp)}
            if comp[1] is not None:
                row["enabled"] = comp[1]
            if args.get("includeProperties"):
                row["properties"] = self._props(node, comp)
            out.append(row)
        return {"success": True, "gameObjectPath": self._rel(asset.root, node), "components": out}

    def _skill_prefab_get_serialized_properties(self, agent, args):
        asset = self._asset(args["prefabPath"])
        node = self._resolve(asset, args.get("gameObjectPath"))
        comp = self._find_comp(node, args["componentType"], args.get("componentIndex", 0))
        return {"success": True, "gameObjectPath": self._rel(asset.root, node), "componentType": self._full(comp[0]),
                "componentIndex": self._index_of(node, comp), "properties": self._props(node, comp)}

    # ------------------------------------------------------------ prefab asset edits

    def _set_transform(self, node, args):
        """PrefabAssetEditor.SetTransform: localPosition, localEulerAngles, localScale, then the RectTransform fields
        (anchors keep anchoredPosition, so posX/posY land under the anchors the node had before this call)."""

        def axes(prefix, current):
            return tuple(float(args[prefix + axis]) if args.get(prefix + axis) is not None else value
                         for axis, value in zip("XYZ", current))

        self._set_local_position(node, axes("pos", node.pos))
        if any(args.get("rot" + axis) is not None for axis in "XYZ"):
            node.rot = q_from_euler(axes("rot", euler_from_q(node.rot)))
        node.scale = axes("scale", node.scale)
        rect_args = [key for key in args if key.startswith(("anchor", "pivot", "sizeDelta")) and args[key] is not None]
        if node.rect is None:
            if rect_args:
                raise _fail("SKILL_ERROR", "RectTransform layout fields require a RectTransform")
            return
        for prefix, name in (("anchorMin", "m_AnchorMin"), ("anchorMax", "m_AnchorMax"), ("pivot", "m_Pivot"),
                             ("sizeDelta", "m_SizeDelta"), ("anchoredPos", "m_AnchoredPosition")):
            node.rect[name] = axes(prefix, node.rect[name] + (0.0,))[:2]
        self._relayout(node)

    def _op(self, asset, skill, args):
        """One prefab asset operation (the prefab_* edit skills and prefab_edit_batch steps)."""
        if skill == "prefab_create_gameobject":
            parent = self._resolve(asset, args.get("parentPath"))
            if any(child.name == args["name"] for child in parent.children):
                raise _fail("SKILL_ERROR", "A sibling is already called %s" % args["name"])
            node = self._blank(False, args["name"], parent)
            rect = bool(args.get("rectTransform"))
            node.components = [Comp("RectTransform" if rect else "Transform")]
            node.rect = dict(RECT_DEFAULT) if rect else None
            parent.children.append(node)
            return {"gameObjectPath": self._rel(asset.root, node)}
        node = self._resolve(asset, args.get("gameObjectPath"))
        rel = self._rel(asset.root, node)
        if skill == "prefab_set_transform":
            self._set_transform(node, args)
        elif skill == "prefab_set_active":
            node.active = bool(args["active"])
        elif skill == "prefab_set_sibling_index":
            if node.parent is None or node.parent.src is not None:
                raise _fail("SKILL_ERROR", "the children of %s belong to another prefab" % rel)
            siblings = node.parent.children
            siblings.remove(node)
            siblings.insert(max(0, min(int(args["index"]), len(siblings))), node)
        elif skill == "prefab_add_component":
            node.components.append(self._comp(args["componentType"]))
        elif skill == "prefab_set_serialized_property":
            comp = self._find_comp(node, args["componentType"], args.get("componentIndex", 0))
            self._set_property(node, comp, args["propertyPath"], args)
        elif skill == "prefab_set_property":
            comp = self._find_comp(node, args["componentType"], args.get("componentIndex", 0))
            self._set_property(node, comp, self._property_path(comp, args["propertyName"]), args)
        else:
            raise _fail("SKILL_NOT_FOUND", "prefab operation %s is not modelled" % skill)
        return {"gameObjectPath": rel}

    def _edit(self, agent, skill, args):
        path = args["prefabPath"]
        result = self._asset_edit(agent, path, lambda asset: self._op(asset, skill, args))
        return dict(result, success=True, prefabPath=path)

    def _edit_batch(self, agent, skill, args):
        groups = []
        for item in json.loads(args["items"]):
            if not groups or groups[-1][0] != item["prefabPath"]:
                groups.append((item["prefabPath"], []))
            groups[-1][1].append(item)
        results = []
        for path, items in groups:
            done = self._asset_edit(agent, path, lambda asset, items=items: [self._op(asset, skill, item)
                                                                            for item in items])
            results += [dict(row, success=True, prefabPath=path) for row in done]
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_prefab_edit_batch(self, agent, args):
        steps = json.loads(args["steps"]) if isinstance(args["steps"], str) else args["steps"]
        path = args["prefabPath"]
        results = self._asset_edit(agent, path, lambda asset: [self._op(asset, step["skill"], step.get("args") or {})
                                                               for step in steps])
        return {"success": True, "prefabPath": path, "count": len(results), "results": results}

    def __getattr__(self, name):
        """prefab_<edit> and prefab_<edit>_batch for every operation _op models."""
        for skill in ("prefab_create_gameobject", "prefab_set_transform", "prefab_set_active",
                      "prefab_set_sibling_index", "prefab_add_component", "prefab_set_serialized_property",
                      "prefab_set_property"):
            if name == "_skill_" + skill:
                return lambda agent, args, skill=skill: self._edit(agent, skill, args)
            if name == "_skill_" + skill + "_batch":
                return lambda agent, args, skill=skill: self._edit_batch(agent, skill, args)
        raise AttributeError(name)

    # ------------------------------------------------------------ YAML

    def _yaml_fields(self, comp, indent="  "):
        lines, parents = [], set()
        for name, kind, _default in self._schema(comp[0]):
            if name in ("m_Enabled", "m_Script"):
                continue
            value = comp.values.get(name)
            text = self._yaml_value(kind, value)
            if ".Array.data[" in name:
                base = name.split(".Array.")[0]
                lines += ["%s%s:" % (indent, base), "%s- %s" % (indent, text)]
            elif "." in name:
                base, _dot, leaf = name.partition(".")
                if base not in parents:
                    parents.add(base)
                    lines.append("%s%s:" % (indent, base))
                lines.append("%s  %s: %s" % (indent, leaf, text))
            else:
                lines.append("%s%s: %s" % (indent, name, text))
        return lines

    def _yaml_value(self, kind, value):
        if kind in LEAVES:
            return "{%s}" % ", ".join("%s: %s" % (axis, _yf(item)) for axis, item in zip(LEAVES[kind], value))
        if kind == R:
            return self._ref_yaml(value)
        return self._scalar(kind, value)

    @staticmethod
    def _head(class_id, fid, type_name, stripped=False):
        return ["--- !u!%d &%d%s" % (class_id, fid, " stripped" if stripped else ""), "%s:" % type_name]

    def _go_doc(self, node):
        lines = self._head(1, node.fid, "GameObject") + [
            "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
            "  m_PrefabAsset: {fileID: 0}", "  serializedVersion: 6", "  m_Component:"]
        lines += ["  - component: {fileID: %d}" % comp.fid for comp in node.components]
        lines += ["  m_Layer: %d" % LAYERS.get(node.layer, 0), "  m_Name: %s" % node.name,
                  "  m_TagString: %s" % node.tag, "  m_Icon: {fileID: 0}", "  m_NavMeshLayer: 0",
                  "  m_StaticEditorFlags: 0", "  m_IsActive: %d" % int(bool(node.active))]
        return lines

    def _transform_doc(self, node):
        comp = node.components[0]
        lines = self._head(CLASS_IDS[comp[0]], comp.fid, comp[0]) + [
            "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
            "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: %d}" % node.fid, "  serializedVersion: 2",
            "  m_LocalRotation: %s" % self._yaml_value(Q, node.rot),
            "  m_LocalPosition: %s" % self._yaml_value(V3, node.pos),
            "  m_LocalScale: %s" % self._yaml_value(V3, node.scale), "  m_ConstrainProportionsScale: 0"]
        if node.children:
            lines += ["  m_Children:"] + ["  - {fileID: %d}" % child.components[0].fid for child in node.children]
        else:
            lines.append("  m_Children: []")
        lines += ["  m_Father: {fileID: %d}" % (node.parent.components[0].fid if node.parent is not None else 0),
                  "  m_LocalEulerAnglesHint: %s" % self._yaml_value(V3, euler_from_q(node.rot))]
        if node.rect is not None:
            lines += ["  %s: %s" % (name, self._yaml_value(V2, node.rect[name])) for name in RECT_FIELDS]
        return lines

    def _comp_doc(self, node, comp):
        mono = comp[0] in self.classes or comp[0] in UI_SCRIPTS
        lines = self._head(114 if mono else CLASS_IDS[comp[0]], comp.fid, "MonoBehaviour" if mono else comp[0]) + [
            "  m_ObjectHideFlags: 0", "  m_CorrespondingSourceObject: {fileID: 0}", "  m_PrefabInstance: {fileID: 0}",
            "  m_PrefabAsset: {fileID: 0}", "  m_GameObject: {fileID: %d}" % node.fid]
        if comp[1] is not None:
            lines.append("  m_Enabled: %d" % int(bool(comp[1])))
        if mono:
            script = comp.values.get("m_Script") or ("uiscript", comp[0])
            lines += ["  m_EditorHideFlags: 0", "  m_Script: %s" % self._ref_yaml(script), "  m_Name: ",
                      "  m_EditorClassIdentifier: "]
        return lines + self._yaml_fields(comp)

    def _stripped(self, node, comp=None):
        root = self._instance_root(node)
        source = self.assets[node.src[0]]
        if comp is None:
            fid, class_id, type_name, src_fid = node.fid, 1, "GameObject", node.src[1]
        else:
            fid, class_id, type_name, src_fid = comp.fid, CLASS_IDS[comp[0]], comp[0], comp.src[1]
        return self._head(class_id, fid, type_name, stripped=True) + [
            "  m_CorrespondingSourceObject: {fileID: %d, guid: %s, type: 3}" % (src_fid, source.guid),
            "  m_PrefabInstance: {fileID: %d}" % root.inst_fid, "  m_PrefabAsset: {fileID: 0}"]

    def _instance_doc(self, root):
        source = self.assets[root.src[0]]
        diff = self._diff(root)
        parent = root.parent.components[0].fid if root.parent is not None else 0
        lines = self._head(1001, root.inst_fid, "PrefabInstance") + [
            "  m_ObjectHideFlags: 0", "  serializedVersion: 2", "  m_Modification:", "    serializedVersion: 3",
            "    m_TransformParent: {fileID: %d}" % parent, "    m_Modifications:"]
        mods = [(source.root.fid, "m_Name", S, root.name)]
        mods += [(gfid, "m_Name", S, name) for gfid, name in sorted(diff.names.items())]
        mods += [(gfid if cfid is None else cfid, leaf, kind, value) for gfid, cfid, leaf, kind, value in diff.props]
        for target, leaf, kind, value in mods:
            lines += ["    - target: {fileID: %d, guid: %s, type: 3}" % (target, source.guid),
                      "      propertyPath: %s" % leaf, "      value: %s" % self._scalar(kind, value),
                      "      objectReference: %s" % (self._ref_yaml(value) if kind == R else "{fileID: 0}")]
        if diff.removed:
            lines += ["    m_RemovedComponents:"] + ["    - {fileID: %d, guid: %s, type: 3}" % (cfid, source.guid)
                                                     for _gfid, cfid in diff.removed]
        else:
            lines.append("    m_RemovedComponents: []")
        lines.append("    m_RemovedGameObjects: []")
        if diff.added_gos:
            lines.append("    m_AddedGameObjects:")
            for gfid, _index, sub in diff.added_gos:
                lines += ["    - targetCorrespondingSourceObject: {fileID: %d, guid: %s, type: 3}" % (
                    source.nodes[gfid].components[0].fid, source.guid), "      insertIndex: -1",
                    "      addedObject: {fileID: %d}" % sub.components[0].fid]
        else:
            lines.append("    m_AddedGameObjects: []")
        if diff.added_comps:
            lines.append("    m_AddedComponents:")
            for gfid, comp in diff.added_comps:
                lines += ["    - targetCorrespondingSourceObject: {fileID: %d, guid: %s, type: 3}" % (gfid, source.guid),
                          "      insertIndex: -1", "      addedObject: {fileID: %d}" % comp.fid]
        else:
            lines.append("    m_AddedComponents: []")
        return lines + ["  m_SourcePrefab: {fileID: 100100000, guid: %s, type: 3}" % source.guid]

    def _yaml(self, asset):
        docs = {}
        for node in self.walk([asset.root]):
            if node.src is None:
                docs[node.fid] = self._go_doc(node)
                docs[node.components[0].fid] = self._transform_doc(node)
                for comp in node.components[1:]:
                    docs[comp.fid] = self._comp_doc(node, comp)
                if node.parent is not None and node.parent.src is not None:
                    docs.setdefault(node.parent.components[0].fid,
                                    self._stripped(node.parent, node.parent.components[0]))
                continue
            for comp in node.components:
                if comp.src is None:
                    docs[comp.fid] = self._comp_doc(node, comp)
                    docs.setdefault(node.fid, self._stripped(node))
            if self._is_root_of_instance(node) and self._instance_root(node) is node:
                docs[node.inst_fid] = self._instance_doc(node)
                if node.parent is not None and node.parent.src is None:
                    docs.setdefault(node.components[0].fid, self._stripped(node, node.components[0]))
        return HEADER + "".join("\n".join(docs[fid]) + "\n" for fid in sorted(docs))

# Producer:Betsy
