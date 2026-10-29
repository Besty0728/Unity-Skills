"""DC6: coin pickups with trigger colliders and a tally component, collected by a player that a fixture mover walks
along a fixed out-and-back path (dev, challenge).

Fixture:
- Two user layer slots (LAYER_SLOTS) get seeded names: one for the player, one for the coins. Every collision pair of
  those two slots starts enabled except the pair (player layer, Default), which starts disabled; the runner restores
  the pairs and teardown puts the layer names back.
- A/Scripts/<Mover>_<k>.cs compiled at build: moves its GameObject along a Transform[] waypoints array at a constant
  speed with Vector3.MoveTowards in Update and stops at the last waypoint.
- R/<Site> (seeded offset and yaw) > <Route> > P0, P1, P2 (out along local +X, back past the start);
  <Player> (Capsule tagged Player on the player layer, no Rigidbody, carrying the mover) > <Box> (a BoxCollider of
  size 1 x 2 x 1 around the capsule, tagged Player on the player layer); <Coins> > seven Sphere coins (scale 0.8,
  layer Default, SphereCollider not a trigger) named GoldCoin_<n> / SilverCoin_<n>, four on the path line between
  its ends, one beyond the far end, one beyond the final end and one beside the line; <Board> (an empty object).
- Class names, object names, layer names and slots, the site pose, the coin positions along the line, the tiers and
  the creation order come from the seed.
"""
import hashlib
import json
import os
import random
import re

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

TAG_MANAGER = "ProjectSettings/TagManager.asset"
LAYER_SLOTS = (28, 29)
COLLIDER_TYPES = ("BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider")
FIRST_USER_LAYER = 8
LAYER_NAME = re.compile(r"^Ev[a-z]{3}(Actor|Pickup)$")
WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")
SITES = (
    ("Plaza", "Rover", "Bumper", "Coins", "Route", "Tally"),
    ("Canyon", "Scout", "Shell", "Loot", "Trail", "Counter"),
    ("Harbor", "Courier", "Hull", "Treasure", "Lane", "Ledger"),
)
YAWS = (25, 40, 55, 70, -30, -45, -65)
WAYPOINT_X = (-8.0, 7.0, -12.0)
PATH_Y = 1.0
SPEED = 4.0
COIN_SCALE = 0.8

