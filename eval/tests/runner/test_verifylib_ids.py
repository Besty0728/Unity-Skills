"""usk_eval.verifylib object ids: int instanceIds before Unity 6000.4, entityId texts after (instanceId is 0)."""
import os
import shutil
import tempfile
import types
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import fixture, task_api
from usk_eval import verifylib as V
from usk_eval.task_api import BuildContext
from usk_eval.verifylib import Probe

BIG = "18446744073709551601"          # an entityId of Unity 6000.4+: unsigned 64 bit


def calls_of(editor, skill):
    return [args for name, args in editor.calls if name == skill]


class ObjectIdTests(unittest.TestCase):
    CASES = [
        (5, 5), (-5, -5), (0, None), (True, None), (False, None), (None, None), (2.0, 2), (-3.0, -3), (2.5, None),
        (float("nan"), None), (float("inf"), None),
        ("12", "12"), (" 12 ", "12"), ("-12", "-12"), ("007", "7"), ("0", None), ("-0", None), ("", None),
        ("abc", None), ("1.5", None), ("1e3", None), ("+5", None), (BIG, BIG), ("1" * 21, None),
        ({"instanceId": 5, "entityId": "5"}, 5),
        ({"instanceId": 0, "entityId": BIG}, BIG),
        ({"instanceId": 0, "entityId": "0"}, None),
        ({"instanceId": 0}, None),
        ({"entityId": "-7"}, "-7"),
        ({"entityId": 7}, 7),
        ({"instanceId": "x", "entityId": "9"}, "9"),
        ({"instanceId": True, "entityId": "9"}, "9"),
        ({}, None), ([], None), ([5], None), (b"5", None), (object(), None),
    ]

    def test_canonical_ids(self):
        for value, expected in self.CASES:
            with self.subTest(value=value):
                result = V.object_id(value)
                self.assertEqual(result, expected)
                self.assertIs(type(result), type(expected))

    def test_the_legacy_int_wins_over_the_entity_text_when_the_editor_has_both(self):
        self.assertEqual(V.object_id({"entityId": "99", "instanceId": 5}), 5)

    def test_same_id_mixes_every_spelling(self):
        for a, b in [(5, "5"), ("5", 5), (5, {"instanceId": 5, "entityId": "5"}), ({"entityId": BIG}, BIG),
                     ({"instanceId": 0, "entityId": BIG}, {"entityId": " %s " % BIG}), ("-12", -12), (7, 7.0),
                     (BIG, int(BIG)), ("007", 7)]:
            with self.subTest(a=a, b=b):
                self.assertTrue(V.same_id(a, b))

    def test_same_id_needs_two_objects(self):
        for a, b in [(5, 6), ("5", "6"), (BIG, "18446744073709551602"), (5, BIG), (None, None), (0, 0), ("x", "x"),
                     (None, 5), ({}, {}), (True, True)]:
            with self.subTest(a=a, b=b):
                self.assertFalse(V.same_id(a, b))


