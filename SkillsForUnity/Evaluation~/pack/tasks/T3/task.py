"""T3: a hinge bridge of five same-name planks, a physic material and a layer collision pair (ops, standard).

Fixture: R/Bridge holds AnchorLeft (a cube with a kinematic Rigidbody), Planks (five cubes named Plank in a shuffled
sibling order, each with a Rail child, one of them with a SphereCollider ahead of its BoxCollider) and Spare (one more
Plank). The tag manager names the layers in slots 30 and 31 and every collision pair involving those slots starts
enabled; the runner restores the pairs and teardown puts the layer names back. A/Physics exists.
"""
import re
import time

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

TAG_MANAGER = "ProjectSettings/TagManager.asset"
LAYER_SLOTS = (30, 31)
FIRST_USER_LAYER = 8
LAYER_NAME = re.compile(r"^Ev[a-z]{3}(Plank|Guard)$")
PLANK_COUNT = 5
PLANK_X = [round(1.1 * (index + 1), 1) for index in range(PLANK_COUNT)]
SPHERE_RANK = 2
PAUSE = 0.02


def _pairs():
    return tuple(sorted({tuple(sorted((other, slot))) for slot in LAYER_SLOTS for other in range(32)}))


def layer_names(client):
    """{slot: name} of the named layers."""
    layers = client.read("editor_get_layers").get("layers") or []
    return {int(row["index"]): row.get("name") for row in layers}


