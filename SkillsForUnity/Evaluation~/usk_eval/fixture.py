"""Run-scoped fixture lifecycle (contract 2.8 N2-N4, S11; ported from v1 eval/harness/fixture.py).

Every attempt lives in its own scene and folder under Assets/__UnitySkillsEval/<runId>/, so the user's scenes are
never saved or edited. Entering refuses unsaved or untitled scenes (scene_create discards them without a prompt);
leaving reloads the recorded resting scenes before deleting the run folder, and only ever deletes a path that
equals Assets/__UnitySkillsEval/<runId> with a well-formed runId.
"""
import os
import re
import time

from . import store, submodule
from .paths import EVAL_ASSET_ROOT
from .rest import SkillError

RUN_ID_RE = re.compile(r"^r\d{8}T\d{6}[0-9a-f]{6}$")
RETRY_DELAY_S = 5.0


class FixtureError(RuntimeError):
    pass


def new_run_id():
    return store.stamp_id("r")


def object_id(value):
    """verifylib's canonical object id: the non-zero int instanceId, else the entityId text (the only id Unity 6000.4+
    reports, instanceId is always 0 there); None when `value` names nothing."""
    return submodule("verifylib").object_id(value)


def address(value, prefix=""):
    """verifylib.address: skill arguments naming `value` (instanceId while it fits C# int, entityId otherwise;
    prefix "parent" gives parentInstanceId / parentEntityId). One rule for the runner, verifiers and Probe."""
    return submodule("verifylib").address(value, prefix)


class Scope(object):
    def __init__(self, run_id, root_iid=None, folder_created=False, scene_created=False, root_entity_id=None,
                 root_id=None):
        if not RUN_ID_RE.match(run_id or ""):
            raise ValueError("bad run id %r" % (run_id,))
        self.run_id = run_id
        self.object_root = "EV_" + run_id
        self.asset_root = EVAL_ASSET_ROOT + "/" + run_id
        self.scene_path = "%s/Eval_%s.unity" % (self.asset_root, run_id)
        self.root_iid = root_iid                 # legacy instanceId, 0 on Unity 6000.4+
        self.root_entity_id = root_entity_id
        self.root_id = root_id                   # canonical id (verifylib.object_id): what tasks and Probe use
        self.folder_created = folder_created
        self.scene_created = scene_created

    def as_dict(self):
        return {"runId": self.run_id, "objectRoot": self.object_root, "assetRoot": self.asset_root,
                "scenePath": self.scene_path, "rootInstanceId": self.root_iid, "rootEntityId": self.root_entity_id,
                "rootId": self.root_id, "folderCreated": self.folder_created, "sceneCreated": self.scene_created}

    @classmethod
    def from_dict(cls, data):
        return cls(data["runId"], data.get("rootInstanceId"), bool(data.get("folderCreated")),
                   bool(data.get("sceneCreated")), data.get("rootEntityId"), data.get("rootId"))

    def owned(self):
        return bool(RUN_ID_RE.match(self.run_id)) and self.asset_root == EVAL_ASSET_ROOT + "/" + self.run_id


def under(path, root):
    if not path or not root:
        return False
    path = path.replace("\\", "/")
    root = root.replace("\\", "/").rstrip("/")
    return path == root or path.startswith(root + "/")


# ---------------------------------------------------------------- scenes


def loaded_scenes(client):
    res = client.read("scene_get_loaded")
    scenes = res.get("scenes") or res.get("loadedScenes") or []
    return [s for s in scenes if isinstance(s, dict) and not s.get("isPseudoScene")]


def scene_summary(scenes):
    dirty = [s.get("path") or s.get("name") or "<untitled>" for s in scenes if s.get("isDirty")]
    active = next((s for s in scenes if s.get("isActive")), None)
    return {"loaded": [{"path": s.get("path") or "", "isDirty": bool(s.get("isDirty")),
                        "isActive": bool(s.get("isActive"))} for s in scenes],
            "dirty": dirty, "activeUntitled": active is None or not active.get("path")}


def resting_record(scenes):
    return {"scenes": [{"path": s.get("path"), "isActive": bool(s.get("isActive")),
                        "isLoaded": bool(s.get("isLoaded", True))} for s in scenes if s.get("path")]}


def active_scene_path(client):
    return client.read("scene_get_info").get("scenePath")


def asset_exists(client, path):
    try:
        client.read("asset_get_info", assetPath=path)
        return True
    except SkillError:
        return False


def _matches_resting(scenes, resting):
    wanted = [(s["path"], s["isActive"]) for s in resting.get("scenes") or [] if s.get("isLoaded", True)]
    current = [(s.get("path"), bool(s.get("isActive"))) for s in scenes if s.get("isLoaded", True)]
    return bool(wanted) and wanted == current


def restore_resting(client, resting):
    """First scene Single, the rest additive in order, then the original active scene (contract 2.8 S11)."""
    scenes = [s for s in (resting or {}).get("scenes") or [] if s.get("path") and s.get("isLoaded", True)]
    if not scenes:
        raise FixtureError("no resting scene recorded")
    actions = []
    client.write("scene_load", scenePath=scenes[0]["path"], additive=False)
    actions.append("scene_load %s" % scenes[0]["path"])
    for scene in scenes[1:]:
        client.write("scene_load", scenePath=scene["path"], additive=True)
        actions.append("scene_load+ %s" % scene["path"])
    active = next((s for s in scenes if s.get("isActive")), None)
    if active and active is not scenes[0]:
        name = active["path"][:-len(".unity")] if active["path"].endswith(".unity") else active["path"]
        client.write("scene_set_active", sceneName=name)
        actions.append("scene_set_active %s" % name)
    return actions