class AddressTests(unittest.TestCase):
    """address(): the one rule that turns an object reference into skill arguments (instanceId vs entityId)."""

    def test_legacy_ids_go_out_as_instance_ids(self):
        result = {"entityId": "-1234", "instanceId": -1234, "name": "Props"}
        self.assertEqual(V.address(result), {"instanceId": -1234})
        self.assertEqual(V.address(result, "parent"), {"parentInstanceId": -1234})
        self.assertEqual(V.address(-1234), {"instanceId": -1234})
        self.assertEqual(V.address(7.0), {"instanceId": 7})

    def test_modern_ids_go_out_as_entity_ids(self):
        result = {"entityId": BIG, "instanceId": 0}
        self.assertEqual(V.address(result), {"entityId": BIG})
        self.assertEqual(V.address(result, "parent"), {"parentEntityId": BIG})
        self.assertEqual(V.address(BIG), {"entityId": BIG})
        self.assertEqual(V.address({"entityId": BIG}), {"entityId": BIG})
        self.assertEqual(V.address(" %s " % BIG), {"entityId": BIG})

    def test_text_is_always_an_entity_id_even_when_it_looks_like_a_small_int(self):
        for text, expected in (("102", "102"), (" 102 ", "102"), ("-12", "-12"), ("007", "7")):
            with self.subTest(text=text):
                self.assertEqual(V.address(text), {"entityId": expected})

    def test_the_c_int_range_decides_between_the_two_spellings(self):
        low, high = -2 ** 31, 2 ** 31 - 1
        self.assertEqual(V.address(low), {"instanceId": low})
        self.assertEqual(V.address(high), {"instanceId": high})
        self.assertEqual(V.address(low - 1), {"entityId": str(low - 1)})
        self.assertEqual(V.address(high + 1), {"entityId": str(high + 1)})
        self.assertEqual(V.address(9999999999), {"entityId": "9999999999"})
        self.assertEqual(V.address(float(high + 1)), {"entityId": str(high + 1)})

    def test_a_result_dict_prefers_the_legacy_id_the_way_object_id_does(self):
        self.assertEqual(V.address({"entityId": "99", "instanceId": 5}), {"instanceId": 5})
        self.assertEqual(V.address({"entityId": "99", "instanceId": 0}), {"entityId": "99"})
        self.assertEqual(V.address({"entityId": "99", "instanceId": 2 ** 40}), {"entityId": str(2 ** 40)})

    def test_value_types(self):
        self.assertIs(type(V.address(5)["instanceId"]), int)
        self.assertIs(type(V.address({"entityId": 12})["instanceId"]), int)
        self.assertIs(type(V.address(BIG)["entityId"]), str)
        self.assertIs(type(V.address(2 ** 40)["entityId"]), str)

    def test_prefixes(self):
        self.assertEqual(V.address(5, "parent"), {"parentInstanceId": 5})
        self.assertEqual(V.address(BIG, "parent"), {"parentEntityId": BIG})
        self.assertEqual(V.address(5, "child"), {"childInstanceId": 5})
        self.assertEqual(V.address(BIG, "target"), {"targetEntityId": BIG})
        for none in ("", None):
            self.assertEqual(V.address(5, none), {"instanceId": 5})
        self.assertEqual(V.address(5, prefix="parent"), {"parentInstanceId": 5})

    def test_it_returns_a_fresh_dict(self):
        first = V.address(5)
        first["name"] = "changed"
        self.assertEqual(V.address(5), {"instanceId": 5})

    def test_a_reference_that_names_nothing_is_a_value_error(self):
        for bad in ({}, {"instanceId": True}, {"instanceId": 0}, {"entityId": "0", "instanceId": 0}, {"entityId": ""},
                    None, "Props", "", "0", "-0", "1.5", 0, 0.0, True, False, 2.5, float("nan"), float("inf"), [], [5],
                    b"5", object()):
            with self.subTest(bad=bad):
                with self.assertRaises(ValueError):
                    V.address(bad)
                with self.assertRaises(ValueError):
                    V.address(bad, "parent")

    def test_the_error_says_what_was_passed(self):
        with self.assertRaises(ValueError) as caught:
            V.address({"instanceId": 0})
        self.assertIn("{'instanceId': 0}", str(caught.exception))
        self.assertIn("names no object", str(caught.exception))

    def test_it_only_accepts_what_object_id_accepts(self):
        refs = [5, -5, 0, True, None, 2.0, 2.5, "12", " 12 ", "abc", BIG, "1" * 21, {"entityId": BIG, "instanceId": 0},
                {"instanceId": 5}, {}, [], b"5"]
        for ref in refs:
            with self.subTest(ref=ref):
                if V.object_id(ref) is None:
                    with self.assertRaises(ValueError):
                        V.address(ref)
                else:
                    self.assertEqual(len(V.address(ref)), 1)

    def test_the_addressed_object_is_the_one_object_id_names(self):
        for ref in (5, "5", {"instanceId": 5, "entityId": "5"}, {"instanceId": 0, "entityId": BIG}, BIG, 2 ** 40):
            with self.subTest(ref=ref):
                ((key, value),) = V.address(ref).items()
                self.assertTrue(V.same_id(ref, value), (key, value))
                self.assertEqual(key.endswith("EntityId") or key == "entityId", isinstance(value, str))


