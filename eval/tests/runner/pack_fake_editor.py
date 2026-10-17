"""In-memory stand-in for a UnitySkills editor, used by the offline tests of the pack tasks (no Unity, no HTTP).

It models what the six basic tasks, their solutions and verifiers touch: GameObjects with local TRS under rotated and
scaled parents, primitives with their components and meshes, materials per shader (Standard, URP Lit, Unlit/Color)
with the plugin's colour-property resolution, asset files on disk, the query skills, and journal entries shaped like
EditorChangeTrackerService's (gameobject_created, property_changed with previousValue, gameobject_reparented, ...).
refresh() plays the runner's submit-time asset_refresh: files under Assets/ that no skill wrote are imported then.
"""
import hashlib
import json
import math
import os
import re
import shutil
import sys

sys.dont_write_bytecode = True
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
RUNNER_DIR = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
if RUNNER_DIR not in sys.path:
    sys.path.insert(0, RUNNER_DIR)

from usk_eval import store  # noqa: E402
from usk_eval.rest import RestError, SkillError  # noqa: E402
from usk_eval.verifylib import checks  # noqa: E402

SHADERS = {
    "Standard": {"colors": {"_Color": (1.0, 1.0, 1.0, 1.0), "_EmissionColor": (0.0, 0.0, 0.0, 1.0)},
                 "floats": {"_Glossiness": 0.5, "_Metallic": 0.0}},
    "Universal Render Pipeline/Lit": {"colors": {"_BaseColor": (1.0, 1.0, 1.0, 1.0), "_Color": (1.0, 1.0, 1.0, 1.0),
                                                 "_EmissionColor": (0.0, 0.0, 0.0, 1.0),
                                                 "_SpecColor": (0.2, 0.2, 0.2, 1.0)},
                                      "floats": {"_Smoothness": 0.5, "_Metallic": 0.0}},
    "Unlit/Color": {"colors": {"_Color": (1.0, 1.0, 1.0, 1.0)}, "floats": {}},
}
PIPELINES = {"BuiltIn": ("Standard", "_Color", "Built-in Render Pipeline"),
             "URP": ("Universal Render Pipeline/Lit", "_BaseColor", "PC_RPAsset")}
PRIMITIVES = {"cube": ("Cube", 24, "BoxCollider"), "sphere": ("Sphere", 515, "SphereCollider"),
              "capsule": ("Capsule", 550, "CapsuleCollider"), "cylinder": ("Cylinder", 88, "CapsuleCollider")}
