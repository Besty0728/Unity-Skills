"""usk_eval.verifylib number, rotation, reference and state-comparison helpers (v1 GeometryTests ported, extended)."""
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import verifylib as V
from usk_eval.verifylib import Probe


class GeometryTests(unittest.TestCase):
    def test_euler_y90(self):
        self.assertTrue(V.quat_close(V.quat_from_euler(0, 90, 0), (0, 0.70710678, 0, 0.70710678)))

    def test_euler_x90(self):
        self.assertTrue(V.quat_close(V.quat_from_euler(90, 0, 0), (0.70710678, 0, 0, 0.70710678)))

    def test_negated_quaternion_is_same_rotation(self):
        q = V.quat_from_euler(30, 45, 60)
        self.assertTrue(V.quat_close(q, tuple(-x for x in q)))
        self.assertAlmostEqual(V.quat_angle_deg(q, tuple(-x for x in q)), 0.0, places=4)

    def test_small_angle_tolerance(self):
        self.assertFalse(V.quat_close(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 1.0, 0)))
        self.assertTrue(V.quat_close(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 0.05, 0)))
        self.assertTrue(V.quat_close(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 1.0, 0), tol_deg=1.5))

    def test_unity_composition_order(self):
        # Euler(90, 90, 0) = AngleAxis(90, up) * AngleAxis(90, right): known value (0.5, 0.5, -0.5, 0.5)
        self.assertTrue(V.quat_close(V.quat_from_euler(90, 90, 0), (0.5, 0.5, -0.5, 0.5)))

    def test_angle_between_rotations(self):
        quarter = V.quat_angle_deg(V.quat_from_euler(0, 0, 0), V.quat_from_euler(0, 90, 0))
        self.assertAlmostEqual(quarter, 90.0, places=4)
        self.assertAlmostEqual(V.quat_angle_deg((0, 0, 0, 1), (0, 0, 0, 3)), 0.0, places=4)    # not normalised

    def test_quaternions_from_text_dicts_and_lists(self):
        self.assertTrue(V.quat_close("(0, 0, 0, 1)", {"x": 0, "y": 0, "z": 0, "w": 1}))
        self.assertTrue(V.quat_close([0, 0, 0, -1], "0,0,0,1"))

    def test_not_a_quaternion_is_not_close(self):
        for bad in [None, "", "1,2,3", [0, 0, 0], [0, 0, 0, 1, 0], {"x": 0, "y": 0, "z": 0}, "nope", True]:
            with self.subTest(bad=bad):
                self.assertIsNone(V.quat_angle_deg(bad, (0, 0, 0, 1)))
                self.assertFalse(V.quat_close(bad, (0, 0, 0, 1)))
                self.assertFalse(V.quat_close((0, 0, 0, 1), bad))

    def test_non_finite_quaternions_are_never_close(self):
        nan = float("nan")
        self.assertIsNone(V.quat_angle_deg((nan, 0, 0, 1), (0, 0, 0, 1)))
        self.assertFalse(V.quat_close((nan, nan, nan, nan), (nan, nan, nan, nan)))
        self.assertFalse(V.quat_close((float("inf"), 0, 0, 1), (float("inf"), 0, 0, 1)))

    def test_zero_quaternions_do_not_divide_by_zero(self):
        self.assertEqual(V.quat_angle_deg((0, 0, 0, 0), (0, 0, 0, 0)), 180.0)

    def test_identity_rotation(self):
        self.assertTrue(V.identity_rotation((0, 0, 0, 1)))
        self.assertTrue(V.identity_rotation("0, 0, 0, -1"))
        self.assertFalse(V.identity_rotation(V.quat_from_euler(0, 5, 0)))
        self.assertFalse(V.identity_rotation(None))


