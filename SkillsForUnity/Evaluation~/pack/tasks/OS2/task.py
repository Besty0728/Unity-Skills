"""OS2: an overlay Canvas and non-UI scene branches with asset folders (ops, standard).

The Canvas starts empty. Its name and reference resolution supply the menu's names, labels and dimensions.
Branch count, nesting, sibling order, names, local poses, collider sizes and asset folders come from ctx.rng.
"""
import hashlib
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask

WORDS = ("Aster", "Cedar", "Mica", "Tidal", "Vale", "Wren")
DRIVEN = ("m_LocalPosition", "m_LocalScale", "m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta")


def menu_layout(name, canvas_units):
    """Menu dimensions in reference-resolution units, and names containing the Canvas name."""
    x, y = canvas_units
    left, top = x / 80, y / 60
    right, bottom = left + y / 120, top + x / 320
    return {"panel": "Menu_" + name, "buttons": [name + "_Play", name + "_Settings", name + "_Exit"],
            "texts": ["Play " + name, "Settings " + name, "Exit " + name],
            "width": x / 4, "height": y / 2, "spacing": x / 160,
            "padding": [left, right, top, bottom], "buttonWidth": x / 4 - left - right, "buttonHeight": y / 12}


def serialized(ctx, ref, kind):
    rows = ctx.read("component_get_serialized_properties", componentType=kind, includeChildren=True,
                    includeHidden=True, limit=5000, **V.address(ref))
    props = rows.get("properties") or []
    if rows.get("success") is False or len(props) >= 5000:
        raise ValueError("Component properties are unavailable or incomplete")
    return {row["propertyPath"]: row.get("value") for row in props}


def object_state(ctx, ref):
    p, h = ctx.probe, ctx.handles
    info = p.go(ref)
    if not info:
        return None
    ids = {str(V.object_id(item)) for item in h["objects"]}
    state = {key: info.get(key) for key in ("name", "path", "parentPath", "isActive", "tag", "layer")}
    state["children"] = [str(V.object_id(child)) for child in info.get("children") or []
                         if str(V.object_id(child)) in ids]
    components = []
    for comp in p.components(ref):
        kind = comp.get("fullType") or comp["type"]
        props = serialized(ctx, ref, kind)
        drops = ("m_Children",)
        if V.same_id(ref, h["canvas"]) and comp["type"] == "RectTransform":
            drops += DRIVEN
        props = {key: val for key, val in props.items()
                 if not any(key == field or key.startswith(field + ".") for field in drops)}
        components.append([kind, comp.get("enabled"), props])
    state["components"] = components
    return state


def asset_tree(ctx):
    """Directories and raw file digests under A; run scene contents are omitted and metadata is retained."""
    base = os.path.join(ctx.project_path, *ctx.A.split("/"))
    scene = os.path.join(ctx.project_path, *ctx.scene.split("/"))
    rows = []
    for folder, dirs, files in os.walk(base):
        dirs.sort()
        rel = os.path.relpath(folder, base).replace(os.sep, "/")
        rows.append(["directory", rel])
        for name in sorted(files):
            path = os.path.join(folder, name)
            if path == scene:
                continue
            with open(path, "rb") as handle:
                digest = hashlib.sha256(handle.read()).hexdigest()
            rows.append(["file", os.path.relpath(path, base).replace(os.sep, "/"), digest])
    with open(base + ".meta", "rb") as handle:
        rows.append(["metadata", ".", hashlib.sha256(handle.read()).hexdigest()])
    return rows


