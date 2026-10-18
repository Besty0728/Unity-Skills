"""OS1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import math
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

ROLES = ("key", "fill", "rim")


def _spec(ctx):
    return ctx.private.rig_spec(ctx.seed)


def _subject(ctx):
    """(world position, world euler) of the subject."""
    info = ctx.probe.go(ctx.handles["subject"]) or {}
    return V.floats(info.get("position")), info.get("rotation")


def _positions(ctx, spec, rotation=None):
    """Role -> world position in the frame of the subject rotated by `rotation` (its world euler by default)."""
    pos, world = _subject(ctx)
    rotation = world if rotation is None else rotation
    return {role: ctx.private.frame_position(pos, rotation, spec["lights"][role]["az"], spec["lights"][role]["el"],
                                             spec["lights"][role]["d"]) for role in ROLES}


def _aim(light_pos, goal, away=False):
    """World euler (rotX, rotY) whose forward points from light_pos at goal (or straight away from it)."""
    v = [g - p for g, p in zip(goal, light_pos)]
    if away:
        v = [-c for c in v]
    norm = math.sqrt(sum(c * c for c in v)) or 1.0
    v = [c / norm for c in v]
    return math.degrees(-math.asin(max(-1.0, min(1.0, v[1])))), math.degrees(math.atan2(v[0], v[2]))


def _light_args(spec, role, spot=True, shadows=None, intensity=None, color=None):
    """light_create arguments for one role; `intensity` is one number for all three lights or a role -> value map."""
    light = spec["lights"][role]
    rgb = color if color is not None else light["color"]
    if isinstance(intensity, dict):
        intensity = intensity.get(role)
    args = dict(name=light["name"], lightType=light["type"], r=rgb[0], g=rgb[1], b=rgb[2],
                intensity=light["intensity"] if intensity is None else intensity, range=spec["range"],
                shadows=shadows or light["shadows"])
    if spot and light["spot"] is not None:
        args["spotAngle"] = light["spot"]
    return args


def _place(ctx, spec, positions, aim=True, away=False, parent=True, roll=0, **tweaks):
    """light_create at the world position, then reparent under the rig and aim at the subject (with `roll` degrees
    around the forward axis, which R3 leaves free)."""
    goal = _subject(ctx)[0]
    ids = {}
    for role in ROLES:
        x, y, z = positions[role]
        ids[role] = V.object_id(ctx.c.write("light_create", x=x, y=y, z=z, **_light_args(spec, role, **tweaks)))
        if parent:
            ctx.c.write("gameobject_set_parent", **dict(V.address(ids[role], "child"),
                                                        **V.address(ctx.handles["rig"], "parent")))
        if aim:
            rot_x, rot_y = _aim(positions[role], goal, away)
            ctx.c.write("gameobject_set_transform", rotX=rot_x, rotY=rot_y, rotZ=roll, **V.address(ids[role]))
    return ids


def _switch_off(ctx):
    ctx.c.write("light_set_enabled", path=ctx.handles["lampPath"], enabled=False)


def _answer(ctx, ids):
    out = {role + "Path": (ctx.probe.go(ids[role]) or {}).get("path") for role in ROLES}
    out["declaredComplete"] = True
    return out


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec))
    _switch_off(ctx)
    return _answer(ctx, ids)


def deactivate_old(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec))
    ctx.c.write("gameobject_set_active", active=False, **V.address(ctx.handles["lamp"]))
    return _answer(ctx, ids)


def component_route(ctx):
    """Empty objects created under the rig, Light components added and configured one property at a time."""
    spec = _spec(ctx)
    positions = _positions(ctx, spec)
    goal = _subject(ctx)[0]
    ids = {}
    for role in ROLES:
        light = spec["lights"][role]
        x, y, z = positions[role]
        rot_x, rot_y = _aim(positions[role], goal)
        ids[role] = V.object_id(ctx.c.write("gameobject_create", name=light["name"], x=x, y=y, z=z, rotX=rot_x,
                                            rotY=rot_y, rotZ=0, space="world",
                                            **V.address(ctx.handles["rig"], "parent")))
        ctx.c.write("component_add", componentType="Light", **V.address(ids[role]))
        ctx.c.write("component_set_property", componentType="Light", propertyName="type", value=light["type"],
                    **V.address(ids[role]))
        path = (ctx.probe.go(ids[role]) or {}).get("path")
        args = {key: value for key, value in _light_args(spec, role).items() if key not in ("name", "lightType")}
        ctx.c.write("light_set_properties", path=path, **args)
    _switch_off(ctx)
    return _answer(ctx, ids)


def batch_props(ctx):
    """Plain lights first, then one light_set_properties_batch and one light_set_enabled_batch."""
    spec = _spec(ctx)
    positions = _positions(ctx, spec)
    goal = _subject(ctx)[0]
    ids = {}
    for role in ROLES:
        light = spec["lights"][role]
        x, y, z = positions[role]
        ids[role] = V.object_id(ctx.c.write("light_create", name=light["name"], lightType=light["type"], x=x, y=y,
                                            z=z))
        ctx.c.write("gameobject_set_parent", **dict(V.address(ids[role], "child"),
                                                    **V.address(ctx.handles["rig"], "parent")))
        rot_x, rot_y = _aim(positions[role], goal)
        ctx.c.write("gameobject_set_transform", rotX=rot_x, rotY=rot_y, rotZ=0, **V.address(ids[role]))
    items = []
    for role in ROLES:
        args = {key: value for key, value in _light_args(spec, role).items() if key not in ("name", "lightType")}
        items.append(dict(args, path=(ctx.probe.go(ids[role]) or {}).get("path")))
    ctx.c.write("light_set_properties_batch", items=json.dumps(items))
    ctx.c.write("light_set_enabled_batch", items=json.dumps([{"path": ctx.handles["lampPath"], "enabled": False}]))
    return _answer(ctx, ids)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


def _intensities(spec, key, offset, of_actual):
    """Key at `key`; fill and rim `offset` away from their percentage of the specified key (of_actual=False) or of
    `key` itself (of_actual=True)."""
    base = key if of_actual else spec["lights"]["key"]["intensity"]
    return {"key": key, "fill": base * spec["lights"]["fill"]["pct"] / 100.0 + offset,
            "rim": base * spec["lights"]["rim"]["pct"] / 100.0 + offset}


def ratio_of_actual_key(ctx):
    """The key 0.009 high and fill/rim 0.009 above their percentage of that actual key (inside R4 by either
    reading)."""
    spec = _spec(ctx)
    key = spec["lights"]["key"]["intensity"] + 0.009
    ids = _place(ctx, spec, _positions(ctx, spec), intensity=_intensities(spec, key, 0.009, True))
    _switch_off(ctx)
    return _answer(ctx, ids)


def intensity_inside_tolerance(ctx):
    """The key 0.009 low and fill/rim 0.009 above their percentage of the specified key."""
    spec = _spec(ctx)
    key = spec["lights"]["key"]["intensity"] - 0.009
    ids = _place(ctx, spec, _positions(ctx, spec), intensity=_intensities(spec, key, 0.009, False))
    _switch_off(ctx)
    return _answer(ctx, ids)


def rolled_aim(ctx):
    """Aimed as the reference, with a 37 degree roll around the forward axis (R3 leaves roll free)."""
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), roll=37)
    _switch_off(ctx)
    return _answer(ctx, ids)


# ---------------------------------------------------------------- negatives


def local_axes(ctx):
    """Positions in the frame of the subject's local rotation (its parent's yaw ignored)."""
    spec = _spec(ctx)
    props = ctx.c.read("component_get_properties", componentType="Transform", **V.address(ctx.handles["subject"]))
    local = next((prop.get("value") for prop in props.get("properties") or []
                  if prop.get("name") == "localEulerAngles"), None)
    ids = _place(ctx, spec, _positions(ctx, spec, rotation=local))
    _switch_off(ctx)
    return _answer(ctx, ids)


def world_axes(ctx):
    """Positions in the world axes, the subject's rotation ignored."""
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec, rotation=(0, 0, 0)))
    _switch_off(ctx)
    return _answer(ctx, ids)


