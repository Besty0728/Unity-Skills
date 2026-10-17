"""OC7: sort the objects of a yard into its tagged containers, keeping world poses and a natural sibling order
(ops, challenge).

Fixture: R/<Yard> > <Zones> > six containers named <Tag>_<Suffix> (seeded rotation, non-uniform scale and position),
each holding one object that already belongs to it and two of them one that belongs to another container; R/<Yard> >
<Loose> holding the other tagged objects (tags Respawn and Finish, seeded names <Kind>_<n>_<Suffix>, some inactive, some
rotated differently from their container, some names used twice) and four untagged objects. Every folder name, suffix
letter, kind, number, pose and the order of creation come from the seed.
"""
import json
import re

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

TAGS = ("Respawn", "Finish")
SUFFIX_SETS = (("N", "C", "S"), ("E", "M", "W"), ("A", "B", "D"), ("L", "R", "T"))
KINDS = ("Barrel", "Crate", "Pallet", "Rock", "Lamp", "Sign", "Pipe", "Tank")
AREA_NAMES = (("Yard", "Loose", "Zones"), ("Depot", "Unsorted", "Sectors"), ("Lot", "Inbox", "Groups"),
              ("Site", "Pile", "Bays"))
OBJECT_SCALES = ((1, 1, 1), (0.5, 0.5, 0.5), (1, 2, 1), (2, 1, 0.5), (1.5, 1.5, 1.5), (0.75, 1.25, 1))
CONTAINER_SCALES = ((2, 1, 0.5), (1, 2, 1), (0.5, 1, 2), (1.5, 1, 1.5), (1, 0.5, 2), (2, 2, 1))
YAWS = (17, 35, 52, 70, 110, 145, -25, -48, -80)
TILTS = ((0, 0), (8, 0), (0, 5), (-12, 0), (6, -4))
PER_CONTAINER = 25          # objects that belong to one container, one of them created there
TWINS_PER_SUFFIX = 2        # names shared by the Respawn and the Finish container of one suffix
ROTATED = 8                 # loose objects whose rotation differs from their container's
HIDDEN = 6                  # loose objects created inactive
UNTAGGED = 4
BATCH = 50


