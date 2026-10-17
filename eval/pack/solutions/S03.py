"""S03 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

LAMP_POS = (0.5, 1.2, -0.3)
LAMP_ROT = (10.0, 20.0, 0.0)
LAMP_SCALE = 0.8
BULB_POS = (0.0, 0.4, 0.0)


def _answer(ctx, ref):
    info = ctx.probe.go(ref) or {}
    props = ctx.c.read("component_get_properties", componentType="Transform", **V.address(ref))
    values = {prop.get("name"): V.floats(prop.get("value")) for prop in props.get("properties") or []}
    return {"newPath": info.get("path"), "localPosition": values.get("localPosition"),
            "localEulerAngles": values.get("localEulerAngles"), "localScale": values.get("localScale"),
            "declaredComplete": True}


def _reparent(ctx, child, parent):
    ctx.c.write("gameobject_set_parent", **dict(V.address(child, "child"), **V.address(parent, "parent")))


def _local_pose(ctx, ref, world_rot=False):
    c = ctx.c
    c.write("gameobject_set_transform", localPosX=LAMP_POS[0], localPosY=LAMP_POS[1], localPosZ=LAMP_POS[2],
            scaleX=LAMP_SCALE, scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE, **V.address(ref))
    if world_rot:
        c.write("gameobject_set_transform", rotX=LAMP_ROT[0], rotY=LAMP_ROT[1], rotZ=LAMP_ROT[2], **V.address(ref))
    else:
        c.write("component_set_property", componentType="Transform", propertyName="localEulerAngles",
                value="%g,%g,%g" % LAMP_ROT, **V.address(ref))


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    h = ctx.handles
    _reparent(ctx, h["lamp"], h["props"])
    _local_pose(ctx, h["lamp"])
    return _answer(ctx, h["lamp"])


def only_reparent(ctx):
    h = ctx.handles
    _reparent(ctx, h["lamp"], h["props"])
    return _answer(ctx, h["lamp"])


def world_rot(ctx):
    h = ctx.handles
    _reparent(ctx, h["lamp"], h["props"])
    _local_pose(ctx, h["lamp"], world_rot=True)
    return _answer(ctx, h["lamp"])


def recreate(ctx):
    h, c = ctx.handles, ctx.c
    c.write("gameobject_delete", **V.address(h["lamp"]))
    lamp = V.object_id(c.write("gameobject_create", name="Lamp", x=LAMP_POS[0], y=LAMP_POS[1], z=LAMP_POS[2],
                               rotX=LAMP_ROT[0], rotY=LAMP_ROT[1], rotZ=LAMP_ROT[2], scaleX=LAMP_SCALE,
                               scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE, **V.address(h["props"], "parent")))
    c.write("component_add", componentType="AudioSource", **V.address(lamp))
    c.write("component_add", componentType="BoxCollider", **V.address(lamp))
    c.write("component_set_enabled", componentType="BoxCollider", enabled=False, **V.address(lamp))
    c.write("gameobject_create", name="Bulb", primitiveType="Sphere", x=BULB_POS[0], y=BULB_POS[1], z=BULB_POS[2],
            scaleX=0.2, scaleY=0.2, scaleZ=0.2, **V.address(lamp, "parent"))
    return _answer(ctx, lamp)


def first_child(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_sibling_index", index=0, **V.address(ctx.handles["lamp"]))
    return answer


def edit_bulb(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_transform", localPosY=0.6, **V.address(ctx.handles["bulb"]))
    return answer


def move_shelf_lamp(ctx):
    h = ctx.handles
    _reparent(ctx, h["shelfLamp"], h["props"])
    _local_pose(ctx, h["shelfLamp"])
    return _answer(ctx, h["shelfLamp"])


def wrong_report(ctx):
    answer = reference(ctx)
    answer["localScale"] = [0.5333, 0.5333, 0.5333]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayLamp")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "lamp.txt"), b"moved\n")
    return answer


def serialized_quaternion(ctx):
    h, c = ctx.handles, ctx.c
    _reparent(ctx, h["lamp"], h["props"])
    q = V.quat_from_euler(*LAMP_ROT)
    for prop, value in (("m_LocalPosition", "%g,%g,%g" % LAMP_POS), ("m_LocalRotation", "%.9f,%.9f,%.9f,%.9f" % q),
                        ("m_LocalScale", "%g,%g,%g" % ((LAMP_SCALE,) * 3))):
        c.write("component_set_serialized_property", componentType="Transform", propertyPath=prop, value=value,
                **V.address(h["lamp"]))
    return _answer(ctx, h["lamp"])


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("only_reparent", only_reparent, {"B2", "B3", "B4"}, "set_parent alone keeps the world pose"),
    Negative("world_rot", world_rot, {"B3"}, "rotX/Y/Z of set_transform are world angles"),
    Negative("recreate", recreate, {"B1", "B5"}, "a new Lamp replaces the original"),
    Negative("first_child", first_child, {"B10"}),
    Negative("edit_bulb", edit_bulb, {"B5"}),
    Negative("move_shelf_lamp", move_shelf_lamp, {"B1", "B6"}, "the other Lamp is moved"),
    Negative("wrong_report", wrong_report, {"B7"}),
    Negative("outside_root", outside_root, {"B8"}),
    Negative("direct_file", direct_file, {"B9"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("serialized_quaternion", serialized_quaternion), ("save_run_scene", save_run_scene)]

# Producer:Betsy
