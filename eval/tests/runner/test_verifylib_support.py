"""Shared bootstrap, fakes and record builders for the usk_eval.verifylib and usk_eval.scope tests (no tests here).

Importing it puts the packaged runner on sys.path.
"""
import json
import sys
from pathlib import Path

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[3]
PACKAGE = REPO / "SkillsForUnity" / "Evaluation~"
VERIFYLIB_SOURCES = PACKAGE / "usk_eval" / "verifylib"
FIXTURES = Path(__file__).resolve().parent / "fixtures" / "scope"
sys.path.insert(0, str(PACKAGE))

from usk_eval.rest import SkillError  # noqa: E402

RUN_ID = "r20261017T030000a1b2c3"
OTHER_RUN_ID = "r20261017T031500d4e5f6"
R = "EV_" + RUN_ID
A = "Assets/__UnitySkillsEval/" + RUN_ID
SCENE = "%s/Eval_%s.unity" % (A, RUN_ID)
OTHER_SCENE = "Assets/Scenes/Main.unity"
RUNNER = "UnitySkillsEval"
AGENT = "ClaudeCode"


# ---------------------------------------------------------------- fixtures


def fixture_entries(name):
    """Raw journal entries (as the plugin writes them) of fixtures/scope/<name>.jsonl, in seq order."""
    entries = []
    with open(str(FIXTURES / name), "r", encoding="utf-8") as handle:
        for line in handle:
            if line.strip():
                entries.append(json.loads(line))
    return sorted(entries, key=lambda entry: entry["seq"])


# ---------------------------------------------------------------- flat record builders (usk_eval.journal.flatten shape)


def rec(kind, **fields):
    base = {"seq": 1, "type": "scene_changes", "source": "rest", "agent": AGENT, "isPlaying": False,
            "origin": "attempt", "kind": kind}
    base.update(fields)
    return base


def created(identity, path, scene=SCENE, **fields):
    return rec("gameobject_created", entityId=str(identity), instanceId=int(identity), path=path, scenePath=scene,
               name=path.split("/")[-1], **fields)


def reparented(identity, from_path, to_path, scene=SCENE, **fields):
    return rec("gameobject_reparented", entityId=str(identity), instanceId=int(identity), fromPath=from_path,
               toPath=to_path, newParentPath=to_path.rsplit("/", 1)[0] if "/" in to_path else None, scenePath=scene,
               name=to_path.split("/")[-1], **fields)


def destroyed(identity, path=None, scene=None, **fields):
    """gameobject_destroyed; without a path it is the pathless record of an object the plugin's catalog did not know."""
    if path is None:
        return rec("gameobject_destroyed", entityId=str(identity), instanceId=int(identity), **fields)
    return rec("gameobject_destroyed", entityId=str(identity), instanceId=int(identity), path=path,
               scenePath=scene or SCENE, name=path.split("/")[-1], **fields)


def prop(path, scene=SCENE, **fields):
    return rec("property_changed", path=path, scenePath=scene, property="m_Name", targetType="GameObject", **fields)


def file_rec(kind, asset_path, **fields):
    return rec(kind, type="file_changes", assetPath=asset_path, **fields)


def scene_rec(kind, path, **fields):
    return rec(kind, type=kind, path=path, **fields)


# ---------------------------------------------------------------- fake editor


