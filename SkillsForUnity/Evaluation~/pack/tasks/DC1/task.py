"""DC1: an inventory script whose stacking logic must be fixed in place (dev, challenge).

Fixture: A/Scripts/{Inventory_<w>_<run>,InventoryWatcher_<w>_<run>}.cs compiled at build time; R/<Area>[/<Shelf>]/<Box>
carries the inventory (seeded maxStack, slotCount and one or two existing stacks) and the watcher that counts its
Changed event; one or two plain props sit next to it. Every name, the depth of the box and the order of the objects
come from the seed.
"""
import hashlib
import json
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
AREAS = ("Vault", "Cellar", "Storeroom", "Hold")
SHELVES = ("Shelf", "Rack", "Alcove")
BOXES = ("Chest", "Locker", "Trunk", "Coffer")
PROPS = ("Lamp", "Barrel", "Crate", "Bench")
ITEMS = ("Ore", "Gem", "Herb", "Wood", "Iron", "Salt", "Silk", "Coal")
MAX_STACKS = (12, 15, 20)
SLOT_COUNTS = (4, 5)
FIRST_COUNTS = (3, 5, 7)
SECOND_COUNTS = (2, 4, 6)
SPEC_SALT = 0x5C01

INVENTORY_SRC = """using System;
using System.Collections.Generic;
using UnityEngine;

public class {cls} : MonoBehaviour
{{
    [Serializable]
    public class Stack
    {{
        public string item;
        public int count;
    }}

    public int maxStack = 10;
    public int slotCount = 3;
    public List<Stack> stacks = new List<Stack>();

    public event Action Changed;

    public int CountOf(string item)
    {{
        int total = 0;
        foreach (var stack in stacks)
        {{
            if (stack.item == item) total += stack.count;
        }}
        return total;
    }}

    public int Add(string item, int count)
    {{
        var stack = stacks.Find(s => s.item == item);
        if (stack != null)
        {{
            stack.count += count;
            RaiseChanged();
            return 0;
        }}
        if (stacks.Count >= slotCount) return count;
        stacks.Add(new Stack {{ item = item, count = count }});
        RaiseChanged();
        return 0;
    }}

    public int Remove(string item, int count)
    {{
        var stack = stacks.Find(s => s.item == item);
        if (stack == null || count <= 0) return 0;
        int taken = Math.Min(count, stack.count);
        stack.count -= taken;
        if (stack.count == 0) stacks.Remove(stack);
        return taken;
    }}

    void RaiseChanged()
    {{
        Changed?.Invoke();
    }}
}}
"""

WATCHER_SRC = """using UnityEngine;

public class {watcher} : MonoBehaviour
{{
    public {cls} inventory;
    public int notifications;

    void OnEnable()
    {{
        if (inventory != null) inventory.Changed += Count;
    }}

    void OnDisable()
    {{
        if (inventory != null) inventory.Changed -= Count;
    }}

    void Count()
    {{
        notifications += 1;
    }}
}}
"""


def spec(seed):
    """Scene values of the inventory and the item names, a pure function of the seed (shared with the private module):
    maxStack, slotCount, the initial stacks [[item, count]], the item of the first-drawn stack and two item names the
    scene does not hold."""
    rng = random.Random(seed ^ SPEC_SALT)
    items = rng.sample(ITEMS, 4)
    initial = [[items[0], rng.choice(FIRST_COUNTS)]]
    if rng.random() < 0.5:
        second = [items[3], rng.choice(SECOND_COUNTS)]
        initial = [second] + initial if rng.random() < 0.5 else initial + [second]
    return {"maxStack": rng.choice(MAX_STACKS), "slotCount": rng.choice(SLOT_COUNTS), "initial": initial,
            "main": items[0], "fresh": items[1:3]}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def subtree_states(probe, root):
    """[[ref, go_state]] of root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append([V.object_id(ref), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


def script_hash(ctx, script_path):
    content = ctx.read("script_read", scriptPath=script_path).get("content")
    return hashlib.sha256(content.encode("utf-8")).hexdigest() if isinstance(content, str) else None


def stack_fields(probe, ref, cls):
    """The inventory's scene values: maxStack, slotCount and the stacks as the serialized reader lists them."""
    ser = probe.ser(ref, cls) or {}
    return {key: value for key, value in ser.items()
            if key in ("maxStack", "slotCount") or key.startswith("stacks.")}


