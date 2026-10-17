"""T7: a repair pass over the crates of R/Yard (ops, standard).

Fixture: R/Yard with the rows Row_A to Row_F (crates, other named objects, the group Row_C/Stack) and R/Archive with one
crate. 110 objects sit under the Yard, nine of them inside inactive branches or inactive themselves.
"""
import json

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

PATTERN = r"^Crate_[A-F][0-9]{2}$"
MASS = 4.5
ROWS = "ABCDEF"
PER_ROW = {"A": 13, "B": 11, "C": 9, "D": 12, "E": 11, "F": 12}
# (collider, Rigidbody mass or None); collider is none, box, box_off (a disabled BoxCollider), sphere or mesh
REGULAR = ([("none", None)] * 6 + [("none", 1.0), ("none", 4.0), ("none", MASS), ("none", MASS)] +
           [("sphere", 5.0), ("sphere", MASS), ("mesh", 45.0), ("mesh", None), ("box_off", 4.25)] +
           [("box", 0.45), ("box", 3.5)] + [("box", MASS)] * 14 + [("box", None)] * 31)
# (collider, mass, activeSelf)
DORMANT = [("none", None, False), ("sphere", 2.0, False), ("box_off", 9.0, False), ("box", MASS, False),
           ("box", None, False), ("none", 1.0, False)]
STACK = [("none", None), ("mesh", 7.0), ("box", MASS)]
CASE_VARIANTS = ("crate_A01", "Crate_a01", "CRATE_A01")
LOOKALIKES = ("Crate_G01", "Crate_A1", "Crate_A012", "Crate_A01_old", "CrateA01", "Crate-A01", "Crate_AA1",
              "Crate_F7x", "Crate_D05 (1)")
FILLERS = ("Pallet", "Barrel", "Sign")


def _item(spec, parent_path, index):
    item = {"name": spec["name"], "parentPath": parent_path, "x": index * 1.5}
    if spec.get("collider") in ("box", "box_off"):
        item["primitiveType"] = "Cube"
    elif spec.get("collider") == "sphere":
        item["primitiveType"] = "Sphere"
    return item