# ---------------------------------------------------------------- enter (N4)


def enter(client, scope, persist):
    """Create the run folder, the run scene (Single) and the object root; persist() after every step."""
    if not asset_exists(client, EVAL_ASSET_ROOT):
        client.write("asset_create_folder", folderPath=EVAL_ASSET_ROOT)
    if asset_exists(client, scope.asset_root):
        raise FixtureError("run folder %s already exists" % scope.asset_root)
    client.write("asset_create_folder", folderPath=scope.asset_root)
    scope.folder_created = True
    persist()
    client.write("scene_create", scenePath=scope.scene_path)
    scope.scene_created = True
    persist()
    active = active_scene_path(client)
    if active != scope.scene_path:
        raise FixtureError("run scene not active after scene_create (active=%r)" % active)
    root = client.write("gameobject_create", name=scope.object_root)
    scope.root_id = object_id(root)
    if scope.root_id is None:
        raise FixtureError("gameobject_create reported no usable id for %s" % scope.object_root)
    iid = root.get("instanceId")
    scope.root_iid = iid if isinstance(iid, int) and not isinstance(iid, bool) else 0
    scope.root_entity_id = str(root["entityId"]) if root.get("entityId") not in (None, "") else str(scope.root_id)
    persist()
    return scope


def save_run_scene(client, scope):
    if active_scene_path(client) != scope.scene_path:
        raise FixtureError("refusing to save: active scene is not the run scene")
    client.write("scene_save")


# ---------------------------------------------------------------- settings


def collision_snapshot(client, layer_pairs):
    out = {}
    for a, b in layer_pairs or ():
        res = client.read("physics_get_layer_collision", layer1=int(a), layer2=int(b))
        out["%d,%d" % (int(a), int(b))] = res.get("collisionEnabled")
    return out


def restore_collision(client, collision):
    restored = []
    for key, enabled in (collision or {}).items():
        a, b = (int(x) for x in key.split(","))
        now = client.read("physics_get_layer_collision", layer1=a, layer2=b).get("collisionEnabled")
        if now != enabled:
            client.write("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=bool(enabled))
            restored.append(key)
    return restored


# ---------------------------------------------------------------- teardown (S11)


def _stop_play(client, timeout_s=60.0):
    if not client.read("editor_get_state").get("isPlaying"):
        return False
    client.write("editor_stop")
    deadline = time.time() + timeout_s
    while client.read("editor_get_state").get("isPlaying"):
        if time.time() > deadline:
            raise FixtureError("editor still in Play Mode after editor_stop")
        time.sleep(0.5)
    return True


def _teardown_once(client, scope, resting, collision, task_teardown, actions):
    hook_error = None
    if _stop_play(client):
        actions.append("editor_stop")
    if task_teardown is not None:
        try:
            task_teardown()
            actions.append("task_teardown")
        except Exception as exc:       # the run folder and the scenes are still cleaned up
            hook_error = "task teardown: %s: %s" % (type(exc).__name__, exc)
    restored = restore_collision(client, collision)
    if restored:
        actions.append("collision %s" % ",".join(restored))
    if not scope.owned():
        raise FixtureError("ownership check failed for %r" % scope.asset_root)
    scenes = loaded_scenes(client)
    run_scene_open = any(under(s.get("path"), scope.asset_root) for s in scenes)
    if run_scene_open or ((resting or {}).get("scenes") and not _matches_resting(scenes, resting)):
        actions.extend(restore_resting(client, resting))
    if asset_exists(client, scope.asset_root):
        client.write("asset_delete", assetPath=scope.asset_root)
        actions.append("asset_delete %s" % scope.asset_root)
    if asset_exists(client, scope.asset_root):
        raise FixtureError("run folder %s still present after cleanup" % scope.asset_root)
    client.wait_idle(timeout_s=900)
    if hook_error:
        raise FixtureError(hook_error)


def teardown(client, scope, resting, collision, task_teardown=None, sleep=time.sleep):
    """{ok, actions, error}; one retry after 5 s and wait_idle (v1 runner.py:232-250)."""
    actions = []
    try:
        _teardown_once(client, scope, resting, collision, task_teardown, actions)
        return {"ok": True, "actions": actions, "error": None}
    except Exception as first:
        actions.append("retry after %s: %s" % (type(first).__name__, first))
    try:
        sleep(RETRY_DELAY_S)
        client.wait_idle(timeout_s=900)
        _teardown_once(client, scope, resting, collision, task_teardown, actions)
        return {"ok": True, "actions": actions, "error": None}
    except Exception as exc:
        return {"ok": False, "actions": actions, "error": "%s: %s" % (type(exc).__name__, exc)}


# ---------------------------------------------------------------- residue


def residue(eval_assets_dir, active_run_id=None):
    """([{runId, assetRoot}] owned leftovers, [names] foreign entries) under Assets/__UnitySkillsEval."""
    try:
        names = sorted(os.listdir(eval_assets_dir))
    except OSError:
        return [], []
    owned, foreign = [], []
    for name in names:
        if not os.path.isdir(os.path.join(eval_assets_dir, name)):
            continue
        if RUN_ID_RE.match(name):
            if name != active_run_id:
                owned.append({"runId": name, "assetRoot": EVAL_ASSET_ROOT + "/" + name})
        else:
            foreign.append(name)
    return owned, foreign


def eval_root_empty(eval_assets_dir):
    try:
        return os.path.isdir(eval_assets_dir) and not [n for n in os.listdir(eval_assets_dir) if n != ".DS_Store"]
    except OSError:
        return False

# Producer:Betsy
