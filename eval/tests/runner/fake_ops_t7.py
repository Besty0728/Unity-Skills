"""In-memory editor for the T7 offline tests: pack_fake_editor plus what a repair of many crates touches.

Added to the base model: Rigidbody with its mass (read as m_Mass), the batch skills component_add_batch,
component_set_property_batch, component_set_serialized_property_batch and gameobject_set_active_batch, component_remove,
and the query rows of batch_query_gameobjects. The batch skills take items addressed by name, path or instanceId only
(entityId is not an item key in the plugin), run all-or-nothing, and an item that names an object by name reaches the
first object of that name. A component without DisallowMultipleComponent (BoxCollider) can be added twice; a
Rigidbody cannot.
"""
import json

from .pack_fake_editor import BUILTIN_COMPONENTS, ENABLEABLE, _fail
from .pack_offline import OpsFakeEditor

ITEM_KEYS = ("name", "path", "instanceId")
SINGLE = ("Rigidbody",)


class FakeEditorT7(OpsFakeEditor):
    # ------------------------------------------------------------ queries

    def _row(self, obj):
        row = super()._row(obj)
        row.update({"scene": "Eval", "tag": obj.tag, "layer": obj.layer, "activeSelf": obj.active,
                    "activeInHierarchy": self.active_in_hierarchy(obj), "componentCount": len(obj.components)})
        return row

    # ------------------------------------------------------------ components

    def _add_component(self, agent, obj, kind):
        if kind not in BUILTIN_COMPONENTS:
            raise _fail("COMPONENT_TYPE_NOT_FOUND", "Component type not found: %s" % kind)
        if kind in SINGLE and self.component(obj, kind) is not None:
            return {"warning": "Component %s already exists on %s" % (kind, obj.name), "alreadyPresent": True}
        obj.components.append([kind, True if kind in ENABLEABLE else None])
        if kind == "Rigidbody":
            obj.fields["Rigidbody"] = {"m_Mass": 1.0}
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {}

    def _skill_component_add(self, agent, args):
        obj = self.find(args)
        out = self._add_component(agent, obj, args["componentType"])
        return dict({"success": True, "gameObject": obj.name, "component": args["componentType"]}, **out)

    def _skill_component_remove(self, agent, args):
        obj = self.find(args)
        kind, index = args["componentType"], int(args.get("componentIndex") or 0)
        found = [comp for comp in obj.components if comp[0].lower() == kind.lower()]
        if not found:
            raise _fail("COMPONENT_NOT_FOUND", "Component not found on %s: %s" % (obj.name, kind))
        if not 0 <= index < len(found):
            raise _fail("COMPONENT_NOT_FOUND", "Component index %d out of range" % index)
        obj.components.remove(found[index])
        if kind == "Rigidbody":
            obj.fields.pop("Rigidbody", None)
        self._change(agent, self._identity(obj, kind="components_changed"))
        return {"success": True, "gameObject": obj.name, "removed": kind, "remainingOfType": len(found) - 1}

    def _set_property(self, agent, obj, kind, prop, value):
        comp = self.component(obj, kind)
        if comp is None:
            raise _fail("COMPONENT_NOT_FOUND", "Component not found: %s" % kind)
        if comp[0] == "Rigidbody" and prop in ("mass", "m_Mass"):
            before = obj.fields["Rigidbody"]["m_Mass"]
            obj.fields["Rigidbody"]["m_Mass"] = float(value)
            self._change(agent, self._identity(obj, kind="property_changed", targetType="Rigidbody",
                                               property="m_Mass", previousValue=self.format_value(before),
                                               value=self.format_value(float(value))))
        elif prop in ("enabled", "m_Enabled") and comp[1] is not None:
            before = comp[1]
            comp[1] = value if isinstance(value, bool) else str(value).strip().lower() == "true"
            self._change(agent, self._identity(obj, kind="property_changed", targetType=comp[0],
                                               property="m_Enabled", previousValue="1" if before else "0",
                                               value="1" if comp[1] else "0"))
        else:
            raise _fail("PROPERTY_NOT_FOUND", "%s.%s is not modelled" % (kind, prop))

    def _skill_component_set_property(self, agent, args):
        kind, prop = args["componentType"], args["propertyName"]
        if kind.lower() == "rigidbody" or prop in ("enabled", "m_Enabled"):
            self._set_property(agent, self.find(args), kind, prop, args.get("value"))
            return {"success": True}
        return super()._skill_component_set_property(agent, args)

    def _skill_component_set_serialized_property(self, agent, args):
        if args["componentType"].lower() == "rigidbody":
            self._set_property(agent, self.find(args), "Rigidbody", args["propertyPath"], args.get("value"))
            return {"success": True}
        return super()._skill_component_set_serialized_property(agent, args)

    # ------------------------------------------------------------ batches (all or nothing)

    def _items(self, args):
        items = json.loads(args["items"])
        if not isinstance(items, list) or not items:
            raise _fail("SEMANTIC_INVALID", "items parameter is empty or invalid JSON")
        return items

    def _resolve(self, item):
        """The object an item names; only name, path and instanceId count, as in the plugin's item classes."""
        return self.find({key: item[key] for key in ITEM_KEYS if key in item}, required=True)

    def _batch(self, args, check, apply):
        items = self._items(args)
        bad = []
        for index, item in enumerate(items):
            try:
                check(self._resolve(item), item)
            except Exception as exc:
                bad.append("items[%d]: %s" % (index, str(exc)[:120]))
        if bad:
            raise _fail("SEMANTIC_INVALID", "Batch failed and was rolled back, nothing was changed: %s" % "; ".join(bad))
        results = []
        for item in items:
            obj = self._resolve(item)
            apply(obj, item)
            results.append({"target": obj.name, "success": True})
        return {"success": True, "totalItems": len(items), "successCount": len(items), "failCount": 0,
                "results": results}

    def _skill_component_add_batch(self, agent, args):
        def check(obj, item):
            if item.get("componentType") not in BUILTIN_COMPONENTS:
                raise ValueError("Component type not found: %s" % item.get("componentType"))

        return self._batch(args, check, lambda obj, item: self._add_component(agent, obj, item["componentType"]))

    def _skill_component_set_property_batch(self, agent, args):
        def check(obj, item):
            if self.component(obj, item.get("componentType") or "") is None:
                raise ValueError("Component not found: %s" % item.get("componentType"))

        return self._batch(args, check, lambda obj, item: self._set_property(
            agent, obj, item["componentType"], item["propertyName"], item.get("value")))

    def _skill_component_set_serialized_property_batch(self, agent, args):
        def check(obj, item):
            if self.component(obj, item.get("componentType") or "") is None:
                raise ValueError("Component not found: %s" % item.get("componentType"))

        return self._batch(args, check, lambda obj, item: self._set_property(
            agent, obj, item["componentType"], item["propertyPath"], item.get("value")))

    def _skill_gameobject_set_active_batch(self, agent, args):
        def check(obj, item):
            pass

        return self._batch(args, check, lambda obj, item: self._skill_gameobject_set_active(
            agent, dict(item, **{"path": self.path(obj)})))

# Producer:Betsy
