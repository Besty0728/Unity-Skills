"""OC1: a Muzzle node added inside Turret.prefab under its nested Barrel instance (ops, challenge).

Fixture: A/<folder>/{Barrel,Turret,Tank}.prefab, nested Barrel -> Turret -> Tank with seeded holder nodes; R/<container>
holds three bays with two Tank instances and one Turret instance, each at its asset root's local position (an instance
root placed elsewhere would carry a position override); one Tank instance carries a Barrel rotation override and an
added AudioSource.
"""
import json
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

FOLDERS = ("Prefabs/Vehicles", "Prefabs/Armory", "Prefabs/Units")
CONTAINERS = ("Garrison", "Motorpool", "Depot")
BAYS = ("Bay_North", "Bay_South", "Bay_East", "Bay_West", "Bay_Gate", "Bay_Yard")
BAY_SPOTS = ((-6.0, 0.0), (0.0, 0.0), (6.0, 0.0), (0.0, 6.0))
TANK_NAMES = ("Tank_Alpha", "Tank_Bravo", "Tank_Delta", "Tank_Echo", "Tank_Kilo")
TURRET_NAMES = ("Turret_Spare", "Turret_Bunker", "Turret_Gate")
BARREL_ANGLES = (-30, 20, 35)
MUZZLE_Y = (0.0, 0.1, 0.15)
MUZZLE_Z = (1.0, 1.25, 1.5)
MUZZLE_ROT = ((0, 0, 0), (90, 0, 0), (0, 90, 0))
MUZZLE_SCALE = (1.0, 0.5)
POSE_SALT = 0x4D55
NODE_KEYS = ("activeSelf", "layer", "tag", "siblingIndex")
PREFAB_NAMES = ("barrel", "turret", "tank")


