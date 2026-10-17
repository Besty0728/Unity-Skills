"""In-memory editor for the offline tests of T4: renderer material arrays, shader keywords, GI flags and scene_materials.

It extends pack_fake_editor.FakeEditor (through pack_offline.OpsFakeEditor) with what T4 and its solutions touch, taken
from MaterialSkills.cs, ComponentSkills.cs (component_set_serialized_property), SerializedPropertySkillUtility.cs
(object reference values read "<id>:<type>:<name>") and PerceptionSkills.cs (scene_materials lists the materials the
active renderers use, with their asset paths and the names of the objects using them).
"""
import hashlib
import os
import re

from .pack_fake_editor import MAIN_COLORS, FakeEditor, _fail
from .pack_offline import OpsFakeEditor

GI_FLAGS = {name.lower(): name for name in ("None", "RealtimeEmissive", "BakedEmissive", "EmissiveIsBlack",
                                           "AnyEmissive")}
BUILTIN_PATH = "Resources/unity_builtin_extra"
ARRAY_SIZE = "m_Materials.Array.size"
ARRAY_ITEM = re.compile(r"^m_Materials\.Array\.data\[(\d+)\]$")
MAX_USERS = 5


class FakeEditorT4(OpsFakeEditor):
    extra_reads = frozenset(("scene_materials",))

    # ------------------------------------------------------------ materials

    def _new_material(self, agent, path, shader, colors=None, floats=None):
        super()._new_material(agent, path, shader, colors, floats)
        self.materials[path].update(keywords=set(), gi="EmissiveIsBlack")

    @staticmethod
    def _extras(material):
        material.setdefault("keywords", set())
        material.setdefault("gi", "EmissiveIsBlack")
        return material

    def _record(self, agent, path, prop):
        self._change(agent, {"kind": "property_changed", "targetType": "Material", "property": prop,
                             "previousValue": "", "value": "", "assetPath": path})

    def _name(self, slot):
        """Material.name: what material_create was given, else the file name (built-in materials: the slot text)."""
        material = self.materials.get(slot) or {}
        return material.get("name") or os.path.splitext(os.path.basename(slot))[0]

    def _skill_material_create(self, agent, args):
        out = super()._skill_material_create(agent, args)
        self.materials[out["path"]]["name"] = args["name"]
        return out

    def _skill_material_duplicate(self, agent, args):
        source = self._extras(self._material(args["sourcePath"]))
        out = super()._skill_material_duplicate(agent, args)
        copy = self._extras(self.materials[out["path"]])
        copy["keywords"], copy["gi"], copy["name"] = set(source["keywords"]), source["gi"], args["newName"]
        return out

    def _skill_asset_get_info(self, agent, args):
        out = super()._skill_asset_get_info(agent, args)
        if out.get("type") == "Material":
            out["name"] = self._name(args["assetPath"])
        return out

    def _skill_material_get_properties(self, agent, args):
        out = super()._skill_material_get_properties(agent, args)
        material = self._extras(self._target_material(args)[1])
        out["keywords"] = sorted(material["keywords"])
        out["giFlags"] = material["gi"]
        return out

    def _color_property(self, material, requested):
        """MaterialSkills.TryResolveColorProperty: exact, then case-insensitive, then the main-colour family."""
        colors = material["colors"]
        if not requested:
            for name in (self._pipeline_color(), "_BaseColor", "_Color", "_TintColor", "_EmissionColor"):
                if name in colors:
                    return name
            raise _fail("EXECUTION_FAILED", "Material does not have a color property")
        if requested in colors:
            return requested
        insensitive = [name for name in colors if name.lower() == requested.lower()]
        if len(insensitive) == 1:
            return insensitive[0]
        if requested.lower() in {name.lower() for name in MAIN_COLORS}:
            fallback = next((name for name in MAIN_COLORS if name in colors), None)
            if fallback:
                return fallback
        raise _fail("SEMANTIC_INVALID", "shader has no colour property named %s" % requested)

    def _pipeline_color(self):
        return "_BaseColor" if self.pipeline == "URP" else "_Color"

    def _skill_material_set_color(self, agent, args):
        path, material = self._target_material(args)
        self._extras(material)
        used = self._color_property(material, args.get("propertyName"))
        intensity = float(args.get("intensity", 1.0))
        r, g, b, a = (float(args.get(key, 1)) for key in "rgba")
        material["colors"][used] = (r * intensity, g * intensity, b * intensity, a)
        if used == "_EmissionColor" and intensity > 0:
            material["keywords"].add("_EMISSION")
            material["gi"] = "RealtimeEmissive"
        self._record(agent, path, used)
        return {"success": True, "propertyUsed": used, "intensity": intensity,
                "materialPath": path if path in self.materials else ""}

    def _skill_material_set_emission(self, agent, args):
        path, material = self._target_material(args)
        self._extras(material)
        prop = next((name for name in ("_EmissionColor", "_Emission") if name in material["colors"]), None)
        if prop is None:
            raise _fail("EXECUTION_FAILED", "Material does not support emission")
        intensity = float(args.get("intensity", 1.0))
        r, g, b = (float(args.get(key, 1)) for key in "rgb")
        material["colors"][prop] = (r * intensity, g * intensity, b * intensity, 1.0)
        if bool(args.get("enableEmission", True)) and intensity > 0:
            material["keywords"].add("_EMISSION")
            material["gi"] = "RealtimeEmissive"
        else:
            material["keywords"].discard("_EMISSION")
            material["gi"] = "EmissiveIsBlack"
        self._record(agent, path, prop)
        return {"success": True, "emissionEnabled": "_EMISSION" in material["keywords"], "giFlags": material["gi"],
                "materialPath": path if path in self.materials else ""}

    def _skill_material_set_gi_flags(self, agent, args):
        path, material = self._target_material(args)
        flags = GI_FLAGS.get(str(args.get("flags", "RealtimeEmissive")).lower())
        if flags is None:
            raise _fail("EXECUTION_FAILED", "Invalid GI flags: %s" % args.get("flags"))
        self._extras(material)["gi"] = flags
        self._record(agent, path, "m_LightmapFlags")
        return {"success": True, "giFlags": flags, "materialPath": path if path in self.materials else ""}

    # ------------------------------------------------------------ renderer material arrays

    def _material_id(self, slot):
        return int(hashlib.md5(slot.encode("utf-8")).hexdigest()[:8], 16)

    def _serialized(self, obj, kind):
        if kind.lower() == "meshrenderer" and self.component(obj, "MeshRenderer"):
            out = {ARRAY_SIZE: str(len(obj.materials))}
            for index, slot in enumerate(obj.materials):
                key = "m_Materials.Array.data[%d]" % index
                if slot is None:
                    out[key] = "null"
                else:
                    out[key] = "%d:UnityEngine.Material:%s" % (self._material_id(slot), self._name(slot))
            return out
        return super()._serialized(obj, kind)

    def _skill_component_set_serialized_property(self, agent, args):
        if str(args.get("componentType")).lower() != "meshrenderer":
            return super()._skill_component_set_serialized_property(agent, args)
        obj = self.find(args)
        if not self.component(obj, "MeshRenderer"):
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: MeshRenderer")
        path = args["propertyPath"]
        item = ARRAY_ITEM.match(path)
        if path == ARRAY_SIZE:
            size = int(args["value"])
            del obj.materials[size:]
            obj.materials.extend([None] * (size - len(obj.materials)))
            shown = str(size)
        elif item and int(item.group(1)) < len(obj.materials):
            if args.get("assetPath"):
                self._material(args["assetPath"])
                obj.materials[int(item.group(1))] = args["assetPath"]
                shown = args["assetPath"]
            elif args.get("value") in (None, "null"):
                obj.materials[int(item.group(1))] = None
                shown = "null"
            else:
                raise _fail("NOT_MODELLED", "only assetPath references are modelled")
        else:
            raise _fail("PROPERTY_NOT_FOUND", "Serialized property not found: %s" % path)
        self._change(agent, self._identity(obj, kind="property_changed", targetType="MeshRenderer", property=path,
                                           previousValue="", value=shown))
        return {"success": True, "gameObject": obj.name, "component": "MeshRenderer", "propertyPath": path,
                "valueSet": shown}

    # ------------------------------------------------------------ scene overview

    def _skill_scene_materials(self, agent, args):
        found = {}
        for obj in self.walk():
            if not self.active_in_hierarchy(obj) or not self.component(obj, "MeshRenderer"):
                continue
            for slot in obj.materials:
                if slot is None:
                    continue
                known = self.materials.get(slot)
                entry = found.setdefault(slot, {
                    "name": self._name(slot) if known else slot,
                    "shader": known["shader"] if known else "Standard",
                    "path": slot if known else BUILTIN_PATH, "users": []})
                entry["users"].append(obj.name)
        groups = {}
        for entry in found.values():
            groups.setdefault(entry["shader"], []).append(entry)
        shaders = [{"shader": shader, "materialCount": len(items),
                    "materials": [{"name": m["name"], "path": m["path"], "renderQueue": 2000,
                                   "userCount": len(m["users"]), "users": m["users"][:MAX_USERS],
                                   "properties": None} for m in items]}
                   for shader, items in sorted(groups.items(), key=lambda pair: -len(pair[1]))]
        return {"success": True, "totalMaterials": len(found), "totalShaders": len(shaders), "shaders": shaders}

# Producer:Betsy
