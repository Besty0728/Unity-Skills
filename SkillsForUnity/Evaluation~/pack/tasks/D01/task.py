"""D01: inventory of R/Inventory by a name prefix (ops, basic, read-only).

Fixture: R/Inventory > Section_A..C > Rack_1..2 > Tray > Box, with the Slot_ objects and the objects in OTHER_NAMES
spread over the racks, trays and boxes (seeded names, places and active states); R/Vault/Slot_Q.
"""
import json

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

SLOT_COUNT = 63
DEACTIVATED = 7
OTHER_NAMES = ("slot_lower", "OldSlot_9", "Slot-12", "Slots_3")
BATCH = 50


class D01(PublicTask):
    id = "D01"
    track = "ops"
    tier = "basic"
    case_version = 2
    minutes = 7
    requires = {"pipeline": ["URP", "BuiltIn"], "mode": "bypass", "dedicated": False, "packages": []}

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng

        def make(name, parent):
            return V.object_id(c.write("gameobject_create", name=name, **address(parent, "parent")))

        inventory = make("Inventory", ctx.root)
        places = []
        for section in "ABC":
            holder = make("Section_" + section, inventory)
            for rack_number in (1, 2):
                rack = make("Rack_%d" % rack_number, holder)
                tray = make("Tray", rack)
                places += [rack, tray, make("Box", tray)]
        numbers = rng.sample(range(1, 400), SLOT_COUNT - 2 - 12)
        lettered = ["%s%d" % (rng.choice("ABCDEFGH"), n) for n in rng.sample(range(1, 90), 12)]
        suffixes = [str(n) for n in numbers] + lettered
        repeats = rng.sample(suffixes, 2)
        names = ["Slot_" + suffix for suffix in suffixes + repeats]
        homes = [rng.choice(places) for _ in names]
        for offset, name in enumerate(names[-2:]):
            twin = names.index(name)
            while homes[len(names) - 2 + offset] == homes[twin]:
                homes[len(names) - 2 + offset] = rng.choice(places)
        items = [dict({"name": name, "x": i % 9, "y": 0, "z": i // 9}, **address(home, "parent"))
                 for i, (name, home) in enumerate(zip(names, homes))]
        items += [dict({"name": name}, **address(rng.choice(places), "parent")) for name in OTHER_NAMES]
        order = list(range(len(items)))
        rng.shuffle(order)
        created = {}
        for start in range(0, len(order), BATCH):
            chunk = order[start:start + BATCH]
            res = c.write("gameobject_create_batch", items=json.dumps([items[k] for k in chunk]))
            for k, row in zip(chunk, res.get("results") or []):
                created[k] = V.object_id(row)
        slots = [created[k] for k in range(len(names))]
        for ref in rng.sample(slots, DEACTIVATED):
            c.write("gameobject_set_active", active=False, **address(ref))
        vault = make("Vault", ctx.root)
        make("Slot_Q", vault)
        return {"inventory": inventory, "inventoryPath": ctx.path("Inventory"), "vault": vault}

    def snapshot(self, ctx):
        return {"data": {},
                "digests": {"inventory": V.canonical_digest(V.structure(ctx.probe, [ctx.handles["inventory"]])),
                            "vault": V.canonical_digest(V.structure(ctx.probe, [ctx.handles["vault"]]))}}

    def prompt_vars(self, ctx):
        return {"root": ctx.handles["inventoryPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["total", "items"],
            "properties": {
                "total": {"type": "integer", "desc": {"zh-CN": "符合条件的对象总数", "en": "number of matching objects"}},
                "items": {"type": "array", "items": {"type": "object", "properties": {
                    "path": {"type": "string", "desc": {"zh-CN": "完整层级路径", "en": "full hierarchy path"}},
                    "instanceId": {"type": "integer", "desc": {"zh-CN": "instanceId（Unity 6000.4+ 填 entityId）",
                                                               "en": "instanceId (entityId on Unity 6000.4+)"}},
                }}},
            },
        }


TASK = D01()

# Producer:Betsy