def muzzle_pose(seed):
    """Local pose Muzzle must have, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ POSE_SALT)
    return {"position": (0.0, rng.choice(MUZZLE_Y), rng.choice(MUZZLE_Z)), "rotation": rng.choice(MUZZLE_ROT),
            "scale": rng.choice(MUZZLE_SCALE)}


def _num(value):
    return "%g" % value


def _full(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def yaml_digest(ctx, asset_path):
    docs = V.uyaml.load(_full(ctx, asset_path))
    return V.canonical_digest([[d.class_id, d.file_id, d.stripped, d.type_name, d.body] for d in docs])


def prefab_nodes(ctx, prefab_path):
    """Nodes of a prefab asset as the comparison keeps them: root-relative path, parent, flags, sibling index and
    local pose; the root carries only its flags (its name and pose are not part of the comparison)."""
    res = ctx.read("prefab_get_hierarchy", prefabPath=prefab_path, limit=500)
    out = []
    for node in res.get("nodes") or []:
        item = {"path": node.get("gameObjectPath"), "parent": node.get("parentPath")}
        item.update({key: node.get(key) for key in NODE_KEYS})
        if item["path"]:
            item.update({"name": node.get("gameObject"), "pos": V.floats(node.get("localPosition")),
                         "rot": V.floats(node.get("localEulerAngles")), "scale": V.floats(node.get("localScale"))})
        out.append(item)
    return sorted(out, key=lambda item: item["path"] or "")


def unhashed_prefab(rel):
    """asset_inventory predicate: the prefab files' content is judged by A1-A4, everything else (.meta files, folders,
    other files) by A7."""
    return not rel.endswith(".prefab")


def prefab_components(ctx, prefab_path):
    """The components of every node of a prefab asset but Muzzle, with their serialized values: sorted
    [node path, [[type, index, enabled, properties]]] rows. A Transform keeps only its type (its pose is in
    prefab_nodes, and its child list grows with Muzzle)."""
    res = ctx.read("prefab_get_hierarchy", prefabPath=prefab_path, limit=500)
    out = []
    for node in res.get("nodes") or []:
        path = node.get("gameObjectPath") or ""
        if path.split("/")[-1] == "Muzzle":
            continue
        listed = ctx.read("prefab_list_components", prefabPath=prefab_path, gameObjectPath=path, includeProperties=True)
        comps = []
        for comp in listed.get("components") or []:
            kind = str(comp.get("componentType"))
            props = None if kind.endswith("Transform") else sorted(
                [prop.get("propertyPath"), prop.get("value")] for prop in comp.get("properties") or [])
            comps.append([kind, comp.get("componentIndex"), comp.get("enabled"), props])
        out.append([path, comps])
    return sorted(out, key=lambda row: row[0])


def added_values(ctx, instance_path):
    """Serialized values of the components added on an instance: sorted [node path, type, [[propertyPath, value]]]."""
    res = ctx.read("prefab_get_overrides", path=instance_path, includeDetails=True)
    out = []
    for entry in res.get("overrides") or []:
        if entry.get("kind") != "addedComponent":
            continue
        rel = entry.get("gameObjectPath") or ""
        node = ctx.probe.go_path(instance_path + ("/" + rel if rel else ""))
        values = V.component_values(ctx.probe, node, types=(entry.get("component"),)) if node else {}
        out.append([rel, entry.get("component"), sorted([key, value] for key, value in
                                                        (values.get(entry.get("component")) or {}).items())])
    return sorted(out, key=lambda row: (row[0], str(row[1])))


def overrides(ctx, instance_path):
    """Counts and entries of prefab_get_overrides for the instance at instance_path, without the values."""
    res = ctx.read("prefab_get_overrides", path=instance_path, includeDetails=True)
    counts = {key: res.get(key) for key in ("propertyOverrides", "addedComponents", "removedComponents",
                                            "addedGameObjects")}
    entries = [{key: entry.get(key) for key in ("kind", "gameObjectPath", "component", "componentIndex", "property",
                                               "name")} for entry in res.get("overrides") or []]
    return {"counts": counts, "entries": sorted(json.dumps(entry, sort_keys=True) for entry in entries)}


def descend(probe, ref, names):
    """The object reached from ref by following uniquely named children, or None."""
    node = probe.go(ref)
    for name in names:
        kids = [kid for kid in (node or {}).get("children") or [] if kid.get("name") == name]
        if len(kids) != 1:
            return None
        node = probe.go(kids[0])
    return node


class OC1(PublicTask):
    id = "OC1"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        folder = rng.choice(FOLDERS)
        turret_holder = rng.choice(("", "Hull"))
        barrel_holder = rng.choice(("", "Mount"))
        container_name = rng.choice(CONTAINERS)
        bays = rng.sample(BAYS, 3)
        spots = rng.sample(BAY_SPOTS, 3)
        tanks = rng.sample(TANK_NAMES, 2)
        turret_name = rng.choice(TURRET_NAMES)
        angle = rng.choice(BARREL_ANGLES)
        carrier = tanks[rng.randrange(2)]
        specs = [("tank", tanks[0]), ("tank", tanks[1]), ("turret", turret_name)]
        rng.shuffle(specs)
        turret_node = (turret_holder + "/" if turret_holder else "") + "Turret"
        barrel_node = (barrel_holder + "/" if barrel_holder else "") + "Barrel"

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": ctx.asset("Prefabs")},
                                                               {"folderPath": ctx.asset(folder)}]))
        paths = {name: ctx.asset(folder, name.capitalize() + ".prefab") for name in PREFAB_NAMES}
        # Construction hierarchy under R; the prefabs are saved bottom-up so each one nests the previous.
        tank = make("Tank", ctx.root)
        hull = make("Hull", tank, y=0.6)
        make("Chassis", hull, primitiveType="Cube", scaleX=2, scaleY=0.6, scaleZ=3)
        turret_y = 0.3 if turret_holder else 0.9
        turret = make("Turret", hull if turret_holder else tank, y=turret_y)
        make("Dome", turret, primitiveType="Sphere", scaleX=0.8, scaleY=0.8, scaleZ=0.8)
        mount = make("Mount", turret, y=0.2) if barrel_holder else turret
        barrel = make("Barrel", mount, y=0.1 if barrel_holder else 0.3, z=0.6)
        make("Tube", barrel, primitiveType="Cylinder", z=0.6, rotX=90, scaleX=0.2, scaleY=0.6, scaleZ=0.2)
        make("Tip", barrel, z=1.2)
        tank_path = ctx.path("Tank")
        turret_path = tank_path + "/" + turret_node
        c.write("prefab_create", path=turret_path + "/" + barrel_node, savePath=paths["barrel"])
        c.write("prefab_create", path=turret_path, savePath=paths["turret"])
        c.write("prefab_create", path=tank_path, savePath=paths["tank"])
        c.write("gameobject_delete", **address(tank))

        container = make(container_name, ctx.root)
        bay_ids = [make(bay, container, x=spot[0], z=spot[1]) for bay, spot in zip(bays, spots)]
        # Each instance root sits where its asset root does (Tank at 0, Turret at turret_y): no root position override.
        items = [dict({"prefabPath": paths[kind], "name": name, "y": turret_y if kind == "turret" else 0},
                      **address(bay, "parent")) for (kind, name), bay in zip(specs, bay_ids)]
        res = c.write("prefab_instantiate_batch", items=json.dumps(items))
        ids = [V.object_id(row) for row in res.get("results") or []]
        tank_ids = [ref for (kind, _name), ref in zip(specs, ids) if kind == "tank"]
        turret_ids = [ref for (kind, _name), ref in zip(specs, ids) if kind == "turret"]
        bay_of = {name: bay for (_kind, name), bay in zip(specs, bays)}
        carrier_path = ctx.path(container_name, bay_of[carrier], carrier)
        c.write("component_set_property", path=carrier_path + "/" + turret_node + "/" + barrel_node,
                componentType="Transform", propertyName="localEulerAngles", value="0,%g,0" % angle)
        c.write("component_add", path=carrier_path + "/" + turret_node, componentType="AudioSource")
        return {"turret": paths["turret"], "barrel": paths["barrel"], "tank": paths["tank"],
                "turretNode": turret_node, "barrelNode": barrel_node, "container": container,
                "containerPath": ctx.path(container_name), "tanks": tank_ids, "turrets": turret_ids}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        instance_overrides, barrel_pose, added = {}, {}, {}
        for ref in h["tanks"] + h["turrets"]:
            key = str(V.object_id(ref))
            info = p.go(ref) or {}
            instance_overrides[key] = overrides(ctx, info.get("path"))
            added[key] = added_values(ctx, info.get("path"))
            rel = (h["turretNode"] + "/" if ref in h["tanks"] else "") + h["barrelNode"]
            barrel_pose[key] = V.local_trs(p, descend(p, ref, rel.split("/")))
        return {"data": {"prefabDocs": {h[name]: yaml_digest(ctx, h[name]) for name in PREFAB_NAMES},
                         "turretNodes": prefab_nodes(ctx, h["turret"]),
                         "turretComponents": prefab_components(ctx, h["turret"]),
                         "barrelGuid": p.guid(h["barrel"]),
                         "overrides": instance_overrides,
                         "addedValues": added,
                         "barrelPose": barrel_pose,
                         "container": V.structure(p, [h["container"]], poses=True),
                         "assets": V.asset_inventory(ctx.project_path, ctx.A, hashed=unhashed_prefab)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h, pose = ctx.handles, muzzle_pose(ctx.seed)
        out = {"turret": h["turret"], "barrel": h["barrel"], "tank": h["tank"], "barrelNode": h["barrelNode"],
               "container": h["containerPath"], "s": _num(pose["scale"])}
        out.update({"m" + axis: _num(value) for axis, value in zip("xyz", pose["position"])})
        out.update({"r" + axis: _num(value) for axis, value in zip("xyz", pose["rotation"])})
        return out

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["turretPrefabPath", "muzzleNodePath", "sceneMuzzles", "overrideInstancePath",
                         "overrideCount"],
            "properties": {
                "turretPrefabPath": {"type": "string", "desc": {"zh-CN": "Turret.prefab 的资产路径",
                                                                "en": "asset path of Turret.prefab"}},
                "muzzleNodePath": {"type": "string", "desc": {
                    "zh-CN": "Muzzle 在 Turret.prefab 内相对预制体根的路径",
                    "en": "path of Muzzle inside Turret.prefab, relative to the prefab root"}},
                "sceneMuzzles": {"type": "array", "items": {"type": "object", "properties": {
                    "path": {"type": "string", "desc": {"zh-CN": "场景中 Muzzle 的完整层级路径",
                                                        "en": "full hierarchy path of the Muzzle in the scene"}},
                    "instanceId": {"type": "integer", "desc": {
                        "zh-CN": "该 Muzzle 的 instanceId（Unity 6000.4+ 填 entityId）",
                        "en": "instanceId of that Muzzle (entityId on Unity 6000.4+)"}},
                }}},
                "overrideInstancePath": {"type": "string", "desc": {
                    "zh-CN": "原本带有覆盖的那个实例的完整层级路径",
                    "en": "full hierarchy path of the instance that already had overrides"}},
                "overrideCount": {"type": "integer", "desc": {
                    "zh-CN": "完成后该实例的覆盖总数（属性覆盖 + 新增组件 + 移除组件 + 新增对象）",
                    "en": "total overrides on that instance after your change (property overrides + added "
                          "components + removed components + added objects)"}},
            },
        }


TASK = OC1()

# Producer:Betsy
