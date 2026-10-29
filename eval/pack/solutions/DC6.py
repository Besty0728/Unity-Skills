"""DC6 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources compile against Unity 2022.3 and 6000.x; each behavioural negative also carries a
`// usk-fake: <variant>` comment so the offline fake editor models the corresponding wrong behaviour (a real editor
ignores the comment and runs the C#).
"""
import json
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

MANAGER_SRC = """using UnityEngine;

public class {mgr} : MonoBehaviour
{{
    public int score = 0;

    public void Add(int amount)
    {{
        score += amount;
    }}
}}
"""

COIN_SRC = """using UnityEngine;
{marker}
public class {coin} : MonoBehaviour
{{
    public int value = 1;
    public {mgr} manager;

    private bool _collected;

    void OnTriggerEnter(Collider other)
    {{
        if (!other.CompareTag("Player")) return;
{body}
    }}
}}
"""

BODIES = {
    None: """        if (_collected) return;
        _collected = true;
        if (manager != null) manager.Add(value);
        gameObject.SetActive(false);""",
    "no-guard": """        if (manager != null) manager.Add(value);""",
    "flag-only": """        if (_collected) return;
        _collected = true;
        if (manager != null) manager.Add(value);""",
}

HELPER_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("coin setup"); }}
}}
"""


def _names(ctx):
    h = ctx.handles
    return (os.path.basename(h["managerScript"])[:-len(".cs")], os.path.basename(h["coinScript"])[:-len(".cs")])


def _wait(ctx, res):
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res


def _create(ctx, name, content, folder=None):
    return _wait(ctx, ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"),
                                  content=content))


def _coin_source(ctx, variant=None, broken=False):
    mgr, coin = _names(ctx)
    text = COIN_SRC.format(marker="// usk-fake: %s" % variant if variant else "", coin=coin, mgr=mgr,
                           body=BODIES[variant])
    return text.replace("void OnTriggerEnter(Collider other)", "void OnTriggerEnter(Collider other) {") if broken \
        else text


def _scripts(ctx, variant=None, broken=False):
    mgr, coin = _names(ctx)
    _create(ctx, mgr, MANAGER_SRC.format(mgr=mgr))
    _create(ctx, coin, _coin_source(ctx, variant, broken))


def _coins(ctx):
    """(ref, name) of the seven coins."""
    return [(ref, (ctx.probe.go(ref) or {}).get("name")) for ref in ctx.handles["coins"]]


def _attach(ctx, values=True):
    h = ctx.handles
    mgr, coin = _names(ctx)
    ctx.c.write("component_add", componentType=mgr, **V.address(h["board"]))
    for ref, name in _coins(ctx):
        at = V.address(ref)
        ctx.c.write("component_add", componentType=coin, **at)
        ctx.c.write("component_set_serialized_property", componentType=coin, propertyPath="manager",
                    referencePath=h["boardPath"], objectType=mgr, **at)
        if values:
            ctx.c.write("component_set_property", componentType=coin, propertyName="value",
                        value=str(ctx.private.coin_value(ctx.seed, name)), **at)


def _coin_physics(ctx, trigger=True, layer=True, bodies=False):
    h = ctx.handles
    for ref, _name in _coins(ctx):
        if trigger:
            ctx.c.write("component_set_property", componentType="SphereCollider", propertyName="isTrigger",
                        value="true", **V.address(ref))
        if bodies:
            _kinematic(ctx, ref)
    if layer:
        ctx.c.write("gameobject_set_layer_batch", items=json.dumps(
            [dict({"layer": h["pickupLayer"]["name"]}, **V.address(ref)) for ref in h["coins"]]))


def _kinematic(ctx, ref):
    at = V.address(ref)
    ctx.c.write("component_add", componentType="Rigidbody", **at)
    ctx.c.write("component_set_property", componentType="Rigidbody", propertyName="isKinematic", value="true", **at)
    ctx.c.write("component_set_property", componentType="Rigidbody", propertyName="useGravity", value="false", **at)


def _near_path(points, pos, reach=1.0):
    """True when pos lies within reach of the polyline through points (the waypoints of the route)."""
    for a, b in zip(points, points[1:]):
        ab = [y - x for x, y in zip(a, b)]
        size = sum(v * v for v in ab)
        t = 0.0 if size <= 0.0 else max(0.0, min(1.0, sum((q - x) * v for q, x, v in zip(pos, a, ab)) / size))
        if sum((x + v * t - q) ** 2 for x, v, q in zip(a, ab, pos)) < reach * reach:
            return True
    return False


def _final_score(ctx):
    """Sum of the tier values (the numbers of the prompt) of the coins that the route passes, from the scene's
    public positions."""
    h = ctx.handles

    def at(ref):
        pos = (ctx.probe.go(ref) or {}).get("position") or {}
        return tuple(float(pos.get(axis, 0.0)) for axis in "xyz")

    points = [at(ref) for ref in h["points"]]
    total = 0
    for ref, name in _coins(ctx):
        if _near_path(points, at(ref)):
            total += ctx.private.coin_value(ctx.seed, name)
    return total


def _answer(ctx):
    h = ctx.handles
    return {"managerScriptPath": h["managerScript"], "coinScriptPath": h["coinScript"],
            "boardPath": (ctx.probe.go(h["board"]) or {}).get("path"),
            "finalScore": _final_score(ctx), "declaredComplete": True}


def _solve(ctx, variant=None, values=True, trigger=True, layer=True, player_body=True, coin_bodies=False):
    _scripts(ctx, variant)
    _attach(ctx, values=values)
    _coin_physics(ctx, trigger=trigger, layer=layer, bodies=coin_bodies)
    if player_body:
        _kinematic(ctx, ctx.handles["player"])
    return _answer(ctx)


def _write_file(ctx, asset_path, data):
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def coin_side_bodies(ctx):
    """Kinematic Rigidbodies on the coins instead of the player."""
    return _solve(ctx, player_body=False, coin_bodies=True)


def save_run_scene(ctx):
    out = reference(ctx)
    ctx.c.write("scene_save")
    return out


# ---------------------------------------------------------------- negatives


def no_guard(ctx):
    return _solve(ctx, variant="no-guard")


def flag_only(ctx):
    return _solve(ctx, variant="flag-only")


def missing_rigidbody(ctx):
    return _solve(ctx, player_body=False)


def coins_on_default(ctx):
    return _solve(ctx, layer=False)


def enable_pair(ctx):
    out = _solve(ctx, layer=False)
    ctx.c.write("physics_set_layer_collision", layer1=0, layer2=ctx.handles["actorLayer"]["index"],
                enableCollision=True)
    return out


def not_trigger(ctx):
    return _solve(ctx, trigger=False)


def default_values(ctx):
    return _solve(ctx, values=False)


def tweak_player_collider(ctx):
    """The reference plus the player's capsule turned into a trigger."""
    out = reference(ctx)
    ctx.c.write("component_set_property", componentType="CapsuleCollider", propertyName="isTrigger", value="true",
                **V.address(ctx.handles["player"]))
    return out


