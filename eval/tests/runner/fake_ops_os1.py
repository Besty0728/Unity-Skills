"""In-memory editor for the offline tests of OS1: Light components as LightSkills.cs models them.

It extends pack_fake_editor.FakeEditor (through pack_offline.OpsFakeEditor) with light_create (a new object at the
scene root carrying a Light), light_set_properties and its batch (colour channels, intensity, range for Point/Spot,
spotAngle for Spot, shadows None/Hard/Soft), light_set_enabled and its batch, light_get_info / light_get_properties,
light_find_all, component_add Light, component_set_property Light.type, and the Light's serialized properties
(component_get_serialized_properties: m_Enabled, m_Type and m_Shadows.m_Type as enum indexes, colour, intensity...).
"""
import json

from .pack_fake_editor import _fail
from .pack_offline import OpsFakeEditor

LIGHT_TYPES = {name.lower(): name for name in ("Directional", "Point", "Spot", "Area", "Disc")}
SHADOWS = {name.lower(): name for name in ("None", "Hard", "Soft")}
TYPE_INDEX = {"Spot": 0, "Directional": 1, "Point": 2, "Area": 3, "Disc": 4}     # UnityEngine.LightType
SHADOW_INDEX = {"None": 0, "Hard": 1, "Soft": 2}                                 # UnityEngine.LightShadows


