"""OC3: Loot.table references on enemy prefabs across nested tier folders (ops, challenge).

Fixture: A/Scripts/{LootTable,Loot}.cs compiled at build time; LootTable assets under A/<lootDir> and one more under
A/<archive>; A/<enemies>/<zone>/<tier>[/<sub>]/<name>.prefab enemy prefabs (Capsules with a Loot component), three
variants of them, one prefab with two Loot components, two prefabs without Loot, and a few tables already referenced.
"""
import hashlib
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

ZONES = ("Forest", "Caves", "Ruins", "Swamp", "Peaks", "Marsh")
TIERS = ("Common", "Rare", "Elite")
SUBFOLDERS = ("Bosses", "Packs", "Scouts", "Guards")
CREATURES = ("Goblin", "Wolf", "Bandit", "Spider", "Ghoul", "Wisp", "Troll", "Harpy", "Lizard", "Rat", "Bat", "Slime",
             "Knight", "Archer", "Shaman", "Ogre", "Wraith", "Golem", "Imp", "Serpent", "Boar", "Crab", "Mantis",
             "Drake", "Hound", "Raider", "Cultist", "Mimic", "Specter", "Zealot", "Brute", "Stalker", "Warden",
             "Lurker", "Howler", "Viper")
NON_LOOT = ("Chest", "Totem")
VARIANT_SUFFIXES = ("Elder", "Alpha", "Dire", "Grim", "Pale")
LOOT_DIRS = ("LootTables", "Drops", "Tables")
ARCHIVE_DIRS = ("Archive", "Old", "Backup")
ENEMY_DIRS = ("Enemies", "Monsters", "Foes")
CHANCES = ("0.25", "0.5", "0.75")
TABLE_TIERS = {"Common": 1, "Rare": 2, "Elite": 3, "Bonus": 9}
BASE_COUNT = 33
WIRED_COUNT = 7
CHANCE_COUNT = 5
NAMESPACE = "UnitySkillsEval.Tasks.OC3"
TYPE_NAMES = ("Loot", "LootTable")
COMPONENT = NAMESPACE + ".Loot"
MASKED = {"MonoBehaviour": ("table",), "Transform": ("m_RootOrder", "m_LocalEulerAnglesHint")}

TABLE_SRC = """using UnityEngine;

namespace UnitySkillsEval.Tasks.OC3
{
    public class LootTable : ScriptableObject
    {
        public int tier;
        public string label;
    }
}
"""

LOOT_SRC = """using UnityEngine;

namespace UnitySkillsEval.Tasks.OC3
{
    public class Loot : MonoBehaviour
    {
        public LootTable table;
        public float dropChance = 1f;
    }
}
"""


def tier_of_folder(rel):
    for segment in reversed(rel.split("/")):
        if segment in TIERS:
            return segment
    return None