class FakeEditor(object):
    """In-memory scene answering the read skills Probe uses. It has no write method, so a Probe that tried one fails.

    entity_ids=True behaves like Unity 6000.4+: every instanceId it reports is 0, entityIds are large unsigned numbers
    and only an entityId names an object. Otherwise an entityId is the instanceId as text, as before 6000.4. Objects
    are added under a test-side key (`iid`); entity() turns the key into the id text the editor reports.
    """

    ENTITY_BASE = 18446744073709551000

    def __init__(self, entity_ids=False):
        self.entity_ids = entity_ids
        self.objects = {}
        self.entities = {}
        self.serialized = {}
        self.materials = {}
        self.object_materials = {}
        self.assets = {}
        self.calls = []

    def entity(self, iid):
        return str(self.ENTITY_BASE + iid) if self.entity_ids else str(iid)

    def instance(self, iid):
        return 0 if self.entity_ids else iid

    def add(self, iid, name, parent=None, active=True, tag="Untagged", layer="Default", position=(0, 0, 0),
            rotation=(0, 0, 0), scale=(1, 1, 1), components=("Transform",), disabled=()):
        parent_path = self.objects[parent]["path"] if parent is not None else None
        path = name if parent_path is None else parent_path + "/" + name
        self.objects[iid] = {"iid": iid, "name": name, "parent": parent, "path": path, "active": active, "tag": tag,
                             "layer": layer, "position": position, "rotation": rotation, "scale": scale,
                             "components": list(components), "disabled": set(disabled), "children": []}
        self.entities[self.entity(iid)] = iid
        if parent is not None:
            self.objects[parent]["children"].append(iid)
        return iid

    def alias(self, component_id, iid):
        """A component's own id: skills resolve it to the GameObject that carries it."""
        self.entities[str(component_id)] = iid

    def remove(self, iid):
        obj = self.objects.pop(iid)
        if obj["parent"] in self.objects:
            self.objects[obj["parent"]]["children"].remove(iid)

    def read(self, skill, **args):
        self.calls.append((skill, args))
        handler = getattr(self, "_" + skill, None)
        if handler is None:
            raise SkillError("unknown skill %s" % skill, payload={"errorCode": "UNKNOWN_SKILL"})
        return handler(**args)

    def _find(self, instanceId=None, path=None, entityId=None):
        key = None
        if entityId is not None:
            key = self.entities.get(str(entityId).strip())
        elif instanceId:
            key = None if self.entity_ids else self.entities.get(str(instanceId))
        elif path is not None:
            key = next((k for k, o in self.objects.items() if o["path"] == path), None)
        obj = self.objects.get(key)
        if obj is None:
            raise SkillError("not found", payload={"errorCode": "TARGET_NOT_FOUND"})
        return obj

    def _ids(self, iid):
        return {"entityId": self.entity(iid), "instanceId": self.instance(iid)}

    def _gameobject_get_info(self, instanceId=None, path=None, entityId=None):
        obj = self._find(instanceId, path, entityId)

        def vec(t):
            return {"x": t[0], "y": t[1], "z": t[2]}

        parent = self.objects.get(obj["parent"])
        info = {"name": obj["name"], "path": obj["path"], "tag": obj["tag"], "layer": obj["layer"],
                "isActive": obj["active"], "position": vec(obj["position"]), "rotation": vec(obj["rotation"]),
                "scale": vec(obj["scale"]), "parent": parent["name"] if parent else None,
                "parentPath": parent["path"] if parent else None, "childCount": len(obj["children"]),
                "children": [self._child(c) for c in obj["children"]], "components": list(obj["components"])}
        info.update(self._ids(obj["iid"]))
        return info

    def _child(self, iid):
        """A child entry; a stale id (the object vanished after its parent listed it) has no name or path."""
        child = self.objects.get(iid)
        entry = {"name": child["name"] if child else None, "path": child["path"] if child else None}
        entry.update(self._ids(iid))
        return entry

    def _component_list(self, instanceId=None, path=None, entityId=None):
        obj = self._find(instanceId, path, entityId)
        comps = []
        for name in obj["components"]:
            entry = {"type": name, "fullType": "UnityEngine." + name}
            if name != "Transform":
                entry["enabled"] = name not in obj["disabled"]
            comps.append(entry)
        info = {"gameObject": obj["name"], "path": obj["path"], "componentCount": len(comps), "components": comps}
        info.update(self._ids(obj["iid"]))
        return info

    def _component_get_serialized_properties(self, instanceId=None, entityId=None, componentType=None, limit=200):
        obj = self._find(instanceId, None, entityId)
        props = self.serialized.get((obj["iid"], componentType))
        if props is None:
            raise SkillError("component not found", payload={"errorCode": "COMPONENT_NOT_FOUND"})
        return {"success": True, "properties": [{"propertyPath": k, "value": v} for k, v in props.items()]}

    def _material_get_properties(self, path=None, instanceId=None, entityId=None):
        if path is not None:
            info = self.materials.get(path)
        else:
            info = self.object_materials.get(self._find(instanceId, None, entityId)["iid"])
        if info is None:
            raise SkillError("material not found", payload={"errorCode": "ASSET_NOT_FOUND"})
        return info

    def _asset_get_info(self, assetPath=None):
        info = self.assets.get(assetPath)
        if info is None:
            raise SkillError("asset not found", payload={"errorCode": "ASSET_NOT_FOUND"})
        return info

    def _cleaner_get_dependency_tree(self, assetPath=None, recursive=True):
        self._asset_get_info(assetPath)
        return {"assetPath": assetPath, "dependencies": []}

    def _scriptableobject_get_serialized_properties(self, assetPath=None):
        self._asset_get_info(assetPath)
        return {"assetPath": assetPath, "properties": []}

    def _batch_query_gameobjects(self, queryJson=None, sampleLimit=500):
        found = []
        for obj in self.objects.values():
            entry = {"name": obj["name"], "path": obj["path"]}
            entry.update(self._ids(obj["iid"]))
            found.append(entry)
        return {"count": len(found), "objects": found[:sampleLimit]}


def sample_scene(entity_ids=False):
    """R / Props / Crate and R / Props / Lamp, test keys 100..103."""
    editor = FakeEditor(entity_ids)
    editor.add(100, R, position=(0, 0, 0))
    editor.add(101, "Props", parent=100)
    editor.add(102, "Crate", parent=101, position=(0, 0.5, 0), components=("Transform", "BoxCollider"))
    editor.add(103, "Lamp", parent=101, active=False, components=("Transform", "Light"), disabled=("Light",))
    return editor

# Producer:Betsy