class T7(PublicTask):
    id = "T7"
    track = "ops"
    tier = "standard"
    case_version = 2
    minutes = 20
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        yard = ctx.path("Yard")
        row_path = {row: "%s/Row_%s" % (yard, row) for row in ROWS}
        stack_path = row_path["C"] + "/Stack"
        specs = [{"collider": collider, "mass": mass, "active": True} for collider, mass in REGULAR]
        specs += [{"collider": collider, "mass": mass, "active": active} for collider, mass, active in DORMANT]
        rng.shuffle(specs)
        rows = {row: [] for row in ROWS}
        stack, cursor = [], 0
        for row in ROWS:
            pool = [n for n in range(1, 40) if not (row == "B" and n == 7) and not (row == "A" and n == 1)]
            fixed = [1] if row == "A" else []
            numbers = fixed + rng.sample(pool, PER_ROW[row] - len(fixed) + (3 if row == "C" else 0))
            for n in numbers[:PER_ROW[row]]:
                rows[row].append(dict(specs[cursor], name="Crate_%s%02d" % (row, n)))
                cursor += 1
            if row == "C":
                stack = [{"collider": collider, "mass": mass, "active": True, "name": "Crate_C%02d" % n}
                         for (collider, mass), n in zip(STACK, numbers[PER_ROW[row]:])]
        for row in "BE":
            rows[row].append({"collider": "none", "mass": None, "active": True, "name": "Crate_B07"})
        variant_rows = rng.sample("BCDEF", len(CASE_VARIANTS))
        other_rows = [ROWS[i // 2] for i in range(2 * len(ROWS))]
        rng.shuffle(other_rows)
        for name, row in list(zip(CASE_VARIANTS, variant_rows)) + list(zip(LOOKALIKES, other_rows)):
            rows[row].append({"collider": "none", "mass": 2.0, "active": True, "name": name})
        for row in ROWS:
            for index, base in enumerate(FILLERS):
                rows[row].append({"collider": "box" if index == 0 else "none", "mass": 1.0 if index == 1 else None,
                                  "active": True, "name": "%s_%s%d" % (base, row, rng.randint(1, 9))})
        rows["C"].append({"group": True, "name": "Stack", "active": False})
        for row in ROWS:
            rng.shuffle(rows[row])

        root = address(ctx.root, "parent")
        items = [dict({"name": "Yard"}, **root), dict({"name": "Archive", "x": 30}, **root)]
        plan = [None, None]
        for index, row in enumerate(ROWS):
            items.append({"name": "Row_" + row, "parentPath": yard, "z": index * 3})
            plan.append(None)
        for row in ROWS:
            for index, spec in enumerate(rows[row]):
                items.append(_item(spec, row_path[row], index))
                plan.append(spec)
        for index, spec in enumerate(stack):
            items.append(_item(spec, stack_path, index))
            plan.append(spec)
        archived = {"collider": "none", "mass": 2.0, "active": True, "name": "Crate_A01"}
        items.append(_item(archived, ctx.path("Archive"), 0))
        plan.append(archived)
        seen = set()
        for item in items:
            key = (item.get("parentPath", "").lower(), item["name"].lower())
            if key in seen:
                raise ValueError("the layout gives two siblings the name %r (paths compare ignoring case)"
                                 % item["name"])
            seen.add(key)

        made = c.write("gameobject_create_batch", items=json.dumps(items)).get("results") or []
        if len(made) != len(items):
            raise ValueError("gameobject_create_batch created %d of %d objects" % (len(made), len(items)))
        add, props, dormant = [], [], []
        for row, spec in zip(made, plan):
            if spec is None:
                continue
            path = row["path"]
            if spec.get("group"):
                dormant.append({"path": path, "active": False})
                continue
            if spec["collider"] == "mesh":
                add.append({"path": path, "componentType": "MeshCollider"})
            if spec["collider"] == "box_off":
                props.append({"path": path, "componentType": "BoxCollider", "propertyName": "enabled",
                              "value": False})
            if spec["mass"] is not None:
                add.append({"path": path, "componentType": "Rigidbody"})
                props.append({"path": path, "componentType": "Rigidbody", "propertyName": "mass",
                              "value": spec["mass"]})
            if not spec["active"]:
                dormant.append({"path": path, "active": False})
        c.write("component_add_batch", items=json.dumps(add))
        c.write("component_set_property_batch", items=json.dumps(props))
        c.write("gameobject_set_active_batch", items=json.dumps(dormant))
        return {"yardPath": yard, "archivePath": ctx.path("Archive")}

    def snapshot(self, ctx):
        p = ctx.probe
        nodes = V.structure(p, [ctx.root])
        mass = {}
        for node in nodes:
            if any(comp and comp[0] == "Rigidbody" for comp in node["components"]):
                values = V.floats((p.ser(node["id"], "Rigidbody") or {}).get("m_Mass"))
                mass[str(node["id"])] = values[0] if len(values) == 1 else None
        return {"data": {"nodes": nodes, "mass": mass}, "digests": {}}

    def prompt_vars(self, ctx):
        return {"yard": ctx.handles["yardPath"], "pattern": PATTERN}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["matchedCount", "addedBoxCollider", "fixedMass"],
            "properties": {
                "matchedCount": {"type": "integer", "desc": {"zh-CN": "匹配对象的总数",
                                                             "en": "total number of matching objects"}},
                "addedBoxCollider": {"type": "array", "items": {"type": "integer", "desc": {
                    "zh-CN": "新加了 BoxCollider 的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of an object that received a BoxCollider (entityId on Unity 6000.4+)"}}},
                "fixedMass": {"type": "array", "items": {"type": "integer", "desc": {
                    "zh-CN": "质量被修改的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of an object whose mass was changed (entityId on Unity 6000.4+)"}}},
            },
        }


TASK = T7()

# Producer:Betsy
