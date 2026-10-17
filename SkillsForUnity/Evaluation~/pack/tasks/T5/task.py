"""T5: replace one of three same-name prefab instances by an instance of a new prefab variant (ops, standard).

Fixture: A/Prefabs/Crate.prefab (a cube with a Rigidbody of mass 5 and a child Label); R/Depot/Row, a rotated parent
holding Pallet, three Crate instances (seeded world rotations and scales, one with a mass override), Pallet2, in this
sibling order.
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

ROW_POS = (3.0, 0.0, 2.0)
ROW_YAW = 15.0
CRATE_MASS = 5
OVERRIDE_MASS = 9
MARKED_YAW = 75.0
OTHER_YAWS = (30.0, 120.0)
CRATE_SLOTS = (1, 2, 4)


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _prefab_digest(path):
    """Content digest of a prefab file: its parsed documents, so formatting does not matter."""
    return V.canonical_digest([[d.class_id, d.file_id, d.type_name, d.body] for d in V.uyaml.load(path)])


class T5(PublicTask):
    id = "T5"
    track = "ops"
    tier = "standard"
    case_version = 2
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        base = ctx.asset("Prefabs", "Crate.prefab")
        source = make("Crate", ctx.root, primitiveType="Cube")
        c.write("component_add", componentType="Rigidbody", **address(source))
        c.write("component_set_property", componentType="Rigidbody", propertyName="mass", value=str(CRATE_MASS),
                **address(source))
        make("Label", source, y=0.7)
        c.write("prefab_create", savePath=base, **address(source))
        c.write("gameobject_delete", **address(source))

        depot = make("Depot", ctx.root)
        row = make("Row", depot, x=ROW_POS[0], y=ROW_POS[1], z=ROW_POS[2], rotY=ROW_YAW)
        # Crates sit at sibling indices 1, 2 and 4; the one with the marked yaw is at 1 or 2, and the one with the
        # mass override is another of the three.
        marked = rng.choice([1, 2])
        others = [i for i in CRATE_SLOTS if i != marked]
        heavy = rng.choice(others)
        yaws = list(OTHER_YAWS)
        rng.shuffle(yaws)
        yaw_by_index = {marked: MARKED_YAW, others[0]: yaws[0], others[1]: yaws[1]}
        scales = [0.8, 1.1, 0.95]
        rng.shuffle(scales)
        scale_by_index = dict(zip(CRATE_SLOTS, scales))
        scale_by_index[heavy] = 1.2
        children = []
        for index in range(5):
            x = -4.0 + 2.0 * index
            if index in (0, 3):
                ref = make("Pallet" if index == 0 else "Pallet2", row, primitiveType="Cube", x=x, y=-0.4, z=0,
                           scaleY=0.2)
            else:
                # Unique names while the crates are being set up, so every call can address its own crate by path.
                ref = V.object_id(c.write("prefab_instantiate", prefabPath=base, x=x, y=0.5, z=0,
                                          name="Crate_%d" % index, **address(row, "parent")))
                scale = scale_by_index[index]
                c.write("gameobject_set_transform", rotX=0, rotY=yaw_by_index[index], rotZ=0, scaleX=scale,
                        scaleY=scale, scaleZ=scale, **address(ref))
                if index == heavy:
                    c.write("component_set_property", componentType="Rigidbody", propertyName="mass",
                            value=str(OVERRIDE_MASS), **address(ref))
            children.append(ref)
        for index in CRATE_SLOTS:
            c.write("gameobject_rename", newName="Crate", **address(children[index]))
        return {"depot": depot, "row": row, "rowPath": ctx.path("Depot", "Row"), "children": children,
                "basePath": base}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        disk = _disk(ctx, h["basePath"])
        return {"data": {"root": V.go_state(p, ctx.root),
                         "depot": V.go_state(p, h["depot"]),
                         "row": V.go_state(p, h["row"], with_children=False),
                         "children": [[ref, V.go_state(p, ref)] for ref in h["children"]],
                         "baseGuid": V.uyaml.meta_guid(disk)},
                "digests": {"base": _prefab_digest(disk)}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"row": h["rowPath"], "base": h["basePath"], "variant": ctx.asset("Prefabs", "Crate_Heavy.prefab")}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["variantPath", "newInstanceId", "siblingIndex"],
            "properties": {
                "variantPath": {"type": "string", "desc": {"zh-CN": "变体预制体的资产路径",
                                                           "en": "asset path of the prefab variant"}},
                "newInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "新实例的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the new instance (entityId on Unity 6000.4+)"}},
                "siblingIndex": {"type": "integer", "desc": {
                    "zh-CN": "新实例的同级顺序（sibling index，从 0 开始）",
                    "en": "sibling index of the new instance (counting from 0)"}},
            },
        }


TASK = T5()

# Producer:Betsy
