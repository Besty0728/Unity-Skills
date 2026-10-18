"""OS1 private verifier: a three-point lighting rig placed in the subject's world frame.

What the task probes: the azimuth/elevation/distance are relative to the subject's world axes, and the subject sits
under a rotated parent, so its local yaw (or the world axes) gives the wrong positions; every light must be aimed
with a world rotation whose forward (+Z) hits the subject (there is no look-at skill: rotY = atan2(dx, dz),
rotX = -asin(dy / distance)); light_create puts the light at the scene root, so it has to be reparented under the
rig; a Spot needs its spotAngle and a Point has none; fill and rim intensities are percentages of the key (of its
specified intensity or of the one it ends up with, R4 allows both); the old lamp is switched off (light disabled or
object inactive) but neither deleted nor otherwise changed: every light_get_info value, the Transform and Light
serialized values and the component list stay, the Light's enabled flag and the object's active state aside.

The rig is recomputed from the seed exactly as the public module does (rig_spec is copied verbatim); the subject's
pose comes from C0, so moving the subject afterwards does not move the goal.
"""
import math
import random

from usk_eval import verifylib as V

ROLES = ("key", "fill", "rim")
NAME_SETS = (("KeyLight", "FillLight", "RimLight"), ("Key", "Fill", "Rim"), ("Light_Key", "Light_Fill", "Light_Rim"))
POSITION_TOL = 0.05      # R2, per component
AIM_TOL = 1.0            # R3, degrees
VALUE_TOL = 0.01         # R4 intensity, R5 colour, R6 range
ANGLE_TOL = 0.1          # R6 spotAngle
LOCAL_TOL = 1e-4         # R7 the old lamp's transform
LAMP_INFO = ("lightType", "color", "intensity", "range", "spotAngle", "shadows", "cullingMask", "bounceIntensity")