class VectorTests(unittest.TestCase):
    def test_floats_and_vectors(self):
        self.assertEqual(V.floats("0,0.5,-1e-3"), [0.0, 0.5, -0.001])
        self.assertEqual(V.floats({"x": 1, "y": 2, "z": 3}), [1.0, 2.0, 3.0])
        self.assertTrue(V.vec_close("(1.0, 2.0, 3.0)", {"x": 1, "y": 2, "z": 3.0005}))
        self.assertFalse(V.vec_close("1,2", "1,2,3"))

    def test_floats_of_every_supported_shape(self):
        self.assertEqual(V.floats(5), [5.0])
        self.assertEqual(V.floats(2.5), [2.5])
        self.assertEqual(V.floats((1, 2)), [1.0, 2.0])
        self.assertEqual(V.floats(["1", 2.5]), [1.0, 2.5])
        self.assertEqual(V.floats({"w": 1, "x": 0, "y": 0, "z": 0}), [0.0, 0.0, 0.0, 1.0])
        self.assertEqual(V.floats({"x": 1, "y": 2}), [1.0, 2.0])
        self.assertEqual(V.floats("(1, -2.5, 3e2)"), [1.0, -2.5, 300.0])

    def test_colors(self):
        self.assertEqual(V.floats({"r": 0.8, "g": 0.1, "b": 0.1, "a": 1}), [0.8, 0.1, 0.1, 1.0])
        self.assertTrue(V.vec_close({"r": 0.8, "g": 0.1, "b": 0.1, "a": 1}, (0.8, 0.1, 0.1, 1.0)))
        self.assertFalse(V.vec_close({"r": 0.8, "g": 0.1, "b": 0.1, "a": 1}, (0.8, 0.1, 0.2, 1.0)))
        self.assertTrue(V.close_tuple((0.8, 0.1, 0.1, 1), {"r": 0.8, "g": 0.1, "b": 0.1, "a": 1}))

    def test_nothing_comparable_gives_an_empty_list(self):
        for bad in [None, True, False, "", "no numbers", {}, {"name": "x"}, [1, None], [1, "x"], [[1]], [True, 1],
                    {"x": None}]:
            with self.subTest(bad=bad):
                self.assertEqual(V.floats(bad), [])

    def test_missing_readings_never_equal_each_other(self):
        for a, b in [(None, None), (None, []), ([], []), ("", ""), ({}, {}), (None, (0, 0, 0))]:
            with self.subTest(a=a, b=b):
                self.assertFalse(V.vec_close(a, b))
                self.assertFalse(V.close_tuple(a, b))

    def test_tolerance(self):
        self.assertTrue(V.vec_close((0.25,), (0.75,), tol=0.5))                   # exactly on the limit
        self.assertFalse(V.vec_close((0.25,), (0.76,), tol=0.5))
        self.assertTrue(V.vec_close((0, 0, 0), (0.0009, 0, 0)))
        self.assertFalse(V.vec_close((0, 0, 0), (0.002, 0, 0)))
        self.assertTrue(V.vec_close((0, 0, 0), (0.002, 0, 0), tol=0.01))
        self.assertFalse(V.vec_close((nan_value(),), (nan_value(),)))

    def test_close_tuple(self):
        self.assertTrue(V.close_tuple((1, 2, 3), (1, 2, 3.0005)))
        self.assertFalse(V.close_tuple(None, (1, 2, 3)))
        self.assertFalse(V.close_tuple((1, 2, 3), None))
        self.assertFalse(V.close_tuple((1, 2), (1, 2, 3)))


def nan_value():
    return float("nan")