class FakeEditorOS1(OpsFakeEditor):
    extra_reads = frozenset(("light_get_info", "light_get_properties", "light_find_all"))

    # ------------------------------------------------------------ light state

    @staticmethod
    def _light(obj):
        light = getattr(obj, "light", None)
        if light is None:
            raise _fail("NO_LIGHT", "No Light component on %s" % obj.name)
        return light

    def _add_light(self, obj, light_type="Point", color=(1.0, 1.0, 1.0), intensity=1.0, range_=10.0, spot=30.0,
                   shadows="None"):
        entry = self.component(obj, "Light")
        if entry is None:
            obj.components.append(["Light", True])
        else:
            entry[1] = True
        obj.light = {"type": light_type, "color": [float(c) for c in color] + [1.0], "intensity": float(intensity),
                     "range": float(range_), "spotAngle": float(spot), "shadows": shadows}
        return obj.light

    def _light_info(self, obj):
        light = self._light(obj)
        return {"name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
                "lightType": light["type"], "color": dict(zip("rgba", light["color"])),
                "intensity": light["intensity"], "range": light["range"], "spotAngle": light["spotAngle"],
                "shadows": light["shadows"], "enabled": bool(self.component(obj, "Light")[1]),
                "cullingMask": -1, "bounceIntensity": 1.0}

    def _serialized(self, obj, kind):
        """component_get_serialized_properties of a Light, as DescribeValue spells it."""
        entry = self.component(obj, "Light")
        if str(kind).lower() != "light" or entry is None or getattr(obj, "light", None) is None:
            return OpsFakeEditor._serialized(self, obj, kind)
        light = obj.light
        return {"m_Enabled": "True" if entry[1] else "False", "m_Type": str(TYPE_INDEX[light["type"]]),
                "m_Color": ",".join("%.9g" % c for c in light["color"]), "m_Intensity": "%.9g" % light["intensity"],
                "m_Range": "%.9g" % light["range"], "m_SpotAngle": "%.9g" % light["spotAngle"], "m_Shadows": "Generic",
                "m_Shadows.m_Type": str(SHADOW_INDEX[light["shadows"]]), "m_CullingMask": "-1",
                "m_BounceIntensity": "1"}

    def _apply(self, agent, obj, args):
        light = self._light(obj)
        shadows = args.get("shadows")
        if shadows is not None and str(shadows).lower() not in SHADOWS:
            raise _fail("SEMANTIC_INVALID", "Invalid shadows value %r" % (shadows,))
        applied, skipped = [], []
        if any(args.get(channel) is not None for channel in "rgba"):
            for index, channel in enumerate("rgba"):
                if args.get(channel) is not None:
                    light["color"][index] = float(args[channel])
                    applied.append(channel)
        if args.get("intensity") is not None:
            light["intensity"] = float(args["intensity"])
            applied.append("intensity")
        if args.get("range") is not None:
            if light["type"] in ("Point", "Spot"):
                light["range"] = float(args["range"])
                applied.append("range")
            else:
                skipped.append("range (ignored: %s lights have no range)" % light["type"])
        if args.get("spotAngle") is not None:
            if light["type"] == "Spot":
                light["spotAngle"] = float(args["spotAngle"])
                applied.append("spotAngle")
            else:
                skipped.append("spotAngle (ignored: only Spot lights have a cone angle)")
        if shadows is not None:
            light["shadows"] = SHADOWS[str(shadows).lower()]
            applied.append("shadows")
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Light", property="m_Light",
                                           previousValue="", value=json.dumps(light, sort_keys=True)))
        return {"success": True, "name": obj.name, "applied": applied, "skipped": skipped,
                "lightType": light["type"], "color": dict(zip("rgba", light["color"])),
                "intensity": light["intensity"], "range": light["range"], "spotAngle": light["spotAngle"],
                "shadows": light["shadows"]}

    def _batch(self, results):
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    # ------------------------------------------------------------ light skills

    def _skill_light_create(self, agent, args):
        light_type = LIGHT_TYPES.get(str(args.get("lightType", "Point")).lower())
        shadows = SHADOWS.get(str(args.get("shadows", "Soft")).lower())
        if light_type is None or shadows is None:
            raise _fail("SEMANTIC_INVALID", "Invalid lightType or shadows")
        obj = self._create(agent, {"name": args.get("name", "New Light"), "x": args.get("x", 0),
                                   "y": args.get("y", 3), "z": args.get("z", 0)}, None)
        light = self._add_light(obj, light_type, (args.get("r", 1), args.get("g", 1), args.get("b", 1)),
                                args.get("intensity", 1), args.get("range", 10) if light_type in ("Point", "Spot")
                                else 10, args.get("spotAngle", 30) if light_type == "Spot" else 30, shadows)
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid,
                "lightType": light["type"], "position": dict(zip("xyz", obj.pos)),
                "color": dict(zip("rgb", light["color"][:3])), "intensity": light["intensity"],
                "range": light["range"], "spotAngle": light["spotAngle"], "shadows": light["shadows"]}

    def _skill_light_set_properties(self, agent, args):
        return self._apply(agent, self.find(args), args)

    def _skill_light_set_properties_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            res = self._apply(agent, self.find(item), item)
            results.append(dict(res, target=res["name"]))
        return self._batch(results)

    def _set_enabled(self, agent, obj, enabled):
        self._light(obj)
        entry = self.component(obj, "Light")
        before, entry[1] = entry[1], bool(enabled)
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Light", property="m_Enabled",
                                           previousValue="1" if before else "0", value="1" if entry[1] else "0"))
        return {"success": True, "name": obj.name, "enabled": entry[1]}

    def _skill_light_set_enabled(self, agent, args):
        return self._set_enabled(agent, self.find(args), args.get("enabled", True))

    def _skill_light_set_enabled_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            if "enabled" not in item:
                raise _fail("MISSING_PARAM", "enabled is required for each item")
            res = self._set_enabled(agent, self.find(item), item["enabled"])
            results.append(dict(res, target=res["name"]))
        return self._batch(results)

    def _skill_light_get_info(self, agent, args):
        return self._light_info(self.find(args))

    def _skill_light_get_properties(self, agent, args):
        return self._light_info(self.find(args))

    def _skill_light_find_all(self, agent, args):
        wanted = args.get("lightType")
        if wanted is not None and str(wanted).lower() not in LIGHT_TYPES:
            raise _fail("SEMANTIC_INVALID", "Invalid lightType")
        rows = []
        for obj in self.walk():
            light = getattr(obj, "light", None)
            if light is None or (wanted is not None and light["type"] != LIGHT_TYPES[str(wanted).lower()]):
                continue
            rows.append({"name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj),
                         "lightType": light["type"], "intensity": light["intensity"],
                         "enabled": bool(self.component(obj, "Light")[1])})
        limit = int(args.get("limit") or 50)
        return {"count": min(len(rows), limit), "lights": rows[:limit]}

    # ------------------------------------------------------------ component skills that touch lights

    def _skill_component_add(self, agent, args):
        out = OpsFakeEditor._skill_component_add(self, agent, args)
        if args["componentType"] == "Light":
            self._add_light(self.find(args))
        return out

    def _skill_component_set_property(self, agent, args):
        if str(args["componentType"]).lower() != "light":
            return OpsFakeEditor._skill_component_set_property(self, agent, args)
        obj = self.find(args)
        light, prop = self._light(obj), str(args["propertyName"])
        if prop.lower() == "type":
            value = LIGHT_TYPES.get(str(args.get("value")).lower())
            if value is None:
                raise _fail("SEMANTIC_INVALID", "Invalid light type %r" % (args.get("value"),))
            light["type"] = value
        elif prop in ("intensity", "range", "spotAngle"):
            light[prop] = float(args.get("value"))
        else:
            raise _fail("PROPERTY_NOT_FOUND", "Light.%s is not modelled" % prop)
        self._change(agent, self._identity(obj, kind="property_changed", targetType="Light", property=prop,
                                           previousValue="", value=str(args.get("value"))))
        return {"success": True}

# Producer:Betsy
