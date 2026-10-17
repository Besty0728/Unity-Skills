"""Read-only view of the editor for verifiers (contract 5.5, 5.8; ported from v1 eval/harness/verify.py).

Every method answers through client.read, which only runs skills the plugin marks readOnly; a business error
(SkillError) becomes None / [] so a missing object is a failed assertion, not a crashed verifier. Identifiers and
paths come from agent answers, so values that cannot name anything are answered the same way without a request.
Transport errors (RestError, Unreachable) propagate: the runner then marks the attempt invalid, not scored on a guess.

An object is named by an int instanceId, an entityId text or a result dict carrying them (see checks.object_id); the
skills get the arguments checks.address() builds for it: `instanceId` for a non-zero int that fits C# int and
`entityId` for everything else, which is the only spelling that works from Unity 6000.4 on.
"""
import json

from ..rest import SkillError
from .checks import address, object_id, parse_ref


def _text(value):
    return value if isinstance(value, str) and value else None


def _target(ref):
    """Skill arguments that name the object `ref` stands for, or None when it names nothing."""
    try:
        return address(ref)
    except ValueError:
        return None


class Probe(object):
    def __init__(self, client):
        self.c = client

    def go(self, ref):
        target = _target(ref)
        if target is None:
            return None
        try:
            return self.c.read("gameobject_get_info", **target)
        except SkillError:
            return None

    def go_path(self, path):
        if _text(path) is None:
            return None
        try:
            return self.c.read("gameobject_get_info", path=path)
        except SkillError:
            return None

    def children(self, ref):
        info = self.go(ref) or {}
        return info.get("children") or []

    def components(self, ref):
        target = _target(ref)
        if target is None:
            return []
        try:
            res = self.c.read("component_list", **target)
        except SkillError:
            return []
        return res.get("components") or []

    def component_count(self, ref, type_name):
        return sum(1 for c in self.components(ref) if c.get("type") == type_name or c.get("fullType") == type_name)

    def ser(self, ref, component_type, limit=5000):
        target = _target(ref)
        if target is None:
            return None
        try:
            res = self.c.read("component_get_serialized_properties", componentType=component_type, limit=limit,
                              **target)
        except SkillError:
            return None
        return {p.get("propertyPath"): p.get("value") for p in res.get("properties") or []}

    def owner(self, ref_value):
        """Id (checks.object_id) of the GameObject owning the referenced object (component ids resolve to their GO).

        A reference reads "<entityId>:<FullType>:<name>"; before Unity 6000.4 the entityId is the instanceId as text."""
        ref = parse_ref(ref_value)
        if not ref:
            return None
        info = self.go(str(ref[0]))
        return object_id(info) if info else None

    def material(self, path=None, iid=None):
        if path:
            if _text(path) is None:
                return None
            args = {"path": path}
        else:
            args = _target(iid)
            if args is None:
                return None
        try:
            return self.c.read("material_get_properties", **args)
        except SkillError:
            return None

    def asset(self, path):
        if _text(path) is None:
            return None
        try:
            return self.c.read("asset_get_info", assetPath=path)
        except SkillError:
            return None

    def guid(self, path):
        info = self.asset(path)
        return info.get("guid") if info else None

    def deps(self, path):
        if _text(path) is None:
            return None
        try:
            return self.c.read("cleaner_get_dependency_tree", assetPath=path, recursive=False)
        except SkillError:
            return None

    def so(self, path):
        if _text(path) is None:
            return None
        try:
            return self.c.read("scriptableobject_get_serialized_properties", assetPath=path)
        except SkillError:
            return None

    def query(self, query_json, sample_limit=500):
        payload = query_json if isinstance(query_json, str) else json.dumps(query_json)
        return self.c.read("batch_query_gameobjects", queryJson=payload, sampleLimit=sample_limit)

# Producer:Betsy