class AddressSharedRuleTests(unittest.TestCase):
    """Probe, fixture and task_api all use the same rule, so a builder and a verifier always name an object alike."""

    REFS = [5, -5, "5", " 5 ", 2 ** 31 - 1, 2 ** 31, -2 ** 31, -2 ** 31 - 1, 2 ** 40, BIG, 3.0, {"instanceId": 7},
            {"entityId": BIG, "instanceId": 0}, {"entityId": "12", "instanceId": 0}, 0, "0", None, "", "Props", True,
            2.5, {}, {"instanceId": True}, []]

    def test_probe_sends_exactly_the_address_arguments(self):
        for ref in self.REFS:
            with self.subTest(ref=ref):
                editor = support.sample_scene(entity_ids=True)
                Probe(editor).go(ref)
                try:
                    expected = V.address(ref)
                except ValueError:
                    self.assertEqual(editor.calls, [])
                else:
                    self.assertEqual(editor.calls, [("gameobject_get_info", expected)])

    def test_every_object_method_of_the_probe_sends_the_address_arguments(self):
        for ref in (5, "5", BIG, 2 ** 40, {"instanceId": 0, "entityId": BIG}):
            with self.subTest(ref=ref):
                editor = support.sample_scene(entity_ids=True)
                probe = Probe(editor)
                probe.components(ref)
                probe.ser(ref, "Transform")
                probe.material(iid=ref)
                self.assertEqual(len(editor.calls), 3)
                for _name, args in editor.calls:
                    self.assertEqual({k: v for k, v in args.items() if k in ("instanceId", "entityId")},
                                     V.address(ref))

    def test_fixture_and_task_api_agree_with_verifylib(self):
        for ref in self.REFS:
            for prefix in ("", "parent"):
                with self.subTest(ref=ref, prefix=prefix):
                    try:
                        expected = V.address(ref, prefix)
                    except ValueError:
                        for module in (fixture, task_api):
                            with self.assertRaises(ValueError):
                                module.address(ref, prefix)
                    else:
                        for module in (fixture, task_api):
                            self.assertEqual(module.address(ref, prefix), expected)