def natural_key(name):
    """Digit runs as integers, the rest as text; a shorter name sorts before a longer one with the same start."""
    return [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", name)]


def _names(rng, kinds, suffix):
    """PER_CONTAINER distinct names for one container, always including a one-digit and a two-digit number of one
    kind whose text order differs from their numeric order."""
    chosen = {(rng.choice(kinds), rng.randint(5, 9))}
    kind = next(iter(chosen))[0]
    chosen.add((kind, rng.randint(10, 49)))
    while len(chosen) < PER_CONTAINER:
        chosen.add((rng.choice(kinds), rng.randint(1, 399)))
    return ["%s_%d_%s" % (kind, number, suffix) for kind, number in sorted(chosen)]


def _plan(rng):
    """Containers and objects of one seed: deterministic given the rng state."""
    yard, loose, zones = rng.choice(AREA_NAMES)
    suffixes = rng.choice(SUFFIX_SETS)
    kinds = rng.sample(KINDS, 4)
    yaws = rng.sample(YAWS, 6)
    containers = []
    for index, tag in enumerate(TAGS):
        for position, suffix in enumerate(suffixes):
            tilt = rng.choice(TILTS)
            containers.append({
                "name": "%s_%s" % (tag, suffix), "tag": tag, "suffix": suffix,
                "euler": (tilt[0], yaws[index * 3 + position], tilt[1]),
                "scale": rng.choice(CONTAINER_SCALES),
                "pos": (rng.randint(-15, 15), rng.randint(0, 4), rng.randint(-15, 15)),
                "names": _names(rng, kinds, suffix)})
    for position in range(len(suffixes)):
        first, second = containers[position], containers[3 + position]
        shared = rng.sample(first["names"][2:], TWINS_PER_SUFFIX)
        spare = [name for name in second["names"][2:] if name not in shared]
        for name in shared:
            if name not in second["names"]:
                second["names"][second["names"].index(spare.pop())] = name
    objects = []
    for index, container in enumerate(containers):
        ranked = sorted(container["names"], key=natural_key)
        settled = ranked[len(ranked) // 2]
        for name in container["names"]:
            objects.append({"name": name, "tag": container["tag"], "home": index,
                            "start": index if name == settled else None,
                            "scale": rng.choice(OBJECT_SCALES), "active": True, "euler": None})
    movers = [obj for obj in objects if obj["start"] is None]
    twins = {obj["name"] for obj in movers if sum(1 for other in movers if other["name"] == obj["name"]) > 1}
    plain = [obj for obj in movers if obj["name"] not in twins]
    rng.shuffle(plain)
    for obj, offset in zip(plain[:2], (3, -2)):
        obj["start"] = (obj["home"] + offset) % len(containers)
    for obj in plain[2:2 + ROTATED]:
        tilt, yaw = rng.choice(TILTS), rng.choice((25, 40, -35, 60))
        base = containers[obj["home"]]["euler"]
        obj["euler"] = (base[0] + tilt[0], base[1] + yaw, base[2] + tilt[1])
    for obj in plain[2 + ROTATED:2 + ROTATED + HIDDEN]:
        obj["active"] = False
    used = {obj["name"] for obj in objects}
    while len(objects) < len(containers) * PER_CONTAINER + UNTAGGED:
        name = "%s_%d_%s" % (rng.choice(kinds), rng.randint(1, 399), rng.choice(suffixes))
        if name not in used:
            used.add(name)
            objects.append({"name": name, "tag": "Untagged", "home": None, "start": None,
                            "scale": rng.choice(OBJECT_SCALES), "active": True,
                            "euler": (0, rng.choice(YAWS), 0)})
    return {"yard": yard, "loose": loose, "zones": zones, "containers": containers, "objects": objects}


class OC7(PublicTask):
    id = "OC7"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        plan = _plan(rng)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        def create_batch(items):
            out = []
            for start in range(0, len(items), BATCH):
                res = c.write("gameobject_create_batch", items=json.dumps(items[start:start + BATCH]))
                out += [V.object_id(item) for item in res.get("results") or []]
            if len(out) != len(items) or any(ident is None for ident in out):
                raise ValueError("gameobject_create_batch did not return one id per item")
            return out

        yard = make(plan["yard"], ctx.root, x=rng.randint(-8, 8), y=0, z=rng.randint(-8, 8))
        zones = make(plan["zones"], yard, x=0, y=0, z=6)
        loose = make(plan["loose"], yard, x=rng.randint(-5, 5), y=0, z=-6)
        containers = create_batch([
            dict(name=spec["name"], x=spec["pos"][0], y=spec["pos"][1], z=spec["pos"][2], rotX=spec["euler"][0],
                 rotY=spec["euler"][1], rotZ=spec["euler"][2], scaleX=spec["scale"][0], scaleY=spec["scale"][1],
                 scaleZ=spec["scale"][2], **address(zones, "parent"))
            for spec in plan["containers"]])
        tags = {}
        hidden = []
        pile = [obj for obj in plan["objects"] if obj["start"] is None]
        rng.shuffle(pile)
        items = []
        for obj in pile:
            euler = obj["euler"] or plan["containers"][obj["home"]]["euler"]
            items.append(dict(name=obj["name"], x=rng.randint(-20, 20), y=rng.randint(0, 3), z=rng.randint(-20, 20),
                              rotX=euler[0], rotY=euler[1], rotZ=euler[2], scaleX=obj["scale"][0],
                              scaleY=obj["scale"][1], scaleZ=obj["scale"][2], **address(loose, "parent")))
        ids = create_batch(items)
        for obj, ident in zip(pile, ids):
            if obj["tag"] != "Untagged":
                tags[ident] = obj["tag"]
            if not obj["active"]:
                hidden.append(ident)
        seated = [obj for obj in plan["objects"] if obj["start"] is not None]
        items = [dict(name=obj["name"], x=rng.randint(-3, 3), y=rng.randint(0, 2), z=rng.randint(-3, 3),
                      scaleX=obj["scale"][0], scaleY=obj["scale"][1], scaleZ=obj["scale"][2],
                      **address(containers[obj["start"]], "parent")) for obj in seated]
        for obj, ident in zip(seated, create_batch(items)):
            tags[ident] = obj["tag"]
            ids.append(ident)
        tagged = sorted(tags, key=str)
        for start in range(0, len(tagged), BATCH):
            c.write("gameobject_set_tag_batch", items=json.dumps(
                [dict(address(ident), tag=tags[ident]) for ident in tagged[start:start + BATCH]]))
        for ident in hidden:
            c.write("gameobject_set_active", active=False, **address(ident))
        rng.shuffle(ids)
        return {"yard": yard, "yardPath": ctx.path(plan["yard"]),
                "zones": zones, "zonesPath": ctx.path(plan["yard"], plan["zones"]),
                "loose": loose, "loosePath": ctx.path(plan["yard"], plan["loose"]),
                "containers": {spec["name"]: ident for spec, ident in zip(plan["containers"], containers)},
                "objects": ids}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        chains = {}

        def world_scale(info):
            """Product of the local scales from the object up to R."""
            scale = V.floats(info.get("scale"))
            parent = info.get("parentPath")
            if parent not in chains:
                chains[parent] = [1.0, 1.0, 1.0]
                node = p.go_path(parent) if parent and parent != ctx.R else None
                while node:
                    chains[parent] = [a * b for a, b in zip(chains[parent], V.floats(node.get("scale")))]
                    above = node.get("parentPath")
                    node = p.go_path(above) if above and above != ctx.R else None
            return [a * b for a, b in zip(scale, chains[parent])]

        def pose(ref):
            info = p.go(ref) or {}
            return {"name": info.get("name"), "tag": info.get("tag"), "active": info.get("isActive"),
                    "path": info.get("path"), "parentPath": info.get("parentPath"),
                    "position": info.get("position"), "rotation": info.get("rotation"), "scale": info.get("scale"),
                    "worldScale": world_scale(info), "local": V.local_trs(p, ref)}

        objects = {str(ident): pose(ident) for ident in h["objects"]}
        containers = {name: dict(pose(ident), id=ident) for name, ident in h["containers"].items()}
        folders = {key: pose(h[key]) for key in ("yard", "zones", "loose")}
        return {"data": {"objects": objects, "containers": containers, "folders": folders}, "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"loose": h["loosePath"], "zones": h["zonesPath"], "yard": h["yardPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["movedCount", "scaleNotPreserved"],
            "properties": {
                "movedCount": {"type": "integer", "desc": {"zh-CN": "父物体发生改变的对象数量",
                                                           "en": "number of objects whose parent changed"}},
                "scaleNotPreserved": {"type": "array", "items": {"type": "string", "desc": {
                    "zh-CN": "世界缩放无法保持的对象移动后的完整层级路径",
                    "en": "full hierarchy path, after the move, of each object whose world scale could not be kept"}}},
            },
        }


TASK = OC7()

# Producer:Betsy