def no_aim(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), aim=False)
    _switch_off(ctx)
    return _answer(ctx, ids)


def aim_away(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), away=True)
    _switch_off(ctx)
    return _answer(ctx, ids)


def flat_intensity(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), intensity=spec["lights"]["key"]["intensity"])
    _switch_off(ctx)
    return _answer(ctx, ids)


def white_colors(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), color=(1, 1, 1))
    _switch_off(ctx)
    return _answer(ctx, ids)


def swapped_shadows(ctx):
    """Key Hard and rim Soft; the fill keeps None through light_set_properties afterwards."""
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), shadows="Hard")
    ctx.c.write("light_set_properties", path=(ctx.probe.go(ids["rim"]) or {}).get("path"), shadows="Soft")
    ctx.c.write("light_set_properties", path=(ctx.probe.go(ids["fill"]) or {}).get("path"), shadows="None")
    _switch_off(ctx)
    return _answer(ctx, ids)


def default_spot(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), spot=False)
    _switch_off(ctx)
    return _answer(ctx, ids)


def fill_as_spot(ctx):
    spec = _spec(ctx)
    spec["lights"]["fill"]["type"] = "Spot"
    ids = _place(ctx, spec, _positions(ctx, spec))
    _switch_off(ctx)
    return _answer(ctx, ids)