class ProbeLegacyIdTests(unittest.TestCase):
    """Before 6000.4: the int stays an instanceId, text and big numbers go out as entityId."""

    def setUp(self):
        self.editor = support.sample_scene()
        self.probe = Probe(self.editor)

    def last(self):
        return self.editor.calls[-1]

    def test_a_non_zero_int_is_an_instance_id(self):
        self.assertEqual(self.probe.go(102)["name"], "Crate")
        self.assertEqual(self.last(), ("gameobject_get_info", {"instanceId": 102}))

    def test_text_is_an_entity_id_even_when_it_looks_like_an_int(self):
        self.assertEqual(self.probe.go("102")["name"], "Crate")
        self.assertEqual(self.last(), ("gameobject_get_info", {"entityId": "102"}))
        self.assertEqual(self.probe.go("  102 ")["name"], "Crate")
        self.assertEqual(self.last(), ("gameobject_get_info", {"entityId": "102"}))

    def test_a_result_dict_prefers_the_instance_id_and_falls_back_to_the_entity_id(self):
        self.probe.go({"instanceId": 102, "entityId": "102", "name": "Crate"})
        self.assertEqual(self.last()[1], {"instanceId": 102})
        self.probe.go({"instanceId": 0, "entityId": "102"})
        self.assertEqual(self.last()[1], {"entityId": "102"})

    def test_numbers_beyond_c_int_can_only_be_entity_ids(self):
        self.probe.go(2 ** 40)
        self.assertEqual(self.last()[1], {"entityId": str(2 ** 40)})
        self.probe.go(-2 ** 31)
        self.assertEqual(self.last()[1], {"instanceId": -2 ** 31})
        self.probe.go(2 ** 31)
        self.assertEqual(self.last()[1], {"entityId": str(2 ** 31)})

    def test_integral_floats_from_json_answers_are_instance_ids(self):
        self.assertEqual(self.probe.go(102.0)["name"], "Crate")
        self.assertEqual(self.last()[1], {"instanceId": 102})

    def test_zero_and_junk_name_nothing_and_ask_nothing(self):
        for bad in [0, "0", "-0", None, "", "abc", "1.5", True, 2.5, {}, {"instanceId": 0}, [], [102]]:
            with self.subTest(bad=bad):
                self.assertIsNone(self.probe.go(bad))
                self.assertEqual(self.probe.components(bad), [])
                self.assertIsNone(self.probe.ser(bad, "Transform"))
                self.assertIsNone(self.probe.material(iid=bad))
                self.assertIsNone(self.probe.owner(bad))
        self.assertEqual(self.editor.calls, [])

    def test_every_object_method_takes_either_spelling(self):
        self.editor.serialized[(102, "Transform")] = {"m_LocalPosition": "0,0.5,0"}
        self.editor.object_materials[102] = {"shader": "Lit"}
        for ref, key in [(102, "instanceId"), ("102", "entityId"), ({"entityId": "102", "instanceId": 0}, "entityId")]:
            with self.subTest(ref=ref):
                self.editor.calls[:] = []
                self.assertEqual(self.probe.go(ref)["name"], "Crate")
                self.assertEqual([c["type"] for c in self.probe.components(ref)], ["Transform", "BoxCollider"])
                self.assertEqual(self.probe.component_count(ref, "BoxCollider"), 1)
                self.assertEqual(self.probe.ser(ref, "Transform"), {"m_LocalPosition": "0,0.5,0"})
                self.assertEqual(self.probe.material(iid=ref), {"shader": "Lit"})
                self.assertEqual(len(self.editor.calls), 5)
                for name, args in self.editor.calls:
                    self.assertEqual([k for k in args if k in ("instanceId", "entityId")], [key], name)

    def test_the_owner_of_a_reference_is_found_by_its_entity_id_and_answered_as_the_legacy_int(self):
        self.editor.alias(5001, 102)
        self.assertEqual(self.probe.owner("5001:UnityEngine.BoxCollider:Crate"), 102)
        self.assertEqual(self.editor.calls, [("gameobject_get_info", {"entityId": "5001"})])
        self.assertIsNone(self.probe.owner("-17:UnityEngine.Transform:Crate"))


class ProbeEntityIdTests(unittest.TestCase):
    """Unity 6000.4+: every instanceId is 0, only entityId texts name objects, and the probe never sends instanceId."""

    def setUp(self):
        self.editor = support.sample_scene(entity_ids=True)
        self.probe = Probe(self.editor)
        self.crate = self.editor.entity(102)

    def assert_no_instance_ids_sent(self):
        self.assertTrue(self.editor.calls)
        for name, args in self.editor.calls:
            self.assertNotIn("instanceId", args, name)

    def test_results_carry_both_ids(self):
        info = self.probe.go(self.crate)
        self.assertEqual((info["entityId"], info["instanceId"]), (self.crate, 0))
        self.assertEqual([(c["entityId"], c["instanceId"]) for c in self.probe.children(self.editor.entity(101))],
                         [(self.crate, 0), (self.editor.entity(103), 0)])
        self.assert_no_instance_ids_sent()

    def test_children_can_be_passed_straight_back(self):
        first = self.probe.children(self.editor.entity(101))[0]
        self.assertEqual(self.probe.go(first)["name"], "Crate")
        self.assertEqual([c["type"] for c in self.probe.components(first)], ["Transform", "BoxCollider"])
        self.assert_no_instance_ids_sent()

    def test_every_object_method_sends_the_entity_id(self):
        self.editor.serialized[(102, "Transform")] = {"m_LocalPosition": "0,0.5,0"}
        self.editor.object_materials[102] = {"shader": "Lit"}
        self.assertEqual(self.probe.go(self.crate)["name"], "Crate")
        self.assertEqual(self.probe.component_count(self.crate, "BoxCollider"), 1)
        self.assertEqual(self.probe.ser(self.crate, "Transform"), {"m_LocalPosition": "0,0.5,0"})
        self.assertEqual(self.probe.material(iid=self.crate), {"shader": "Lit"})
        self.assert_no_instance_ids_sent()
        self.assertEqual(calls_of(self.editor, "component_get_serialized_properties")[0]["entityId"], self.crate)
        self.assertEqual(calls_of(self.editor, "material_get_properties")[0]["entityId"], self.crate)

    def test_a_numeric_entity_id_beyond_c_int_goes_out_as_text(self):
        self.assertEqual(self.probe.go(int(self.crate))["name"], "Crate")
        self.assertEqual(calls_of(self.editor, "gameobject_get_info")[0], {"entityId": self.crate})

    def test_a_legacy_int_cannot_find_anything_and_is_not_guessed_into_an_entity_id(self):
        self.assertIsNone(self.probe.go(102))
        self.assertEqual(self.editor.calls, [("gameobject_get_info", {"instanceId": 102})])

    def test_the_owner_of_a_reference_is_answered_as_an_entity_id(self):
        component = str(int(self.crate) + 100)
        self.editor.alias(component, 102)
        owner = self.probe.owner("%s:UnityEngine.BoxCollider:Crate" % component)
        self.assertEqual(owner, self.crate)
        self.assertTrue(V.same_id(owner, self.probe.go(self.crate)))
        self.assert_no_instance_ids_sent()
        self.assertIsNone(self.probe.owner("%d:UnityEngine.BoxCollider:Gone" % (int(self.crate) + 200)))

    def test_a_query_result_can_be_walked_by_its_entity_ids(self):
        found = self.probe.query({"componentType": "BoxCollider"})["objects"]
        by_name = {o["name"]: o for o in found}
        self.assertEqual(by_name["Crate"]["entityId"], self.crate)
        self.assertEqual(self.probe.go(by_name["Lamp"])["isActive"], False)
        self.assert_no_instance_ids_sent()


class StructureIdTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene(entity_ids=True)
        self.probe = Probe(self.editor)
        self.e = self.editor.entity

    def test_nodes_use_entity_ids_and_carry_both(self):
        nodes = V.structure(self.probe, [self.e(100)])
        self.assertEqual([n["id"] for n in nodes], [self.e(100), self.e(101), self.e(102), self.e(103)])
        crate = nodes[2]
        self.assertEqual((crate["entityId"], crate["instanceId"]), (self.e(102), 0))
        self.assertEqual(nodes[1]["children"], [self.e(102), self.e(103)])
        self.assertEqual(crate["components"], [["Transform", None], ["BoxCollider", True]])
        for name, args in self.editor.calls:
            self.assertNotIn("instanceId", args)

    def test_a_root_may_be_text_a_result_or_a_list_of_either(self):
        reference = V.structure(self.probe, [self.e(100)])
        self.assertEqual(V.structure(self.probe, self.e(100)), reference)
        self.assertEqual(V.structure(self.probe, {"entityId": self.e(100), "instanceId": 0}), reference)
        self.assertEqual(V.structure(self.probe, [{"entityId": self.e(100)}]), reference)
        self.assertEqual(V.structure(self.probe, (self.e(100),)), reference)

    def test_two_roots_keep_their_order(self):
        self.editor.add(200, "Reserve")
        ids = [n["id"] for n in V.structure(self.probe, [self.e(200), self.e(101)])]
        self.assertEqual(ids, [self.e(200), self.e(101), self.e(102), self.e(103)])

    def test_the_node_id_is_the_editors_not_the_spelling_of_the_request(self):
        legacy = Probe(support.sample_scene())
        reference = V.structure(legacy, [100])
        for root in ("100", {"entityId": "100"}, 100.0):
            with self.subTest(root=root):
                self.assertEqual(V.structure(legacy, [root]), reference)
        self.assertEqual([n["id"] for n in reference], [100, 101, 102, 103])

    def test_a_child_without_any_id_is_listed_but_not_walked(self):
        class Bare(object):
            def go(self, ref):
                if object_id_of(ref) == "1":
                    return {"name": "root", "entityId": "1", "instanceId": 0, "children": [{"name": "ghost"}]}
                return None

            def components(self, ref):
                return []

        def object_id_of(ref):
            return V.object_id(ref) and str(V.object_id(ref))

        nodes = V.structure(Bare(), ["1"])
        self.assertEqual([n["name"] for n in nodes], ["root"])
        self.assertEqual(nodes[0]["children"], [None])

    def test_every_kind_of_edit_changes_the_digest(self):
        base = V.canonical_digest(V.structure(self.probe, [self.e(100)]))
        self.editor.objects[102]["name"] = "Box"
        self.assertNotEqual(V.canonical_digest(V.structure(self.probe, [self.e(100)])), base)
        self.editor.objects[102]["name"] = "Crate"
        self.assertEqual(V.canonical_digest(V.structure(self.probe, [self.e(100)])), base)
        self.editor.objects[101]["children"].reverse()
        self.assertNotEqual(V.canonical_digest(V.structure(self.probe, [self.e(100)])), base)

    def test_a_deleted_object_differs_from_its_snapshot(self):
        before = V.structure(self.probe, [self.e(100)])
        self.editor.remove(102)
        self.editor.objects[101]["children"].append(102)        # the parent still lists it
        after = V.structure(self.probe, [self.e(100)])
        self.assertEqual([n["id"] for n in after], [self.e(100), self.e(101), self.e(103), self.e(102)])
        self.assertIsNone(after[-1]["name"])
        self.assertFalse(V.same(before, after)[0])

    def test_a_cycle_terminates(self):
        self.editor.objects[102]["children"].append(100)
        self.assertEqual(len(V.structure(self.probe, [self.e(100)])), 4)

    def test_the_same_scene_has_the_same_structure_apart_from_the_ids_in_every_version(self):
        legacy = V.structure(Probe(support.sample_scene()), [100])
        modern = V.structure(self.probe, [self.e(100)])

        def without_ids(nodes):
            return [{k: v for k, v in n.items() if k not in ("id", "entityId", "instanceId", "children")}
                    for n in nodes]

        self.assertEqual(without_ids(legacy), without_ids(modern))
        self.assertEqual([len(n["children"]) for n in legacy], [len(n["children"]) for n in modern])
        self.assertEqual([n["id"] for n in legacy], [100, 101, 102, 103])


