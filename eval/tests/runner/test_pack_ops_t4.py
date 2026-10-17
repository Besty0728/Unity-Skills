"""Offline checks of T4 (two cubes sharing materials: new body material and glow material) against FakeEditorT4.

The shared checklist (prompts, handles, fingerprint, reference/negatives/alternatives on URP with legacy and 6000.4 ids,
untouched fixture) comes from pack_offline.OpsTaskTests; the tests here pin the slot logic and the decisions of the port.
"""
import unittest

from .fake_ops_t4 import FakeEditorT4
from .pack_offline import Offline, OpsTaskTests, detail, load_modules
from usk_eval import pack  # noqa: E402


class T4Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "T4"
    EDITOR = FakeEditorT4
    SEED = 7809
    FAILS_UNTOUCHED = {"M2", "M6", "M7", "M8", "M9", "M11"}

    def solutions(self):
        return load_modules("T4")[2]

    def test_task_attributes(self):
        task = load_modules("T4")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("T4", "ops", "standard", 2, 15))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, []))

    def test_material_assign_writes_slot_zero_only(self):
        solutions = self.solutions()
        run = self.run_for()

        def assign(ctx):
            path = solutions._new_body(ctx)
            ctx.c.write("material_assign", materialPath=path, **solutions.V.address(ctx.handles["car"]))
            return path

        path = run.solve(assign)
        slots = run.private.slot_paths(run.build_ctx, run.handles["car"], "Car")
        self.assertEqual(slots["paths"], [path, run.handles["trim"]])
        self.assertEqual(slots["names"], ["CarBody", "Trim"])

    def test_slot_one_is_judged_by_asset_path_not_by_name(self):
        solutions = self.solutions()
        run = self.run_for()

        def decoy_in_slot(ctx):
            h = ctx.handles
            solutions._slot(ctx, h["car"], 0, solutions._new_body(ctx))
            solutions._new_glow(ctx)
            other = solutions._new_glow(ctx, path=ctx.asset("Elsewhere", "%s.mat" % h["glowName"]))
            solutions._slot(ctx, h["car"], 1, other)
            return solutions._answer(ctx)

        card = run.verify(run.solve(decoy_in_slot))
        self.assertEqual(set(card.failed()), {"M6"}, detail(card))

    def test_a_stray_material_with_the_glow_name_is_not_penalised(self):
        solutions = self.solutions()
        run = self.run_for()

        def with_stray(ctx):
            answer = solutions.reference(ctx)
            solutions._new_glow(ctx, path=ctx.asset("Elsewhere", "%s.mat" % ctx.handles["glowName"]))
            return answer

        card = run.verify(run.solve(with_stray))
        self.assertTrue(card.strict, detail(card))

    def test_the_new_body_material_may_sit_anywhere_under_a(self):
        solutions = self.solutions()
        run = self.run_for()

        def deep_body(ctx):
            h, c = ctx.handles, ctx.c
            path = ctx.asset("Paint", "Red", "CarBody.mat")
            c.write("material_create", name="CarBody", savePath=path)
            c.write("material_set_color", path=path, propertyName=ctx.env["colorProperty"], r=0.8, g=0.1, b=0.1, a=1)
            solutions._slot(ctx, h["car"], 0, path)
            solutions._slot(ctx, h["car"], 1, solutions._new_glow(ctx))
            return solutions._answer(ctx)

        card = run.verify(run.solve(deep_body))
        self.assertTrue(card.strict, detail(card))

    def test_a_body_material_outside_a_fails_the_material_and_the_scope_checks(self):
        solutions = self.solutions()
        run = self.run_for()

        def outside(ctx):
            h, c = ctx.handles, ctx.c
            path = "Assets/Elsewhere/CarBody.mat"
            c.write("material_create", name="CarBody", savePath=path)
            c.write("material_set_color", path=path, propertyName=ctx.env["colorProperty"], r=0.8, g=0.1, b=0.1, a=1)
            solutions._slot(ctx, h["car"], 0, path)
            solutions._slot(ctx, h["car"], 1, solutions._new_glow(ctx))
            return solutions._answer(ctx)

        card = run.verify(run.solve(outside))
        self.assertEqual(set(card.failed()), {"M2", "M10"}, detail(card))

    def test_a_glow_material_with_another_name_fails_only_the_asset_check(self):
        solutions = self.solutions()
        run = self.run_for()

        def renamed(ctx):
            h, c = ctx.handles, ctx.c
            solutions._slot(ctx, h["car"], 0, solutions._new_body(ctx))
            c.write("material_create", name="Glow", savePath=h["glowPath"])
            c.write("material_set_emission", path=h["glowPath"], r=1, g=0.5, b=0, intensity=2)
            solutions._slot(ctx, h["car"], 1, h["glowPath"])
            return solutions._answer(ctx)

        card = run.verify(run.solve(renamed))
        self.assertEqual(set(card.failed()), {"M7"}, detail(card))

    def test_build_fails_loudly_when_slot_paths_cannot_be_resolved(self):
        class Blind(FakeEditorT4):
            def _skill_scene_materials(self, agent, args):
                return {"success": True, "totalMaterials": 0, "totalShaders": 0, "shaders": []}

        with self.assertRaises(ValueError):
            Offline(self, "T4", Blind, self.SEED)

    def test_a_missing_declared_complete_flag_only_fails_the_report(self):
        solutions = self.solutions()
        run = self.run_for()
        answer = run.solve(solutions.reference)
        answer["declaredComplete"] = "yes"
        card = run.verify(answer)
        self.assertEqual(set(card.failed()), {"M11"}, detail(card))

# Producer:Betsy
