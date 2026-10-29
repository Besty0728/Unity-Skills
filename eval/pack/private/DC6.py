"""DC6 private verifier: coin pickups with trigger colliders that add their value to a score manager once each.

What the task probes:
- Trigger messages need a Rigidbody on at least one side. Neither the player nor the coins carry one, and the prompt
  never says so: without one (a kinematic Rigidbody on the player, or kinematic ones on the coins) no coin is ever
  collected. A Rigidbody that falls under gravity drags the player off its path, so it never reaches the last
  waypoint and the scenario times out.
- The pair (player layer, Default) is disabled in the collision matrix and the coins start on Default: coins left on
  Default are never touched. Enabling the pair instead breaks R6 (C6) and leaves the coins off their layer (C5).
- The player has two colliders tagged Player of the same footprint (the capsule and the box around it), so both enter
  a coin in the same physics step; the coin also lies on the way back. A coin without a collected flag counts once per
  collider and once more per pass; Destroy() is deferred to the end of the frame and does not stop the second
  callback of the same step.
- Three coins are never touched: one past the far end of the path, one past its final end, one beside the line. The
  finalScore answer needs that geometry, including the coin the player only reaches on the way back.

The touched set, the coin values and the final position are derived from C0 (waypoint and coin world positions) and
the seed; coin_values is a verbatim copy of the public module's.
"""
import hashlib
import json
import math
import os
import random
import re

from usk_eval import verifylib as V

COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
SETTINGS_FILES = ("ProjectSettings/TagManager.asset", "ProjectSettings/DynamicsManager.asset")
COLLIDERS = ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider")
REACH = 1.3                 # between the contact distance (capsule 0.5 + coin 0.4) and the nearest untouched coin (2.6)
END_TOL = 0.05              # R8: the player stands on its last waypoint
SETTLE_S = 0.5
TIMEOUT_S = 90
VALUE_TOL = 1e-3