def other_physics_setting(ctx):
    """The reference plus one unrelated physics setting."""
    out = reference(ctx)
    ctx.c.write("scriptableobject_set_serialized_property", assetPath="ProjectSettings/DynamicsManager.asset",
                propertyPath="m_AutoSyncTransforms", value="true")
    return out


def falling_body(ctx):
    """A plain Rigidbody on the player: gravity on, not kinematic."""
    out = _solve(ctx, player_body=False)
    ctx.c.write("component_add", componentType="Rigidbody", **V.address(ctx.handles["player"]))
    return out


def player_on_default(ctx):
    h = ctx.handles
    out = _solve(ctx, layer=False)
    ctx.c.write("gameobject_set_layer_batch", items=json.dumps(
        [dict({"layer": "Default"}, **V.address(ref)) for ref in (h["player"], h["box"])]))
    return out


def nudge_coin(ctx):
    """Moves the coin beside the line onto it."""
    out = reference(ctx)
    for ref, _name in _coins(ctx):
        local = V.local_trs(ctx.probe, ref)["position"]
        if len(local) == 3 and abs(local[2]) > 1.0:
            ctx.c.write("gameobject_set_transform", localPosZ=0.0, **V.address(ref))
            break
    return out


def extra_manager(ctx):
    out = reference(ctx)
    mgr, _coin = _names(ctx)
    ctx.c.write("component_add", componentType=mgr, **V.address(ctx.handles["site"]))
    return out