class OS2(PublicTask):
    id = "OS2"
    track = "ops"
    tier = "standard"
    case_version = 1
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.ugui"]}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def word():
            return "%s_%d" % (rng.choice(WORDS), rng.randrange(100, 1000))

        def make(parent):
            return V.object_id(c.write("gameobject_create", name=word(), x=rng.randint(-4, 4),
                                      y=rng.randint(-2, 3), z=rng.randint(-5, 5), rotY=rng.choice((0, 15, 35)),
                                      **V.address(parent, "parent")))

        holders = [ctx.root]
        for _index in range(rng.randint(2, 4)):
            parent = make(ctx.root)
            holders.append(parent)
            for _depth in range(rng.randint(0, 2)):
                parent = make(parent)
                holders.append(parent)
            c.write("component_add", componentType="UnityEngine.BoxCollider", **V.address(parent))
            c.write("component_set_property", componentType="UnityEngine.BoxCollider", propertyName="size",
                    value="%d,%d,%d" % (rng.randint(1, 3), rng.randint(2, 4), rng.randint(1, 4)), **V.address(parent))
        canvas = V.object_id(c.write("ui_create_canvas", name=word(), renderMode="ScreenSpaceOverlay"))
        c.write("gameobject_set_parent", **dict(V.address(canvas, "child"), **V.address(rng.choice(holders), "parent")))
        for prop, value in (("uiScaleMode", "ScaleWithScreenSize"),
                            ("referenceResolution", "%d,%d" % (rng.choice((1280, 1600, 1920)),
                                                               rng.choice((720, 960, 1200)))),
                            ("screenMatchMode", "MatchWidthOrHeight"), ("matchWidthOrHeight", "0.5")):
            c.write("component_set_property", componentType="UnityEngine.UI.CanvasScaler", propertyName=prop,
                    value=value, **V.address(canvas))
        c.write("component_set_property", componentType="UnityEngine.Canvas", propertyName="additionalShaderChannels",
                value="TexCoord1,Normal,Tangent", **V.address(canvas))
        for parent in holders:
            kids = list(ctx.probe.children(parent))
            rng.shuffle(kids)
            for index, child in enumerate(kids):
                c.write("gameobject_set_sibling_index", index=index, **V.address(child))
        folders = []
        for _index in range(rng.randint(1, 3)):
            folder = ctx.asset(word())
            c.write("asset_create_folder", folderPath=folder)
            folders.append(folder)
            if rng.choice((False, True)):
                folder += "/" + word()
                c.write("asset_create_folder", folderPath=folder)
                folders.append(folder)
        ids = [V.object_id(row) for row in V.structure(ctx.probe, [ctx.root])]
        rng.shuffle(ids)
        return {"canvas": canvas, "canvasPath": ctx.probe.go(canvas)["path"], "objects": ids, "folders": folders}

    def snapshot(self, ctx):
        return {"data": {}, "digests": {
            "objects": {str(ref): V.canonical_digest(object_state(ctx, ref)) for ref in ctx.handles["objects"]},
            "files": V.canonical_digest(asset_tree(ctx))}}

    def prompt_vars(self, ctx):
        name = ctx.probe.go(ctx.handles["canvas"])["name"]
        res = V.floats(serialized(ctx, ctx.handles["canvas"], "UnityEngine.UI.CanvasScaler")["m_ReferenceResolution"])
        spec = menu_layout(name, res)
        return {"canvas": ctx.handles["canvasPath"], "canvas_units": "%g, %g" % tuple(res),
                "panel": spec["panel"], "width": spec["width"], "height": spec["height"],
                "spacing": spec["spacing"], "padding": ", ".join("%g" % v for v in spec["padding"]),
                "button_width": spec["buttonWidth"], "button_height": spec["buttonHeight"],
                "first": spec["buttons"][0], "second": spec["buttons"][1], "third": spec["buttons"][2],
                "text_first": spec["texts"][0], "text_second": spec["texts"][1], "text_third": spec["texts"][2]}

    def answer_schema(self):
        return {"type": "object", "required": ["panelPath", "buttonPaths"], "properties": {
            "panelPath": {"type": "string", "desc": {"zh-CN": "面板的实际完整层级路径；不存在时为空字符串",
                                                       "en": "actual full hierarchy path of the panel; empty if absent"}},
            "buttonPaths": {"type": "array", "items": {"type": "string", "desc": {
                "zh-CN": "本轮菜单按钮的实际完整路径，按 R 中深度优先、兄弟顺序排列",
                "en": "actual full paths of the menu buttons in depth-first sibling order within R"}}}}}


TASK = OS2()

# Producer:Betsy