class HierarchySignatureIdTests(unittest.TestCase):
    def test_the_whole_tree_is_read_through_entity_ids(self):
        editor = support.sample_scene(entity_ids=True)
        sig = V.hierarchy_signature(Probe(editor), editor.entity(100))
        self.assertEqual(sig["name"], support.R)
        crate, lamp = sig["children"][0]["children"]
        self.assertEqual((crate["name"], lamp["name"]), ("Crate", "Lamp"))
        self.assertEqual(crate["components"], ["Transform", "BoxCollider"])
        self.assertEqual(crate["pos"], {"x": 0, "y": 0.5, "z": 0})
        for name, args in editor.calls:
            self.assertNotIn("instanceId", args)

    def test_the_signature_does_not_depend_on_the_id_scheme(self):
        legacy, modern = support.sample_scene(), support.sample_scene(entity_ids=True)
        self.assertEqual(V.hierarchy_signature(Probe(legacy), 100),
                         V.hierarchy_signature(Probe(modern), modern.entity(100)))
        self.assertNotIn(modern.entity(100), str(V.hierarchy_signature(Probe(modern), modern.entity(100))))

    def test_the_root_may_be_a_result_dict(self):
        editor = support.sample_scene(entity_ids=True)
        probe = Probe(editor)
        root = probe.go(editor.entity(100))
        self.assertEqual(V.hierarchy_signature(probe, root), V.hierarchy_signature(probe, editor.entity(100)))


class StateIdTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene(entity_ids=True)
        self.probe = Probe(self.editor)
        self.e = self.editor.entity

    def test_go_state_lists_the_children_by_entity_id_in_sibling_order(self):
        state = V.go_state(self.probe, self.e(101))
        self.assertEqual(state["children"], [self.e(102), self.e(103)])
        self.editor.objects[101]["children"].reverse()
        moved = V.go_state(self.probe, self.e(101))
        self.assertEqual(moved["children"], [self.e(103), self.e(102)])
        self.assertFalse(V.same(state, moved)[0])
        self.assertIsNone(V.go_state(self.probe, self.e(999)))

    def test_new_children_are_found_whichever_spelling_the_snapshot_used(self):
        before_text = [self.e(102), self.e(103)]
        before_dicts = self.probe.children(self.e(101))
        before_mixed = [self.e(102), before_dicts[1]]
        self.editor.add(104, "Barrel", parent=101)
        for before in (before_text, before_dicts, before_mixed):
            with self.subTest(before=before):
                self.assertEqual([c["name"] for c in V.new_children(self.probe, self.e(101), before)], ["Barrel"])
        self.assertEqual(V.new_children(self.probe, self.e(101), before_text + [self.e(104)]), [])

    def test_local_trs_reads_by_entity_id(self):
        self.editor.serialized[(102, "Transform")] = {"m_LocalPosition": "0,0.5,0", "m_LocalRotation": "0,0,0,1",
                                                      "m_LocalScale": "1,1,1"}
        trs = V.local_trs(self.probe, self.e(102))
        self.assertEqual(trs["position"], [0.0, 0.5, 0.0])
        self.assertTrue(V.identity_rotation(trs["rotation"]))

    def test_answer_rows_go_straight_into_the_probe(self):
        rows = [{"path": support.R + "/Props/Crate", "entityId": self.e(102)},
                {"path": support.R + "/Props/Crate", "instanceId": 0, "entityId": self.e(102)}]
        for row in rows:
            self.assertEqual(self.probe.go(row)["path"], row["path"])
            self.assertTrue(V.same_id(row, self.probe.go(row)))
        self.assertFalse(V.same_id(rows[0], self.probe.go(self.e(103))))


class FingerprintIdTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="usk-eval-fp-")
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def context(self, run_id, editor, root_iid, root_id=None):
        scope = fixture.Scope(run_id, root_iid, root_id=root_id)
        project = tempfile.mkdtemp(prefix="p-", dir=self.tmp)
        folder = os.path.join(project, *scope.asset_root.split("/"))
        os.makedirs(folder)
        with open(os.path.join(folder, "Mat.mat"), "w") as handle:
            handle.write("m")
        return BuildContext(editor, scope, 7001, project, {}, "en", "ops", "basic", None)

    def scene(self, run_id, entity_ids):
        editor = support.FakeEditor(entity_ids)
        editor.add(100, "EV_" + run_id)
        editor.add(101, "Props", parent=100)
        editor.add(102, "Crate", parent=101, position=(0, 0.5, 0), components=("Transform", "BoxCollider"))
        return editor

    def test_unity_6000_4_with_a_zero_root_id_fingerprints_the_real_tree(self):
        editor = self.scene(support.RUN_ID, True)
        zero = V.fingerprint(self.context(support.RUN_ID, editor, 0))
        known = V.fingerprint(self.context(support.RUN_ID, editor, editor.entity(100)))
        self.assertEqual(zero, known)
        editor.objects[102]["position"] = (0, 0.6, 0)
        self.assertNotEqual(V.fingerprint(self.context(support.RUN_ID, editor, 0)), zero)

    def test_a_missing_root_id_is_not_a_vacuous_fingerprint(self):
        """With root_iid 0 and no lookup every build would hash the same empty node and the selftest gate would pass."""
        editor = self.scene(support.RUN_ID, True)
        empty = self.scene(support.RUN_ID, True)
        empty.remove(102)
        empty.remove(101)
        self.assertNotEqual(V.fingerprint(self.context(support.RUN_ID, editor, 0)),
                            V.fingerprint(self.context(support.RUN_ID, empty, 0)))

    def test_two_builds_agree_across_run_ids_and_id_schemes(self):
        legacy = V.fingerprint(self.context(support.RUN_ID, self.scene(support.RUN_ID, False), 100))
        modern = V.fingerprint(self.context(support.OTHER_RUN_ID, self.scene(support.OTHER_RUN_ID, True), 0))
        self.assertEqual(legacy, modern)

    def test_a_legacy_root_id_is_still_used_when_the_editor_has_one(self):
        editor = self.scene(support.RUN_ID, False)
        V.fingerprint(self.context(support.RUN_ID, editor, 100))
        self.assertNotIn(("gameobject_get_info", {"path": "EV_" + support.RUN_ID}), editor.calls)

    def test_a_vanished_root_still_has_a_fingerprint(self):
        editor = self.scene(support.RUN_ID, True)
        editor.remove(101)
        editor.remove(100)
        self.assertRegex(V.fingerprint(self.context(support.RUN_ID, editor, 0)), r"^[0-9a-f]{64}$")

    def test_the_stored_canonical_root_id_is_read_without_a_path_lookup(self):
        editor = self.scene(support.RUN_ID, True)
        stored = V.fingerprint(self.context(support.RUN_ID, editor, 0, root_id=editor.entity(100)))
        self.assertEqual(calls_of(editor, "gameobject_get_info")[0], {"entityId": editor.entity(100)})
        self.assertNotIn({"path": "EV_" + support.RUN_ID}, calls_of(editor, "gameobject_get_info"))
        for name, args in editor.calls:
            self.assertNotIn("instanceId", args, name)
        self.assertEqual(stored, V.fingerprint(self.context(support.RUN_ID, editor, 0)))     # same tree via the path

    def test_the_canonical_id_wins_over_the_legacy_instance_id(self):
        for entity_ids, root_id in ((True, "entity"), (False, 100)):
            with self.subTest(entity_ids=entity_ids):
                editor = self.scene(support.RUN_ID, entity_ids)
                root = editor.entity(100) if root_id == "entity" else root_id
                real = V.fingerprint(self.context(support.RUN_ID, editor, 0 if entity_ids else 100, root_id=root))
                stale = V.fingerprint(self.context(support.RUN_ID, editor, 31337, root_id=root))
                self.assertEqual(stale, real)
                empty = self.scene(support.RUN_ID, entity_ids)
                empty.remove(102)
                empty.remove(101)
                self.assertNotEqual(V.fingerprint(self.context(support.RUN_ID, empty, 31337, root_id=root)), real)

    def test_the_run_state_round_trip_keeps_the_root(self):
        editor = self.scene(support.RUN_ID, True)
        ctx = self.context(support.RUN_ID, editor, 0, root_id=editor.entity(100))
        state = ctx.scope.as_dict()
        self.assertEqual((state["rootInstanceId"], state["rootId"]), (0, editor.entity(100)))
        again = BuildContext(editor, fixture.Scope.from_dict(state), 7001, ctx.project_path, {}, "en", "ops", "basic",
                             None)
        self.assertEqual(V.fingerprint(again), V.fingerprint(ctx))
        editor.calls[:] = []
        V.fingerprint(again)
        self.assertEqual(calls_of(editor, "gameobject_get_info")[0], {"entityId": editor.entity(100)})

    def test_a_state_written_before_root_id_existed_finds_the_root_by_path(self):
        editor = self.scene(support.RUN_ID, True)
        ctx = self.context(support.RUN_ID, editor, 0, root_id=editor.entity(100))
        old = dict(ctx.scope.as_dict())
        del old["rootId"], old["rootEntityId"]
        before = V.fingerprint(ctx)
        restored = BuildContext(editor, fixture.Scope.from_dict(old), 7001, ctx.project_path, {}, "en", "ops", "basic",
                                None)
        self.assertIsNone(restored.scope.root_id)
        editor.calls[:] = []
        self.assertEqual(V.fingerprint(restored), before)
        self.assertEqual(calls_of(editor, "gameobject_get_info")[0], {"path": "EV_" + support.RUN_ID})

    def test_a_scope_without_a_canonical_root_id_still_works(self):
        editor = self.scene(support.RUN_ID, False)
        full = self.context(support.RUN_ID, editor, 100)
        bare = types.SimpleNamespace(
            scope=types.SimpleNamespace(run_id=support.RUN_ID, root_iid=100), probe=Probe(editor),
            project_path=full.project_path, A=full.A, R=full.R)
        self.assertEqual(V.fingerprint(bare), V.fingerprint(full))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