class ReferenceTests(unittest.TestCase):
    def test_parse_ref(self):
        self.assertEqual(V.parse_ref("-12484:UnityEngine.Rigidbody:R_A"), (-12484, "UnityEngine.Rigidbody", "R_A"))
        self.assertEqual(V.parse_ref("27884:UnityEngine.Material:Lit:2"), (27884, "UnityEngine.Material", "Lit:2"))
        self.assertIsNone(V.parse_ref("null"))
        self.assertIsNone(V.parse_ref(None))

    def test_entity_ids_of_6000_4_are_unsigned_64_bit(self):
        self.assertEqual(V.parse_ref("18446744073709551601:UnityEngine.GameObject:Pivot"),
                         (18446744073709551601, "UnityEngine.GameObject", "Pivot"))

    def test_malformed_references(self):
        for bad in ["", "12", "12:Type", "abc:Type:Name", "1.5:Type:Name", ":Type:Name", "12::Name", 5, []]:
            with self.subTest(bad=bad):
                self.assertIsNone(V.parse_ref(bad))

    def test_ref_pattern_is_exported(self):
        self.assertTrue(V.REF_RE.match("-5:A.B:c"))

    def test_owner_resolves_a_component_reference_to_its_game_object(self):
        editor = support.sample_scene()
        editor.alias(5001, 102)                         # the BoxCollider's own id resolves to the Crate
        probe = Probe(editor)
        self.assertEqual(probe.owner("5001:UnityEngine.BoxCollider:Crate"), 102)
        self.assertEqual(probe.owner("102:UnityEngine.GameObject:Crate"), 102)
        self.assertIsNone(probe.owner("999:UnityEngine.BoxCollider:Gone"))
        self.assertIsNone(probe.owner("null"))


class SameTests(unittest.TestCase):
    def test_equal_values(self):
        self.assertEqual(V.same({"a": [1, 2.0, {"b": "x"}]}, {"a": (1.0, 2, {"b": "x"})}), (True, None))
        self.assertEqual(V.same(None, None), (True, None))
        self.assertEqual(V.same("a", "a"), (True, None))

    def test_float_tolerance(self):
        self.assertEqual(V.same(1.0, 1.0005), (True, None))
        self.assertEqual(V.same(1.0, 1.01)[0], False)
        self.assertEqual(V.same(1.0, 1.01, tol=0.1), (True, None))

    def test_first_difference_is_located(self):
        ok, diff = V.same({"a": {"b": [1, 2, 3]}}, {"a": {"b": [1, 9, 3]}})
        self.assertFalse(ok)
        self.assertEqual(diff, ".a.b[1]: 2 != 9")

    def test_length_and_key_differences(self):
        self.assertEqual(V.same([1, 2], [1, 2, 3]), (False, ": length 2 != 3"))
        ok, diff = V.same({"a": 1}, {"a": 1, "b": 2})
        self.assertFalse(ok)
        self.assertEqual(diff, ".b: None != 2")

    def test_sibling_order_matters(self):
        self.assertFalse(V.same({"children": [1, 2]}, {"children": [2, 1]})[0])

    def test_types_do_not_mix(self):
        self.assertFalse(V.same("1", 1)[0])
        self.assertFalse(V.same([1], {"0": 1})[0])
        self.assertFalse(V.same(None, 0)[0])

    def test_a_bool_is_not_a_number(self):
        for a, b in [(True, 1), (1, True), (False, 0), (0, False), (True, 1.0)]:
            with self.subTest(a=a, b=b):
                self.assertFalse(V.same(a, b)[0])
        self.assertEqual(V.same(True, True), (True, None))

    def test_nan_is_never_the_same(self):
        self.assertFalse(V.same(float("nan"), float("nan"))[0])

    def test_mixed_key_types_do_not_crash(self):
        self.assertEqual(V.same({1: "a", "b": 2}, {1: "a", "b": 2}), (True, None))


