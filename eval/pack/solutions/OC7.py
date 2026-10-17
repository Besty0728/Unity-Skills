"""OC7 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import json
import math
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

BATCH = 50


# ---------------------------------------------------------------- reading the yard


class Study(object):
    """What an agent reads before moving anything: every object with its tag, name, parent and world pose, the six
    containers, and from those the move plan, the natural order per container and the objects that cannot keep
    their world scale. Objects are keyed by their canonical id text; `ident` keeps the id for addressing."""

    def __init__(self, ctx):
        self.ctx = ctx
        p, h = ctx.probe, ctx.handles
        self.containers = {name: p.go(ident) or {} for name, ident in h["containers"].items()}
        self.chain = {}
        self.objects, self.ident = {}, {}
        for ident in h["objects"]:
            key = str(V.object_id(ident))
            self.objects[key] = p.go(ident) or {}
            self.ident[key] = ident
        self.plan = []            # (key, container name) for every object that has to move
        self.home = []            # keys of the tagged objects already under their container
        self.untagged = []
        for key in sorted(self.objects):
            info = self.objects[key]
            dest = self.dest_of(key)
            if dest in self.containers:
                if info.get("parentPath") == self.containers[dest].get("path"):
                    self.home.append(key)
                else:
                    self.plan.append((key, dest))
            else:
                self.untagged.append(key)
        self.keepers = {key for key, dest in self.plan
                        if ctx.private.aligned(self.objects[key].get("rotation"),
                                               self.containers[dest].get("rotation"))}

    def dest_of(self, key):
        info = self.objects[key]
        return "%s_%s" % (info.get("tag"), self.ctx.private.suffix(info.get("name")))

    def address(self, key, prefix=""):
        return V.address(self.ident[key], prefix)

    def lossy(self, path):
        """Product of the local scales from the object at `path` up to R."""
        if path not in self.chain:
            scale = [1.0, 1.0, 1.0]
            node = self.ctx.probe.go_path(path) if path and path != self.ctx.R else None
            while node:
                scale = [a * b for a, b in zip(scale, V.floats(node.get("scale")))]
                above = node.get("parentPath")
                node = self.ctx.probe.go_path(above) if above and above != self.ctx.R else None
            self.chain[path] = scale
        return self.chain[path]

    def members(self, name):
        """Keys of every object that belongs to container `name`, in natural name order."""
        keys = [key for key, dest in self.plan if dest == name] + [key for key in self.home if self.dest_of(key) == name]
        return sorted(keys, key=lambda key: self.ctx.private.natural_key(self.objects[key].get("name")))

    def final_path(self, key, dest):
        return "%s/%s" % (self.containers[dest].get("path"), self.objects[key].get("name"))

    def answer(self, moved=None, declared=None):
        plan = self.plan if moved is None else moved
        if declared is None:
            declared = [self.final_path(key, dest) for key, dest in plan if key not in self.keepers]
        return {"movedCount": len(plan), "scaleNotPreserved": declared, "declaredComplete": True}


def _move(ctx, study, plan):
    """gameobject_set_parent_batch in pages of BATCH items, by id."""
    for start in range(0, len(plan), BATCH):
        items = [dict(study.address(key, "child"), **V.address(study.containers[dest], "parent"))
                 for key, dest in plan[start:start + BATCH]]
        ctx.c.write("gameobject_set_parent_batch", items=json.dumps(items))


def _order(ctx, study, key_of=None, single=False):
    """Set the sibling index of every object that moved so each container reads in `key_of` order (natural by
    default); the object that was already there is never addressed, it ends in the one slot left."""
    steps = []
    for name in sorted(study.containers):
        members = study.members(name)
        if key_of is not None:
            members = sorted(members, key=lambda key: key_of(study.objects[key].get("name")))
        for index, key in enumerate(members):
            if key in study.home:
                continue
            steps.append({"skill": "gameobject_set_sibling_index", "args": dict(study.address(key), index=index)})
    if single:
        for step in steps:
            ctx.c.write(step["skill"], **step["args"])
        return
    for start in range(0, len(steps), BATCH):
        ctx.c.batch(steps[start:start + BATCH])


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- quaternions (x, y, z, w), Unity conventions


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


def _euler_quat(value):
    return V.quat_from_euler(*V.floats(value))


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    study = Study(ctx)
    _move(ctx, study, study.plan)
    _order(ctx, study)
    return study.answer()


def single_calls(ctx):
    study = Study(ctx)
    for key, dest in study.plan:
        ctx.c.write("gameobject_set_parent", **dict(study.address(key, "child"),
                                                    **V.address(study.containers[dest], "parent")))
    _order(ctx, study, single=True)
    return study.answer()


def explicit_local(ctx):
    """Reparent, then write the local pose computed from the world pose for the objects that can keep it."""
    study = Study(ctx)
    before = {key: (V.floats(info.get("position")), _euler_quat(info.get("rotation")),
                    [a * b for a, b in zip(V.floats(info.get("scale")), study.lossy(info.get("parentPath")))])
              for key, info in study.objects.items()}
    _move(ctx, study, study.plan)
    for key, dest in study.plan:
        if key not in study.keepers:
            continue
        container = study.containers[dest]
        pos, rot, scale = before[key]
        cpos, crot = V.floats(container.get("position")), _euler_quat(container.get("rotation"))
        cscale = study.lossy(container.get("path"))
        local_pos = [a / b for a, b in zip(q_rotate(q_inv(crot), [a - b for a, b in zip(pos, cpos)]), cscale)]
        local_rot = q_mul(q_inv(crot), rot)
        norm = math.sqrt(sum(c * c for c in local_rot)) or 1.0
        local_rot = [c / norm for c in local_rot]
        local_scale = [a / b for a, b in zip(scale, cscale)]
        for prop, value in (("m_LocalPosition", "%.6f,%.6f,%.6f" % tuple(local_pos)),
                            ("m_LocalRotation", "%.9f,%.9f,%.9f,%.9f" % tuple(local_rot)),
                            ("m_LocalScale", "%.6f,%.6f,%.6f" % tuple(local_scale))):
            ctx.c.write("component_set_serialized_property", componentType="Transform", propertyPath=prop,
                        value=value, **study.address(key))
    _order(ctx, study)
    return study.answer()


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def lexicographic(ctx):
    study = Study(ctx)
    _move(ctx, study, study.plan)
    _order(ctx, study, key_of=lambda name: name or "")
    return study.answer()


def restore_scale(ctx):
    """Writes the pre-move scale numbers back after reparenting (scaleX/Y/Z are local)."""
    study = Study(ctx)
    old = {key: V.floats(info.get("scale")) for key, info in study.objects.items()}
    _move(ctx, study, study.plan)
    for key, _dest in study.plan:
        ctx.c.write("gameobject_set_transform", scaleX=old[key][0], scaleY=old[key][1], scaleZ=old[key][2],
                    **study.address(key))
    _order(ctx, study)
    return study.answer()


def restore_local_position(ctx):
    """Writes the pre-move world position into the local position after reparenting."""
    study = Study(ctx)
    old = {key: V.floats(info.get("position")) for key, info in study.objects.items()}
    _move(ctx, study, study.plan)
    for key, _dest in study.plan:
        ctx.c.write("gameobject_set_transform", localPosX=old[key][0], localPosY=old[key][1], localPosZ=old[key][2],
                    **study.address(key))
    _order(ctx, study)
    return study.answer()


def touch_home(ctx):
    """The reference, then one of the objects that was already in its container is nudged."""
    study = Study(ctx)
    _move(ctx, study, study.plan)
    _order(ctx, study)
    ctx.c.write("gameobject_set_transform", localPosX=1.25, localPosY=0.75, localPosZ=-0.5,
                **study.address(sorted(study.home)[0]))
    return study.answer()


def reset_rotation(ctx):
    """The reference, then every moved object is given a zero world rotation."""
    study = Study(ctx)
    _move(ctx, study, study.plan)
    for key, _dest in study.plan:
        ctx.c.write("gameobject_set_transform", rotX=0, rotY=0, rotZ=0, **study.address(key))
    _order(ctx, study)
    return study.answer()


def move_untagged(ctx):
    """Also files the untagged objects into the Respawn container of their suffix."""
    answer = reference(ctx)
    study = Study(ctx)
    for key in study.untagged:
        dest = "Respawn_%s" % ctx.private.suffix(study.objects[key].get("name"))
        if dest in study.containers:
            ctx.c.write("gameobject_set_parent", **dict(study.address(key, "child"),
                                                        **V.address(study.containers[dest], "parent")))
    return answer


def keyed_by_name(ctx):
    """Keeps one entry per object name, so one of each pair of same-named objects is never moved."""
    study = Study(ctx)
    by_name = {}
    for key, dest in study.plan:
        by_name[study.objects[key].get("name")] = (key, dest)
    study.plan = sorted(by_name.values())
    _move(ctx, study, study.plan)
    _order(ctx, study)
    return study.answer()


def first_page(ctx):
    """Discovers the objects with gameobject_find per tag at its default limit (50 active objects) and declares no
    scale exception."""
    study = Study(ctx)
    found = set()
    for tag in sorted({name.partition("_")[0] for name in study.containers}):
        res = ctx.c.read("gameobject_find", tag=tag)
        found |= {str(V.object_id(obj)) for obj in res.get("objects") or []}
    study.plan = [(key, dest) for key, dest in study.plan if key in found]
    _move(ctx, study, study.plan)
    _order(ctx, study)
    return study.answer(declared=[])


def recreate_one(ctx):
    """Deletes one object that can keep its scale and creates a same-named one in its container instead."""
    study = Study(ctx)
    key, dest = next((key, dest) for key, dest in study.plan if key in study.keepers)
    info = study.objects[key]
    study.plan = [item for item in study.plan if item[0] != key]
    _move(ctx, study, study.plan)
    ctx.c.write("gameobject_delete", **study.address(key))
    pos, rot = V.floats(info.get("position")), V.floats(info.get("rotation"))
    ctx.c.write("gameobject_create", name=info.get("name"), space="world", x=pos[0], y=pos[1], z=pos[2],
                rotX=rot[0], rotY=rot[1], rotZ=rot[2], **V.address(study.containers[dest], "parent"))
    _order(ctx, study)
    answer = study.answer()
    answer["movedCount"] += 1
    return answer


def declare_none(ctx):
    study = Study(ctx)
    _move(ctx, study, study.plan)
    _order(ctx, study)
    return study.answer(declared=[])


def wrong_count(ctx):
    answer = reference(ctx)
    answer["movedCount"] = answer["movedCount"] + 1
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCrate")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Notes", "yard.txt"), b"sorted\n")
    return answer


NEGATIVES = [
    Negative("lexicographic", lexicographic, {"H5"}, "Crate_12 sorted before Crate_7"),
    Negative("restore_scale", restore_scale, {"H4"}, "the old scale numbers written as local scale"),
    Negative("restore_local_position", restore_local_position, {"H2"}, "the old world position written as local"),
    Negative("touch_home", touch_home, {"H6"}, "an object that was already in its container is moved"),
    Negative("reset_rotation", reset_rotation, {"H3"}, "the moved objects rotated to zero afterwards"),
    Negative("move_untagged", move_untagged, {"H5", "H7"}, "untagged objects filed into containers"),
    Negative("keyed_by_name", keyed_by_name, {"H1", "H5", "H10"}, "one of each pair of same-named objects is lost"),
    Negative("first_page", first_page, {"H1", "H5", "H8", "H10"}, "gameobject_find at its default limit, no "
                                                                   "inactive objects, nothing declared"),
    Negative("recreate_one", recreate_one, {"H1", "H5", "H9"}, "an object deleted and created anew"),
    Negative("declare_none", declare_none, {"H8"}),
    Negative("wrong_count", wrong_count, {"H10"}),
    Negative("outside_root", outside_root, {"H11"}),
    Negative("direct_file", direct_file, {"H12"}, "a file written to disk next to the skills"),
]

ALTERNATIVES = [("single_calls", single_calls), ("explicit_local", explicit_local), ("save_run_scene", save_run_scene)]

# Producer:Betsy