def delete_old(ctx):
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec))
    ctx.c.write("gameobject_delete", **V.address(ctx.handles["lamp"]))
    return _answer(ctx, ids)


def leave_old(ctx):
    spec = _spec(ctx)
    return _answer(ctx, _place(ctx, spec, _positions(ctx, spec)))


def root_lights(ctx):
    """The lights stay where light_create put them: at the scene root."""
    spec = _spec(ctx)
    ids = _place(ctx, spec, _positions(ctx, spec), parent=False)
    _switch_off(ctx)
    return _answer(ctx, ids)


def fill_outside_tolerance(ctx):
    """The key exact, fill and rim 0.011 above their percentage of it: outside R4 by either reading."""
    spec = _spec(ctx)
    key = spec["lights"]["key"]["intensity"]
    ids = _place(ctx, spec, _positions(ctx, spec), intensity=_intensities(spec, key, 0.011, True))
    _switch_off(ctx)
    return _answer(ctx, ids)


def key_outside_tolerance(ctx):
    """The key 0.011 high, fill and rim at their percentage of that key: the key misses R4 (fill and rim may still be
    within 0.01 of their percentage of the specified key; a key out of tolerance is no base for the other reading)."""
    spec = _spec(ctx)
    key = spec["lights"]["key"]["intensity"] + 0.011
    ids = _place(ctx, spec, _positions(ctx, spec), intensity=_intensities(spec, key, 0.0, True))
    _switch_off(ctx)
    return _answer(ctx, ids)


def old_shadows(ctx):
    """The reference, then the old lamp's shadows changed (it was switched off as asked)."""
    answer = reference(ctx)
    ctx.c.write("light_set_properties", path=ctx.handles["lampPath"], shadows="Hard")
    return answer


def extra_light(ctx):
    answer = reference(ctx)
    extra = V.object_id(ctx.c.write("light_create", name="Bounce", lightType="Point", x=0, y=2, z=0))
    ctx.c.write("gameobject_set_parent", **dict(V.address(extra, "child"), **V.address(ctx.handles["rig"], "parent")))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["keyPath"], answer["fillPath"] = answer["fillPath"], answer["keyPath"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayStand")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "rig.txt"), b"lit\n")
    return answer


NEGATIVES = [
    Negative("local_axes", local_axes, {"L2"}, "the subject's local yaw used instead of its world rotation"),
    Negative("world_axes", world_axes, {"L2"}, "the world axes used instead of the subject's"),
    Negative("no_aim", no_aim, {"L3"}, "the lights keep their default rotation"),
    Negative("aim_away", aim_away, {"L3"}, "forward points away from the subject"),
    Negative("flat_intensity", flat_intensity, {"L4"}, "fill and rim at the key's intensity"),
    Negative("white_colors", white_colors, {"L5"}),
    Negative("swapped_shadows", swapped_shadows, {"L6"}),
    Negative("default_spot", default_spot, {"L6"}, "spotAngle left at its default"),
    Negative("fill_as_spot", fill_as_spot, {"L1"}, "the fill created as a Spot"),
    Negative("delete_old", delete_old, {"L7"}),
    Negative("leave_old", leave_old, {"L7"}, "the old lamp still lit"),
    Negative("root_lights", root_lights, {"L1", "L10"}, "the lights left at the scene root"),
    Negative("extra_light", extra_light, {"L8"}),
    Negative("wrong_report", wrong_report, {"L9"}),
    Negative("outside_root", outside_root, {"L10"}),
    Negative("direct_file", direct_file, {"L11"}, "a file written to disk next to the skills"),
    Negative("fill_outside_tolerance", fill_outside_tolerance, {"L4"}, "fill and rim 0.011 off their percentage"),
    Negative("key_outside_tolerance", key_outside_tolerance, {"L4"}, "the key 0.011 off its intensity"),
    Negative("old_shadows", old_shadows, {"L7"}, "the old lamp's shadows changed"),
]

ALTERNATIVES = [("deactivate_old", deactivate_old), ("component_route", component_route), ("batch_props", batch_props),
                ("save_run_scene", save_run_scene), ("ratio_of_actual_key", ratio_of_actual_key),
                ("intensity_inside_tolerance", intensity_inside_tolerance), ("rolled_aim", rolled_aim)]

# Producer:Betsy