class StateTests(unittest.TestCase):
    def test_go_state(self):
        probe = Probe(support.sample_scene())
        state = V.go_state(probe, 102)
        self.assertEqual(state["name"], "Crate")
        self.assertEqual(state["path"], support.R + "/Props/Crate")
        self.assertEqual(state["parentPath"], support.R + "/Props")
        self.assertEqual(state["position"], {"x": 0, "y": 0.5, "z": 0})
        self.assertEqual(state["components"], ["Transform", "BoxCollider"])
        self.assertEqual(state["children"], [])
        self.assertEqual(V.go_state(probe, 101)["children"], [102, 103])
        self.assertNotIn("children", V.go_state(probe, 101, with_children=False))
        self.assertIsNone(V.go_state(probe, 999))

    def test_go_state_detects_changes(self):
        editor = support.sample_scene()
        before = V.go_state(Probe(editor), 102)
        editor.objects[102]["position"] = (0, 1.5, 0)
        self.assertFalse(V.same(before, V.go_state(Probe(editor), 102))[0])

    def test_new_children(self):
        editor = support.sample_scene()
        probe = Probe(editor)
        before = [c["instanceId"] for c in probe.children(101)]
        editor.add(104, "Barrel", parent=101)
        self.assertEqual([c["name"] for c in V.new_children(probe, 101, before)], ["Barrel"])
        self.assertEqual(V.new_children(probe, 101, before + [104]), [])
        self.assertEqual(V.new_children(probe, 999, []), [])

    def test_local_trs(self):
        editor = support.sample_scene()
        editor.serialized[(102, "Transform")] = {"m_LocalPosition": "(0, 0.5, 0)", "m_LocalRotation": "(0, 0, 0, 1)",
                                                 "m_LocalScale": "(1, 1, 1)"}
        trs = V.local_trs(Probe(editor), 102)
        self.assertEqual(trs, {"position": [0.0, 0.5, 0.0], "rotation": [0.0, 0.0, 0.0, 1.0], "scale": [1.0, 1.0, 1.0]})
        self.assertTrue(V.identity_rotation(trs["rotation"]))
        self.assertEqual(V.local_trs(Probe(editor), 999), {"position": [], "rotation": [], "scale": []})

    def test_material_state_and_color(self):
        editor = support.sample_scene()
        editor.materials["Assets/M.mat"] = {
            "shader": "Universal Render Pipeline/Lit", "keywords": ["_B", "_A"], "renderQueue": 2000,
            "properties": {"colors": [{"name": "_BaseColor", "value": {"r": 0.8, "g": 0.1, "b": 0.1, "a": 1}}],
                           "floats": [{"name": "_Metallic", "value": 0.0}]}}
        editor.assets["Assets/M.mat"] = {"guid": "abc123"}
        probe = Probe(editor)
        state = V.material_state(probe, "Assets/M.mat")
        self.assertEqual(state["keywords"], ["_A", "_B"])
        self.assertEqual(state["guid"], "abc123")
        self.assertEqual(state["floats"], {"_Metallic": 0.0})
        self.assertEqual(V.material_color(probe, "Assets/M.mat", "_BaseColor"), (0.8, 0.1, 0.1, 1.0))
        self.assertEqual(V.material_color(probe, state, "_BaseColor"), (0.8, 0.1, 0.1, 1.0))
        self.assertIsNone(V.material_color(probe, state, "_Missing"))
        self.assertEqual(V.material_color(probe, {"colors": {"_C": {"r": 0.5}}}, "_C"), (0.5, 0.0, 0.0, 0.0))
        self.assertIsNone(V.material_color(probe, {"colors": {"_C": "0.5,0,0,1"}}, "_C"))
        self.assertIsNone(V.material_state(probe, "Assets/Gone.mat"))
        self.assertIsNone(V.material_color(probe, "Assets/Gone.mat", "_BaseColor"))


class AnswerTests(unittest.TestCase):
    class Ctx(object):
        def __init__(self, answer):
            self.answer = answer

    def test_answer_access(self):
        ctx = self.Ctx({"total": 8, "n": None})
        self.assertEqual(V.answer(ctx, "total"), 8)
        self.assertIsNone(V.answer(ctx, "missing"))
        self.assertEqual(V.answer(ctx, "missing", 0), 0)
        self.assertIsNone(V.answer(ctx, "n", 5))

    def test_a_missing_or_malformed_answer_reads_as_empty(self):
        for answer in [None, [], "text", 5]:
            with self.subTest(answer=answer):
                self.assertEqual(V.answer(self.Ctx(answer), "total", "d"), "d")

    def test_as_int(self):
        for value, expected in [(5, 5), (-3, -3), ("12", 12), (" 7 ", 7), (8.0, 8), ("-4", -4), (True, None),
                                (False, None), (2.5, None), ("2.5", None), ("x", None), (None, None), ([1], None),
                                ({}, None), (float("nan"), None), (float("inf"), None), ("", None),
                                (18446744073709551601, 18446744073709551601)]:
            with self.subTest(value=value):
                self.assertEqual(V.as_int(value), expected)

    def test_norm_path(self):
        self.assertEqual(V.norm_path("  A\\B/C "), "A/B/C")
        self.assertEqual(V.norm_path(None), "")
        for bad in [5, ["A"], {"a": 1}, True]:
            with self.subTest(bad=bad):
                self.assertEqual(V.norm_path(bad), "")


