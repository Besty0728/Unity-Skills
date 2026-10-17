"""T3 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

MASS = 2.0
FRICTION = (0.8, 0.9, 0.0)
TAG_MANAGER = "ProjectSettings/TagManager.asset"
LINK_NAME = "PlankLink"


def _number(ident):
    """An object id as the integer the answer schema asks for (entityId texts are decimal)."""
    return int(ident) if isinstance(ident, str) else ident


def _numbering(ctx):
    """The planks by ascending world X, read from the editor."""
    return ctx.private.by_x([(ref, ((ctx.probe.go(ref) or {}).get("position") or {}).get("x", 0.0))
                             for ref in ctx.handles["planks"]])


def _answer(ctx, order=None, layer_index=None, path=None):
    h = ctx.handles
    order = order or _numbering(ctx)
    return {"planks": [{"number": index + 1, "instanceId": _number(ref)} for index, ref in enumerate(order)],
            "materialPath": path or h["materialPath"],
            "plankLayerIndex": h["plankLayer"]["index"] if layer_index is None else layer_index,
            "declaredComplete": True}


def _create_material(ctx, friction=FRICTION, defaults=False):
    path = ctx.handles["materialPath"]
    args = dict(name=os.path.splitext(os.path.basename(path))[0], savePath=path.rsplit("/", 1)[0])
    if not defaults:
        args.update(dynamicFriction=friction[0], staticFriction=friction[1], bounciness=friction[2])
    return ctx.c.write("physics_create_material", **args).get("path")


def _connect(ctx, ref, previous):
    """Sets the connectedBody of ref's HingeJoint to the Rigidbody of the object `previous`."""
    c = ctx.c
    args = dict(componentType="HingeJoint", propertyPath="m_ConnectedBody", objectType="Rigidbody", **V.address(ref))
    if isinstance(previous, int):
        c.write("component_set_serialized_property", referenceInstanceId=previous, **args)
        return
    # Unity 6000.4+ has no legacy ids: a unique temporary name lets a path name the object.
    info = ctx.probe.go(previous) or {}
    c.write("gameobject_rename", newName=LINK_NAME, **V.address(previous))
    try:
        c.write("component_set_serialized_property", referencePath="%s/%s" % (info.get("parentPath"), LINK_NAME),
                **args)
    finally:
        c.write("gameobject_rename", newName=info.get("name"), **V.address(previous))


def _joints(ctx, order, axis="0,0,1", mass=MASS):
    c, previous = ctx.c, ctx.handles["anchor"]
    for ref in order:
        c.write("component_add", componentType="Rigidbody", **V.address(ref))
        if mass is not None:
            c.write("component_set_property", componentType="Rigidbody", propertyName="mass", value=str(mass),
                    **V.address(ref))
        c.write("component_add", componentType="HingeJoint", **V.address(ref))
        _connect(ctx, ref, previous)
        c.write("component_set_property", componentType="HingeJoint", propertyName="axis", value=axis,
                **V.address(ref))
        previous = ref


def _box_material(ctx, path, refs):
    for ref in refs:
        ctx.c.write("component_set_serialized_property", componentType="BoxCollider", propertyPath="m_Material",
                    assetPath=path, **V.address(ref))


def _tool_material(ctx, path):
    for ref in ctx.handles["planks"]:
        ctx.c.write("physics_set_material", materialPath=path, **V.address(ref))


def _layers(ctx, recursive=False, extra=()):
    h = ctx.handles
    items = [dict(V.address(ref), layer=h["plankLayer"]["name"], recursive=recursive)
             for ref in list(h["planks"]) + list(extra)]
    ctx.c.write("gameobject_set_layer_batch", items=json.dumps(items))


def _collision_off(ctx, first=None, second=None):
    h = ctx.handles
    ctx.c.write("physics_set_layer_collision", layer1=h["plankLayer"]["index"] if first is None else first,
                layer2=h["guardLayer"]["index"] if second is None else second, enableCollision=False)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def _solve(ctx, order=None, axis="0,0,1", mass=MASS, friction=FRICTION, via_tool=False, recursive=False, extra=(),
           pair=None, report=None):
    h = ctx.handles
    path = _create_material(ctx, friction)
    chain = order or _numbering(ctx)
    _joints(ctx, chain, axis=axis, mass=mass)
    if via_tool:
        _tool_material(ctx, path)
    else:
        _box_material(ctx, path, h["planks"])
    _layers(ctx, recursive=recursive, extra=extra)
    _collision_off(ctx, *(pair or ()))
    return _answer(ctx, chain, layer_index=report)