ASSERTIONS = [
    ("C1", 0.75, False, ["R1"], "both scripts exist at A/Scripts/<cls>.cs, the project compiled without errors, and "
                                "they declare score, Add(int), value and manager"),
    ("C2", 0.75, False, ["R3"], "exactly one score manager in the scene, on the board, and exactly one coin component "
                                "on each of the 7 coins and on nothing else"),
    ("C3", 0.75, False, ["R3"], "every coin's manager references the board's score manager and its value matches its "
                                "Gold/Silver tier"),
    ("C4", 0.5, False, ["R4"], "every coin keeps exactly one collider, its SphereCollider, and it is a trigger"),
    ("C5", 0.5, False, ["R4"], "every coin object is on the pickup layer"),
    ("C6", 0.75, True, ["R6"], "layer names, tags, gravity and every collision pair of the two task layers are "
                               "unchanged"),
    ("C7", 0.75, True, ["R5"], "the player and its box keep name, path, tag, layer, transform, active state and "
                               "colliders (components only appended); the mover component, the route and its "
                               "waypoints are unchanged"),
    ("C8", 0.5, True, ["R7"], "no code file other than the two scripts was added, removed or modified"),
    ("C9", 0.25, False, ["R7"], "the site, board, coin container and coins keep name, path, position, rotation, scale "
                                "and active state"),
    ("C10", 0.5, True, ["G1", "G2"], "no change outside R/A (the two settings files are judged by C6), no other scene "
                                     "saved or opened"),
    ("C11", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
    ("C12", 0.25, False, ["R9", "G8"], "the answer reports both script paths and the board path"),
    ("C13", 0.5, False, ["R9"], "the reported finalScore equals the sum of the values of the coins the path touches"),
    ("C14", 2.25, False, ["R2", "R8"], "in Play Mode, once the player stands on its last waypoint, score equals the "
                                       "sum of the touched coins' values and no error is logged"),
    ("C15", 0.75, False, ["R2", "R8"], "in Play Mode the touched coins are inactive, the others active, and all 7 "
                                       "still exist"),
]

HINTS = {
    "C1": {"zh-CN": "两个脚本都放在 A/Scripts 下、文件名与类名一致，声明 score、Add(int)、value 和 manager，并等待编译通过。",
           "en": "Put both scripts under A/Scripts named after their classes, declare score, Add(int), value and "
                 "manager, and wait for a clean compile."},
    "C2": {"zh-CN": "计分管理组件只挂在指定的对象上；7 枚金币各挂一个金币组件，其他对象不挂。",
           "en": "Put the score manager only on the given object and one coin component on each of the 7 coins, "
                 "nowhere else."},
    "C3": {"zh-CN": "在场景中为每枚金币设置 manager（指向该计分管理组件）和按名称前缀给出的 value。",
           "en": "Set every coin's manager to that score manager component and its value from its name prefix, in "
                 "the scene."},
    "C4": {"zh-CN": "把金币已有的 SphereCollider 设为触发器，不要添加其他碰撞体。",
           "en": "Make each coin's existing SphereCollider a trigger and add no other collider."},
    "C5": {"zh-CN": "把每个金币对象放到题目给出的拾取层。",
           "en": "Put every coin object on the pickup layer the task names."},
    "C6": {"zh-CN": "不要修改层碰撞矩阵、层名称、标签或其他工程设置；让金币进入能与玩家层碰撞的那个层即可。",
           "en": "Do not change the collision matrix, layer names, tags or other project settings; move the coins to "
                 "the layer that collides with the player's layer instead."},
    "C7": {"zh-CN": "不要改动玩家、其子碰撞体、移动组件和路点；只可以给玩家添加组件。",
           "en": "Leave the player, its child collider, the mover and the waypoints as they are; you may only add "
                 "components to the player."},
    "C8": {"zh-CN": "除两个新脚本外，不要新增、删除或修改其他代码文件。",
           "en": "Apart from the two new scripts, do not add, delete or modify any code file."},
    "C9": {"zh-CN": "不要移动、改名或停用金币、计分对象及其容器。",
           "en": "Do not move, rename or deactivate the coins, the board or their containers."},
    "C10": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "C11": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
    "C12": {"zh-CN": "答案里报告两个脚本的资产路径和计分对象的完整层级路径，并写明 declaredComplete。",
            "en": "Report both script paths and the full hierarchy path of the board, plus declaredComplete."},
    "C13": {"zh-CN": "finalScore 只计路径途中真正碰到的金币：看清去程、返程的终点，以及偏离路线的金币。",
            "en": "finalScore counts only the coins the path really touches: mind where the outbound and return legs "
                  "end and which coin lies beside the line."},
    "C14": {"zh-CN": "触发消息要求至少一方带 Rigidbody（玩家上的运动学刚体即可）；玩家有两个带 Player 标签的碰撞体，"
                     "金币需用标记保证只加一次分；金币要在能与玩家层碰撞的层上。",
            "en": "Trigger messages need a Rigidbody on at least one side (a kinematic one on the player works); the "
                  "player has two colliders tagged Player, so a coin needs a flag to add its value only once; the "
                  "coins must be on a layer that collides with the player's layer."},
    "C15": {"zh-CN": "被拾取的金币用 SetActive(false) 停用而不是销毁，未碰到的金币保持激活。",
            "en": "Deactivate a collected coin with SetActive(false) instead of destroying it, and leave untouched "
                  "coins active."},
}


def coin_values(seed):
    """Verbatim copy of the public task's coin_values(seed) (pinned by unit tests)."""
    rng = random.Random("DC6:%d" % seed)
    return {"gold": rng.choice((5, 7, 10)), "silver": rng.choice((2, 3))}


def class_name(path):
    return os.path.basename(path)[:-len(".cs")]


def allowed_change(ctx, rec):
    """Records of the two settings files: C6 judges the layer names, tags and collision pairs they hold."""
    path = V.norm_path(rec.get("assetPath"))
    if path.endswith(".meta"):
        path = path[:-5]
    if path not in SETTINGS_FILES or rec.get("kind") not in ("imported", "property_changed"):
        return False
    if rec.get("path") or rec.get("scenePath"):
        return False
    if rec.get("kind") == "property_changed":
        prop = str(rec.get("property") or "")
        prefixes = ("m_LayerCollisionMatrix", "m_Gravity") if path.endswith("DynamicsManager.asset") else (
            "layers", "tags")
        return prop.startswith(prefixes)
    return True


# ---------------------------------------------------------------- geometry


def _vec(state):
    pos = (state or {}).get("position") or {}
    values = V.floats([pos.get(axis) for axis in ("x", "y", "z")])
    return tuple(values) if len(values) == 3 else None


def _segment_distance(a, b, p):
    ab = [y - x for x, y in zip(a, b)]
    length = sum(v * v for v in ab)
    t = 0.0 if length <= 0.0 else max(0.0, min(1.0, sum((q - x) * v for q, x, v in zip(p, a, ab)) / length))
    return math.sqrt(sum((x + v * t - q) ** 2 for x, v, q in zip(a, ab, p)))


def path_points(c0):
    return [_vec(state) for _ref, state in c0.get("points") or []]


def coin_rows(c0):
    """[(ref, path, name, world position)] of the coins at issue."""
    return [(ref, (state or {}).get("path"), (state or {}).get("name"), _vec(state))
            for ref, state, _parts in c0.get("coins") or []]


def touched(c0):
    """Paths of the coins that the player's colliders touch along the waypoints."""
    points = path_points(c0)
    if len(points) < 2 or any(point is None for point in points):
        return set()
    out = set()
    for _ref, path, _name, pos in coin_rows(c0):
        if pos is not None and min(_segment_distance(a, b, pos) for a, b in zip(points, points[1:])) < REACH:
            out.add(path)
    return out


def coin_value(seed, name):
    values = coin_values(seed)
    if str(name).startswith("Gold"):
        return values["gold"]
    if str(name).startswith("Silver"):
        return values["silver"]
    return None


def total_for(c0, seed):
    hits = touched(c0)
    return sum(coin_value(seed, name) or 0 for _ref, path, name, _pos in coin_rows(c0) if path in hits)


def total(ctx):
    return total_for(ctx.c0, ctx.seed)


# ---------------------------------------------------------------- reads


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def compiled(ctx):
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def keeper(ctx, found=None):
    """The object carrying the score manager: the board when it does, else the first holder."""
    found = holders(ctx, class_name(ctx.handles["managerScript"])) if found is None else found
    board = ctx.handles["board"]
    return next((ref for ref in found if V.same_id(ref, board)), found[0] if found else None)


def _text(ctx, asset_path):
    try:
        with open(os.path.join(ctx.project_path, *asset_path.split("/")), "r", encoding="utf-8-sig") as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError):
        return None