def _full(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def yaml_digest(docs):
    return V.canonical_digest([[d.class_id, d.file_id, d.stripped, d.type_name, d.body] for d in docs])


def masked_digest(docs):
    """Digest with the Loot.table references and Unity's resave-volatile Transform keys left out."""
    rows = []
    for d in docs:
        body = dict(d.body) if isinstance(d.body, dict) else d.body
        for key in MASKED.get(d.type_name, ()):
            if isinstance(body, dict):
                body.pop(key, None)
        rows.append([d.class_id, d.file_id, d.stripped, d.type_name, body])
    return V.canonical_digest(rows)


def loot_count(docs, loot_guid):
    return sum(1 for d in V.uyaml.by_type(docs, "MonoBehaviour") if d.get("m_Script", "guid") == loot_guid)


def script_hash(ctx, script_path):
    content = ctx.read("script_read", scriptPath=script_path).get("content")
    return hashlib.sha256(content.encode("utf-8")).hexdigest() if isinstance(content, str) else None


def _residue(ctx):
    """Scripts or loaded ScriptableObject types that already carry the fixture's type names (a crashed earlier run
    leaves them behind); the build must stop on them rather than compile a duplicate."""
    found = []
    listed = ctx.read("script_list", folder="Assets", filter="Loot", limit=100)
    found += [item.get("path") for item in listed.get("scripts") or [] if item.get("name") in TYPE_NAMES]
    types = ctx.read("scriptableobject_list_types", filter="LootTable", limit=50)
    found += [item.get("fullName") for item in types.get("types") or [] if item.get("name") == "LootTable"]
    return [item for item in found if item]


def unhashed_prefab(rel):
    """asset_inventory predicate: a prefab's content is judged by B1-B4's YAML digests, everything else (folders,
    .meta files with their labels and GUIDs, tables, scripts) byte for byte."""
    return not rel.endswith(".prefab")


def _script_guids(docs):
    return [d.get("m_Script", "guid") for d in V.uyaml.by_type(docs, "MonoBehaviour")]


def _folders(enemies, leaves):
    """Every folder path under enemies needed for the leaves, parents first, without repeats."""
    out = []
    for leaf in leaves:
        parts = leaf.split("/")
        for depth in range(1, len(parts) + 1):
            path = enemies + "/" + "/".join(parts[:depth])
            if path not in out:
                out.append(path)
    return out


class OC3(PublicTask):
    id = "OC3"
    track = "ops"
    tier = "challenge"
    case_version = 1
    minutes = 30
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        loot_dir = ctx.asset(rng.choice(LOOT_DIRS))
        archive = ctx.asset(rng.choice(ARCHIVE_DIRS))
        enemies = ctx.asset(rng.choice(ENEMY_DIRS))
        zones = rng.sample(ZONES, 3)
        leaves = ["%s/%s" % (zone, tier) for zone in zones for tier in TIERS]
        for leaf in rng.sample(leaves, 2):
            leaves.append(leaf + "/" + rng.choice(SUBFOLDERS))
        holder = rng.choice(leaves[:9])
        nested = holder + "/" + rng.choice([tier for tier in TIERS if tier != tier_of_folder(holder)])
        leaves.append(nested)
        names = rng.sample(CREATURES, BASE_COUNT)
        homes = list(leaves) + [nested] + [rng.choice(leaves) for _ in range(BASE_COUNT - len(leaves) - 1)]
        rng.shuffle(homes)
        bases = [(name, home) for name, home in zip(names, homes)]
        plain = [(name, rng.choice(leaves)) for name in NON_LOOT]
        picks = rng.sample(range(BASE_COUNT), 3)
        variants = []
        for k, (index, suffix) in enumerate(zip(picks, rng.sample(VARIANT_SUFFIXES, 3))):
            name, home = bases[index]
            if k == 0:
                variant_home = home
            elif k == 1:
                variant_home = rng.choice([leaf for leaf in leaves if tier_of_folder(leaf) != tier_of_folder(home)])
            else:
                variant_home = rng.choice(leaves)
            variants.append((name + "_" + suffix, variant_home, name))
        twin = rng.choice([index for index in range(BASE_COUNT) if index not in picks])
        wired = rng.sample([index for index in range(BASE_COUNT) if index not in picks], WIRED_COUNT)
        chances = [(index, rng.choice(CHANCES)) for index in rng.sample(range(BASE_COUNT), CHANCE_COUNT)]

        clash = _residue(ctx)
        if clash:
            raise RuntimeError("OC3 fixture: Loot/LootTable already exist in this project (residue of an earlier "
                               "run?): %s" % ", ".join(clash))
        c.write("asset_create_folder", folderPath=ctx.asset("Scripts"))
        res = c.write("script_create_batch", items=json.dumps([
            {"scriptName": "LootTable", "folder": ctx.asset("Scripts"), "content": TABLE_SRC},
            {"scriptName": "Loot", "folder": ctx.asset("Scripts"), "content": LOOT_SRC}]))
        for row in res.get("results") or []:
            if isinstance(row, dict) and row.get("jobId"):
                ctx.wait_job(row["jobId"])
                break
        ctx.wait_idle()
        scripts = {name: ctx.asset("Scripts", name + ".cs") for name in TYPE_NAMES}
        guids = {name: ctx.probe.guid(path) for name, path in scripts.items()}
        if not all(guids.values()):
            raise RuntimeError("OC3 fixture: the scripts were not imported: %s" % guids)

        folders = [loot_dir, archive, enemies] + _folders(enemies, leaves)
        c.write("asset_create_folder_batch", items=json.dumps([{"folderPath": path} for path in folders]))
        tables = {tier: loot_dir + "/LootTable_%s.asset" % tier for tier in TABLE_TIERS}
        spare = archive + "/LootTable_Common.asset"
        steps = []
        for tier, path in list(tables.items()) + [("Archived", spare)]:
            steps.append({"skill": "scriptableobject_create", "args": {"typeName": "LootTable", "savePath": path}})
            steps.append({"skill": "scriptableobject_set_batch", "args": {
                "assetPath": path, "fields": json.dumps({"tier": str(TABLE_TIERS.get(tier, 0)), "label": tier})}})
        c.batch(steps)
        for path in list(tables.values()) + [spare]:
            made_from = _script_guids(V.uyaml.load(_full(ctx, path)))
            if made_from != [guids["LootTable"]]:
                raise RuntimeError("OC3 fixture: %s was created from another LootTable type (%s)" % (path, made_from))

        prefab_of = {name: "%s/%s/%s.prefab" % (enemies, home, name) for name, home in bases + plain}
        items = [dict({"name": name, "primitiveType": "Capsule" if name not in NON_LOOT else "Cube",
                       "x": index % 8 * 2.0, "z": index // 8 * 2.0}, **address(ctx.root, "parent"))
                 for index, (name, _home) in enumerate(bases + plain)]
        c.write("gameobject_create_batch", items=json.dumps(items))
        c.batch([{"skill": "prefab_create", "args": {"path": ctx.path(name), "savePath": prefab_of[name]}}
                 for name, _home in bases + plain])
        c.write("gameobject_delete_batch", items=json.dumps([{"path": ctx.path(name)} for name, _home in bases + plain]))
        loot_items = [{"prefabPath": prefab_of[name], "componentType": COMPONENT} for name, _home in bases]
        loot_items.append({"prefabPath": prefab_of[bases[twin][0]], "componentType": COMPONENT})
        c.write("prefab_add_component_batch", items=json.dumps(loot_items))
        for index, (name, _home) in enumerate(bases):
            wanted = 2 if index == twin else 1
            got = loot_count(V.uyaml.load(_full(ctx, prefab_of[name])), guids["Loot"])
            if got != wanted:
                raise RuntimeError("OC3 fixture: %s carries %d Loot of the fixture's type, wanted %d" % (
                    prefab_of[name], got, wanted))
        variant_paths = []
        for name, home, base in variants:
            path = "%s/%s/%s.prefab" % (enemies, home, name)
            c.write("prefab_create_variant", sourcePrefabPath=prefab_of[base], variantPath=path)
            variant_paths.append(path)
        wiring = []
        for k, index in enumerate(wired):
            name, home = bases[index]
            tier = tier_of_folder(home)
            if k < 4:
                table = tables[tier]
            elif k < WIRED_COUNT - 1:
                table = tables[rng.choice([other for other in TIERS if other != tier])]
            else:
                table = spare
            wiring.append({"prefabPath": prefab_of[name], "componentType": COMPONENT, "propertyPath": "table",
                           "assetPath": table})
        for index, chance in chances:
            wiring.append({"prefabPath": prefab_of[bases[index][0]], "componentType": COMPONENT,
                           "propertyPath": "dropChance", "value": chance})
        c.write("prefab_set_serialized_property_batch", items=json.dumps(wiring))
        return {"enemies": enemies, "lootDir": loot_dir, "tables": tables,
                "prefabs": sorted(list(prefab_of.values()) + variant_paths),
                "scripts": ctx.asset("Scripts"), "lootScript": scripts["Loot"], "tableScript": scripts["LootTable"]}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        loot_guid = p.guid(h["lootScript"])
        listing = V.asset_listing(ctx.project_path, ctx.A)
        full, masked, counts = {}, {}, {}
        for rel in listing:
            path = ctx.A + "/" + rel
            if not rel.endswith((".prefab", ".asset")):
                continue
            docs = V.uyaml.load(_full(ctx, path))
            full[path] = yaml_digest(docs)
            if rel.endswith(".prefab"):
                masked[path] = masked_digest(docs)
                counts[path] = loot_count(docs, loot_guid)
        return {"data": {"full": full, "masked": masked, "lootCounts": counts,
                         "tableGuids": {tier: p.guid(path) for tier, path in h["tables"].items()},
                         "lootGuid": loot_guid,
                         "assets": V.asset_inventory(ctx.project_path, ctx.A, hashed=unhashed_prefab),
                         "scriptHashes": {h[key]: script_hash(ctx, h[key]) for key in ("lootScript", "tableScript")}},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"enemies": h["enemies"], "lootDir": h["lootDir"], "scripts": h["scripts"], "lootType": "Loot",
                "tableType": "LootTable", "common": h["tables"]["Common"], "rare": h["tables"]["Rare"],
                "elite": h["tables"]["Elite"], "bonus": h["tables"]["Bonus"]}

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["wired", "variants", "commonTableGuid"],
            "properties": {
                "wired": {"type": "array", "items": {"type": "object", "properties": {
                    "prefabPath": {"type": "string", "desc": {"zh-CN": "预制体资产路径", "en": "prefab asset path"}},
                    "componentIndex": {"type": "integer", "desc": {"zh-CN": "该 Loot 组件在预制体上的 componentIndex",
                                                                   "en": "componentIndex of that Loot component"}},
                    "tablePath": {"type": "string", "desc": {"zh-CN": "它最终引用的 LootTable 资产路径",
                                                             "en": "asset path of the LootTable it references"}},
                }}},
                "variants": {"type": "array", "items": {"type": "string", "desc": {
                    "zh-CN": "未改动的变体预制体路径", "en": "path of a variant prefab left untouched"}}},
                "commonTableGuid": {"type": "string", "desc": {"zh-CN": "你引用的 LootTable_Common 资产的 GUID",
                                                               "en": "GUID of the LootTable_Common asset you referenced"}},
            },
        }


TASK = OC3()

# Producer:Betsy