class DC1(PublicTask):
    id = "DC1"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        values = spec(ctx.seed)
        word, area, shelf = rng.choice(WORDS), rng.choice(AREAS), rng.choice(SHELVES)
        box = "%s_%s" % (rng.choice(BOXES), rng.choice(WORDS))
        props = rng.sample(PROPS, rng.choice((1, 2)))
        nested, box_first = rng.random() < 0.5, rng.random() < 0.5
        cls = "Inventory_%s_%s" % (word, ctx.scope.run_id)
        watcher = "InventoryWatcher_%s_%s" % (word, ctx.scope.run_id)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        c.write("asset_create_folder", folderPath=ctx.asset("Scripts"))
        res = c.write("script_create_batch", items=json.dumps([
            {"scriptName": cls, "folder": ctx.asset("Scripts"), "content": INVENTORY_SRC.format(cls=cls)},
            {"scriptName": watcher, "folder": ctx.asset("Scripts"),
             "content": WATCHER_SRC.format(cls=cls, watcher=watcher)}]))
        for row in res.get("results") or []:
            if isinstance(row, dict) and row.get("jobId"):
                ctx.wait_job(row["jobId"])
                break
        ctx.wait_idle()
        scripts = {"inventory": ctx.asset("Scripts", cls + ".cs"), "watcher": ctx.asset("Scripts", watcher + ".cs")}
        if not all(ctx.probe.guid(path) for path in scripts.values()):
            raise RuntimeError("DC1 fixture: the scripts were not imported")

        root = make(area, ctx.root, x=rng.randint(-6, 6), y=0, z=rng.randint(-6, 6))
        holder = make(shelf, root, x=0, y=1, z=0) if nested else root
        parts = [area, shelf] if nested else [area]

        def place_props():
            for index, prop in enumerate(props):
                make(prop, root, primitiveType="Cube", x=2 + index, y=0.5, z=0)

        if not box_first:
            place_props()
        chest = make(box, holder, primitiveType="Cube", y=0.5)
        if box_first:
            place_props()
        chest_path = ctx.path(*(parts + [box]))
        c.write("component_add", componentType=cls, **address(chest))
        c.write("component_set_property", componentType=cls, propertyName="maxStack", value=str(values["maxStack"]),
                **address(chest))
        c.write("component_set_property", componentType=cls, propertyName="slotCount",
                value=str(values["slotCount"]), **address(chest))
        c.write("component_set_serialized_property", componentType=cls, propertyPath="stacks.Array.size",
                value=str(len(values["initial"])), **address(chest))
        for index, (item, count) in enumerate(values["initial"]):
            for field, value in (("item", item), ("count", str(count))):
                c.write("component_set_serialized_property", componentType=cls,
                        propertyPath="stacks.Array.data[%d].%s" % (index, field), value=value, **address(chest))
        c.write("component_add", componentType=watcher, **address(chest))
        c.write("component_set_serialized_property", componentType=watcher, propertyPath="inventory",
                referencePath=chest_path, objectType=cls, **address(chest))
        fields = stack_fields(ctx.probe, chest, cls)
        if V.as_int(fields.get("maxStack")) != values["maxStack"] or \
                V.as_int(fields.get("stacks.Array.size")) != len(values["initial"]):
            raise RuntimeError("DC1 fixture: the inventory did not take its scene values: %s" % fields)
        return {"area": root, "chest": chest, "chestPath": chest_path, "script": scripts["inventory"],
                "watcherScript": scripts["watcher"], "scripts": ctx.asset("Scripts")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        cls, watcher = class_name(h["script"]), class_name(h["watcherScript"])
        return {"data": {"states": subtree_states(p, h["area"]),
                         "inventory": stack_fields(p, h["chest"], cls),
                         "watcher": {key: value for key, value in (p.ser(h["chest"], watcher) or {}).items()
                                     if key in ("inventory", "notifications")},
                         "guids": {h["script"]: p.guid(h["script"]), h["watcherScript"]: p.guid(h["watcherScript"])},
                         "watcherHash": script_hash(ctx, h["watcherScript"]),
                         "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"script": h["script"], "cls": class_name(h["script"]), "watcher": h["watcherScript"],
                "watcherCls": class_name(h["watcherScript"]), "chest": h["chestPath"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "chestInstanceId"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "修复后的脚本资产路径",
                                                          "en": "asset path of the fixed script"}},
                "chestInstanceId": {"type": "integer", "desc": {
                    "zh-CN": "挂载了库存组件的对象的 instanceId（Unity 6000.4+ 填 entityId）",
                    "en": "instanceId of the object carrying the inventory component (entityId on Unity 6000.4+)"}},
            },
        }


TASK = DC1()

# Producer:Betsy