def all_coins_report(ctx):
    out = reference(ctx)
    out["finalScore"] = sum(ctx.private.coin_value(ctx.seed, name) for _ref, name in _coins(ctx))
    return out


def wrong_board_report(ctx):
    out = reference(ctx)
    out["boardPath"] = ctx.handles["coinsPath"]
    return out


def extra_script(ctx):
    out = reference(ctx)
    helper = "CoinSetup_" + _names(ctx)[1][len("CoinPickup_"):]
    _create(ctx, helper, HELPER_SRC.format(cls=helper), folder=ctx.asset("Editor"))
    return out


def outside_root(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCoin")
    return out


def direct_file(ctx):
    out = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "coins.txt"), b"gold\n")
    return out


def broken_script(ctx):
    mgr, coin = _names(ctx)
    _create(ctx, mgr, MANAGER_SRC.format(mgr=mgr))
    _create(ctx, coin, _coin_source(ctx, broken=True))
    _coin_physics(ctx)
    _kinematic(ctx, ctx.handles["player"])
    try:
        _attach(ctx)
    except SkillError:
        pass
    out = _answer(ctx)
    out["declaredComplete"] = False
    return out


NEGATIVES = [
    Negative("no_guard", no_guard, {"C14", "C15"}, "adds the value on every Player collider entry, never deactivates"),
    Negative("flag_only", flag_only, {"C15"}, "counts once but leaves collected coins active"),
    Negative("missing_rigidbody", missing_rigidbody, {"C14", "C15"}, "no Rigidbody on either side: no trigger message"),
    Negative("coins_on_default", coins_on_default, {"C5", "C14", "C15"}, "coins left on Default, which the player's "
                                                                        "layer does not collide with"),
    Negative("enable_pair", enable_pair, {"C5", "C6"}, "enables the disabled pair instead of moving the coins"),
    Negative("not_trigger", not_trigger, {"C4", "C14", "C15"}, "SphereCollider.isTrigger left false"),
    Negative("default_values", default_values, {"C3", "C14"}, "value left at the code default"),
    Negative("tweak_player_collider", tweak_player_collider, {"C7"}, "the player's capsule made a trigger"),
    Negative("other_physics_setting", other_physics_setting, {"C10"}, "changes an unrelated physics setting"),
    Negative("falling_body", falling_body, {"C14", "C15"}, "a Rigidbody on the player left to gravity"),
    Negative("player_on_default", player_on_default, {"C5", "C7"}, "moves the player and its box to Default"),
    Negative("nudge_coin", nudge_coin, {"C9", "C14", "C15"}, "moves the coin beside the line onto the path"),
    Negative("extra_manager", extra_manager, {"C2"}, "a second score manager on the site"),
    Negative("all_coins_report", all_coins_report, {"C13"}, "reports the sum of every coin"),
    Negative("wrong_board_report", wrong_board_report, {"C12"}),
    Negative("extra_script", extra_script, {"C8"}),
    Negative("outside_root", outside_root, {"C10"}),
    Negative("direct_file", direct_file, {"C11"}, "a non-script file written to disk next to the skills"),
    Negative("broken_script", broken_script, {"C1", "C2", "C3", "C14", "C15"}, "a compile error, coins not wired"),
]

ALTERNATIVES = [("coin_side_bodies", coin_side_bodies), ("save_run_scene", save_run_scene)]

# Producer:Betsy
