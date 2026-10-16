"""Run-scoped fixture lifecycle: ownership, run-owned scene, settings snapshot/restore, cleanup.

Every attempt lives in its own scene file and folder under Assets/__UnitySkillsEval/<runId>/, so
the user's scenes are never saved or edited. Entering refuses to start while any loaded scene
has unsaved changes (scene_create discards them without a prompt); leaving switches back to the
resting scene before deleting the run folder, and only ever deletes a path that matches the
run-id pattern exactly.
"""
import datetime
import re
import secrets

from .rest import SkillError

EVAL_ROOT = "Assets/__UnitySkillsEval"
RESTING_SCENE = "Assets/Scenes/SampleScene.unity"
RUN_ID_RE = re.compile(r"^r\d{8}T\d{6}[0-9a-f]{6}$")


class AwaitingUser(RuntimeError):
    """The environment needs the user (unsaved scene, wrong mode); not a model failure."""


class FixtureError(RuntimeError):
    pass


def new_run_id():
    return "r%s%s" % (datetime.datetime.now().strftime("%Y%m%dT%H%M%S"), secrets.token_hex(3))


class Scope(object):
    def __init__(self, run_id):
        if not RUN_ID_RE.match(run_id):
            raise ValueError("bad run id %r" % run_id)
        self.run_id = run_id
        self.object_root = "EV_" + run_id
        self.asset_root = EVAL_ROOT + "/" + run_id
        self.scene_path = "%s/Eval_%s.unity" % (self.asset_root, run_id)
        self.root_iid = None
        self.folder_created = False
        self.scene_created = False
        self.facts = {}          # task-private fixture facts (ids, names, expected values)
        self.settings = None     # ProjectSettings snapshot taken before the fixture

    def as_dict(self):
        return {"runId": self.run_id, "objectRoot": self.object_root, "assetRoot": self.asset_root,
                "scenePath": self.scene_path, "rootInstanceId": self.root_iid,
                "folderCreated": self.folder_created, "sceneCreated": self.scene_created}


def loaded_scenes(client):
    res = client.read("scene_get_loaded")
    return res.get("scenes") or res.get("loadedScenes") or []


def guard_unsaved(client):
    dirty = [s for s in loaded_scenes(client) if s.get("isDirty")]
    if dirty:
        raise AwaitingUser("unsaved changes in %s; save or discard them before evaluating"
                           % ", ".join(s.get("path") or s.get("name") or "<untitled>" for s in dirty))


def asset_exists(client, path):
    try:
        client.read("asset_get_info", assetPath=path)
        return True
    except SkillError:
        return False


def enter(client, scope):
    guard_unsaved(client)
    if not asset_exists(client, EVAL_ROOT):
        client.write("asset_create_folder", folderPath=EVAL_ROOT)
    if asset_exists(client, scope.asset_root):
        raise FixtureError("run folder %s already exists" % scope.asset_root)
    client.write("asset_create_folder", folderPath=scope.asset_root)
    scope.folder_created = True
    client.write("scene_create", scenePath=scope.scene_path)
    scope.scene_created = True
    active = active_scene_path(client)
    if active != scope.scene_path:
        raise FixtureError("run scene not active after scene_create (active=%r)" % active)
    root = client.write("gameobject_create", name=scope.object_root)
    scope.root_iid = int(root["instanceId"])
    return scope


def active_scene_path(client):
    info = client.read("scene_get_info")
    return info.get("scenePath")


def save_run_scene(client, scope):
    if active_scene_path(client) != scope.scene_path:
        raise FixtureError("refusing to save: active scene is not the run scene")
    client.write("scene_save")


def settings_snapshot(client, layer_pairs=()):
    snap = {"tags": client.read("editor_get_tags"), "layers": client.read("editor_get_layers"),
            "collision": {}}
    for a, b in layer_pairs:
        res = client.read("physics_get_layer_collision", layer1=int(a), layer2=int(b))
        snap["collision"]["%d,%d" % (a, b)] = res.get("collisionEnabled")
    return snap


def restore_collision(client, snapshot):
    restored = []
    for key, enabled in (snapshot or {}).get("collision", {}).items():
        a, b = (int(x) for x in key.split(","))
        now = client.read("physics_get_layer_collision", layer1=a, layer2=b).get("collisionEnabled")
        if now != enabled:
            client.write("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=bool(enabled))
            restored.append(key)
    return restored


def exit(client, scope, resting_scene=RESTING_SCENE):
    """Leave the run scene (its unsaved state is discarded by design) and delete the run folder."""
    actions = []
    if not RUN_ID_RE.match(scope.run_id) or scope.asset_root != EVAL_ROOT + "/" + scope.run_id:
        raise FixtureError("ownership check failed for %r" % scope.asset_root)
    if active_scene_path(client) != resting_scene:
        client.write("scene_load", scenePath=resting_scene)
        actions.append("scene_load %s" % resting_scene)
    if asset_exists(client, scope.asset_root):
        client.write("asset_delete", assetPath=scope.asset_root)
        actions.append("asset_delete %s" % scope.asset_root)
    if asset_exists(client, scope.asset_root):
        raise FixtureError("run folder %s still present after cleanup" % scope.asset_root)
    return actions

# Producer:Betsy