def _claim_layers(ctx, names):
    held = layer_names(ctx.c)
    for slot in LAYER_SLOTS:
        if held.get(slot) and not LAYER_NAME.match(held[slot]):
            raise ValueError("layer slot %d is named %r; this task needs the layer slots %s unused" % (
                slot, held[slot], LAYER_SLOTS))
    for slot, name in zip(LAYER_SLOTS, names):
        ctx.c.write("scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                    propertyPath="layers.Array.data[%d]" % slot, value=name)
    now = layer_names(ctx.c)
    if [now.get(slot) for slot in LAYER_SLOTS] != list(names):
        raise ValueError("the tag manager did not take the layer names %s (found %s)" % (
            list(names), [now.get(slot) for slot in LAYER_SLOTS]))


def _collision(ctx, pair):
    time.sleep(PAUSE)
    return ctx.c.read("physics_get_layer_collision", layer1=pair[0], layer2=pair[1]).get("collisionEnabled")


class T3(PublicTask):
    id = "T3"
    track = "ops"
    tier = "standard"
    case_version = 2
    minutes = 25
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    layer_pairs = _pairs()

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        token = "".join(rng.choice("abcdefghjkmnpqrstuvwxyz") for _ in range(3))
        order = list(range(PLANK_COUNT))
        while order == sorted(order):
            rng.shuffle(order)
        plank_layer, guard_layer = "Ev%sPlank" % token, "Ev%sGuard" % token
        layers_at_start = {str(slot): name for slot, name in layer_names(c).items()
                           if slot >= FIRST_USER_LAYER and slot not in LAYER_SLOTS}
        _claim_layers(ctx, (plank_layer, guard_layer))
        for pair in self.layer_pairs:
            if not _collision(ctx, pair):
                c.write("physics_set_layer_collision", layer1=pair[0], layer2=pair[1], enableCollision=True)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        bridge = make("Bridge", ctx.root)
        anchor = make("AnchorLeft", bridge, primitiveType="Cube", x=0, y=1, z=0)
        c.write("component_add", componentType="Rigidbody", **address(anchor))
        c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic", value="true",
                **address(anchor))
        planks_root = make("Planks", bridge)
        planks, rails = [], []
        for rank in order:
            plank = make("Plank", planks_root, primitiveType="Cube", x=PLANK_X[rank], y=1, z=0, scaleX=1,
                         scaleY=0.2, scaleZ=0.8)
            if rank == SPHERE_RANK:
                c.write("component_remove", componentType="BoxCollider", **address(plank))
                c.write("component_add", componentType="SphereCollider", **address(plank))
                c.write("component_add", componentType="BoxCollider", **address(plank))
            planks.append(plank)
            rails.append(make("Rail", plank, y=0.6))
        spare_root = make("Spare", bridge)
        spare = make("Plank", spare_root, primitiveType="Cube", x=7.7, y=1, z=0, scaleX=1, scaleY=0.2, scaleZ=0.8)
        c.write("asset_create_folder", folderPath=ctx.asset("Physics"))
        return {"bridge": bridge, "anchor": anchor, "planksRoot": planks_root, "planks": planks, "rails": rails,
                "spareRoot": spare_root, "spare": spare,
                "anchorPath": ctx.path("Bridge", "AnchorLeft"), "planksPath": ctx.path("Bridge", "Planks"),
                "sparePath": ctx.path("Bridge", "Spare"),
                "materialPath": ctx.asset("Physics", "BridgeGrip_%s.physicMaterial" % token),
                "layersAtStart": layers_at_start,
                "plankLayer": {"index": LAYER_SLOTS[0], "name": plank_layer},
                "guardLayer": {"index": LAYER_SLOTS[1], "name": guard_layer}}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        collision = {"%d,%d" % pair: _collision(ctx, pair) for pair in self.layer_pairs}
        if not all(collision.values()):
            raise ValueError("the collision pairs of layers %s could not be enabled: %s" % (
                LAYER_SLOTS, sorted(key for key, enabled in collision.items() if not enabled)))
        return {"data": {
            "bridge": V.go_state(p, h["bridge"]),
            "planksRoot": V.go_state(p, h["planksRoot"]),
            "planks": [[ref, V.go_state(p, ref)] for ref in h["planks"]],
            "rails": [[ref, V.go_state(p, ref)] for ref in h["rails"]],
            "anchor": {"go": V.go_state(p, h["anchor"]), "rb": p.ser(h["anchor"], "Rigidbody"),
                       "box": p.ser(h["anchor"], "BoxCollider")},
            "spare": {"go": V.go_state(p, h["spare"]), "box": p.ser(h["spare"], "BoxCollider"),
                      "root": V.go_state(p, h["spareRoot"])},
            "collision": collision,
            "layers": {str(slot): name for slot, name in sorted(layer_names(ctx.c).items())}},
            "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"planks": h["planksPath"], "anchor": h["anchorPath"], "spare": h["sparePath"],
                "mat": h["materialPath"], "l1": h["plankLayer"]["name"], "i1": h["plankLayer"]["index"],
                "l2": h["guardLayer"]["name"], "i2": h["guardLayer"]["index"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["planks", "materialPath", "plankLayerIndex"],
            "properties": {
                "planks": {"type": "array", "items": {"type": "object", "properties": {
                    "number": {"type": "integer", "desc": {
                        "zh-CN": "木板编号（1 到 5，按世界坐标 X 从小到大）",
                        "en": "plank number (1 to 5, by ascending world X)"}},
                    "instanceId": {"type": "integer", "desc": {
                        "zh-CN": "该木板的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of that plank (entityId on Unity 6000.4+)"}},
                }}},
                "materialPath": {"type": "string", "desc": {"zh-CN": "物理材质的资产路径",
                                                            "en": "asset path of the physic material"}},
                "plankLayerIndex": {"type": "integer", "desc": {"zh-CN": "木板所在 Layer 的层序号",
                                                                "en": "layer index of the layer the planks are on"}},
            },
        }

    def teardown(self, ctx):
        """Puts the tag manager's layer names back. With the handles of a finished build every user layer returns to
        its name before the build and the two slots of this task end up unnamed; without them (the build stopped
        midway) only names of this task's own kind in its two slots are cleared."""
        c = ctx.c
        start = (ctx.handles or {}).get("layersAtStart")
        held = layer_names(c)
        wanted = {}
        for slot in range(FIRST_USER_LAYER, 32):
            if slot in LAYER_SLOTS:
                if start is not None or LAYER_NAME.match(held.get(slot) or ""):
                    wanted[slot] = ""
            elif start is not None:
                wanted[slot] = start.get(str(slot)) or ""
        restored, errors = [], []
        for slot, name in wanted.items():
            if (held.get(slot) or "") != name:
                try:
                    c.write("scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                            propertyPath="layers.Array.data[%d]" % slot, value=name)
                    restored.append(slot)
                except Exception as exc:
                    errors.append("layer %d: %r" % (slot, exc))
        now = layer_names(c)
        left = [slot for slot, name in wanted.items() if (now.get(slot) or "") != name]
        if left:
            raise RuntimeError("layer slots %s still differ from their start (%s)" % (left, errors))
        return {"layersRestored": restored, "errors": errors}


TASK = T3()

# Producer:Betsy
