"""Offline checks of DB4 (follow camera) against fake_db4.FakeEditorDB4.

Runs the common DevTaskTests checklist (prompts, handles, fingerprint, untouched fixture, reference twice, every
negative with its exact expected-failure set and every alternative on URP, BuiltIn and Unity 6000.4 ids, the
scenario's shape) plus DB4's seed agreement, the teleport scenario, the edit-mode scenario for compile errors, the
source parsers, the verdicts at low frame rates (smoothing left on must still lag at 5 fps) and a second seed.
"""
import json
import math
import unittest

from .fake_db4 import FakeEditorDB4
from .pack_offline import load_modules
from .test_pack_dev_db import DevTaskTests


class DB4Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB4"
    EDITOR = FakeEditorDB4
    SEED = 10536
    FAILS_UNTOUCHED = {"J1", "J2", "J3", "J4", "J5", "J8", "J9", "J12"}

    def test_spec_agrees_and_never_takes_the_code_default(self):
        task, private, _solutions = load_modules(self.TASK_ID)
        public = __import__(type(task).__module__)
        self.assertEqual(public.DEFAULT_OFFSET, private.DEFAULT_OFFSET)
        self.assertEqual(public.DEFAULT_SMOOTH_SPEED, private.DEFAULT_SMOOTH_SPEED)
        for seed in range(7001, 7201):
            offset = public.spec(seed)["offset"]
            self.assertEqual(offset, private.spec(seed)["offset"], seed)
            self.assertNotEqual(tuple(float(v) for v in offset), private.DEFAULT_OFFSET)
            moves = private.spec(seed)["moves"]
            self.assertEqual(len(moves), private.MOVE_COUNT)
            # every teleport jumps at least 4 m, so a lerping camera is far off a few frames later
            for before, after in zip([(0, 0, 0)] + moves, moves):
                self.assertGreaterEqual(math.dist(before, after), 4.0, (seed, before, after))

    def test_scenario_teleports_the_followed_vehicle_and_samples_both(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        run.verify(run.solve(load_modules(self.TASK_ID)[2].reference))
        steps = run.spec["steps"]
        self.assertEqual([step["kind"] for step in steps],
                         ["wait_frames", "read", "read"] + ["invoke", "wait_frames", "read", "read"] * 3 + ["assert"])
        reads = [step for step in steps if step["kind"] == "read"]
        self.assertEqual({step["skill"] for step in reads}, {"gameobject_get_info"})
        rover, cam = private.V.address(run.handles["rover"]), private.V.address(run.handles["cam"])
        self.assertEqual([step["args"] for step in reads], [rover, cam] * 4)
        origin = run.snap["data"]["origin"]
        for step, move in zip([step for step in steps if step["kind"] == "invoke"], private.spec(self.SEED)["moves"]):
            args = dict(step["args"])
            self.assertEqual((args.pop("componentType"), args.pop("methodName")), ("UnityEngine.Transform",
                                                                                   "set_position"))
            point = [float(v) for v in json.loads(args.pop("arguments"))[0].split(",")]
            self.assertEqual(args, rover)
            self.assertTrue(all(abs(p - (o + m)) < 1e-3 for p, o, m in zip(point, origin, move)))
        self.assertTrue(run.scenario["passed"], run.scenario)

    def test_compile_errors_give_an_edit_mode_check(self):
        run = self.run_for()
        _task, private, solutions = load_modules(self.TASK_ID)
        run.verify(run.solve(solutions.compile_error))
        self.assertEqual(run.spec["executionMode"], "edit")
        self.assertEqual([step["kind"] for step in run.spec["steps"]], ["assert"])
        self.assertFalse(run.scenario["passed"])

    def test_source_parsers(self):
        private = load_modules(self.TASK_ID)[1]
        cls = "CameraFollow_Talon"

        def report(fields, body="void LateUpdate() { }"):
            text = "using UnityEngine;\npublic class %s : MonoBehaviour\n{\n%s\n%s\n}\n" % (cls, fields, body)
            return private.strip_code(text)

        good = ("public Transform target;\npublic Vector3 offset = new Vector3(0f, 3f, -6f);\n"
                "public bool smoothing = true;\npublic float smoothSpeed = 2f;")
        self.assertTrue(private.fields_ok(private.fields_report(report(good), cls)))
        variants = (
            "[SerializeField] private Transform target;\n[Tooltip(\"a ] b\")] [SerializeField] private Vector3 "
            "offset = new(0, 3, -6);\n[SerializeField] private bool smoothing = true;\n"
            "[SerializeField, Min(0f)] private float smoothSpeed = 2.0f;",
            "public UnityEngine.Transform target;\npublic UnityEngine.Vector3 offset = new UnityEngine.Vector3(0.0f, "
            "3.0f, -6.0f);\n[Header(\"x\")] public bool smoothing = true;\n[Range(0f, 10f)] public float smoothSpeed "
            "= 2;",
        )
        variants += (good.replace("new Vector3(0f, 3f, -6f)", "new Vector3(x: 0f, y: 3f, z: -6f)"),
                     good.replace("-6f", "- 6f"), good.replace("-6f", "- 6F"))
        for fields in variants:
            with self.subTest(fields=fields):
                self.assertTrue(private.fields_ok(private.fields_report(report(fields), cls)))
        wrong = (good.replace("-6f", "-8f"), good.replace("= true", "= false"), good.replace("2f", "5f"),
                 good.replace("Transform target", "GameObject target"),
                 good.replace("public Vector3", "private Vector3"),
                 "// " + good.replace("\n", "\n// "))
        for fields in wrong:
            with self.subTest(fields=fields):
                self.assertFalse(private.fields_ok(private.fields_report(report(fields), cls)))
        self.assertTrue(private.declares_late_update(report(good, "private void LateUpdate ( ) { }")))
        self.assertTrue(private.declares_class(report(good).replace("public class", "public sealed class"), cls))
        self.assertTrue(private.declares_class(report(good).replace("public class", "public sealed partial class"), cls))
        stub = "void Update() { transform.position = target.position; } void LateUpdate() { }"
        self.assertFalse(private.declares_late_update(report(good, stub)))
        self.assertFalse(private.declares_late_update(report(good, stub.replace("Update()", "FixedUpdate()", 1))))
        self.assertTrue(private.declares_late_update(report(good, "void Update() { if (a) { b(); } } void LateUpdate() "
                                                                   "{ transform.position = p; }")))
        lerp = "void LateUpdate() { transform.position = Vector3.Lerp(a, p, Time.deltaTime * smoothSpeed); }"
        self.assertTrue(private.lerps_toward_point(report(good, lerp)))
        self.assertFalse(private.lerps_toward_point(report(good, "void LateUpdate() { transform.position = p; }")))
        self.assertFalse(private.declares_late_update(report(good, "void Update() { } // void LateUpdate()")))
        self.assertFalse(private.declares_late_update(report(good, "/* void LateUpdate() { } */ void Update() { }")))

    def test_verdicts_hold_at_low_frame_rates(self):
        """Every case keeps its verdict at 20 and at 5 player frames per second: a camera left smoothing still lags
        a few frames after each teleport, and an exact one is exact at any rate."""
        solutions = load_modules(self.TASK_ID)[2]
        cases = [("reference", solutions.reference, set())] + \
            [(name, fn, set()) for name, fn in solutions.ALTERNATIVES] + \
            [(negative.name, negative.apply, negative.expect_failed) for negative in solutions.NEGATIVES]
        for fps in (20.0, 5.0):
            for name, fn, failed in cases:
                with self.subTest(fps=fps, case=name):
                    run = self.run_for()
                    run.editor.fps = fps
                    self.assertEqual(set(run.verify(run.solve(fn)).failed()), failed)

    def test_second_seed_rearranges_structure_and_keeps_verdicts(self):
        first, second = self.run_for(seed=self.SEED), self.run_for(seed=7004)
        self.assertNotEqual(first.fingerprint, second.fingerprint)
        self.assertNotEqual(first.handles["script"], second.handles["script"])
        self.assertNotEqual(first.handles["roverPath"], second.handles["roverPath"])
        solutions = load_modules(self.TASK_ID)[2]
        run = self.run_for(seed=7004)
        self.assertTrue(run.verify(run.solve(solutions.reference)).strict)
        for negative in solutions.NEGATIVES:
            with self.subTest(seed=7004, negative=negative.name):
                run = self.run_for(seed=7004)
                self.assertEqual(set(run.verify(run.solve(negative.apply)).failed()), negative.expect_failed)

# Producer:Betsy