MAIN_COLORS = ("_BaseColor", "_Color", "_MainColor", "_TintColor")
ENABLEABLE = ("MeshRenderer", "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "AudioSource")
FULL_TYPES = {"Transform": "UnityEngine.Transform", "AudioSource": "UnityEngine.AudioSource"}
READ_SKILLS = frozenset((
    "gameobject_get_info", "component_list", "component_get_serialized_properties", "component_get_properties",
    "material_get_properties", "asset_get_info", "model_get_mesh_info", "batch_query_gameobjects", "gameobject_find",
    "project_get_render_pipeline", "scene_get_info", "scene_get_loaded", "editor_get_state", "editor_get_changes",
    "debug_check_compilation", "script_get_compile_feedback", "scriptableobject_get_serialized_properties",
    "editor_playmode_inspect"))
BUILTIN_COMPONENTS = ENABLEABLE + ("MeshFilter", "Rigidbody", "Light", "Camera")
CLASS_RE = re.compile(r"public\s+(?:static\s+)?class\s+(\w+)\s*(?::\s*([\w.]+))?")
FIELD_RE = re.compile(r"((?:\[[^\]]*\]\s*)*)public\s+([\w.]+)\s+(\w+)\s*(?:=\s*([^;\n]+))?;")
FORMERLY_RE = re.compile(r"FormerlySerializedAs(?:Attribute)?\s*\(\s*\"(\w+)\"\s*\)")
UNTERMINATED_RE = re.compile(r"^\s*public\s+[\w.]+\s+\w+\s*=\s*[^;{}\n]+$", re.M)
SCALARS = {"float": float, "int": int, "bool": lambda text: text.strip().lower() == "true", "string": str}
RUNNER = "UnitySkillsEval"


# ---------------------------------------------------------------- vector and rotation math (Unity conventions)


def q_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def q_inv(q):
    return (-q[0], -q[1], -q[2], q[3])


def q_rotate(q, v):
    x, y, z, _w = q_mul(q_mul(q, (v[0], v[1], v[2], 0.0)), q_inv(q))
    return (x, y, z)


def q_normalize(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def euler_from_q(q):
    """Unity eulerAngles (degrees, 0..360) of a rotation that applies Z, then X, then Y."""
    x, y, z, w = q_normalize(q)
    m02 = 2 * (x * z + w * y)
    m10 = 2 * (x * y + w * z)
    m11 = 1 - 2 * (x * x + z * z)
    m12 = 2 * (y * z - w * x)
    m22 = 1 - 2 * (x * x + y * y)
    m00 = 1 - 2 * (y * y + z * z)
    m20 = 2 * (x * z - w * y)
    ex = math.asin(max(-1.0, min(1.0, -m12)))
    if math.cos(ex) > 1e-6:
        ey, ez = math.atan2(m02, m22), math.atan2(m10, m11)
    else:
        ey, ez = math.atan2(-m20, m00), 0.0
    return tuple(round(math.degrees(a) % 360.0, 6) % 360.0 for a in (ex, ey, ez))


def q_from_euler(euler):
    return checks.quat_from_euler(*euler)


def _numbers(text, count):
    values = [float(v) for v in checks.NUM_RE.findall(str(text))]
    if len(values) != count:
        raise ValueError("expected %d numbers in %r" % (count, text))
    return tuple(values)


def _g9(values):
    return ",".join("%.9g" % v for v in values)


# ---------------------------------------------------------------- scene model


class Obj(object):
    def __init__(self, eid, iid, name, parent):
        self.eid, self.iid, self.name, self.parent = eid, iid, name, parent
        self.children = []
        self.active = True
        self.pos, self.rot, self.scale = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)
        self.components = [["Transform", None]]
        self.fields = {}
        self.mesh = None
        self.materials = []
        self.tag, self.layer = "Untagged", "Default"


class FakeEditor(object):
    """journal_path: also append every journal entry there, as the plugin does (runner-flow tests read it back)."""

    def __init__(self, project, pipeline="BuiltIn", entity_mode="legacy", unity="2022.3.62f2", journal_path=None):
        self.project = project
        self.journal_path = journal_path
        self.pipeline = pipeline
        self.entity_mode = entity_mode
        self.unity = unity
        self.objects = {}
        self.roots = []
        self.scene_path = "Assets/Scenes/Main.unity"
        self.materials = {}
        self.known = set()
        self.entries = []
        self.seq = 0
        self._next = 1000
        self.classes = {}
        self.scripts = {}
        self.compile_errors = []
        self.sos = {}
        self.prefabs = {}
        self.owners = {}
        self.playing = False
        os.makedirs(os.path.join(project, "Assets", "Scenes"), exist_ok=True)
        self._write_file(self.scene_path, "%YAML 1.1\n")

    def client(self, agent):
        return FakeClient(self, agent)

    # ------------------------------------------------------------ journal

    def publish(self, etype, agent, payload, source="rest"):
        self.seq += 1
        entry = {"seq": self.seq, "type": etype, "source": source, "agent": agent if source == "rest" else None,
                 "isPlaying": False, "tsUtc": store.now_iso(), "payload": payload}
        self.entries.append(entry)
        if self.journal_path:
            with open(self.journal_path, "a", encoding="utf-8") as handle:
                handle.write(json.dumps(entry, separators=(",", ":")) + "\n")

    def _change(self, agent, change):
        self.publish("scene_changes", agent, {"count": 1, "omitted": 0, "changes": [change]})

    def _identity(self, obj, **extra):
        out = {"name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
               "scenePath": self.scene_path}
        out.update(extra)
        return out

    def _imported(self, agent, paths):
        if paths:
            self.publish("file_changes", agent, {"imported": list(paths), "deleted": [], "moved": [],
                                                 "count": len(paths), "omitted": 0, "importedTotal": len(paths),
                                                 "deletedTotal": 0, "movedTotal": 0})

    # ------------------------------------------------------------ files

    def disk(self, asset_path):
        return os.path.join(self.project, *asset_path.split("/"))

    def _write_file(self, asset_path, text):
        path = self.disk(asset_path)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        with open(path + ".meta", "w", encoding="utf-8") as handle:
            handle.write("fileFormatVersion: 2\nguid: %s\n" % self._guid(asset_path))
        self.known.add(asset_path)

    @staticmethod
    def _guid(asset_path):
        return hashlib.md5(asset_path.encode("utf-8")).hexdigest()

    def _ensure_folder(self, folder, agent):
        created = []
        parts = folder.split("/")
        for depth in range(2, len(parts) + 1):
            sub = "/".join(parts[:depth])
            path = self.disk(sub)
            if not os.path.isdir(path):
                os.makedirs(path)
                with open(path + ".meta", "w", encoding="utf-8") as handle:
                    handle.write("fileFormatVersion: 2\nfolderAsset: yes\n")
                created.append(sub)
            self.known.add(sub)
        self._imported(agent, created)

    def refresh(self, agent=RUNNER):
        """The runner's asset_refresh: imports files under Assets/ that no skill wrote (and recompiles scripts whose
        file changed on disk)."""
        changed = False
        for path, source in list(self.scripts.items()):
            try:
                with open(self.disk(path), "r", encoding="utf-8-sig") as handle:
                    now = handle.read()
            except OSError:
                continue
            if now != source:
                self.scripts[path] = now
                changed = True
        if changed:
            self._compile()
        found = []
        for base, dirs, files in os.walk(self.disk("Assets")):
            dirs.sort()
            for name in sorted(files):
                if name.endswith(".meta"):
                    continue
                rel = os.path.relpath(os.path.join(base, name), self.project).replace(os.sep, "/")
                if rel not in self.known:
                    found.append(rel)
                    self.known.add(rel)
        self._imported(agent, found)
        return found

    # ------------------------------------------------------------ objects

    def _new_ids(self):
        self._next += 2
        if self.entity_mode == "6000.4":
            return str(2 ** 33 + self._next), 0
        return str(-self._next), -self._next

    def path(self, obj):
        parts = []
        while obj is not None:
            parts.append(obj.name)
            obj = obj.parent
        return "/".join(reversed(parts))

    def _siblings(self, obj):
        return obj.parent.children if obj.parent is not None else self.roots

    def walk(self, roots=None):
        out = []
        stack = list(reversed(roots if roots is not None else self.roots))
        while stack:
            obj = stack.pop()
            out.append(obj)
            stack.extend(reversed(obj.children))
        return out

    def active_in_hierarchy(self, obj):
        while obj is not None:
            if not obj.active:
                return False
            obj = obj.parent
        return True

    def find(self, args, prefix="", required=True):
        def arg(name):
            return args.get(prefix + name[0].upper() + name[1:] if prefix else name)

        eid, iid, path, name = arg("entityId"), arg("instanceId"), arg("path"), arg("name")
        found = None
        if eid not in (None, ""):
            found = self.objects.get(str(eid)) or self.objects.get(self.owners.get(str(eid)))
        elif iid not in (None, 0):
            found = next((o for o in self.objects.values() if o.iid == iid and o.iid != 0), None) or \
                self.objects.get(self.owners.get(str(iid)))
        elif path:
            found = next((o for o in self.walk() if self.path(o) == path), None)
        elif name:
            found = next((o for o in self.walk() if o.name == name), None)
        else:
            if not required:
                return None
            raise _fail("TARGET_REQUIRED", "no %sname/instanceId/path/entityId given" % prefix)
        if found is None:
            raise _fail("GAMEOBJECT_NOT_FOUND", "GameObject not found")
        return found

    def world(self, obj):
        """(position, rotation, lossy scale) in world space."""
        if obj.parent is None:
            return obj.pos, obj.rot, obj.scale
        ppos, prot, pscale = self.world(obj.parent)
        scaled = tuple(a * b for a, b in zip(pscale, obj.pos))
        pos = tuple(a + b for a, b in zip(ppos, q_rotate(prot, scaled)))
        return pos, q_normalize(q_mul(prot, obj.rot)), tuple(a * b for a, b in zip(pscale, obj.scale))

    def _set_world(self, obj, pos=None, rot=None):
        if obj.parent is None:
            ppos, prot, pscale = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)
        else:
            ppos, prot, pscale = self.world(obj.parent)
        if pos is not None:
            local = q_rotate(q_inv(prot), tuple(a - b for a, b in zip(pos, ppos)))
            obj.pos = tuple(a / b for a, b in zip(local, pscale))
        if rot is not None:
            obj.rot = q_normalize(q_mul(q_inv(prot), rot))

    def component(self, obj, kind):
        return next((c for c in obj.components if c[0].lower() == str(kind).lower()), None)

    def describe(self, obj):
        pos, rot, _scale = self.world(obj)
        euler = euler_from_q(rot)
        return {"name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
                "tag": obj.tag, "layer": obj.layer, "isActive": obj.active,
                "position": dict(zip("xyz", pos)), "rotation": dict(zip("xyz", euler)),
                "scale": dict(zip("xyz", obj.scale)), "parent": obj.parent.name if obj.parent else None,
                "parentPath": self.path(obj.parent) if obj.parent else None, "childCount": len(obj.children),
                "children": [{"name": c.name, "entityId": c.eid, "instanceId": c.iid, "path": self.path(c)}
                             for c in obj.children],
                "components": [c[0] for c in obj.components]}

    def _row(self, obj):
        return {"name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
                "active": self.active_in_hierarchy(obj)}

    # ------------------------------------------------------------ dispatch

    def call(self, skill, args, agent):
        handler = getattr(self, "_skill_" + skill, None)
        if handler is None:
            raise _fail("SKILL_NOT_FOUND", "the fake editor has no skill %s" % skill)
        return handler(agent, dict(args))

    # ------------------------------------------------------------ scene and environment skills

    def _skill_project_get_render_pipeline(self, agent, args):
        shader, color, name = PIPELINES[self.pipeline]
        return {"success": True, "pipelineType": self.pipeline, "pipelineName": name, "defaultShader": shader,
                "colorProperty": color}

    def _skill_scene_get_info(self, agent, args):
        return {"sceneName": os.path.basename(self.scene_path)[:-6], "scenePath": self.scene_path, "isDirty": False}

    def _skill_scene_get_loaded(self, agent, args):
        return {"scenes": [{"name": os.path.basename(self.scene_path)[:-6], "path": self.scene_path,
                            "isLoaded": True, "isDirty": False, "isActive": True}]}

    def _skill_scene_create(self, agent, args):
        self.scene_path = args["scenePath"]
        self.objects, self.roots = {}, []
        self._write_file(self.scene_path, "%YAML 1.1\n")
        self._imported(agent, [self.scene_path])
        return {"success": True, "scenePath": self.scene_path}

    def _skill_scene_save(self, agent, args):
        self.publish("scene_saved", agent, {"name": os.path.basename(self.scene_path)[:-6], "path": self.scene_path})
        self._imported(agent, [self.scene_path])
        return {"success": True, "scenePath": self.scene_path}

    def _skill_scene_load(self, agent, args):
        path = args["scenePath"]
        if not os.path.isfile(self.disk(path)):
            raise _fail("SCENE_NOT_FOUND", "Scene not found: %s" % path)
        self.scene_path = path
        self.objects, self.roots = {}, []
        self.publish("scene_opened", agent, {"path": path})
        return {"success": True, "scenePath": path}

    def _skill_scene_set_active(self, agent, args):
        return {"success": True, "activeScene": args.get("sceneName")}

    def _skill_asset_refresh(self, agent, args):
        self.refresh(agent)
        return {"success": True, "compileTriggered": False}

    def _skill_asset_delete(self, agent, args):
        path = args["assetPath"]
        disk = self.disk(path)
        if not os.path.exists(disk) or path not in self.known:
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)
        if os.path.isdir(disk):
            shutil.rmtree(disk)
        else:
            os.remove(disk)
        if os.path.exists(disk + ".meta"):
            os.remove(disk + ".meta")
        gone = {p for p in self.known if p == path or p.startswith(path + "/")}
        self.known -= gone
        for material in [m for m in self.materials if m in gone]:
            del self.materials[material]
        self.publish("file_changes", agent, {"imported": [], "deleted": sorted(gone), "moved": [], "count": len(gone),
                                             "omitted": 0, "importedTotal": 0, "deletedTotal": len(gone),
                                             "movedTotal": 0})
        return {"success": True, "deleted": path}

    def _skill_editor_stop(self, agent, args):
        raise _fail("NOT_PLAYING", "Not in play mode")

    def _skill_editor_get_state(self, agent, args):
        return {"isPlaying": self.playing, "isPaused": False, "isCompiling": False}

    def _skill_editor_get_changes(self, agent, args):
        return {"success": True, "cursor": self.seq, "changes": []}

    # ------------------------------------------------------------ GameObject skills

    def _create(self, agent, item, parent):
        eid, iid = self._new_ids()
        obj = Obj(eid, iid, item["name"], parent)
        kind = str(item.get("primitiveType") or "").lower()
        if kind not in ("", "empty", "none"):
            if kind not in PRIMITIVES:
                raise _fail("INVALID_PRIMITIVE", "unknown primitive %s" % item.get("primitiveType"))
            mesh, _vertices, collider = PRIMITIVES[kind]
            obj.mesh = mesh
            obj.components += [["MeshFilter", None], ["MeshRenderer", True], [collider, True]]
            obj.materials = ["Default-Material"]
        self._siblings(obj).append(obj)
        self.objects[eid] = obj
        self.owners[self.transform_id(obj)] = eid
        position = (float(item.get("x", 0)), float(item.get("y", 0)), float(item.get("z", 0)))
        rotation = q_from_euler((float(item.get("rotX", 0)), float(item.get("rotY", 0)), float(item.get("rotZ", 0))))
        obj.scale = (float(item.get("scaleX", 1)), float(item.get("scaleY", 1)), float(item.get("scaleZ", 1)))
        if str(item.get("space") or "local").lower() == "world":
            self._set_world(obj, position, rotation)
        else:
            obj.pos, obj.rot = position, rotation
        self._change(agent, self._identity(obj, kind="gameobject_created"))
        return obj

    def _created(self, obj):
        info = self.describe(obj)
        return {key: info[key] for key in ("name", "entityId", "instanceId", "path", "parent", "parentPath",
                                           "position", "rotation", "scale")}

    def _skill_gameobject_create(self, agent, args):
        parent = self.find(args, "parent", required=False)
        return dict(self._created(self._create(agent, args, parent)), success=True)

    def _skill_gameobject_create_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            parent = self.find(item, "parent", required=False)
            results.append(self._created(self._create(agent, item, parent)))
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_gameobject_get_info(self, agent, args):
        return self.describe(self.find(args))

    def _skill_gameobject_set_active(self, agent, args):
        obj = self.find(args)
        before, obj.active = obj.active, bool(args.get("active", True))
        if before != obj.active:
            self._change(agent, self._identity(obj, kind="property_changed", targetType="GameObject",
                                               property="m_IsActive", previousValue="1" if before else "0",
                                               value="1" if obj.active else "0"))
        return {"success": True, "name": obj.name, "entityId": obj.eid, "active": obj.active}

    def _skill_gameobject_rename(self, agent, args):
        obj = self.find(args)
        old, obj.name = obj.name, args["newName"]
        self._change(agent, self._identity(obj, kind="property_changed", targetType="GameObject", property="m_Name",
                                           previousValue=old, value=obj.name))
        return {"success": True, "oldName": old, "newName": obj.name}

    def _skill_gameobject_delete(self, agent, args):
        obj = self.find(args)
        record = self._identity(obj, kind="gameobject_destroyed")
        self._siblings(obj).remove(obj)
        for gone in self.walk([obj]):
            self.objects.pop(gone.eid, None)
        self._change(agent, record)
        return {"success": True, "deleted": obj.name}

    def _skill_gameobject_set_parent(self, agent, args):
        child = self.find(args, "child")
        parent = self.find(args, "parent", required=False)
        pos, rot, scale = self.world(child)
        from_path = self.path(child)
        self._siblings(child).remove(child)
        child.parent = parent
        self._siblings(child).append(child)
        pscale = self.world(parent)[2] if parent is not None else (1.0, 1.0, 1.0)
        self._set_world(child, pos, rot)
        child.scale = tuple(a / b for a, b in zip(scale, pscale))
        self._change(agent, self._identity(child, kind="gameobject_reparented", fromPath=from_path,
                                           toPath=self.path(child),
                                           newParentPath=self.path(parent) if parent else None))
        return {"success": True, "child": child.name, "parent": parent.name if parent else None}

    def _skill_gameobject_set_sibling_index(self, agent, args):
        obj = self.find(args)
        siblings = self._siblings(obj)
        siblings.remove(obj)
        index = max(0, min(int(args.get("index", 0)), len(siblings)))
        siblings.insert(index, obj)
        if obj.parent is not None:
            self._change(agent, self._identity(obj.parent, kind="children_reordered"))
        return {"success": True, "name": obj.name, "index": index}

    def _skill_gameobject_set_transform(self, agent, args):
        obj = self.find(args)
        pos, rot, _scale = self.world(obj)
        if any(args.get(k) is not None for k in ("posX", "posY", "posZ")):
            pos = tuple(float(args[k]) if args.get(k) is not None else v for k, v in zip(("posX", "posY", "posZ"), pos))
            self._set_world(obj, pos=pos)
        if any(args.get(k) is not None for k in ("rotX", "rotY", "rotZ")):
            euler = euler_from_q(rot)
            euler = tuple(float(args[k]) if args.get(k) is not None else v
                          for k, v in zip(("rotX", "rotY", "rotZ"), euler))
            self._set_world(obj, rot=q_from_euler(euler))
        if any(args.get(k) is not None for k in ("scaleX", "scaleY", "scaleZ")):
            obj.scale = tuple(float(args[k]) if args.get(k) is not None else v
                              for k, v in zip(("scaleX", "scaleY", "scaleZ"), obj.scale))
        if any(args.get(k) is not None for k in ("localPosX", "localPosY", "localPosZ")):
            obj.pos = tuple(float(args[k]) if args.get(k) is not None else v
                            for k, v in zip(("localPosX", "localPosY", "localPosZ"), obj.pos))
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Transform",
                                           property="m_LocalPosition", previousValue="", value=_g9(obj.pos)))
        return {"success": True, "name": obj.name}

    # ------------------------------------------------------------ component skills

    def _skill_component_add(self, agent, args):
        obj = self.find(args)
        kind = args["componentType"]
        if kind not in BUILTIN_COMPONENTS and kind not in self.classes:
            raise _fail("COMPONENT_TYPE_NOT_FOUND", "Component type not found: %s" % kind)
        obj.components.append([kind, True if kind in ENABLEABLE or kind in self.classes else None])
        if kind in self.classes:
            obj.fields[kind] = {name: default for _type, name, default, _old in self.classes[kind]["fields"]}
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {"success": True, "gameObject": obj.name, "component": kind}

    def _skill_component_set_enabled(self, agent, args):
        obj = self.find(args)
        comp = self.component(obj, args["componentType"])
        if comp is None:
            raise _fail("COMPONENT_NOT_FOUND", "component not found")
        before, comp[1] = comp[1], bool(args.get("enabled", True))
        self._change(agent, self._identity(obj, kind="property_changed", targetType=comp[0], property="m_Enabled",
                                           previousValue="1" if before else "0", value="1" if comp[1] else "0"))
        return {"success": True, "enabled": comp[1]}

    def _skill_component_list(self, agent, args):
        obj = self.find(args)
        out = []
        for kind, enabled in obj.components:
            row = {"type": kind, "fullType": FULL_TYPES.get(kind, "UnityEngine." + kind)}
            if enabled is not None:
                row["enabled"] = enabled
            out.append(row)
        return {"success": True, "gameObject": obj.name, "count": len(out), "components": out}

    def _serialized(self, obj, kind):
        if kind.lower() == "transform":
            return {"m_LocalPosition": _g9(obj.pos), "m_LocalRotation": _g9(obj.rot), "m_LocalScale": _g9(obj.scale)}
        if kind.lower() == "meshrenderer" and self.component(obj, "MeshRenderer"):
            out = {"m_Materials.Array.size": str(len(obj.materials))}
            for index, path in enumerate(obj.materials):
                name = os.path.splitext(os.path.basename(path))[0]
                out["m_Materials.Array.data[%d]" % index] = "%s:UnityEngine.Material:%s" % (self._guid(path)[:8], name)
            return out
        if kind.lower() == "meshfilter" and obj.mesh:
            return {"m_Mesh": "1:UnityEngine.Mesh:%s" % obj.mesh}
        if kind in obj.fields and self.component(obj, kind):
            return {name: self.format_value(value) for name, value in obj.fields[kind].items()}
        raise _fail("COMPONENT_NOT_FOUND", "component not found: %s" % kind)

    def _skill_component_get_serialized_properties(self, agent, args):
        obj = self.find(args)
        values = self._serialized(obj, args["componentType"])
        return {"success": True, "properties": [{"propertyPath": k, "value": v} for k, v in values.items()]}

    def _skill_component_get_properties(self, agent, args):
        obj = self.find(args)
        if args["componentType"].lower() != "transform":
            raise _fail("COMPONENT_NOT_FOUND", "only Transform is modelled")
        pos, rot, _scale = self.world(obj)
        props = [("position", pos), ("localPosition", obj.pos), ("eulerAngles", euler_from_q(rot)),
                 ("localEulerAngles", euler_from_q(obj.rot)), ("localScale", obj.scale)]
        return {"gameObject": obj.name, "component": "Transform",
                "properties": [{"name": n, "value": "(%s)" % ", ".join("%.5f" % v for v in value)}
                               for n, value in props]}

    def _skill_component_set_property(self, agent, args):
        obj = self.find(args)
        kind, prop = args["componentType"].lower(), args["propertyName"]
        if args["componentType"] in obj.fields:
            self.set_field(obj, args["componentType"], prop, args)
        elif kind == "transform" and prop == "localEulerAngles":
            obj.rot = q_from_euler(_numbers(args["value"], 3))
        elif kind == "transform" and prop == "localPosition":
            obj.pos = _numbers(args["value"], 3)
        elif kind == "meshrenderer" and prop in ("sharedMaterial", "material") and args.get("assetPath"):
            self._material(args["assetPath"])
            obj.materials[:1] = [args["assetPath"]]
        else:
            raise _fail("PROPERTY_NOT_FOUND", "%s.%s is not modelled" % (kind, prop))
        self._change(agent, self._identity(obj, kind="property_changed", targetType=args["componentType"],
                                           property=prop, previousValue="", value=str(args.get("value"))))
        return {"success": True}

    def _skill_component_set_serialized_property(self, agent, args):
        obj = self.find(args)
        path = args["propertyPath"]
        if args["componentType"] in obj.fields:
            self.set_field(obj, args["componentType"], path, args)
            self._change(agent, self._identity(obj, kind="property_changed", targetType=args["componentType"],
                                               property=path, previousValue="", value=str(args.get("value"))))
            return {"success": True}
        if args["componentType"].lower() != "transform" or path not in ("m_LocalPosition", "m_LocalRotation",
                                                                        "m_LocalScale"):
            raise _fail("PROPERTY_NOT_FOUND", "%s is not modelled" % path)
        if path == "m_LocalRotation":
            obj.rot = q_normalize(_numbers(args["value"], 4))
        elif path == "m_LocalPosition":
            obj.pos = _numbers(args["value"], 3)
        else:
            obj.scale = _numbers(args["value"], 3)
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Transform", property=path,
                                           previousValue="", value=args["value"]))
        return {"success": True}

    def _skill_model_get_mesh_info(self, agent, args):
        obj = self.find(args)
        if not obj.mesh:
            raise _fail("NO_MESH", "No mesh found")
        vertices = next(v for m, v, _c in PRIMITIVES.values() if m == obj.mesh)
        return {"success": True, "name": obj.mesh, "vertexCount": vertices}

    # ------------------------------------------------------------ assets and materials

    def _skill_asset_get_info(self, agent, args):
        path = args["assetPath"]
        disk = self.disk(path)
        if not os.path.exists(disk) or path not in self.known:
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)
        if os.path.isdir(disk):
            kind = "DefaultAsset"
        elif path in self.materials:
            kind = "Material"
        elif path in self.sos:
            kind = self.sos[path]["type"]
        elif path in self.prefabs:
            kind = "GameObject"
        elif path.endswith(".cs"):
            kind = "MonoScript"
        elif path.endswith(".unity"):
            kind = "SceneAsset"
        else:
            kind = "TextAsset"
        return {"path": path, "name": os.path.splitext(os.path.basename(path))[0], "type": kind,
                "guid": self._guid(path), "labels": []}

    def _skill_asset_create_folder(self, agent, args):
        folder = args["folderPath"]
        if os.path.isdir(self.disk(folder)):
            raise _fail("FOLDER_EXISTS", "Folder already exists")
        parent = folder.rsplit("/", 1)[0]
        if not os.path.isdir(self.disk(parent)):
            raise _fail("PARENT_MISSING", "parent folder missing")
        self._ensure_folder(folder, agent)
        return {"success": True, "path": folder}

    def _save_path(self, save, name):
        save = save.replace("\\", "/").rstrip("/")
        if save.lower().endswith(".mat"):
            return save
        if os.path.isdir(self.disk(save)) or not os.path.splitext(save)[1]:
            return save + "/" + name + ".mat"
        return save + ".mat"

    def _material(self, path):
        material = self.materials.get(path)
        if material is None:
            raise _fail("MATERIAL_NOT_FOUND", "Material asset not found: %s" % path)
        return material

    def _new_material(self, agent, path, shader, colors=None, floats=None):
        spec = SHADERS[shader]
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self.materials[path] = {"shader": shader, "colors": dict(colors or spec["colors"]),
                                "floats": dict(floats or spec["floats"])}
        self._write_file(path, "%%YAML 1.1\nMaterial: %s\n" % os.path.basename(path))
        self._imported(agent, [path])

    def _skill_material_create(self, agent, args):
        shader = args.get("shaderName") or PIPELINES[self.pipeline][0]
        if shader not in SHADERS:
            shader = PIPELINES[self.pipeline][0]
        if not args.get("savePath"):
            raise _fail("NOT_MODELLED", "in-memory materials are not modelled")
        path = self._save_path(args["savePath"], args["name"])
        if path in self.materials:
            raise _fail("ASSET_EXISTS", "material exists: %s" % path)
        self._new_material(agent, path, shader)
        return {"success": True, "name": args["name"], "shader": shader, "path": path,
                "renderPipeline": self.pipeline, "colorProperty": PIPELINES[self.pipeline][1]}

    def _skill_material_duplicate(self, agent, args):
        source = self._material(args["sourcePath"])
        save = args.get("savePath") or args["sourcePath"].rsplit("/", 1)[0]
        path = self._save_path(save, args["newName"])
        self._new_material(agent, path, source["shader"], source["colors"], source["floats"])
        return {"success": True, "path": path}

    def _target_material(self, args):
        path = args.get("path")
        if path and (path.startswith("Assets/") or path.endswith(".mat")):
            return path, self._material(path)
        obj = self.find(args)
        if not self.component(obj, "MeshRenderer") or not obj.materials:
            raise _fail("NO_RENDERER", "No Renderer component found")
        slot = obj.materials[0]
        return slot, self.materials.get(slot) or {"shader": "Standard", "colors": dict(SHADERS["Standard"]["colors"]),
                                                  "floats": {}}

    def _skill_material_set_color(self, agent, args):
        path, material = self._target_material(args)
        requested = args.get("propertyName")
        colors = material["colors"]
        if not requested:
            used = next(c for c in (PIPELINES[self.pipeline][1], "_BaseColor", "_Color", "_EmissionColor")
                        if c in colors)
        elif requested in colors:
            used = requested
        elif requested in MAIN_COLORS and any(c in colors for c in MAIN_COLORS):
            used = next(c for c in MAIN_COLORS if c in colors)
        else:
            raise _fail("SEMANTIC_INVALID", "shader has no colour property %s" % requested)
        colors[used] = tuple(float(args.get(k, 1)) for k in ("r", "g", "b", "a"))
        self._change(agent, {"kind": "property_changed", "targetType": "Material", "property": used,
                             "previousValue": "", "value": "", "assetPath": path})
        return {"success": True, "propertyUsed": used}

    def _skill_material_assign(self, agent, args):
        path = args["materialPath"]
        self._material(path)
        obj = self.find(args)
        if not self.component(obj, "MeshRenderer"):
            raise _fail("NO_RENDERER", "No Renderer component found")
        obj.materials[:1] = [path]
        self._change(agent, self._identity(obj, kind="property_changed", targetType="MeshRenderer",
                                           property="m_Materials.Array.data[0]", previousValue="", value=path))
        return {"success": True, "material": os.path.basename(path)}

    def _skill_material_get_properties(self, agent, args):
        path, material = self._target_material(args)
        return {"success": True, "materialPath": path if path in self.materials else "", "shader": material["shader"],
                "renderQueue": 2000, "keywords": [],
                "properties": {"colors": [{"name": n, "value": dict(zip("rgba", v))}
                                          for n, v in sorted(material["colors"].items())],
                               "floats": [{"name": n, "value": v} for n, v in sorted(material["floats"].items())]}}

    # ------------------------------------------------------------ dev track: scripts, ScriptableObjects, prefabs

    def transform_id(self, obj):
        if self.entity_mode == "6000.4":
            return str(int(obj.eid) + 1)
        return str(obj.iid - 1)

    def asset_id(self, path):
        return str(int(self._guid(path)[:7], 16))

    def format_value(self, value):
        if value is None:
            return "null"
        if isinstance(value, bool):
            return "True" if value else "False"
        if isinstance(value, float):
            return "%.9g" % value
        if isinstance(value, tuple) and value and value[0] == "asset":
            path = value[1]
            kind = self.sos[path]["type"] if path in self.sos else "UnityEngine.GameObject"
            return "%s:%s:%s" % (self.asset_id(path), kind, os.path.splitext(os.path.basename(path))[0])
        if isinstance(value, tuple) and value and value[0] == "object":
            obj = self.objects.get(value[1])
            if obj is None:
                return "null"
            if value[2] == "Transform":
                return "%s:UnityEngine.Transform:%s" % (self.transform_id(obj), obj.name)
            return "%s:UnityEngine.GameObject:%s" % (obj.eid, obj.name)
        return str(value)

    def _field_type(self, cls, name):
        return next((kind for kind, field, _default, _old in self.classes.get(cls, {}).get("fields", ())
                     if field == name), None)

    def _parse_value(self, kind, text):
        if kind in SCALARS:
            return SCALARS[kind](str(text).rstrip("fF")) if kind != "bool" else SCALARS[kind](str(text))
        return text

    def set_field(self, obj, cls, name, args):
        kind = self._field_type(cls, name)
        if kind is None:
            raise _fail("PROPERTY_NOT_FOUND", "%s has no field %s" % (cls, name))
        if args.get("assetPath"):
            if args["assetPath"] not in self.sos and args["assetPath"] not in self.prefabs:
                raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % args["assetPath"])
            value = ("asset", args["assetPath"])
        elif args.get("referenceInstanceId") not in (None, 0) or args.get("referencePath"):
            target = self.find({"instanceId": args.get("referenceInstanceId"), "path": args.get("referencePath")})
            value = ("object", target.eid, "Transform" if kind == "Transform" or
                     args.get("objectType") == "Transform" else "GameObject")
        else:
            value = self._parse_value(kind, args.get("value"))
        obj.fields[cls][name] = value

    def _compile(self):
        """Parses every script: classes with their public fields (and FormerlySerializedAs names); a source with an
        unterminated field initializer or unbalanced braces is a compile error and keeps the previous classes."""
        errors = [path for path, source in self.scripts.items()
                  if source.count("{") != source.count("}") or UNTERMINATED_RE.search(source)]
        self.compile_errors = errors
        if errors:
            return
        classes = {}
        for path, source in sorted(self.scripts.items()):
            for match in CLASS_RE.finditer(source):
                fields = []
                for attrs, kind, name, default in FIELD_RE.findall(source[match.end():]):
                    old = FORMERLY_RE.search(attrs)
                    value = self._parse_value(kind, default.strip()) if default.strip() else (
                        self._parse_value(kind, "0") if kind in ("float", "int") else
                        (False if kind == "bool" else None))
                    fields.append((kind, name, value, old.group(1) if old else None))
                classes[match.group(1)] = {"base": match.group(2), "fields": fields, "path": path}
        for obj in self.objects.values():
            for cls, values in list(obj.fields.items()):
                spec = classes.get(cls)
                if spec is None:
                    continue
                obj.fields[cls] = {name: values[name] if name in values else
                                   (values[old] if old and old in values else default)
                                   for _kind, name, default, old in spec["fields"]}
        self.classes = classes

    def _skill_script_create(self, agent, args):
        name = args.get("scriptName") or args.get("name")
        path = "%s/%s.cs" % ((args.get("folder") or "Assets/Scripts").rstrip("/"), name)
        if path in self.scripts:
            raise _fail("ASSET_EXISTS", "Script already exists: %s" % path)
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self._write_file(path, args.get("content") or "")
        self.scripts[path] = args.get("content") or ""
        self.publish("file_changes", agent, {"imported": [path], "deleted": [], "moved": [], "count": 1,
                                             "omitted": 0}, source="editor")
        self._compile()
        return {"success": True, "status": "accepted", "path": path, "className": name, "jobId": "job-" + name}

    def _skill_script_replace(self, agent, args):
        path = args["scriptPath"]
        if path not in self.scripts:
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        if args.get("isRegex"):
            source = re.sub(args["find"], lambda _m: args.get("replace") or "", self.scripts[path], count=1)
        else:
            source = self.scripts[path].replace(args["find"], args.get("replace") or "")
        self._write_file(path, source)
        self.scripts[path] = source
        self.publish("file_changes", agent, {"imported": [path], "deleted": [], "moved": [], "count": 1,
                                             "omitted": 0}, source="editor")
        self._compile()
        return {"success": True, "status": "accepted", "path": path, "jobId": "job-replace"}

    def _skill_debug_check_compilation(self, agent, args):
        failed = bool(self.compile_errors)
        return {"isCompiling": False, "isUpdating": False,
                "lastCompilation": {"succeeded": not failed, "errorCount": len(self.compile_errors), "errors": []}}

    def _skill_script_get_compile_feedback(self, agent, args):
        path = args["scriptPath"]
        if not os.path.isfile(self.disk(path)):
            raise _fail("ASSET_NOT_FOUND", "Script not found: %s" % path)
        return {"scriptPath": path, "isCompiling": False, "hasErrors": path in self.compile_errors,
                "errorCount": int(path in self.compile_errors), "errors": []}

    def _write_so(self, path):
        record = self.sos[path]
        lines = ["%YAML 1.1", "--- !u!114 &11400000", "MonoBehaviour:",
                 "  m_Name: %s" % os.path.splitext(os.path.basename(path))[0]]
        for name, value in record["fields"].items():
            if isinstance(value, tuple) and value[0] == "asset":
                lines.append("  %s: {fileID: 100100000, guid: %s, type: 3}" % (name, self._guid(value[1])))
            elif value is None or isinstance(value, tuple):
                lines.append("  %s: {fileID: 0}" % name)
            else:
                lines.append("  %s: %s" % (name, self.format_value(value)))
        self._write_file(path, "\n".join(lines) + "\n")

    def _skill_scriptableobject_create(self, agent, args):
        cls, path = args["typeName"], args["savePath"]
        if cls not in self.classes:
            raise _fail("TYPE_NOT_FOUND", "ScriptableObject type not found: %s" % cls)
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self.sos[path] = {"type": cls, "fields": {name: default for _kind, name, default, _old in
                                                    self.classes[cls]["fields"]}}
        self._write_so(path)
        self._imported(agent, [path])
        return {"success": True, "path": path, "type": cls}

    def _set_so(self, agent, path, name, args):
        record = self.sos.get(path)
        if record is None:
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)
        kind = self._field_type(record["type"], name)
        if kind is None:
            raise _fail("PROPERTY_NOT_FOUND", "%s has no field %s" % (record["type"], name))
        if args.get("valueAssetPath"):
            record["fields"][name] = ("asset", args["valueAssetPath"])
        else:
            record["fields"][name] = self._parse_value(kind, args.get("value"))
        self._write_so(path)
        self._change(agent, {"kind": "property_changed", "targetType": record["type"], "property": name,
                             "previousValue": "", "value": "", "assetPath": path})
        return {"success": True}

    def _skill_scriptableobject_set(self, agent, args):
        return self._set_so(agent, args["assetPath"], args["fieldName"], args)

    def _skill_scriptableobject_set_serialized_property(self, agent, args):
        return self._set_so(agent, args["assetPath"], args["propertyPath"], args)

    def _skill_scriptableobject_get_serialized_properties(self, agent, args):
        record = self.sos.get(args["assetPath"])
        if record is None:
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % args["assetPath"])
        return {"success": True, "properties": [{"propertyPath": name, "value": self.format_value(value)}
                                                for name, value in record["fields"].items()]}

    def _skill_prefab_create(self, agent, args):
        obj = self.find(args)
        path = args["savePath"]
        self._ensure_folder(path.rsplit("/", 1)[0], agent)
        self.prefabs[path] = {"name": obj.name, "mesh": obj.mesh}
        self._write_file(path, "%%YAML 1.1\n--- !u!1 &1\nGameObject:\n  m_Name: %s\n" % obj.name)
        self._imported(agent, [path])
        return {"success": True, "prefabPath": path}

    def _skill_gameobject_set_tag_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            obj = self.find(item)
            obj.tag = item["tag"]
            self._change(agent, self._identity(obj, kind="property_changed", targetType="GameObject",
                                               property="m_TagString", previousValue="", value=obj.tag))
            results.append({"target": obj.name, "entityId": obj.eid, "success": True, "tag": obj.tag})
        return {"success": True, "results": results}

    def _skill_editor_playmode_inspect(self, agent, args):
        obj = self.find(args)
        out = {"success": True, "gameObject": obj.name, "entityId": obj.eid, "instanceId": obj.iid,
               "path": self.path(obj), "activeSelf": obj.active, "isPlaying": self.playing, "component": None}
        cls = args.get("componentType")
        if cls:
            if cls in obj.fields:
                out["component"] = {"component": cls, "fields": [
                    {"name": name, "value": self.format_value(value)} for name, value in obj.fields[cls].items()]}
            else:
                out["component"] = {"error": "Component not found: %s" % cls}
        return out

    def run_scenario(self, spec, agent=RUNNER):
        """editor_run_scenario's job result, run on this model: steps in order, the first failed step or assert ends
        it, `passed` needs every assert to pass (the model logs no errors). Play Mode is a flag only."""
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        self.playing = spec.get("executionMode", "play") == "play"
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except SkillError as exc:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": exc.payload, "passed": False})
                        passed, error = False, "Step refused or failed; inspect its actual response."
                        break
                if kind in ("assert", "wait_condition"):
                    selected = _select(actual, step.get("select"))
                    ok = _matches(selected, step.get("expected"), float(step.get("tolerance") or 0))
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                    "expected": step.get("expected"), "actual": selected, "passed": ok})
                    if not ok:
                        passed, error = False, "Assertion failed."
                        break
                else:
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"), "actual": actual,
                                    "passed": True})
                last = actual
        finally:
            self.playing = False
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

    # ------------------------------------------------------------ queries

    def _skill_batch_query_gameobjects(self, agent, args):
        query = json.loads(args["queryJson"]) if isinstance(args["queryJson"], str) else args["queryJson"]
        sensitive = bool(query.get("caseSensitive"))
        include = bool(query.get("includeInactive")) or query.get("active") is False
        flags = 0 if sensitive else re.IGNORECASE
        pattern = re.compile(query["namePattern"], flags) if query.get("namePattern") else None
        out = []
        for obj in self.walk():
            if not include and not self.active_in_hierarchy(obj):
                continue
            if query.get("active") is not None and self.active_in_hierarchy(obj) != query["active"]:
                continue
            if pattern and not pattern.search(obj.name):
                continue
            if query.get("name") and (query["name"] not in obj.name if sensitive else
                                      query["name"].lower() not in obj.name.lower()):
                continue
            if query.get("parentPath") and (obj.parent is None or
                                            self.path(obj.parent).lower() != query["parentPath"].lower()):
                continue
            if query.get("componentType") and not self.component(obj, query["componentType"]):
                continue
            out.append(self._row(obj))
        limit = int(query.get("limit") or 500)
        return {"success": True, "count": min(len(out), limit), "totalMatched": len(out),
                "truncated": len(out) > limit, "objects": out[:limit]}

    def _skill_gameobject_find(self, agent, args):
        name, regex = args.get("name"), args.get("useRegex")
        out = []
        for obj in self.walk():
            if name and not (re.search(name, obj.name) if regex else name.lower() in obj.name.lower()):
                continue
            if args.get("component") and not self.component(obj, args["component"]):
                continue
            out.append(self._row(obj))
        limit = int(args.get("limit") or 50)
        return {"count": min(len(out), limit), "totalMatched": len(out), "truncated": len(out) > limit,
                "objects": out[:limit]}