def file_sha(project_path, asset_path):
    try:
        with open(os.path.join(project_path, *asset_path.split("/")), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _field(code, kind, name):
    pattern = (r"(?:public|\[[^\]]*\bSerializeField\b[^\]]*\](?:\s*(?:private|protected|internal|public))?)\s+"
               r"%s\s+%s\b" % (re.escape(kind), re.escape(name)))
    return re.search(pattern, code) is not None


def _declares(code, cls):
    return re.search(r"\bclass\s+%s\s*:\s*(?:UnityEngine\.)?MonoBehaviour\b" % re.escape(cls), code) is not None


def _as_bool(value):
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    return True if text in ("true", "1") else False if text in ("false", "0") else None


def _trigger(ctx, ref, kind):
    ser = ctx.probe.ser(ref, kind) or {}
    for name in ("m_IsTrigger", "isTrigger"):
        if name in ser and _as_bool(ser[name]) is not None:
            return _as_bool(ser[name])
    res = _read(ctx, "component_get_properties", componentType=kind, **V.address(ref)) or {}
    props = {row.get("name"): row.get("value") for row in res.get("properties") or [] if isinstance(row, dict)}
    return _as_bool(props.get("isTrigger")) if "isTrigger" in props else None


def _view(state, keys):
    return {key: (state or {}).get(key) for key in keys}


def collision_now(ctx, pairs):
    out = {}
    for key in pairs:
        low, high = (int(part) for part in key.split(","))
        out[key] = (_read(ctx, "physics_get_layer_collision", layer1=low, layer2=high) or {}).get("collisionEnabled")
    return out


def layer_table(ctx):
    rows = (_read(ctx, "editor_get_layers") or {}).get("layers") or []
    return {str(row.get("index")): row.get("name") for row in rows if isinstance(row, dict)}


# ---------------------------------------------------------------- static verification


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    mgr, coin = class_name(h["managerScript"]), class_name(h["coinScript"])

    mgr_code = COMMENT_RE.sub("", _text(ctx, h["managerScript"]) or "")
    coin_code = COMMENT_RE.sub("", _text(ctx, h["coinScript"]) or "")
    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors")
                for path in (h["managerScript"], h["coinScript"])]
    members = {"managerClass": _declares(mgr_code, mgr), "score": _field(mgr_code, "int", "score"),
               "add": re.search(r"\bpublic\s+void\s+Add\s*\(\s*int\s+\w+\s*\)", mgr_code) is not None,
               "coinClass": _declares(coin_code, coin), "value": _field(coin_code, "int", "value"),
               "manager": _field(coin_code, mgr, "manager")}
    card.check("C1", bool(mgr_code) and bool(coin_code) and compiled(ctx) and feedback == [False, False] and
               all(members.values()),
               actual={"compiled": compiled(ctx), "hasErrors": feedback, "members": members})

    coin_refs = [ref for ref, _state, _parts in c0.get("coins") or []]
    managers = holders(ctx, mgr)
    pickups = holders(ctx, coin)
    on_board = p.component_count(h["board"], mgr)
    per_coin = [p.component_count(ref, coin) for ref in coin_refs]
    card.check("C2", len(managers) == 1 and V.same_id(managers[0], h["board"]) and on_board == 1 and
               len(pickups) == len(coin_refs) == 7 and per_coin == [1] * 7 and
               all(any(V.same_id(ref, got) for got in pickups) for ref in coin_refs),
               expected={"manager": h["boardPath"], "coins": h["coinPaths"]},
               actual={"managers": managers, "onBoard": on_board, "coinHolders": pickups, "perCoin": per_coin})

    wiring, wiring_ok = [], bool(coin_refs)
    for ref, _path, name, _pos in coin_rows(c0):
        ser = p.ser(ref, coin) if p.component_count(ref, coin) else None
        ser = ser or {}
        link = V.parse_ref(ser.get("manager"))
        owner = p.owner(ser.get("manager"))
        want = coin_value(ctx.seed, name)
        got = V.as_int(ser.get("value")) if "value" in ser else None
        ok = (bool(link) and (link[1] == mgr or link[1].endswith("." + mgr)) and V.same_id(owner, h["board"]) and
              got == want)
        wiring_ok = wiring_ok and ok
        wiring.append({"coin": name, "manager": ser.get("manager"), "value": got, "want": want, "ok": ok})
    card.check("C3", wiring_ok, actual=wiring)

    shapes, shapes_ok = [], bool(coin_refs)
    for ref in coin_refs:
        kinds = [row.get("type") for row in p.components(ref) if row.get("type") in COLLIDERS]
        trig = _trigger(ctx, ref, "SphereCollider") if kinds == ["SphereCollider"] else None
        shapes_ok = shapes_ok and kinds == ["SphereCollider"] and trig is True
        shapes.append({"coin": ref, "colliders": kinds, "isTrigger": trig})
    card.check("C4", shapes_ok, actual=shapes)

    loot = h["pickupLayer"]["name"]
    layers = [(p.go(ref) or {}).get("layer") for ref in coin_refs]
    card.check("C5", bool(layers) and all(layer == loot for layer in layers), expected=loot, actual=layers)

    pairs = sorted((c0.get("collision") or {}).keys())
    now_pairs = collision_now(ctx, pairs)
    moved = sorted(key for key in pairs if now_pairs.get(key) != c0["collision"][key])
    names_now = layer_table(ctx)
    tags_now = list((_read(ctx, "editor_get_tags") or {}).get("tags") or [])
    gravity_now = _read(ctx, "physics_get_gravity") or {}
    gravity_then = c0.get("gravity") or {}
    gravity_ok = all(V.close_tuple(gravity_now.get(axis), (gravity_then.get(axis),), VALUE_TOL) for axis in "xyz")
    card.check("C6", bool(pairs) and not moved and names_now == (c0.get("layers") or {}) and
               tags_now == list(c0.get("tags") or []) and gravity_ok,
               actual={"pairsChanged": moved[:10], "layers": names_now, "tags": tags_now, "gravity": gravity_now})

    keys = ("name", "path", "isActive", "tag", "layer", "position", "rotation", "scale")
    diffs = {}
    for key, parts_key in (("player", "playerParts"), ("box", "boxParts")):
        same, diff = V.same(_view(c0.get(key), keys), _view(V.go_state(p, h[key]), keys))
        if not same:
            diffs[key] = diff
        parts_then = list(c0.get(parts_key) or [])
        parts_now = [row.get("type") for row in p.components(h[key])]
        extra = parts_now[len(parts_then):]
        if parts_now[:len(parts_then)] != parts_then or any(kind in COLLIDERS for kind in extra):
            diffs[parts_key] = {"before": parts_then, "now": parts_now}
    for key, shapes_key in (("player", "playerShapes"), ("box", "boxShapes")):
        shapes_now = [[kind, sorted([k, v] for k, v in (p.ser(h[key], kind) or {}).items() if k in
                                    {name for name, _value in state})]
                      for kind, state in c0.get(shapes_key) or []]
        if shapes_now != (c0.get(shapes_key) or []):
            diffs[shapes_key] = {"before": c0.get(shapes_key), "now": shapes_now}
    for key in ("route",):
        same, diff = V.same(c0.get(key), V.go_state(p, h[key]))
        if not same:
            diffs[key] = diff
    for ref, state in c0.get("points") or []:
        same, diff = V.same(state, V.go_state(p, ref))
        if not same:
            diffs["point %s" % ref] = diff
    mover_now = sorted([key, value] for key, value in (p.ser(h["player"], h["mover"]) or {}).items())
    if mover_now != (c0.get("mover") or []):
        diffs["mover"] = {"before": c0.get("mover"), "now": mover_now}
    card.check("C7", not diffs, actual=diffs)

    before = {path: sha for path, sha in c0.get("scripts") or []}
    now_files = cs_files(ctx.project_path)
    changed = sorted(path for path, sha in before.items()
                     if path not in now_files or file_sha(ctx.project_path, path) != sha)
    extra = sorted(set(now_files) - set(before) - {h["managerScript"], h["coinScript"]})
    card.check("C8", not changed and not extra, actual={"changed": changed, "extra": extra})

    plain = ("name", "path", "isActive", "position", "rotation", "scale")
    others = {}
    for key in ("site", "board", "coinsRoot"):
        same, diff = V.same(_view(c0.get(key), plain), _view(V.go_state(p, h[key]), plain))
        if not same:
            others[key] = diff
    for ref, state, _parts in c0.get("coins") or []:
        same, diff = V.same(_view(state, plain), _view(V.go_state(p, ref), plain))
        if not same:
            others["coin %s" % ref] = diff
    card.check("C9", not others, actual=others)

    bad = ctx.violations()
    card.check("C10", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("C11", not direct, actual={"directWrites": direct[:10]})

    card.check("C12", V.norm_path(V.answer(ctx, "managerScriptPath")) == h["managerScript"] and
               V.norm_path(V.answer(ctx, "coinScriptPath")) == h["coinScript"] and
               V.norm_path(V.answer(ctx, "boardPath")) == (p.go(h["board"]) or {}).get("path") and
               isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected={"managerScriptPath": h["managerScript"], "coinScriptPath": h["coinScript"],
                         "boardPath": h["boardPath"]},
               actual=ctx.answer)
    reported = V.answer(ctx, "finalScore")
    card.check("C13", not isinstance(reported, bool) and V.as_int(reported) == total(ctx),
               expected=total(ctx), actual=reported)


# ---------------------------------------------------------------- runtime


def SCENARIO(ctx):
    """Play Mode: wait until the player stands on its last waypoint, settle, read the score manager's fields and the
    coins (active state included), then assert Play Mode is still on."""
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    idle = {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    h = ctx.handles
    if not compiled(ctx):
        return idle
    holder = keeper(ctx)
    end = (path_points(ctx.c0) or [None])[-1]
    if holder is None or end is None:
        return idle
    query = {"parentPath": h["coinsPath"], "includeInactive": True, "limit": 50}
    steps = [
        {"kind": "wait_condition", "skill": "gameobject_get_info", "args": V.address(h["player"]),
         "select": "position", "expected": {"x": end[0], "y": end[1], "z": end[2]}, "tolerance": END_TOL},
        {"kind": "wait_seconds", "seconds": SETTLE_S},
        {"kind": "read", "skill": "component_get_serialized_properties",
         "args": dict({"componentType": class_name(h["managerScript"])}, **V.address(holder))},
        {"kind": "read", "skill": "batch_query_gameobjects", "args": {"queryJson": json.dumps(query),
                                                                      "sampleLimit": 50}},
        check,
    ]
    return {"executionMode": "play", "timeoutSeconds": TIMEOUT_S, "steps": steps}


def _reads(scenario):
    return [step for step in scenario.get("steps") or [] if isinstance(step, dict) and step.get("kind") == "read"]


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    reads = _reads(scenario)
    want = total(ctx)

    fields = (reads[0].get("actual") or {}) if reads and isinstance(reads[0].get("actual"), dict) else {}
    props = {row.get("propertyPath"): row.get("value") for row in fields.get("properties") or []
             if isinstance(row, dict)}
    got = V.as_int(props.get("score")) if "score" in props else None
    card.check("C14", passed and got == want, expected=want,
               actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                       "score": got})

    listing = (reads[1].get("actual") or {}) if len(reads) > 1 and isinstance(reads[1].get("actual"), dict) else {}
    rows = [row for row in listing.get("objects") or [] if isinstance(row, dict)]
    hits = touched(ctx.c0)
    states, ok = [], passed and bool(rows)
    for ref, path, name, _pos in coin_rows(ctx.c0):
        row = next((r for r in rows if V.same_id(r, ref)), None) or next((r for r in rows if r.get("path") == path),
                                                                          None)
        active = row.get("activeSelf") if row else None
        good = row is not None and active is (path not in hits)
        ok = ok and good
        states.append({"coin": name, "activeSelf": active, "touched": path in hits, "ok": good})
    card.check("C15", ok, actual={"passed": passed, "coins": states})

# Producer:Betsy
