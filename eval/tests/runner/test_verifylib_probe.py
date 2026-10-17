"""usk_eval.verifylib.Probe: the read-only view of the editor."""
import json
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval.rest import ReadOnlyView, RestError, SkillError, Unreachable
from usk_eval.verifylib import Probe

READ_SKILLS = {"gameobject_get_info", "component_list", "component_get_serialized_properties",
               "material_get_properties", "asset_get_info", "cleaner_get_dependency_tree",
               "scriptableobject_get_serialized_properties", "batch_query_gameobjects"}


class Broken(object):
    """A client whose every read fails the way the transport does."""

    def __init__(self, error):
        self.error = error

    def read(self, skill, **args):
        raise self.error


class GameObjectTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene()
        self.probe = Probe(self.editor)

    def test_go_by_instance_id_and_by_path(self):
        self.assertEqual(self.probe.go(102)["name"], "Crate")
        self.assertEqual(self.probe.go("102")["name"], "Crate")
        self.assertEqual(self.probe.go_path(support.R + "/Props/Lamp")["instanceId"], 103)

    def test_missing_objects_read_as_none(self):
        self.assertIsNone(self.probe.go(999))
        self.assertIsNone(self.probe.go_path("Nowhere"))

    def test_values_that_cannot_name_an_object_ask_nothing(self):
        for bad in [None, "", "abc", 2.5, True, [], {}]:
            with self.subTest(bad=bad):
                self.assertIsNone(self.probe.go(bad))
                self.assertEqual(self.probe.components(bad), [])
                self.assertIsNone(self.probe.ser(bad, "Transform"))
                self.assertIsNone(self.probe.material(iid=bad))
        for bad in [None, "", 5, ["a"]]:
            with self.subTest(path=bad):
                self.assertIsNone(self.probe.go_path(bad))
                self.assertIsNone(self.probe.asset(bad))
                self.assertIsNone(self.probe.guid(bad))
                self.assertIsNone(self.probe.deps(bad))
                self.assertIsNone(self.probe.so(bad))
                self.assertIsNone(self.probe.material(path=bad, iid=None))
        self.assertEqual(self.editor.calls, [])

    def test_children(self):
        self.assertEqual([c["name"] for c in self.probe.children(101)], ["Crate", "Lamp"])
        self.assertEqual(self.probe.children(102), [])
        self.assertEqual(self.probe.children(999), [])

    def test_components_and_counts(self):
        self.assertEqual([c["type"] for c in self.probe.components(102)], ["Transform", "BoxCollider"])
        self.assertEqual(self.probe.component_count(102, "BoxCollider"), 1)
        self.assertEqual(self.probe.component_count(102, "UnityEngine.BoxCollider"), 1)
        self.assertEqual(self.probe.component_count(102, "Rigidbody"), 0)
        self.assertEqual(self.probe.component_count(999, "BoxCollider"), 0)

    def test_a_deleted_object_has_no_components_instead_of_an_error(self):
        self.editor.remove(102)
        self.assertEqual(self.probe.components(102), [])

    def test_disabled_components_are_visible(self):
        light = [c for c in self.probe.components(103) if c["type"] == "Light"][0]
        self.assertFalse(light["enabled"])

    def test_serialized_properties_map_by_property_path(self):
        self.editor.serialized[(102, "Transform")] = {"m_LocalPosition": "0,0.5,0", "m_Father": "101:Transform:Props"}
        self.assertEqual(self.probe.ser(102, "Transform"),
                         {"m_LocalPosition": "0,0.5,0", "m_Father": "101:Transform:Props"})
        self.assertIsNone(self.probe.ser(102, "Rigidbody"))
        self.assertIsNone(self.probe.ser(999, "Transform"))

    def test_serialized_properties_ask_for_a_large_page(self):
        self.editor.serialized[(102, "Transform")] = {}
        self.probe.ser(102, "Transform")
        skill, args = self.editor.calls[-1]
        self.assertEqual((skill, args["limit"], args["componentType"]), ("component_get_serialized_properties", 5000,
                                                                          "Transform"))

    def test_query_serialises_the_query(self):
        result = self.probe.query({"componentType": "BoxCollider", "includeInactive": True}, sample_limit=2)
        self.assertEqual(len(result["objects"]), 2)
        skill, args = self.editor.calls[-1]
        self.assertEqual(skill, "batch_query_gameobjects")
        self.assertEqual(json.loads(args["queryJson"]), {"componentType": "BoxCollider", "includeInactive": True})
        self.assertEqual(args["sampleLimit"], 2)
        self.probe.query('{"x":1}')
        self.assertEqual(self.editor.calls[-1][1]["queryJson"], '{"x":1}')
        self.assertEqual(self.editor.calls[-1][1]["sampleLimit"], 500)


class AssetTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene()
        self.editor.assets["Assets/M.mat"] = {"guid": "abc123", "path": "Assets/M.mat"}
        self.editor.materials["Assets/M.mat"] = {"shader": "Lit", "properties": {}}
        self.probe = Probe(self.editor)

    def test_asset_and_guid(self):
        self.assertEqual(self.probe.asset("Assets/M.mat")["path"], "Assets/M.mat")
        self.assertEqual(self.probe.guid("Assets/M.mat"), "abc123")
        self.assertIsNone(self.probe.asset("Assets/Gone.mat"))
        self.assertIsNone(self.probe.guid("Assets/Gone.mat"))

    def test_material_by_path(self):
        self.assertEqual(self.probe.material(path="Assets/M.mat")["shader"], "Lit")
        self.assertIsNone(self.probe.material(path="Assets/Gone.mat"))

    def test_dependencies_are_read_one_level_deep(self):
        self.assertEqual(self.probe.deps("Assets/M.mat")["assetPath"], "Assets/M.mat")
        self.assertIs(self.editor.calls[-1][1]["recursive"], False)
        self.assertIsNone(self.probe.deps("Assets/Gone.mat"))

    def test_scriptable_object(self):
        self.assertEqual(self.probe.so("Assets/M.mat")["assetPath"], "Assets/M.mat")
        self.assertIsNone(self.probe.so("Assets/Gone.mat"))


class ReadOnlyTests(unittest.TestCase):
    def test_only_reads_are_ever_requested(self):
        editor = support.sample_scene()
        editor.assets["Assets/M.mat"] = {"guid": "g"}
        editor.materials["Assets/M.mat"] = {}
        editor.serialized[(102, "Transform")] = {}
        probe = Probe(editor)
        probe.go(102)
        probe.go_path(support.R)
        probe.children(101)
        probe.components(102)
        probe.component_count(102, "BoxCollider")
        probe.ser(102, "Transform")
        probe.owner("102:UnityEngine.Transform:Crate")
        probe.material(path="Assets/M.mat")
        probe.asset("Assets/M.mat")
        probe.guid("Assets/M.mat")
        probe.deps("Assets/M.mat")
        probe.so("Assets/M.mat")
        probe.query({})
        self.assertTrue(editor.calls)
        self.assertTrue({skill for skill, _ in editor.calls} <= READ_SKILLS)

    def test_the_client_needs_nothing_but_read(self):
        self.assertFalse(any(hasattr(support.FakeEditor, name) for name in ("write", "batch", "wait_idle", "wait_job")))
        self.assertEqual(Probe(support.sample_scene()).go(102)["name"], "Crate")

    def test_it_works_through_the_verifier_view(self):
        probe = Probe(ReadOnlyView(support.sample_scene()))
        self.assertEqual(probe.go(102)["name"], "Crate")
        self.assertEqual(probe.guid("Assets/none"), None)

    def test_the_source_never_names_a_writing_call(self):
        text = (support.VERIFYLIB_SOURCES / "probe.py").read_text(encoding="utf-8-sig")
        for forbidden in (".write(", ".batch(", ".request(", "wait_idle", "wait_job", "import urllib", "import socket"):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, text)


class ErrorTests(unittest.TestCase):
    def test_business_errors_read_as_missing(self):
        probe = Probe(Broken(SkillError("nope", payload={"errorCode": "TARGET_NOT_FOUND"})))
        self.assertIsNone(probe.go(1))
        self.assertIsNone(probe.go_path("a"))
        self.assertEqual(probe.children(1), [])
        self.assertEqual(probe.components(1), [])
        self.assertIsNone(probe.ser(1, "T"))
        self.assertIsNone(probe.material(path="a"))
        self.assertIsNone(probe.material(iid=1))
        self.assertIsNone(probe.asset("a"))
        self.assertIsNone(probe.guid("a"))
        self.assertIsNone(probe.deps("a"))
        self.assertIsNone(probe.so("a"))
        self.assertIsNone(probe.owner("1:T:n"))

    def test_transport_errors_propagate(self):
        for error in [Unreachable("gone"), RestError("refused non-read-only skill x"), RestError("HTTP 500", 500)]:
            probe = Probe(Broken(error))
            for call in [lambda: probe.go(1), lambda: probe.go_path("a"), lambda: probe.components(1),
                         lambda: probe.ser(1, "T"), lambda: probe.material(path="a"), lambda: probe.asset("a"),
                         lambda: probe.deps("a"), lambda: probe.so("a"), lambda: probe.query({})]:
                with self.subTest(error=type(error).__name__):
                    with self.assertRaises(RestError):
                        call()

    def test_a_failed_query_is_an_error_not_an_empty_answer(self):
        with self.assertRaises(SkillError):
            Probe(Broken(SkillError("bad query"))).query({})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
