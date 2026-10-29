"""Offline unit tests for DC6 (coin pickups and a score manager) against fake_dc6: the pack_offline checklist (prompts,
handles, fingerprint, untouched fixture, reference/negatives/alternatives with legacy and Unity 6000.4 entity ids), the
scenario shape, other seeds, the seed-copied coin values, the fixture geometry the touched set rests on, and the
teardown of the layer names and collision pairs."""
import math
import unittest

from .fake_dc6 import FakeEditorDC6
from .pack_offline import load_modules
from .test_pack_dev_dc import DevChallengeTests


class DC6Tests(DevChallengeTests, unittest.TestCase):
    TASK_ID = "DC6"
    EDITOR = FakeEditorDC6
    SEED = 10536                 # the manifest's public seed
    MINUTES = 40
    FAILS_UNTOUCHED = {"C1", "C2", "C3", "C4", "C5", "C12", "C13", "C14", "C15"}

    def test_private_values_match_public(self):
        public = __import__(load_modules("DC6")[0].__class__.__module__, fromlist=["coin_values"])
        private = load_modules("DC6")[1]
        for seed in range(300):
            self.assertEqual(public.coin_values(seed), private.coin_values(seed))

    def test_fixture_geometry_across_seeds(self):
        """Four coins lie on the path (one reached only on the way back), three are at least 2.5 m from it; no coin
        touches the player at the start; the final waypoint is not on the outbound leg; the coins start on Default
        and the pair (player layer, Default) is the only disabled pair of the task layers."""
        private = load_modules("DC6")[1]
        for seed in (10536, 7001, 11, 12, 13, 14, 15, 16):
            run = self.run_for(seed=seed)
            c0, h = run.snap["data"], run.handles
            points = private.path_points(c0)
            rows = private.coin_rows(c0)
            hits = private.touched(c0)
            with self.subTest(seed=seed):
                self.assertEqual((len(rows), len(hits)), (7, 4))
                for _ref, path, name, pos in rows:
                    gap = min(private._segment_distance(a, b, pos) for a, b in zip(points, points[1:]))
                    self.assertLess(gap, 1e-6) if path in hits else self.assertGreaterEqual(gap, 2.5)
                    self.assertGreater(math.dist(pos, points[0]), 1.5)
                    self.assertTrue(name.startswith(("GoldCoin_", "SilverCoin_")))
                outbound = private._segment_distance(points[0], points[1], points[-1])
                self.assertGreater(outbound, 3.0)
                back_only = [pos for _ref, path, _name, pos in rows if path in hits and
                             private._segment_distance(points[0], points[1], pos) > 1.5]
                self.assertEqual(len(back_only), 1)
                tiers = {name.split("Coin_")[0] for _ref, path, name, _pos in rows if path in hits}
                self.assertEqual(tiers, {"Gold", "Silver"})
                self.assertTrue(all(state["layer"] == "Default" for _ref, state, _parts in c0["coins"]))
                self.assertEqual((c0["player"]["tag"], c0["box"]["tag"]), ("Player", "Player"))
                self.assertEqual({c0["player"]["layer"], c0["box"]["layer"]}, {h["actorLayer"]["name"]})
                closed = "%d,%d" % tuple(sorted((0, h["actorLayer"]["index"])))
                self.assertEqual(sorted(key for key, on in c0["collision"].items() if not on), [closed])

    def test_teardown_restores_layer_names_and_pairs(self):
        run = self.run_for()
        self.assertTrue({28, 29} <= set(run.editor.layer_names))
        run.teardown()
        self.assertFalse({28, 29} & set(run.editor.layer_names))
        self.assertTrue(all(run.editor.collides(a, b) for a, b in load_modules("DC6")[0].layer_pairs))

    def test_double_entries_count_twice_without_a_flag(self):
        """The two Player colliders enter a coin in the same step: without a flag each pass adds the value twice."""
        solutions, private = load_modules("DC6")[2], load_modules("DC6")[1]
        run = self.run_for()
        run.verify(run.solve(solutions.no_guard))
        score = [step for step in run.scenario["steps"] if step["kind"] == "read"][0]["actual"]["properties"]
        got = int(next(row["value"] for row in score if row["propertyPath"] == "score"))
        self.assertGreater(got, 2 * private.total_for(run.snap["data"], run.seed))

# Producer:Betsy