def _select(value, path):
    """The subset of Newtonsoft JSONPath the scenarios use: dotted keys, [index] and [?(@.key == 'text')]."""
    if not path:
        return value
    for part in re.findall(r"\[\?\(@\.(\w+)\s*==\s*'([^']*)'\)\]|\[(\d+)\]|([^.\[\]$]+)", path):
        key, wanted, index, name = part
        if key:
            value = next((item for item in value or [] if isinstance(item, dict) and str(item.get(key)) == wanted),
                         None)
        elif index:
            value = value[int(index)] if isinstance(value, list) and int(index) < len(value) else None
        elif name:
            value = value.get(name) if isinstance(value, dict) else None
    return value


def _matches(actual, expected, tolerance):
    if actual is None:
        return False
    numbers = (int, float)
    if isinstance(actual, numbers) and isinstance(expected, numbers) and not isinstance(actual, bool):
        return abs(float(actual) - float(expected)) <= tolerance
    return actual == expected


def _fail(code, message):
    return SkillError("%s failed: %s" % (code, message), payload={"status": "error", "errorCode": code,
                                                                 "error": message})


class FakeClient(object):
    """The client a task, solver or verifier sees: read/read_raw/write/batch on the FakeEditor as `agent`."""

    def __init__(self, editor, agent):
        self.editor = editor
        self.agent = agent

    def is_read_only(self, skill):
        return skill in READ_SKILLS

    def schema(self, names):
        return {name: {"name": name, "flags": ["readOnly"] if name in READ_SKILLS else ["mutatesScene"]}
                for name in names}

    def health(self):
        editor = self.editor
        return {"status": "ok", "version": "test", "unityVersion": editor.unity, "instanceId": "Fake_0000",
                "projectName": "Fake", "currentMode": "bypass", "tokenLevel": "maximum", "surfaceProfile": "full",
                "dryRunPolicy": "off", "summaryAutoTruncate": True, "summaryPageSize": 50,
                "panelApprovalRequired": False, "allowlistCount": 0,
                "compilation": {"isCompiling": False, "isUpdating": False}}

    def read(self, skill, **args):
        if skill not in READ_SKILLS:
            raise RestError("refused non-read-only skill %s" % skill)
        return self.editor.call(skill, args, self.agent)

    def read_raw(self, skill, **args):
        try:
            return {"status": "success", "result": self.read(skill, **args)}
        except SkillError as exc:
            return exc.payload

    def write(self, skill, **args):
        return self.editor.call(skill, args, self.agent)

    def batch(self, steps, mode=None, continue_on_error=False):
        results = []
        for step in steps:
            args = _resolve_refs(step.get("args") or {}, results)
            results.append({"skill": step["skill"], "status": "success", "result": self.write(step["skill"], **args)})
        return {"status": "completed", "results": results}

    def wait_idle(self, **kwargs):
        return self.health()

    def wait_job(self, job_id, **kwargs):
        return {"jobId": job_id, "status": "completed"}


def _resolve_refs(value, results):
    if isinstance(value, dict):
        if set(value) == {"$ref"}:
            index, _sep, field = value["$ref"][1:].partition(".")
            node = results[int(index)]["result"]
            for part in field.split("."):
                node = node[part]
            return node
        return {key: _resolve_refs(item, results) for key, item in value.items()}
    if isinstance(value, list):
        return [_resolve_refs(item, results) for item in value]
    return value


def remove_tree(path):
    shutil.rmtree(path, ignore_errors=True)

# Producer:Betsy
