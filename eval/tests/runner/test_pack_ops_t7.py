"""Offline checks of T7 (a repair pass over the crates of R/Yard) against FakeEditorT7.

The shared checklist (prompts, handles, fingerprint, reference/negatives/alternatives on URP with legacy and 6000.4 ids,
untouched fixture) comes from pack_offline.OpsTaskTests; the tests here pin the layout over many seeds, the fractions
and the decisions of the port.
"""
import json
import unittest

from .fake_ops_t7 import FakeEditorT7
from .pack_offline import OpsTaskTests, detail, load_modules
from usk_eval import verifylib as V  # noqa: E402

SEEDS = range(8000, 8024)


class T7Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "T7"
    EDITOR = FakeEditorT7
    SEED = 8011
    FAILS_UNTOUCHED = {"Y1", "Y4", "Y8", "Y9", "Y10"}

    def solutions(self):
        return load_modules("T7")[2]

    def nodes(self, run):
        return {node["path"]: node for node in run.snap["data"]["nodes"]}

    def matching(self, run):
        yard = run.handles["yardPath"] + "/"
        return [node for node in run.snap["data"]["nodes"] if node["path"].startswith(yard)
                and run.private._matches({run.private._key(node["id"]): node}, run.handles["yardPath"])]

    def test_task_minutes(self):
        self.assertEqual(load_modules("T7")[0].minutes, 20)

    def test_layout_counts_hold_for_every_seed(self):
        for seed in SEEDS:
            run = self.run_for(seed=seed)
            nodes = run.snap["data"]["nodes"]
            with self.subTest(seed=seed):
                self.assertEqual(len(nodes), 114)                                  # R, Yard, Archive and 111 below
                yard = run.handles["yardPath"] + "/"
                self.assertEqual(sum(1 for node in nodes if node["path"].startswith(yard)), 110)
                matches = self.matching(run)
                self.assertEqual(len(matches), 73)
                self.assertEqual(sum(1 for node in matches if not any(c[0] == "BoxCollider"
                                                                      for c in node["components"])), 21)
                self.assertEqual(sum(1 for node in matches if node["active"] is False), 6)
                masses = run.snap["data"]["mass"]
                wrong = [node for node in matches if str(node["id"]) in masses
                         and abs(masses[str(node["id"])] - 4.5) > 1e-4]
                self.assertEqual(len(wrong), 11)
                self.assertEqual(sum(1 for node in matches if str(node["id"]) in masses), 30)
                names = [node["name"] for node in matches]
                self.assertEqual(names.count("Crate_B07"), 2)
                self.assertIn(run.handles["yardPath"] + "/Row_A/Crate_A01", [node["path"] for node in matches])

    def test_no_two_siblings_share_a_name_ignoring_case(self):
        for seed in SEEDS:
            run = self.run_for(seed=seed, entity_mode="6000.4")
            seen = set()
            for node in run.snap["data"]["nodes"]:
                with self.subTest(seed=seed, path=node["path"]):
                    self.assertNotIn(node["path"].lower(), seen)
                seen.add(node["path"].lower())

    def test_case_variants_never_share_a_row_with_the_crate_they_resemble(self):
        for seed in SEEDS:
            run = self.run_for(seed=seed)
            paths = [node["path"] for node in run.snap["data"]["nodes"]]
            variants = [path for path in paths if path.lower().endswith("/crate_a01") and path.split("/")[-1] != "Crate_A01"]
            with self.subTest(seed=seed):
                self.assertEqual(len(variants), 3)
                self.assertEqual(len({path.rsplit("/", 1)[0] for path in variants}), 3)
                self.assertNotIn(run.handles["yardPath"] + "/Row_A", {path.rsplit("/", 1)[0] for path in variants})

    def test_the_reference_passes_on_many_seeds(self):
        solutions = self.solutions()
        for seed in SEEDS[::4]:
            run = self.run_for(seed=seed)
            card = run.verify(run.solve(solutions.reference))
            with self.subTest(seed=seed):
                self.assertTrue(card.strict, detail(card))

    def test_the_negatives_hold_on_other_seeds(self):
        solutions = self.solutions()
        for seed in (8003, 8017):
            for negative in solutions.NEGATIVES:
                run = self.run_for(seed=seed)
                card = run.verify(run.solve(negative.apply))
                with self.subTest(seed=seed, negative=negative.name):
                    self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))

    def test_half_the_work_earns_half_the_fractions(self):
        run = self.run_for()
        solutions = self.solutions()

        def half(ctx):
            found = solutions._scan(ctx)
            add, fix = solutions._plan(found)
            add, fix = add[:len(add) // 2], fix[:len(fix) // 2]
            ctx.c.write("component_add_batch", items=json.dumps([{"path": item["path"], "componentType": "BoxCollider"}
                                                                for item in add]))
            ctx.c.write("component_set_property_batch", items=json.dumps([
                {"path": item["path"], "componentType": "Rigidbody", "propertyName": "mass", "value": 4.5}
                for item in fix]))
            return solutions._answer(found, add, fix)

        card = run.verify(run.solve(half))
        rows = {row["id"]: row for row in card.as_dict()["assertions"]}
        self.assertAlmostEqual(rows["Y1"]["fraction"], 10 / 21.0, places=3)
        self.assertAlmostEqual(rows["Y4"]["fraction"], 5 / 11.0, places=3)
        self.assertEqual(set(card.failed()), {"Y1", "Y4"}, detail(card))

    def test_declared_complete_may_be_false(self):
        run = self.run_for()
        answer = run.solve(self.solutions().reference)
        answer["declaredComplete"] = False
        self.assertTrue(run.verify(answer).strict)

    def test_declared_complete_must_be_a_boolean(self):
        run = self.run_for()
        answer = run.solve(self.solutions().reference)
        answer["declaredComplete"] = "yes"
        self.assertEqual(set(run.verify(answer).failed()), {"Y10"})

    def test_answer_fields_are_each_checked(self):
        solutions = self.solutions()
        cases = {"missing_count": ("matchedCount", None, {"Y10"}), "fraction_count": ("matchedCount", 72.5, {"Y10"}),
                 "missing_added": ("addedBoxCollider", None, {"Y8"}), "scalar_added": ("addedBoxCollider", 5, {"Y8"}),
                 "junk_added": ("addedBoxCollider", ["x"], {"Y8"}), "missing_fixed": ("fixedMass", None, {"Y9"}),
                 "duplicate_fixed": ("fixedMass", "dup", {"Y9"})}
        for label, (key, value, failed) in cases.items():
            run = self.run_for()
            answer = run.solve(solutions.reference)
            if label == "duplicate_fixed":
                value = answer["fixedMass"] + answer["fixedMass"][:1]
            if value is None:
                answer.pop(key)
            else:
                answer[key] = value
            with self.subTest(case=label):
                self.assertEqual(set(run.verify(answer).failed()), failed)

    def test_ids_in_the_answer_may_be_entity_id_texts_or_numbers(self):
        run = self.run_for(entity_mode="6000.4")
        answer = run.solve(self.solutions().reference)
        answer["addedBoxCollider"] = [str(item) for item in answer["addedBoxCollider"]]
        self.assertTrue(run.verify(answer).strict)

    def test_a_renamed_match_is_still_a_match_for_the_repair_checks(self):
        run = self.run_for()
        solutions = self.solutions()

        def renamed_first(ctx):
            found = solutions._scan(ctx)
            answer = solutions._apply(ctx, found)
            ctx.c.write("gameobject_rename", newName="Moved", **V.address(found[3]["ref"]))
            return answer

        card = run.verify(run.solve(renamed_first))
        self.assertEqual(set(card.failed()), {"Y7"}, detail(card))

    def test_a_deleted_object_fails_the_unchanged_checks_but_not_the_scope_check(self):
        run = self.run_for()
        solutions = self.solutions()

        def deleted(ctx):
            answer = solutions.reference(ctx)
            filler = next(node for node in ctx.task.snapshot(ctx)["data"]["nodes"] if node["name"].startswith("Sign_"))
            ctx.c.write("gameobject_delete", **V.address(filler["id"]))
            return answer

        card = run.verify(run.solve(deleted))
        self.assertEqual(set(card.failed()), {"Y6", "Y7"}, detail(card))

    def test_a_second_rigidbody_cannot_be_added(self):
        run = self.run_for()

        def twice(ctx):
            ref = next(node["id"] for node in ctx.task.snapshot(ctx)["data"]["nodes"]
                       if node["name"].startswith("Barrel_"))
            ctx.c.write("component_add", componentType="Rigidbody", **V.address(ref))
            return ctx.probe.component_count(ref, "Rigidbody")

        self.assertEqual(run.solve(twice), 1)

    def test_the_batch_items_do_not_accept_an_entity_id(self):
        run = self.run_for(entity_mode="6000.4")

        def by_entity(ctx):
            ref = next(node["id"] for node in ctx.task.snapshot(ctx)["data"]["nodes"] if node["name"] == "Archive")
            ctx.c.write("component_add_batch", items=json.dumps([dict(V.address(ref), componentType="BoxCollider")]))

        with self.assertRaises(Exception):
            run.solve(by_entity)

# Producer:Betsy