class WorldFromLocalTests(unittest.TestCase):
    def assertVec(self, actual, expected):
        self.assertEqual(len(actual), 3)
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, places=6)

    def test_scale_only(self):
        self.assertVec(V.world_from_local((1, 2, 3), (0, 0, 0), (2, 2, 2), (1, 0, 0)), (3, 2, 3))

    def test_yaw_of_90_degrees_turns_right_into_minus_forward(self):
        self.assertVec(V.world_from_local((1, 2, 3), (0, 90, 0), (2, 2, 2), (1, 0, 0)), (1, 2, 1))

    def test_unity_euler_order_z_then_x_then_y(self):
        # Euler(0, 90, 90): roll 90 about z first (right -> up), then yaw 90 about y (up stays up)
        self.assertVec(V.world_from_local((0, 0, 0), (0, 90, 90), (1, 1, 1), (1, 0, 0)), (0, 1, 0))

    def test_accepts_dicts_and_text(self):
        self.assertVec(V.world_from_local({"x": 1, "y": 0, "z": 0}, "0,0,0", {"x": 1, "y": 1, "z": 1}, "(0, 2, 0)"),
                       (1, 2, 0))


class PublicSurfaceTests(unittest.TestCase):
    NAMES = ["Scorecard", "Probe", "floats", "vec_close", "quat_from_euler", "quat_angle_deg", "quat_close",
             "parse_ref", "REF_RE", "go_state", "material_state", "material_color", "close_tuple", "same",
             "new_children", "local_trs", "identity_rotation", "answer", "as_int", "norm_path", "world_from_local",
             "uyaml", "canonical_digest", "structure", "hierarchy_signature", "asset_listing", "asset_inventory",
             "component_values", "fingerprint",
             "scope_violations", "adopted_paths", "under", "object_id", "same_id", "address",
             "installed_packages", "has_text_mesh_pro", "active_input_handler"]

    def test_contract_5_8_names_are_exported(self):
        for name in self.NAMES:
            with self.subTest(name=name):
                self.assertTrue(hasattr(V, name))
                self.assertIn(name, V.__all__)

    def test_nothing_else_is_advertised(self):
        self.assertEqual(sorted(V.__all__), sorted(self.NAMES))

    def test_scope_functions_are_the_scope_modules(self):
        from usk_eval import scope
        self.assertIs(V.scope_violations, scope.scope_violations)
        self.assertIs(V.adopted_paths, scope.adopted_paths)
        self.assertIs(V.under, scope.under)

    def test_uyaml_api(self):
        for name in ["load", "loads", "by_type", "index", "meta_guid", "material_colors", "scalar", "Doc"]:
            with self.subTest(name=name):
                self.assertTrue(hasattr(V.uyaml, name))

    def test_sources_only_use_the_standard_library_and_the_runner_core(self):
        import re
        allowed = {"json", "math", "os", "re", "usk_eval"}
        for path in sorted(support.VERIFYLIB_SOURCES.glob("*.py")):
            text = path.read_text(encoding="utf-8-sig")
            for match in re.finditer(r"^(?:from|import)\s+([A-Za-z_][\w.]*|\.+[\w.]*)", text, re.M):
                module = match.group(1)
                if module.startswith("."):
                    continue
                with self.subTest(file=path.name, module=module):
                    self.assertIn(module.split(".")[0], allowed)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
