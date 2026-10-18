"""OC8: named layers, a collision matrix and a hierarchy with KeepLayer name markers.

The run folder contains a JSON checkpoint of the project's settings before fixture construction.
"""
import hashlib
import json
import os
import time

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask

TAG = "ProjectSettings/TagManager.asset"
DYNAMICS = "ProjectSettings/DynamicsManager.asset"
PAUSE = 0.02
LIMIT = 10000
PAIRS = tuple((a, b) for a in range(32) for b in range(a, 32))
FORMAT = "OC8-settings-1"


def _rows(result):
    rows = result.get("properties") if isinstance(result, dict) else None
    if not isinstance(rows, list) or not rows or len(rows) >= LIMIT:
        raise ValueError("incomplete serialized properties")
    return sorted([[row["propertyPath"], row.get("value"), row.get("referenceState")]
                   for row in rows], key=lambda row: row[0])


def _without(rows, field):
    return [row for row in rows if row[0] != field and not row[0].startswith(field + ".")]


def _hash_file(path):
    with open(path, "rb") as stream:
        return hashlib.sha256(stream.read()).hexdigest()


class OC8(PublicTask):
    id = "OC8"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 25
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    layer_pairs = PAIRS

    @staticmethod
    def layer_names(ctx):
        rows = ctx.read("editor_get_layers").get("layers")
        if not isinstance(rows, list):
            raise ValueError("unreadable layer names")
        names = [""] * 32
        for row in rows:
            index = row.get("index")
            if type(index) is not int or not 0 <= index < 32 or names[index]:
                raise ValueError("invalid layer list")
            names[index] = row.get("name") or ""
        return names

    @staticmethod
    def matrix(ctx):
        out = {}
        for a, b in PAIRS:
            time.sleep(PAUSE)
            value = ctx.read("physics_get_layer_collision", layer1=a, layer2=b).get("collisionEnabled")
            if type(value) is not bool:
                raise ValueError("unreadable collision pair")
            out["%d,%d" % (a, b)] = value
        return out

    @staticmethod
    def properties(ctx, path):
        return _rows(ctx.read("scriptableobject_get_serialized_properties", assetPath=path,
                              includeChildren=True, includeHidden=True, limit=LIMIT))

    @staticmethod
    def scene_view(ctx, ref):
        info = V.go_state(ctx.probe, ref)
        if info is None:
            return None
        info.pop("layer", None)
        components = ctx.probe.components(ref)
        values = []
        for component in components:
            kind = component.get("fullType") or component.get("type")
            values.append([kind, _rows(ctx.read("component_get_serialized_properties", componentType=kind,
                                               includeChildren=True, includeHidden=True, limit=LIMIT,
                                               **V.address(ref)))])
        return {"info": info, "components": components, "values": values}

    @staticmethod
    def files(ctx, root, scene=False, settings=False, whole=False):
        base = os.path.join(ctx.project_path, *root.split("/"))
        if not os.path.isdir(base):
            raise ValueError("missing folder: " + root)
        out = {}
        if os.path.isfile(base + ".meta"):
            out["@meta"] = _hash_file(base + ".meta")
        for folder, dirs, files in os.walk(base):
            dirs.sort()
            rel = os.path.relpath(folder, base).replace(os.sep, "/")
            out[rel + "/"] = V.canonical_digest("folder")
            for name in sorted(files):
                full = os.path.join(folder, name)
                path = os.path.relpath(full, ctx.project_path).replace(os.sep, "/")
                if scene and path == ctx.scene:
                    continue
                key = os.path.relpath(full, base).replace(os.sep, "/")
                if settings and path in (TAG, DYNAMICS):
                    docs = V.uyaml.load(full)
                    if not docs:
                        raise ValueError("unreadable settings YAML: " + path)
                    content = []
                    for doc in docs:
                        body = dict(doc.body)
                        if not whole:
                            body.pop("layers" if path == TAG else "m_LayerCollisionMatrix", None)
                        content.append([doc.class_id, doc.file_id, doc.type_name, body])
                    out[key] = V.canonical_digest(content)
                else:
                    out[key] = _hash_file(full)
        return out

    @classmethod
    def settings_view(cls, ctx):
        return {"tag": _without(cls.properties(ctx, TAG), "layers"),
                "physics": _without(cls.properties(ctx, DYNAMICS), "m_LayerCollisionMatrix"),
                "gravity": ctx.read("physics_get_gravity"),
                "files": cls.files(ctx, "ProjectSettings", settings=True)}

    @staticmethod
    def nodes(ctx):
        return [[row["id"], row["path"]] for row in V.structure(ctx.probe, ctx.root)]

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        names0 = self.layer_names(ctx)
        free = [i for i in range(8, 32) if not names0[i]]
        if len(free) < 10 or len(set(n for n in names0 if n)) != len([n for n in names0 if n]):
            raise ValueError("OC8 requires ten empty user layer slots and unique nonempty layer names")
        used = set(names0)

        def word(prefix="N"):
            while True:
                name = "%s_%010x" % (prefix, rng.getrandbits(40))
                if name not in used:
                    used.add(name)
                    return name

        folders = []
        for _ in range(rng.randint(2, 4)):
            parent = rng.choice(folders + [ctx.A])
            folder = parent + "/" + word()
            c.write("asset_create_folder", folderPath=folder)
            folders.append(folder)
        checkpoint = rng.choice(folders) + "/" + word() + ".json"
        initial = {"format": FORMAT, "layers": names0, "matrix": self.matrix(ctx),
                   "tag": self.properties(ctx, TAG), "gravity": ctx.read("physics_get_gravity"),
                   "view": V.canonical_digest(self.settings_view(ctx)),
                   "disk": V.canonical_digest(self.files(ctx, "ProjectSettings", settings=True, whole=True))}
        with open(os.path.join(ctx.project_path, *checkpoint.split("/")), "w", encoding="utf-8") as stream:
            json.dump(initial, stream, sort_keys=True, ensure_ascii=False)
            stream.write("\n")
        c.write("asset_refresh")
        digest = _hash_file(os.path.join(ctx.project_path, *checkpoint.split("/")))
        occupied = rng.sample(free, rng.randint(3, min(6, len(free) - 4)))
        rng.shuffle(occupied)
        added = [word("L") for _ in occupied]
        for slot, name in zip(occupied, added):
            c.write("scriptableobject_set_serialized_property", assetPath=TAG,
                    propertyPath="layers.Array.data[%d]" % slot, value=name)
        names = [word("L"), word("L")] + rng.sample(added, 2)
        c_index, d_index = occupied[added.index(names[2])], occupied[added.index(names[3])]
        names1 = self.layer_names(ctx)
        free1 = [i for i in range(8, 32) if not names1[i]]
        changes = {(a, b): True for a in free1 for b in free1 if a <= b}
        changes.update({tuple(sorted((a, c_index))): False for a in free1})
        changes.update({tuple(sorted((a, d_index))): True for a in free1})
        order = list(changes)
        rng.shuffle(order)
        for a, b in order:
            if initial["matrix"]["%d,%d" % (a, b)] != changes[(a, b)]:
                c.write("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=changes[(a, b)])

        originals = [n for n in names1 if n]

        def make(name, parent, layer=None, active=None, body=None):
            args = {"name": name, "x": rng.randint(-7, 7), "y": rng.randint(-3, 5),
                    "z": rng.randint(-9, 9), "rotY": rng.choice((0, 30, 75, 120)),
                    "scaleX": rng.choice((0.75, 1.0, 1.5)), "scaleY": rng.choice((0.8, 1.0, 1.25)),
                    "scaleZ": rng.choice((0.5, 1.0, 2.0))}
            args.update(V.address(parent, "parent"))
            ref = V.object_id(c.write("gameobject_create", **args))
            chosen = layer or rng.choice(originals)
            c.write("gameobject_set_layer_batch", items=json.dumps([dict(V.address(ref), layer=chosen)]))
            if body is True or (body is None and rng.random() < 0.35):
                c.write("component_add", componentType="Rigidbody", **V.address(ref))
                c.write("component_set_property", componentType="Rigidbody", propertyName="mass",
                        value=str(rng.choice((1.5, 3.25, 7.0))), **V.address(ref))
            if active is False or (active is None and rng.random() < 0.2):
                c.write("gameobject_set_active", active=False, **V.address(ref))
            return ref

        site_name = word()
        site = make(site_name, ctx.root)
        tops = []
        for _ in range(rng.randint(3, 5)):
            parent = site
            for _depth in range(rng.randint(1, 3)):
                parent = make(word(), parent)
            tops.append(parent)
        kinds = [0, 1, 0, 1]
        rng.shuffle(kinds)
        for rank, kind in enumerate(kinds):
            root = make(word() + "__" + names[kind], rng.choice(tops), active=(rank != 0))
            copy = make("KeepLayerCopy", root)
            make(word(), copy)
            branch = make(word(), root, active=(rank != 1))
            for _ in range(rng.randint(2, 4)):
                make(word(), rng.choice([root, branch]))
            keep = make("KeepLayer", branch, layer=names[2], body=True)
            leaf = make(word(), keep, layer=names[3])
            make(word() + "__" + names[1 - kind], leaf, layer="Default")
            make("KeepLayer", leaf, layer=names[2])
            nested = make(word() + "__" + names[1 - kind], root)
            make(word(), nested)
        for _ in range(rng.randint(3, 6)):
            make(word(), rng.choice(tops), body=True)
        outside = make(word(), ctx.root, body=True)
        make(word() + "__" + rng.choice(names[:2]), outside, body=True)
        make("KeepLayerCopy", site, body=True)
        return {"site": site, "names": names, "folders": folders, "checkpoint": checkpoint, "digest": digest}

    def snapshot(self, ctx):
        names = self.layer_names(ctx)
        nodes = self.nodes(ctx)
        indices = {name: i for i, name in enumerate(names) if name}
        objects, layers = {}, {}
        for ref, _path in nodes:
            info = ctx.probe.go(ref)
            index = indices.get((info or {}).get("layer"))
            if index is None:
                raise ValueError("object has an unnamed or unreadable layer")
            objects[str(ref)] = V.canonical_digest(self.scene_view(ctx, ref))
            layers[str(ref)] = V.canonical_digest(index)
        return {"data": {"nodes": nodes, "layers": names}, "digests": {
            "objects": objects, "membership": layers,
            "matrix": {key: V.canonical_digest(value) for key, value in self.matrix(ctx).items()},
            "settings": V.canonical_digest(self.settings_view(ctx)),
            "assets": V.canonical_digest(self.files(ctx, ctx.A, scene=True)),
            "labels": V.canonical_digest([ctx.read("asset_get_labels", assetPath=path)
                                           for path in ctx.handles["folders"]])}}

    def prompt_vars(self, ctx):
        names = ctx.handles["names"]
        return {"site": ctx.probe.go(ctx.handles["site"])["path"],
                "l1": names[0], "l2": names[1], "l3": names[2], "l4": names[3]}

    def answer_schema(self):
        return {"type": "object", "required": ["layers"], "properties": {
            "layers": {"type": "array", "items": {"type": "object", "required": ["index", "name"],
                "properties": {
                    "index": {"type": "integer", "desc": {"zh-CN": "改名槽位的最终层序号", "en": "final index of a renamed slot"}},
                    "name": {"type": "string", "desc": {"zh-CN": "该槽位的实际最终名称", "en": "actual final name in that slot"}}}}}}}

    def teardown(self, ctx):
        h = ctx.handles or {}
        path = h.get("checkpoint")
        if not path:
            root = os.path.join(ctx.project_path, *ctx.A.split("/"))
            for folder, _dirs, files in os.walk(root):
                for name in files:
                    if name.endswith(".json"):
                        full = os.path.join(folder, name)
                        try:
                            with open(full, encoding="utf-8") as stream:
                                data = json.load(stream)
                            if data.get("format") == FORMAT:
                                path = os.path.relpath(full, ctx.project_path).replace(os.sep, "/")
                                break
                        except (OSError, ValueError, AttributeError):
                            continue
                if path:
                    break
        if not path:
            if h:
                raise RuntimeError("settings checkpoint is missing")
            return {"restored": False}
        full = os.path.join(ctx.project_path, *path.split("/"))
        if h.get("digest") and _hash_file(full) != h["digest"]:
            raise RuntimeError("settings checkpoint was modified")
        with open(full, encoding="utf-8") as stream:
            data = json.load(stream)
        if data.get("format") != FORMAT or len(data.get("layers", [])) != 32 or len(data.get("matrix", {})) != 528:
            raise RuntimeError("invalid settings checkpoint")
        errors = []

        def put(skill, **args):
            try:
                ctx.c.write(skill, **args)
            except Exception as exc:
                errors.append("%s: %s" % (skill, exc))

        if V.canonical_digest(ctx.read("physics_get_gravity")) != V.canonical_digest(data["gravity"]):
            g = data["gravity"]
            put("physics_set_gravity", x=g["x"], y=g["y"], z=g["z"])
        now = self.matrix(ctx)
        for key, enabled in data["matrix"].items():
            if now[key] != enabled:
                a, b = (int(v) for v in key.split(","))
                put("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=enabled)
        held = self.layer_names(ctx)
        for i, name in enumerate(data["layers"]):
            if held[i] != name:
                put("scriptableobject_set_serialized_property", assetPath=TAG,
                    propertyPath="layers.Array.data[%d]" % i, value=name)
        before_tags = [row for row in data["tag"] if row[0].startswith("tags.")]
        now_tags = [row for row in self.properties(ctx, TAG) if row[0].startswith("tags.")]
        if before_tags != now_tags:
            for prop, value, _state in sorted(before_tags, key=lambda row: (".size" not in row[0], row[0])):
                if ".size" in prop or ".data[" in prop:
                    put("scriptableobject_set_serialized_property", assetPath=TAG, propertyPath=prop, value=str(value or ""))
        clean = (self.layer_names(ctx) == data["layers"] and self.matrix(ctx) == data["matrix"] and
                 V.canonical_digest(self.settings_view(ctx)) == data["view"] and
                 V.canonical_digest(self.files(ctx, "ProjectSettings", settings=True, whole=True)) == data["disk"])
        if not clean:
            raise RuntimeError("project settings still differ from the checkpoint: %s" % errors)
        return {"restored": True, "errors": errors}


TASK = OC8()

# Producer:Betsy