def reference(ctx):
    return _solve(ctx)


def set_material_tool(ctx):
    return _solve(ctx, via_tool=True)


def chain_by_sibling(ctx):
    return _solve(ctx, order=list(ctx.handles["planks"]))


def wrong_pair(ctx):
    first = ctx.handles["plankLayer"]["index"]
    return _solve(ctx, pair=(first, first))


def recursive_layer(ctx):
    return _solve(ctx, recursive=True)


def spare_edited(ctx):
    return _solve(ctx, extra=(ctx.handles["spare"],))


def spare_material(ctx):
    answer = reference(ctx)
    _box_material(ctx, ctx.handles["materialPath"], [ctx.handles["spare"]])
    return answer


def material_twice(ctx):
    answer = reference(ctx)
    _create_material(ctx)
    return answer


def axis_x(ctx):
    return _solve(ctx, axis="1,0,0")


def default_mass(ctx):
    return _solve(ctx, mass=None)


def swapped_friction(ctx):
    return _solve(ctx, friction=(0.9, 0.8, 0.0))


def second_hinge(ctx):
    answer = reference(ctx)
    ctx.c.write("component_add", componentType="HingeJoint", **V.address(_numbering(ctx)[2]))
    return answer


def moved_rail(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_transform", localPosY=0.9, **V.address(ctx.handles["rails"][0]))
    return answer


def renamed_layer(ctx):
    answer = reference(ctx)
    ctx.c.write("scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                propertyPath="layers.Array.data[%d]" % ctx.handles["guardLayer"]["index"], value="EviiiGuard")
    return answer


def wrong_report(ctx):
    return _solve(ctx, report=ctx.handles["guardLayer"]["index"])


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayBridge")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "bridge.txt"), b"linked\n")
    return answer


def property_route(ctx):
    """Joints first (the HingeJoint brings its Rigidbody), then serialized properties and reflection setters."""
    c, h = ctx.c, ctx.handles
    path = _create_material(ctx, defaults=True)
    for name, value in zip(("dynamicFriction", "staticFriction", "bounciness"), FRICTION):
        c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath=name, value=str(value))
    order = _numbering(ctx)
    previous = h["anchor"]
    for ref in order:
        c.write("component_add", componentType="HingeJoint", **V.address(ref))
        c.write("component_set_serialized_property", componentType="Rigidbody", propertyPath="m_Mass",
                value=str(MASS), **V.address(ref))
        _connect(ctx, ref, previous)
        c.write("component_set_serialized_property", componentType="HingeJoint", propertyPath="m_Axis",
                value="0,0,1", **V.address(ref))
        c.write("component_set_property", componentType="BoxCollider", propertyName="sharedMaterial",
                assetPath=path, **V.address(ref))
        previous = ref
    _layers(ctx)
    _collision_off(ctx, h["guardLayer"]["index"], h["plankLayer"]["index"])
    return _answer(ctx, order)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("set_material_tool", set_material_tool, {"P6"}, "physics_set_material writes to the first collider"),
    Negative("chain_by_sibling", chain_by_sibling, {"P2", "P11"}, "numbered by hierarchy order, not by world X"),
    Negative("wrong_pair", wrong_pair, {"P8"}, "the layer collides with itself no more, the pair is left alone"),
    Negative("recursive_layer", recursive_layer, {"P7"}, "the Rail children change layer too"),
    Negative("spare_edited", spare_edited, {"P9"}, "the Plank under Spare is moved to the layer"),
    Negative("spare_material", spare_material, {"P6"}, "the Plank under Spare gets the material"),
    Negative("material_twice", material_twice, {"P5"}),
    Negative("axis_x", axis_x, {"P3"}),
    Negative("default_mass", default_mass, {"P1"}, "the Rigidbody keeps mass 1"),
    Negative("swapped_friction", swapped_friction, {"P4"}),
    Negative("second_hinge", second_hinge, {"P1"}, "a second HingeJoint on one plank"),
    Negative("moved_rail", moved_rail, {"P9"}),
    Negative("renamed_layer", renamed_layer, {"P8"}, "the second layer is renamed"),
    Negative("wrong_report", wrong_report, {"P11"}),
    Negative("outside_root", outside_root, {"P10"}),
    Negative("direct_file", direct_file, {"P12"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("property_route", property_route), ("save_run_scene", save_run_scene)]

# Producer:Betsy