ASSERTIONS = [
    ("L1", 1.0, False, ["R1"], "one light per name, each a direct child of the rig with the requested light type"),
    ("L2", 1.5, False, ["R2"], "each light sits at the position given in the subject's world frame (fraction)"),
    ("L3", 1.5, False, ["R3"], "each light's forward axis points at the subject (fraction)"),
    ("L4", 1.0, False, ["R4"], "intensities: the key's value, fill and rim as percentages of it, of the specified "
                               "or of the actual key intensity (fraction)"),
    ("L5", 1.0, False, ["R5"], "colours (fraction)"),
    ("L6", 1.0, False, ["R6"], "shadows, range and the spots' cone angles (fraction)"),
    ("L7", 1.0, False, ["R7"], "the old lamp is switched off but still there; its light properties, serialized "
                               "Transform and Light values, components and transform unchanged"),
    ("L8", 0.75, False, ["R8"], "no other light under R, nothing else under the rig, the subject, stage and rig "
                                "unchanged"),
    ("L9", 0.5, False, ["R9", "G8"], "the answer reports the three lights' paths"),
    ("L10", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("L11", 0.25, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "L1": {"zh-CN": "三盏灯各一个、名字准确、类型为 Spot/Point/Spot，并且都是 rig 的直接子物体（light_create 建在场景根上，要 set_parent）。",
           "en": "One light per name with the right type (Spot/Point/Spot), each a direct child of the rig "
                 "(light_create puts it at the scene root; reparent it)."},
    "L2": {"zh-CN": "方位角/仰角相对 Subject 的世界坐标轴：用 gameobject_get_info 的 rotation（世界欧拉角），不是本地旋转，也不是世界轴。",
           "en": "Azimuth and elevation are relative to the Subject's world axes: use gameobject_get_info's rotation "
                 "(world euler), not the local rotation and not the world axes."},
    "L3": {"zh-CN": "没有 look-at 技能：令 v = Subject − 灯，rotY = atan2(v.x, v.z)，rotX = −asin(v.y/|v|)，用 gameobject_set_transform 写世界欧拉角。",
           "en": "There is no look-at skill: with v = Subject - light, rotY = atan2(v.x, v.z) and "
                 "rotX = -asin(v.y/|v|), written as world euler angles with gameobject_set_transform."},
    "L4": {"zh-CN": "fill 和 rim 的强度是 key 强度的百分比，要换算后写入；每项误差 0.01。",
           "en": "Fill and rim intensities are percentages of the key's; compute them before writing (0.01 each)."},
    "L5": {"zh-CN": "颜色分量是 0 到 1 的浮点数，按提示给的 RGB 写入 r/g/b。",
           "en": "Colour channels are floats from 0 to 1; write the given RGB into r/g/b."},
    "L6": {"zh-CN": "阴影用 None/Hard/Soft；两盏 Spot 要写 spotAngle；三盏灯的 range 都要设置。",
           "en": "Shadows are None/Hard/Soft; both Spots need their spotAngle; set the range on all three."},
    "L7": {"zh-CN": "旧灯用 light_set_enabled(false) 或 gameobject_set_active(false) 停用，不要删除，也不要改它的任何其他属性（阴影、强度等）、组件或变换。",
           "en": "Switch the old lamp off with light_set_enabled(false) or gameobject_set_active(false); do not delete "
                 "it or change any other property (shadows, intensity...), component or its transform."},
    "L8": {"zh-CN": "不要新建多余的灯，不要动 Subject、Stage 和 rig 本身。",
           "en": "Create no extra light and leave the Subject, the stage and the rig themselves alone."},
    "L9": {"zh-CN": "答案里报告三盏灯的实际完整路径，并写明 declaredComplete。",
           "en": "Report the three lights' actual full paths, and declaredComplete."},
    "L10": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "L11": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def rig_spec(seed):
    """The rig the prompt asks for, from the seed alone: per role the light type, azimuth / elevation / distance in
    the subject's frame, intensity (fill and rim as a percentage of the key), colour, cone angle and shadows, plus
    the common range."""
    rng = random.Random("OS1:%d" % seed)
    key, fill, rim = rng.choice(NAME_SETS)
    lights = {
        "key": {"name": key, "type": "Spot", "az": rng.choice((30, 35, 40, 45)), "el": rng.choice((25, 30, 35)),
                "d": rng.choice((3.5, 4.0, 4.5)), "intensity": rng.choice((2.0, 2.5, 3.0)),
                "color": rng.choice(((1, 0.93, 0.8), (1, 0.9, 0.75), (1, 0.95, 0.85))),
                "spot": rng.choice((40, 45, 50)), "shadows": "Soft"},
        "fill": {"name": fill, "type": "Point", "az": -rng.choice((40, 45, 50, 60)), "el": rng.choice((10, 15, 20)),
                 "d": rng.choice((4.5, 5.0, 5.5)), "pct": rng.choice((30, 40, 50)),
                 "color": rng.choice(((0.7, 0.8, 1), (0.75, 0.85, 1), (0.6, 0.75, 1))), "spot": None,
                 "shadows": "None"},
        "rim": {"name": rim, "type": "Spot", "az": rng.choice((150, 160, 170, 180, -160)),
                "el": rng.choice((40, 45, 50)), "d": rng.choice((3.0, 3.5, 4.0)), "pct": rng.choice((70, 80, 120)),
                "color": rng.choice(((1, 1, 1), (0.9, 0.95, 1), (1, 0.98, 0.9))), "spot": rng.choice((20, 25, 35)),
                "shadows": "Hard"},
    }
    lights["fill"]["intensity"] = round(lights["key"]["intensity"] * lights["fill"]["pct"] / 100.0, 4)
    lights["rim"]["intensity"] = round(lights["key"]["intensity"] * lights["rim"]["pct"] / 100.0, 4)
    return {"lights": lights, "range": rng.choice((10, 12, 15))}


# ---------------------------------------------------------------- geometry (Unity conventions, quaternions x y z w)


def _quat(euler):
    values = V.floats(euler)
    return V.quat_from_euler(*values) if len(values) == 3 else None


def _rotate(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    # q * (v, 0) * q^-1, expanded
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx))


def frame_position(subject_position, subject_rotation, az, el, d):
    """World position at distance d, azimuth az and elevation el (degrees) in the frame of a subject at
    `subject_position` with world euler `subject_rotation`."""
    q = _quat(subject_rotation)
    pos = V.floats(subject_position)
    if q is None or len(pos) != 3:
        return None
    a, e = math.radians(az), math.radians(el)
    offset = _rotate(q, (d * math.cos(e) * math.sin(a), d * math.sin(e), d * math.cos(e) * math.cos(a)))
    return tuple(p + o for p, o in zip(pos, offset))


def forward(rotation):
    """World forward (+Z) of an object with world euler `rotation`."""
    q = _quat(rotation)
    return _rotate(q, (0.0, 0.0, 1.0)) if q is not None else None


def aim_error(light_position, light_rotation, subject_position):
    """Angle in degrees between the light's forward and the direction to the subject; None when unreadable."""
    fwd, pos, goal = forward(light_rotation), V.floats(light_position), V.floats(subject_position)
    if fwd is None or len(pos) != 3 or len(goal) != 3:
        return None
    to = [g - p for g, p in zip(goal, pos)]
    norm = math.sqrt(sum(c * c for c in to)) * math.sqrt(sum(c * c for c in fwd))
    if norm == 0:
        return None
    dot = max(-1.0, min(1.0, sum(a * b for a, b in zip(fwd, to)) / norm))
    return math.degrees(math.acos(dot))


# ---------------------------------------------------------------- reading


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _lights(ctx, spec):
    """Per role: (rows named like it, the chosen one's gameobject info, its light info); the one under the rig wins
    when several share the name."""
    rows = (ctx.probe.query({"componentType": "Light", "includeInactive": True, "limit": 500}) or {}).get("objects")
    rows = [row for row in rows or [] if isinstance(row, dict)]
    rig_path = ctx.handles["rigPath"]
    out = {}
    for role in ROLES:
        named = [row for row in rows if row.get("name") == spec["lights"][role]["name"]]
        chosen = next((row for row in named if (row.get("path") or "").rpartition("/")[0] == rig_path),
                      named[0] if named else None)
        info = ctx.probe.go(chosen) if chosen else None
        light = _read(ctx, "light_get_info", path=chosen.get("path")) if chosen and chosen.get("path") else None
        out[role] = (named, info or {}, light or {})
    return out, rows


def _close(actual, wanted, tol):
    values = V.floats(actual)
    return len(values) == 1 and abs(values[0] - wanted) <= tol


def intensity_ok(spec, role, value, key_value):
    """R4 for one light: the key within VALUE_TOL of its intensity; fill and rim within VALUE_TOL of their percentage
    of the specified key intensity, or, when the key itself is within tolerance, of the key's actual intensity."""
    light = spec["lights"][role]
    if role == "key":
        return _close(value, light["intensity"], VALUE_TOL)
    if _close(value, light["intensity"], VALUE_TOL):
        return True
    key = V.floats(key_value)
    return len(key) == 1 and _close(key_value, spec["lights"]["key"]["intensity"], VALUE_TOL) and \
        _close(value, key[0] * light["pct"] / 100.0, VALUE_TOL)


def lamp_values(probe, ref):
    """Same rows as the public module's lamp_values: Transform and Light serialized values, Light m_Enabled aside."""
    values = V.component_values(probe, ref, types=("Transform", "Light"))
    return [[kind, sorted([path, value] for path, value in (props or {}).items()
                          if not (kind == "Light" and path == "m_Enabled"))] for kind, props in sorted(values.items())]


def lamp_components(probe, ref):
    """Same rows as the public module's lamp_components: [type, enabled], the Light's flag aside, without URP's
    UniversalAdditionalLightData."""
    return [[comp.get("type"), None if comp.get("type") == "Light" else comp.get("enabled")]
            for comp in probe.components(ref) if comp.get("type") != "UniversalAdditionalLightData"]


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    spec = rig_spec(ctx.seed)
    lights, rows = _lights(ctx, spec)
    subject = c0.get("subject") or {}

    placed = {role: bool(info) and len(named) == 1 and info.get("parentPath") == h["rigPath"] and
              light.get("lightType") == spec["lights"][role]["type"] for role, (named, info, light) in lights.items()}
    card.check("L1", all(placed.values()),
               actual={role: {"count": len(named), "parentPath": info.get("parentPath"),
                              "lightType": light.get("lightType")} for role, (named, info, light) in lights.items()})

    goals = {role: frame_position(subject.get("position"), subject.get("rotation"), spec["lights"][role]["az"],
                                  spec["lights"][role]["el"], spec["lights"][role]["d"]) for role in ROLES}
    at_goal = [role for role in ROLES if lights[role][1] and goals[role] and
               V.close_tuple(lights[role][1].get("position"), goals[role], POSITION_TOL)]
    card.fraction("L2", len(at_goal) / float(len(ROLES)), expected=goals,
                  actual={role: lights[role][1].get("position") for role in ROLES})

    errors = {role: aim_error(lights[role][1].get("position"), lights[role][1].get("rotation"),
                              subject.get("position")) if lights[role][1] else None for role in ROLES}
    aimed = [role for role in ROLES if errors[role] is not None and errors[role] <= AIM_TOL]
    card.fraction("L3", len(aimed) / float(len(ROLES)), expected="<= %g deg" % AIM_TOL, actual=errors)

    key_value = lights["key"][2].get("intensity")
    bright = [role for role in ROLES if intensity_ok(spec, role, lights[role][2].get("intensity"), key_value)]
    card.fraction("L4", len(bright) / float(len(ROLES)),
                  expected={role: spec["lights"][role]["intensity"] for role in ROLES},
                  actual={role: lights[role][2].get("intensity") for role in ROLES})

    tinted = [role for role in ROLES if V.close_tuple(V.floats(lights[role][2].get("color"))[:3],
                                                      spec["lights"][role]["color"], VALUE_TOL)]
    card.fraction("L5", len(tinted) / float(len(ROLES)), expected={role: spec["lights"][role]["color"] for role in ROLES},
                  actual={role: lights[role][2].get("color") for role in ROLES})

    tuned = []
    for role in ROLES:
        light, wanted = lights[role][2], spec["lights"][role]
        ok = str(light.get("shadows")).lower() == wanted["shadows"].lower() and \
            _close(light.get("range"), spec["range"], VALUE_TOL) and \
            (wanted["spot"] is None or _close(light.get("spotAngle"), wanted["spot"], ANGLE_TOL))
        if ok:
            tuned.append(role)
    card.fraction("L6", len(tuned) / float(len(ROLES)),
                  expected={role: {"shadows": spec["lights"][role]["shadows"], "range": spec["range"],
                                   "spotAngle": spec["lights"][role]["spot"]} for role in ROLES},
                  actual={role: {key: lights[role][2].get(key) for key in ("shadows", "range", "spotAngle")}
                          for role in ROLES})

    lamp = p.go(h["lamp"]) or {}
    lamp_light = _read(ctx, "light_get_info", path=lamp.get("path")) if lamp.get("path") else None
    before = c0.get("lamp") or {}
    was = before.get("light") or {}
    info_same, info_diff = V.same({key: was.get(key) for key in LAMP_INFO},
                                  {key: (lamp_light or {}).get(key) for key in LAMP_INFO}, VALUE_TOL)
    values_same, values_diff = V.same(before.get("values"), lamp_values(p, h["lamp"]) if lamp else None)
    components_same = lamp_components(p, h["lamp"]) == before.get("components") if lamp else False
    same_lamp = bool(lamp) and bool(lamp_light) and \
        all(lamp.get(field) == (before.get("state") or {}).get(field) for field in ("name", "parentPath", "tag",
                                                                                   "layer")) and \
        V.same(before.get("local"), V.local_trs(p, h["lamp"]), LOCAL_TOL)[0] and info_same and values_same and \
        components_same
    off = lamp_light is not None and (lamp_light.get("enabled") is False or lamp.get("isActive") is False)
    card.check("L7", same_lamp and off, expected={"enabled": False, "light": was},
               actual={"present": bool(lamp), "enabled": (lamp_light or {}).get("enabled"),
                       "isActive": lamp.get("isActive"), "light": lamp_light, "lightDiff": info_diff,
                       "serializedDiff": values_diff, "componentsSame": components_same})

    found = {_key(lights[role][1]) for role in ROLES if lights[role][1]}
    found.discard(None)
    allowed = found | {_key(h["lamp"])}
    strays = [row.get("path") for row in rows if V.under(row.get("path"), ctx.R) and _key(row) not in allowed]
    rig_children = [_key(child) for child in p.children(h["rig"])]
    extra = [key for key in rig_children if key not in found]
    ok_subject, diff_subject = V.same(c0.get("subject"), V.go_state(p, h["subject"]))
    stage_before = dict(c0.get("stage") or {})
    stage_kids = [str(key) for key in stage_before.pop("children", []) or []]
    stage_now = V.go_state(p, h["stage"]) or {}
    stage_children = [str(key) for key in stage_now.pop("children", []) or []]
    ok_stage, diff_stage = V.same(stage_before, stage_now)
    rig_before = dict(c0.get("rig") or {})
    rig_before.pop("children", None)
    ok_rig, diff_rig = V.same(rig_before, V.go_state(p, h["rig"], with_children=False))
    card.check("L8", not strays and not extra and ok_subject and ok_stage and ok_rig and
               set(stage_children) <= set(stage_kids),
               actual={"otherLights": strays[:5], "extraUnderRig": extra[:5], "subject": diff_subject,
                       "stage": diff_stage, "rig": diff_rig})

    reported = {role: V.norm_path(V.answer(ctx, role + "Path")) for role in ROLES}
    card.check("L9", all(lights[role][1] and reported[role] == lights[role][1].get("path") for role in ROLES) and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={role: lights[role][1].get("path") for role in ROLES}, actual=ctx.answer)
    bad = ctx.violations()
    card.check("L10", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("L11", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