MOVER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public Transform[] waypoints;
    public float speed = 4f;

    private int _next = 1;

    void Update()
    {{
        if (waypoints == null || _next >= waypoints.Length) return;
        Vector3 goal = waypoints[_next].position;
        transform.position = Vector3.MoveTowards(transform.position, goal, speed * Time.deltaTime);
        if (transform.position == goal) _next++;
    }}
}}
"""


def _pairs():
    return tuple(sorted({tuple(sorted((other, slot))) for slot in LAYER_SLOTS for other in range(32)}))


def coin_values(seed):
    """Values of a Gold and a Silver coin, from the seed alone."""
    rng = random.Random("DC6:%d" % seed)
    return {"gold": rng.choice((5, 7, 10)), "silver": rng.choice((2, 3))}


def coin_layout(rng):
    """(tier, local x, local z) of the seven coins in creation order."""
    on_line = [rng.choice((-10.0, -10.5)), rng.choice((-5.0, -4.0)), rng.choice((-1.0, 0.0)), rng.choice((3.0, 4.0))]
    off_line = [(rng.choice((10.0, 10.5)), 0.0), (rng.choice((-15.0, -15.5)), 0.0),
                (rng.choice((-2.0, 1.0)), rng.choice((-2.6, 2.6)))]
    gold_on = rng.choice((1, 2, 3))
    tiers_on = ["Gold"] * gold_on + ["Silver"] * (len(on_line) - gold_on)
    rng.shuffle(tiers_on)
    tiers_off = ["Gold", "Silver", rng.choice(("Gold", "Silver"))]
    rng.shuffle(tiers_off)
    rows = [(tier, x, 0.0) for tier, x in zip(tiers_on, on_line)]
    rows += [(tier, x, z) for tier, (x, z) in zip(tiers_off, off_line)]
    rng.shuffle(rows)
    return rows


def layer_names(client):
    """{slot: name} of the named layers."""
    layers = client.read("editor_get_layers").get("layers") or []
    return {int(row["index"]): row.get("name") for row in layers}


def collision_states(ctx, pairs):
    return {"%d,%d" % pair: ctx.read("physics_get_layer_collision", layer1=pair[0], layer2=pair[1])
            .get("collisionEnabled") for pair in pairs}


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def file_sha(project_path, asset_path):
    try:
        with open(os.path.join(project_path, *asset_path.split("/")), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def _claim_layers(ctx, wanted):
    held = layer_names(ctx.c)
    for slot in LAYER_SLOTS:
        if held.get(slot) and not LAYER_NAME.match(held[slot]):
            raise ValueError("layer slot %d is named %r; this task needs the layer slots %s unused" % (
                slot, held[slot], LAYER_SLOTS))
    for slot, name in sorted(wanted.items()):
        ctx.c.write("scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                    propertyPath="layers.Array.data[%d]" % slot, value=name)
    now = layer_names(ctx.c)
    if any(now.get(slot) != name for slot, name in wanted.items()):
        raise ValueError("the tag manager did not take the layer names %s (found %s)" % (
            wanted, {slot: now.get(slot) for slot in wanted}))


class DC6(PublicTask):
    id = "DC6"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 40
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    layer_pairs = _pairs()
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        site_name, player_name, box_name, coins_name, route_name, board_name = rng.choice(SITES)
        word = rng.choice(WORDS)
        token = "".join(rng.choice("abcdefghjkmnpqrstuvwxyz") for _ in range(3))
        slots = list(LAYER_SLOTS)
        rng.shuffle(slots)
        actor = {"index": slots[0], "name": "Ev%sActor" % token}
        pickup = {"index": slots[1], "name": "Ev%sPickup" % token}
        k = "%s_%s" % (word, ctx.scope.run_id)
        mover_cls = "RouteRunner_%s" % k
        scripts_dir = ctx.asset("Scripts")

        layers_at_start = {str(slot): name for slot, name in layer_names(c).items()
                           if slot >= FIRST_USER_LAYER and slot not in LAYER_SLOTS}
        _claim_layers(ctx, {actor["index"]: actor["name"], pickup["index"]: pickup["name"]})
        closed = tuple(sorted((0, actor["index"])))
        for pair, enabled in sorted(collision_states(ctx, self.layer_pairs).items()):
            pair_ids = tuple(int(part) for part in pair.split(","))
            if enabled != (pair_ids != closed):
                c.write("physics_set_layer_collision", layer1=pair_ids[0], layer2=pair_ids[1],
                        enableCollision=pair_ids != closed)

        res = c.write("script_create", scriptName=mover_cls, folder=scripts_dir,
                      content=MOVER_SRC.format(cls=mover_cls))
        if isinstance(res, dict) and res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        mover_script = "%s/%s.cs" % (scripts_dir, mover_cls)
        if not ctx.probe.guid(mover_script):
            raise RuntimeError("DC6 fixture: the mover script was not imported: %s" % mover_script)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        site = make(site_name, ctx.root, x=rng.randint(-4, 4), y=0, z=rng.randint(-4, 4), rotY=rng.choice(YAWS))
        route = make(route_name, site, x=0, y=0, z=0)
        points = [make("P%d" % index, route, x=x, y=PATH_Y, z=0.0) for index, x in enumerate(WAYPOINT_X)]

        coins_root = make(coins_name, site, x=0, y=0, z=0)
        coins, coin_names, counters = [], [], {"Gold": 0, "Silver": 0}
        for tier, x, z in coin_layout(rng):
            counters[tier] += 1
            name = "%sCoin_%d" % (tier, counters[tier])
            coins.append(make(name, coins_root, primitiveType="Sphere", x=x, y=PATH_Y, z=z,
                              scaleX=COIN_SCALE, scaleY=COIN_SCALE, scaleZ=COIN_SCALE))
            coin_names.append(name)

        board = make(board_name, site, x=0, y=2, z=5)
        player = make(player_name, site, primitiveType="Capsule", x=WAYPOINT_X[0], y=PATH_Y, z=0.0)
        box = make(box_name, player, primitiveType="Cube", x=0, y=0, z=0, scaleX=1, scaleY=2, scaleZ=1)
        for kind in ("MeshRenderer", "MeshFilter"):
            c.write("component_remove", componentType=kind, **address(box))
        c.write("gameobject_set_tag_batch", items=json.dumps([dict({"tag": "Player"}, **address(ref))
                                                              for ref in (player, box)]))
        c.write("gameobject_set_layer_batch", items=json.dumps([dict({"layer": actor["name"]}, **address(ref))
                                                                for ref in (player, box)]))

        c.write("component_add", componentType=mover_cls, **address(player))
        c.write("component_set_property", componentType=mover_cls, propertyName="speed", value=str(SPEED),
                **address(player))
        c.write("component_set_serialized_property", componentType=mover_cls, propertyPath="waypoints.Array.size",
                value=str(len(points)), **address(player))
        for index in range(len(points)):
            c.write("component_set_serialized_property", componentType=mover_cls,
                    propertyPath="waypoints.Array.data[%d]" % index,
                    referencePath=ctx.path(site_name, route_name, "P%d" % index), objectType="Transform",
                    **address(player))
        ctx.wait_idle()

        return {
            "site": site, "sitePath": ctx.path(site_name),
            "route": route, "routePath": ctx.path(site_name, route_name), "points": points,
            "player": player, "playerPath": ctx.path(site_name, player_name),
            "box": box, "boxPath": ctx.path(site_name, player_name, box_name),
            "coinsRoot": coins_root, "coinsPath": ctx.path(site_name, coins_name), "coins": coins,
            "coinPaths": [ctx.path(site_name, coins_name, name) for name in coin_names],
            "board": board, "boardPath": ctx.path(site_name, board_name),
            "mover": mover_cls, "moverScript": mover_script,
            "managerScript": "%s/ScoreKeeper_%s.cs" % (scripts_dir, k),
            "coinScript": "%s/CoinPickup_%s.cs" % (scripts_dir, k),
            "actorLayer": actor, "pickupLayer": pickup, "layersAtStart": layers_at_start,
        }

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        collision = collision_states(ctx, self.layer_pairs)
        closed = "%d,%d" % tuple(sorted((0, h["actorLayer"]["index"])))
        if collision.get(closed) is not False or not all(v for key, v in collision.items() if key != closed):
            raise ValueError("the collision pairs of layers %s did not take the fixture states: %s" % (
                LAYER_SLOTS, collision))

        def parts(ref):
            return [row.get("type") for row in p.components(ref)]

        def shapes(ref):
            """Serialized state of every collider on the object: type plus the properties that shape it."""
            wanted = ("m_IsTrigger", "m_Enabled", "m_Center", "m_Size", "m_Radius", "m_Height", "m_Direction")
            out = []
            for kind in parts(ref):
                if kind in COLLIDER_TYPES:
                    state = p.ser(ref, kind) or {}
                    out.append([kind, sorted([key, value] for key, value in state.items() if key in wanted)])
            return out

        return {
            "data": {
                "site": V.go_state(p, h["site"]),
                "route": V.go_state(p, h["route"]),
                "points": [[ref, V.go_state(p, ref)] for ref in h["points"]],
                "player": V.go_state(p, h["player"]),
                "playerParts": parts(h["player"]),
                "playerShapes": shapes(h["player"]),
                "box": V.go_state(p, h["box"]),
                "boxParts": parts(h["box"]),
                "boxShapes": shapes(h["box"]),
                "mover": sorted([key, value] for key, value in (p.ser(h["player"], h["mover"]) or {}).items()),
                "coinsRoot": V.go_state(p, h["coinsRoot"]),
                "coins": [[ref, V.go_state(p, ref), parts(ref)] for ref in h["coins"]],
                "board": V.go_state(p, h["board"]),
                "collision": collision,
                "layers": {str(slot): name for slot, name in sorted(layer_names(ctx.c).items())},
                "tags": list(ctx.read("editor_get_tags").get("tags") or []),
                "gravity": ctx.read("physics_get_gravity"),
                "scripts": [[path, file_sha(ctx.project_path, path)] for path in cs_files(ctx.project_path)],
            },
            "digests": {},
        }

    def prompt_vars(self, ctx):
        h, values = ctx.handles, coin_values(ctx.seed)
        return {
            "scripts": ctx.asset("Scripts"),
            "mgr": os.path.basename(h["managerScript"])[:-len(".cs")],
            "coin": os.path.basename(h["coinScript"])[:-len(".cs")],
            "board": h["boardPath"],
            "coins": h["coinsPath"],
            "player": h["playerPath"],
            "box": h["boxPath"],
            "mover": h["mover"],
            "route": h["routePath"],
            "loot": h["pickupLayer"]["name"],
            "iloot": h["pickupLayer"]["index"],
            "actor": h["actorLayer"]["name"],
            "iactor": h["actorLayer"]["index"],
            "gold": values["gold"],
            "silver": values["silver"],
        }

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["managerScriptPath", "coinScriptPath", "boardPath", "finalScore"],
            "properties": {
                "managerScriptPath": {"type": "string", "desc": {"zh-CN": "计分管理脚本的资产路径",
                                                                 "en": "asset path of the score manager script"}},
                "coinScriptPath": {"type": "string", "desc": {"zh-CN": "金币脚本的资产路径",
                                                              "en": "asset path of the coin script"}},
                "boardPath": {"type": "string", "desc": {"zh-CN": "挂载计分管理组件的对象的完整层级路径",
                                                         "en": "full hierarchy path of the object carrying the score "
                                                               "manager component"}},
                "finalScore": {"type": "integer", "desc": {"zh-CN": "玩家走完整条路径后 score 的值",
                                                           "en": "value of score once the player has finished its "
                                                                 "path"}},
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


TASK = DC6()

# Producer:Betsy
