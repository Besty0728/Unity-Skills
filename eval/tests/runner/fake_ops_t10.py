"""In-memory editor for the offline tests of T10: assets with GUIDs that survive moves, folders, physic materials
and renderer material slots that reference the material object (a copy that replaces the original leaves a dangling
slot).

It follows AssetSkills.cs (asset_move, asset_move_batch, asset_duplicate, asset_delete, asset_find, folder creation),
BatchSkills.cs (batch_query_assets), MaterialSkills.cs (the batch skills), PhysicsSkills.cs (physics_create_material)
and ComponentSkills.cs (component_set_serialized_property_batch addresses objects by name, path or instanceId only).
"""
import copy
import hashlib
import json
import os
import re

from .pack_fake_editor import _fail
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

BUILTIN_MATERIAL = "Default-Material"


class FakeEditorT10(OpsFakeEditor):
    extra_reads = frozenset(("asset_find", "batch_query_assets"))

    def __init__(self, *args, **kwargs):
        self.guids = {}             # asset path -> guid; moves carry the guid along, a new file gets a new one
        self.object_ids = {}        # material asset path -> id shown in slot references
        self.physic = {}            # physic material asset path -> values
        self._guid_count = 0
        OpsFakeEditor.__init__(self, *args, **kwargs)

    # ------------------------------------------------------------ identity

    def _guid(self, asset_path):
        guid = self.guids.get(asset_path)
        if guid is None:
            self._guid_count += 1
            guid = hashlib.md5(("t10-guid-%d" % self._guid_count).encode("ascii")).hexdigest()
            self.guids[asset_path] = guid
        return guid

    @staticmethod
    def _norm(path):
        return str(path).replace("\\", "/")

    def _unique(self, path):
        """AssetDatabase.GenerateUniqueAssetPath: 'Name 1.ext' when the path is taken."""
        if not os.path.exists(self.disk(path)):
            return path
        stem, ext = os.path.splitext(path)
        number = 1
        while os.path.exists(self.disk("%s %d%s" % (stem, number, ext))):
            number += 1
        return "%s %d%s" % (stem, number, ext)

    @staticmethod
    def _items(args):
        return json.loads(args["items"]) if isinstance(args["items"], str) else args["items"]

    def _each(self, agent, args, skill, defaults=None):
        """BatchExecutor: every item runs the single skill; any failed item makes the call fail (success:false)."""
        handler = getattr(self, "_skill_" + skill)
        results, failed = [], []
        for item in self._items(args):
            item = dict(item)
            for key, value in (defaults or {}).items():
                if item.get(key) is None and value is not None:
                    item[key] = value
            try:
                results.append(dict(handler(agent, item), target=item.get("name") or item.get("path")))
            except SkillError as exc:
                failed.append("%s: %s" % (item.get("name") or item.get("path"), exc))
        if failed:
            raise _fail("BATCH_FAILED", "; ".join(failed)[:300])
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    # ------------------------------------------------------------ assets

    def _remember(self, agent, imported=(), deleted=(), moved=()):
        self.publish("file_changes", agent, {
            "imported": list(imported), "deleted": list(deleted), "moved": [{"from": a, "to": b} for a, b in moved],
            "count": len(imported) + len(deleted) + len(moved), "omitted": 0, "importedTotal": len(imported),
            "deletedTotal": len(deleted), "movedTotal": len(moved)})

    def _move(self, source, destination):
        """AssetDatabase.MoveAsset: the asset keeps its guid and object; renderer slots follow it."""
        source, destination = self._norm(source), self._norm(destination)
        if source not in self.known or not os.path.exists(self.disk(source)):
            raise _fail("ASSET_MOVE_FAILED", "Asset not found: %s" % source)
        parent = destination.rsplit("/", 1)[0]
        if parent != "Assets" and (parent not in self.known or not os.path.isdir(self.disk(parent))):
            raise _fail("ASSET_MOVE_FAILED", "Parent directory is missing: %s" % parent)
        if os.path.exists(self.disk(destination)):
            raise _fail("ASSET_MOVE_FAILED", "Destination path already exists: %s" % destination)
        os.rename(self.disk(source), self.disk(destination))
        if os.path.exists(self.disk(source) + ".meta"):
            os.rename(self.disk(source) + ".meta", self.disk(destination) + ".meta")
        remap = {path: destination + path[len(source):] for path in self.known
                 if path == source or path.startswith(source + "/")}
        for old, new in remap.items():
            self.known.discard(old)
            self.known.add(new)
            for table in (self.guids, self.object_ids, self.materials, self.physic):
                if old in table:
                    table[new] = table.pop(old)
        for obj in self.objects.values():
            obj.materials = [remap.get(slot, slot) for slot in obj.materials]
        return source, destination

    def _skill_asset_move(self, agent, args):
        pair = self._move(args["sourcePath"], args["destinationPath"])
        self._remember(agent, moved=[pair])
        return {"success": True, "from": pair[0], "to": pair[1]}

    def _skill_asset_move_batch(self, agent, args):
        pairs, results, failed = [], [], []
        for item in self._items(args):
            try:
                pair = self._move(item.get("sourcePath"), item.get("destinationPath"))
            except SkillError as exc:
                failed.append("%s: %s" % (item.get("sourcePath"), exc))
                continue
            pairs.append(pair)
            results.append({"target": pair[0], "success": True, "from": pair[0], "to": pair[1]})
        if pairs:
            self._remember(agent, moved=pairs)         # StartAssetEditing/StopAssetEditing: one post-process pass
        if failed:
            raise _fail("BATCH_FAILED", "; ".join(failed)[:300])
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_asset_create_folder_batch(self, agent, args):
        return self._each(agent, args, "asset_create_folder")

    def _skill_asset_duplicate(self, agent, args):
        path = self._norm(args["assetPath"])
        if path not in self.known or not os.path.isfile(self.disk(path)):
            raise _fail("TARGET_NOT_FOUND", "Failed to duplicate asset (source may not exist): %s" % path)
        new = self._unique(path)
        with open(self.disk(path), "r", encoding="utf-8") as handle:
            text = handle.read()
        self._write_file(new, text)
        if path in self.materials:
            self.materials[new] = copy.deepcopy(self.materials[path])
        if path in self.physic:
            self.physic[new] = dict(self.physic[path])
        self._remember(agent, imported=[new])
        return {"success": True, "original": path, "copy": new}

    def _skill_asset_delete(self, agent, args):
        path = self._norm(args["assetPath"])
        gone = {item for item in self.known if item == path or item.startswith(path + "/")}
        result = OpsFakeEditor._skill_asset_delete(self, agent, args)
        for item in gone:
            for table in (self.guids, self.object_ids, self.physic):
                table.pop(item, None)
        for obj in self.objects.values():
            obj.materials = [None if slot in gone else slot for slot in obj.materials]
        return result

    def _skill_asset_get_info(self, agent, args):
        info = OpsFakeEditor._skill_asset_get_info(self, agent, args)
        if info["path"] in self.physic:
            info["type"] = "PhysicMaterial"
        return info

    def _skill_physics_create_material(self, agent, args):
        name = args.get("name") or "New PhysicMaterial"
        folder = self._norm(args.get("savePath") or "Assets").rstrip("/")
        self._ensure_folder(folder, agent)
        path = self._unique("%s/%s.physicMaterial" % (folder, name))
        self._write_file(path, "%YAML 1.1\nPhysicMaterial:\n  m_Name: " + name + "\n")
        self.physic[path] = {"dynamicFriction": float(args.get("dynamicFriction", 0.6)),
                             "staticFriction": float(args.get("staticFriction", 0.6)),
                             "bounciness": float(args.get("bounciness", 0.0))}
        self._remember(agent, imported=[path])
        return {"success": True, "path": path}

    # ------------------------------------------------------------ queries

    def _rows(self, folder="Assets", kinds=(), words=(), pattern=None, sensitive=False):
        rows = []
        root = self._norm(folder or "Assets").rstrip("/")
        for path in sorted(self.known):
            if not (path == root or path.startswith(root + "/")) or not os.path.exists(self.disk(path)):
                continue
            if os.path.isdir(self.disk(path)):
                kind = "DefaultAsset"
            elif path in self.materials:
                kind = "Material"
            elif path in self.physic:
                kind = "PhysicMaterial"
            elif path.endswith(".unity"):
                kind = "SceneAsset"
            else:
                kind = "TextAsset"
            name = os.path.splitext(os.path.basename(path))[0]
            if kinds and kind.lower() not in kinds:
                continue
            if any(word not in name.lower() for word in words):
                continue
            if pattern and not re.search(pattern, name, 0 if sensitive else re.IGNORECASE):
                continue
            rows.append({"path": path, "name": name, "type": kind, "guid": self._guid(path)})
        return rows

    @staticmethod
    def _terms(text):
        terms = str(text or "").split()
        kinds = tuple(term[2:].lower() for term in terms if term.startswith("t:"))
        words = tuple(term.lower() for term in terms if not term.startswith(("t:", "l:")))
        return kinds, words

    def _skill_asset_find(self, agent, args):
        kinds, words = self._terms(args.get("searchFilter"))
        rows = self._rows("Assets", kinds, words)
        limit = int(args.get("limit") or 50)
        return {"count": min(len(rows), limit), "totalFound": len(rows),
                "assets": [{"path": r["path"], "name": r["name"], "type": r["type"]} for r in rows[:limit]]}

    def _skill_batch_query_assets(self, agent, args):
        parts = [args.get("searchFilter")]
        if args.get("typeFilter"):
            parts.append("t:" + str(args["typeFilter"]).replace("t:", "", 1))
        kinds, words = self._terms(" ".join(part for part in parts if part))
        rows = self._rows(args.get("folder") or "Assets", kinds, words, args.get("namePattern"),
                          bool(args.get("caseSensitive")))
        limit = int(args.get("maxResults") or 200)
        return {"success": True, "count": min(len(rows), limit), "totalMatched": len(rows),
                "truncated": len(rows) > limit, "assets": rows[:limit],
                "summary": "Found %d assets." % min(len(rows), limit)}

    # ------------------------------------------------------------ materials and renderer slots

    def _skill_material_create_batch(self, agent, args):
        return self._each(agent, args, "material_create")

    def _skill_material_set_colors_batch(self, agent, args):
        return self._each(agent, args, "material_set_color", {"propertyName": args.get("propertyName")})

    def _skill_material_assign_batch(self, agent, args):
        return self._each(agent, args, "material_assign")

    def _skill_component_set_serialized_property_batch(self, agent, args):
        return self._each(agent, args, "component_set_serialized_property")

    def _skill_component_set_serialized_property(self, agent, args):
        if str(args.get("componentType")).lower() != "meshrenderer":
            return OpsFakeEditor._skill_component_set_serialized_property(self, agent, args)
        obj = self.find(args)
        if not self.component(obj, "MeshRenderer"):
            raise _fail("COMPONENT_NOT_FOUND", "MeshRenderer not found on %s" % obj.name)
        path = args["propertyPath"]
        if path == "m_Materials.Array.size":
            size = int(args["value"])
            obj.materials = (list(obj.materials) + [None] * size)[:size]
        else:
            match = re.match(r"^m_Materials\.Array\.data\[(\d+)\]$", path)
            if not match or int(match.group(1)) >= len(obj.materials) or not args.get("assetPath"):
                raise _fail("PROPERTY_NOT_FOUND", "%s is not modelled" % path)
            self._material(args["assetPath"])
            obj.materials[int(match.group(1))] = args["assetPath"]
        self._change(agent, self._identity(obj, kind="property_changed", targetType="MeshRenderer", property=path,
                                           previousValue="", value=str(args.get("value") or args.get("assetPath"))))
        return {"success": True, "propertyPath": path}

    def _slot_ref(self, path):
        """'<object id>:<type>:<name>' of the material object in a slot; 'null' when the slot is empty or dangling."""
        if path is None:
            return "null"
        if path == BUILTIN_MATERIAL:
            return "10303:UnityEngine.Material:" + BUILTIN_MATERIAL
        if path not in self.materials:
            return "null"
        ident = self.object_ids.get(path)
        if ident is None:
            ident = self.object_ids[path] = self._new_ids()[0]
        return "%s:UnityEngine.Material:%s" % (ident, os.path.splitext(os.path.basename(path))[0])

    def _serialized(self, obj, kind):
        if kind.lower() == "meshrenderer" and self.component(obj, "MeshRenderer"):
            out = {"m_Materials.Array.size": str(len(obj.materials))}
            for index, path in enumerate(obj.materials):
                out["m_Materials.Array.data[%d]" % index] = self._slot_ref(path)
            return out
        return OpsFakeEditor._serialized(self, obj, kind)

# Producer:Betsy
