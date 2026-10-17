"""Offline checks of T10 (asset reorganisation keeping GUIDs) against the in-memory editor of fake_ops_t10: the common
checklist of pack_offline.OpsTaskTests plus the facts specific to this task."""
import json
import os
import unittest

from .fake_ops_t10 import FakeEditorT10
from .pack_offline import OpsTaskTests, detail, load_modules
from usk_eval import pack  # noqa: E402


class StateOf(object):
    """The part of a verify context that plan() reads."""

    def __init__(self, run):
        self.c0, self.handles = run.snap["data"], run.handles


class T10Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "T10"
    EDITOR = FakeEditorT10
    SEED = 8112
    FAILS_UNTOUCHED = {"H1", "H3", "H7", "H8"}

    def test_task_attributes(self):
        task = load_modules("T10")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("T10", "ops", "standard", 2, 20))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, []))

    def test_fixture_shape(self):
        run = self.run_for()
        files = run.snap["data"]["files"]
        incoming, materials = run.handles["incoming"], run.handles["materials"]
        under = [path for path in files if path.startswith(incoming + "/") and path.endswith(".mat")]
        self.assertEqual(len(under), 35)
        nested = [path for path in under if path[len(incoming) + 1:].startswith("Vendor/Old/")]
        self.assertEqual(len(nested), 8)
        self.assertIn(incoming + "/Misc/Grip.physicMaterial", files)
        self.assertIn(materials + "/B/Bolt.mat", files)
        self.assertIn(run.scope.asset_root + "/Elsewhere/Brick.mat", files)
        self.assertEqual(len(set(files.values())), len(files))
        self.assertEqual(len(run.handles["renderers"]), 12)
        slots = run.snap["data"]["slots"]
        self.assertEqual(sorted(len(value) for value in slots.values()), [1] * 9 + [2] * 3)
        plan = run.private.plan(StateOf(run))
        clash = [source for source, dest in plan.items() if os.path.basename(source) != os.path.basename(dest)]
        self.assertEqual(len(clash), 4)

    def test_every_clashing_name_is_used_by_a_renderer(self):
        run = self.run_for()
        names = {value.rsplit(":", 1)[1] for values in run.snap["data"]["slots"].values() for value in values}
        for name in ("Brick", "Tile", "Bolt"):
            self.assertIn(name, names)

    def test_destinations_follow_the_prompt_rules(self):
        private = load_modules("T10")[1]
        out = private.destinations(
            ["I/Batch02/Brick.mat", "I/Batch01/Brick.mat", "I/Misc/Tile.mat", "I/Batch02/Tile.mat",
             "I/Vendor/Old/Tile.mat", "I/Vendor/Old/Bolt.mat", "I/Batch01/moss.mat"], ["M/B/Bolt.mat"], "M")
        self.assertEqual(out, {"I/Batch01/Brick.mat": "M/B/Brick.mat", "I/Batch02/Brick.mat": "M/B/Brick_2.mat",
                               "I/Batch02/Tile.mat": "M/T/Tile.mat", "I/Misc/Tile.mat": "M/T/Tile_2.mat",
                               "I/Vendor/Old/Tile.mat": "M/T/Tile_3.mat", "I/Vendor/Old/Bolt.mat": "M/B/Bolt_2.mat",
                               "I/Batch01/moss.mat": "M/M/moss.mat"})
        backwards = private.destinations(["I/A/Tile.mat", "I/B/Tile.mat"], [], "M", reverse=True)
        self.assertEqual(backwards, {"I/B/Tile.mat": "M/T/Tile.mat", "I/A/Tile.mat": "M/T/Tile_2.mat"})

    def test_c0_holds_no_intended_destination(self):
        run = self.run_for()
        text = json.dumps(run.snap)
        for dest in run.private.plan(StateOf(run)).values():
            self.assertNotIn(dest, text)

    def test_answer_schema_asks_only_for_the_moves(self):
        schema = load_modules("T10")[0].answer_schema()
        self.assertEqual(list(schema["properties"]), ["moves"])

    def test_moves_stay_far_below_the_file_change_cap(self):
        run = self.run_for()
        run.solve(run.solutions.reference)
        sizes = [sum(len(entry["payload"].get(key) or []) for key in ("imported", "deleted", "moved"))
                 for entry in run.editor.entries if entry["type"] == "file_changes" and entry["seq"] > run.cursor0]
        self.assertTrue(sizes)
        self.assertLess(max(sizes), 100)

    def test_reference_passes_at_other_seeds(self):
        for seed in (8213, 8314, 8415):
            with self.subTest(seed=seed):
                run = self.run_for(seed=seed)
                card = run.verify(run.solve(run.solutions.reference))
                self.assertTrue(card.strict, detail(card))

    def test_a_malformed_report_fails_only_the_report(self):
        run = self.run_for()
        good = run.solve(run.solutions.reference)
        for answer in ({"moves": "all", "declaredComplete": True}, {"moves": [1, 2], "declaredComplete": True},
                       {"declaredComplete": True}, dict(good, declaredComplete="yes"), dict(good, moves=[])):
            with self.subTest(answer=answer):
                self.assertEqual(set(run.verify(answer).failed()), {"H8"})

    def test_the_report_may_be_unordered_use_backslashes_and_declare_incomplete(self):
        run = self.run_for()
        good = run.solve(run.solutions.reference)
        rows = [{"from": row["from"].replace("/", "\\"), "to": row["to"]} for row in reversed(good["moves"])]
        card = run.verify({"moves": rows, "declaredComplete": False})
        self.assertTrue(card.strict, detail(card))

# Producer:Betsy
