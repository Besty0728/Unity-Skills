"""Offline checks of the challenge tasks OC5 and OC7.

OC7 runs end to end against YardFakeEditor (pack_fake_editor plus tags, parent batches and tag queries): builds,
prompts, one seed one fingerprint, seed-driven structure, and the reference, every negative and every alternative
through the private verifier. OC5's layout derivation (copied into the private verifier), prompt rendering and start
values are checked here over many seeds; its end-to-end run against the prefab model is in
test_pack_challenge_prefabs. The live selftest against a real editor remains the gate.
"""
import json
import random
import unittest

from .pack_offline import OpsFakeEditor, OpsTaskTests, load_modules, PACK_ROOT
from usk_eval import pack, prompts  # noqa: E402

import os


class YardFakeEditor(OpsFakeEditor):
    """pack_fake_editor plus what OC7 touches: tags (set in batches, filtered in queries) and parent batches."""

    def _batch(self, results):
        return {"success": True, "totalItems": len(results), "successCount": len(results), "failCount": 0,
                "results": results}

    def _skill_gameobject_set_tag_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            obj = self.find(item)
            before, obj.tag = obj.tag, item["tag"]
            self._change(agent, self._identity(obj, kind="property_changed", targetType="GameObject",
                                               property="m_TagString", previousValue=before, value=obj.tag))
            results.append({"target": obj.name, "entityId": obj.eid, "success": True, "tag": obj.tag})
        return self._batch(results)

    def _skill_gameobject_set_parent_batch(self, agent, args):
        results = []
        for item in json.loads(args["items"]):
            res = self._skill_gameobject_set_parent(agent, item)
            results.append(dict(res, target=res.get("child")))
        return self._batch(results)

    def _skill_gameobject_find(self, agent, args):
        """With a tag: active objects only (FindGameObjectsWithTag), the default page of 50."""
        tag = args.get("tag")
        if not tag:
            return OpsFakeEditor._skill_gameobject_find(self, agent, args)
        out = [self._row(obj) for obj in self.walk() if obj.tag == tag and self.active_in_hierarchy(obj)]
        limit = int(args.get("limit") or 50)
        return {"count": min(len(out), limit), "totalMatched": len(out), "truncated": len(out) > limit,
                "objects": out[:limit]}

    def _skill_batch_query_gameobjects(self, agent, args):
        query = json.loads(args["queryJson"]) if isinstance(args["queryJson"], str) else args["queryJson"]
        res = OpsFakeEditor._skill_batch_query_gameobjects(self, agent, args)
        tag = query.get("tag")
        if not tag:
            return res
        rows = [row for row in res["objects"] if self.objects[row["entityId"]].tag == tag]
        limit = int(query.get("limit") or 500)
        return {"success": True, "count": min(len(rows), limit), "totalMatched": len(rows),
                "truncated": len(rows) > limit, "objects": rows[:limit]}


class OC7Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "OC7"
    EDITOR = YardFakeEditor
    SEED = 7708
    TIER = "challenge"
    FAILS_UNTOUCHED = {"H1", "H2", "H3", "H4", "H5", "H8", "H10"}

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         (self.TASK_ID, "ops", "challenge", 1, 30))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, []))

    def test_population_and_seed_driven_structure(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        c0 = run.snap["data"]
        dest, moved, home, untagged = private.layout(c0)
        self.assertEqual((len(c0["objects"]), len(c0["containers"]), len(moved), len(home), len(untagged)),
                         (154, 6, 144, 6, 4))
        rotated = [key for key in moved if not private.aligned(c0["objects"][key]["rotation"],
                                                               c0["containers"][dest[key]]["rotation"])]
        self.assertEqual(len(rotated), 10)
        self.assertEqual(sum(1 for key in moved if c0["objects"][key]["active"] is False), 6)
        names = [state["name"] for state in c0["objects"].values()]
        self.assertGreaterEqual(len(names) - len(set(names)), 6)      # at least one pair of twins per suffix, twice
        for suffix in {private.suffix(name) for name in c0["containers"]}:
            pair = [name for name in c0["containers"] if private.suffix(name) == suffix]
            self.assertEqual(len(pair), 2, suffix)
            first = {c0["objects"][key]["name"] for key in dest if dest[key] == pair[0]}
            second = {c0["objects"][key]["name"] for key in dest if dest[key] == pair[1]}
            self.assertGreaterEqual(len(first & second), 2, suffix)
        misplaced = [key for key in moved
                     if c0["objects"][key]["parentPath"] != c0["folders"]["loose"]["path"]]
        self.assertEqual(len(misplaced), 2)
        for name, spec in c0["containers"].items():
            members = [key for key in dest if dest[key] == name]
            self.assertEqual(len(members), 25, name)
            natural = sorted(members, key=lambda key: private.natural_key(c0["objects"][key]["name"]))
            plain = sorted(members, key=lambda key: c0["objects"][key]["name"])
            self.assertNotEqual(natural, plain, name)
            scale = spec["scale"]
            self.assertTrue(len({round(scale[axis], 6) for axis in "xyz"}) > 1, name)
        other = self.run_for(seed=7809)
        self.assertNotEqual(run.fingerprint, other.fingerprint)
        self.assertNotEqual(set(run.handles["containers"]), set(other.handles["containers"]))
        self.assertNotEqual(os.path.basename(run.handles["loosePath"]), os.path.basename(other.handles["loosePath"]))

    def test_world_scale_tolerance_is_relative(self):
        """R5 states a relative 0.001: 0.5 accepts 0.50049 and rejects 0.5009, 2 accepts 2.0019 and rejects 2.0021."""
        private = load_modules(self.TASK_ID)[1]
        self.assertTrue(private._close_scale([0.50049, 1.0, 2.0], [0.5, 1.0, 2.0]))
        self.assertFalse(private._close_scale([0.5009, 1.0, 2.0], [0.5, 1.0, 2.0]))
        self.assertTrue(private._close_scale([0.5, 1.0, 2.0019], [0.5, 1.0, 2.0]))
        self.assertFalse(private._close_scale([0.5, 1.0, 2.0021], [0.5, 1.0, 2.0]))
        self.assertFalse(private._close_scale([0.5, 1.0], [0.5, 1.0, 2.0]))


class OC5LayoutTests(unittest.TestCase):
    SEEDS = range(7001, 7301)

    def modules(self):
        return load_modules("OC5")

    def test_task_attributes(self):
        task = self.modules()[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("OC5", "ops", "challenge", 1, 20))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, ["com.unity.ugui"]))

    def test_layout_spec_agrees_between_public_and_private(self):
        task_module, private, _solutions = self.modules()
        public = __import__(type(task_module).__module__)
        seen = set()
        for seed in self.SEEDS:
            spec = public.layout_spec(seed)
            self.assertEqual(spec, private.layout_spec(seed), seed)
            self.assertEqual(spec, public.layout_spec(seed), seed)
            self.assertEqual(sorted(spec["rects"]), sorted(public.ELEMENTS))
            self.assertEqual(sorted(spec["order"]), sorted(public.ELEMENTS))
            seen.add(json.dumps(spec, sort_keys=True))
        self.assertGreater(len(seen), 100)

    def test_start_values_never_match_the_layout(self):
        task_module = self.modules()[0]
        public = __import__(type(task_module).__module__)
        for seed in self.SEEDS:
            spec = public.layout_spec(seed)
            rng = random.Random(seed)
            self.assertNotEqual(public.start_order(rng, spec), spec["order"])
            for name in public.ELEMENTS:
                start, rect = public.start_rect(rng), spec["rects"][name]
                same = (start["anchorMinX"], start["anchorMinY"]) == tuple(rect["anchorMin"]) and \
                    (start["anchoredPosX"], start["anchoredPosY"]) == tuple(rect["anchoredPosition"]) and \
                    (start["sizeDeltaX"], start["sizeDeltaY"]) == tuple(rect["sizeDelta"])
                self.assertFalse(same, (seed, name))
                self.assertNotEqual((start["sizeDeltaX"], start["sizeDeltaY"]), tuple(rect["sizeDelta"]))

    def test_prompts_render_in_both_languages(self):
        task_module = self.modules()[0]
        public = __import__(type(task_module).__module__)

        class Stub(object):
            seed = 7607
            handles = {"prefabPath": "Assets/__UnitySkillsEval/r1/Prefabs/HUD.prefab",
                       "hudPath": "EV_r1/MainCanvas/HUD"}

        values = task_module.prompt_vars(Stub())
        builtins = {"session": "s", "k": 1, "n": 1, "track": "ops", "tier": "challenge", "project": "/p",
                    "unity": "2022.3.62f2", "pipeline": "URP", "port": 8091, "instance": "Fake_0000",
                    "R": "EV_r1", "A": "Assets/__UnitySkillsEval/r1", "scene": "Assets/__UnitySkillsEval/r1/Eval_r1.unity",
                    "minutes": task_module.minutes}
        variables = prompts.merge_vars(builtins, values)
        for lang in prompts.LANGS:
            with open(os.path.join(PACK_ROOT, "tasks", "OC5", "prompt.%s.md" % lang), "rb") as handle:
                raw = handle.read()
            self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
            text = prompts.render_requirements(prompts.parse_prompt(raw.decode("utf-8")), variables)
            for value in values.values():
                self.assertIn(str(value), text)
            self.assertEqual(text.count("\n   "), len(public.ELEMENTS))
            schema = prompts.render_schema(prompts.with_declared(task_module.answer_schema()), lang)
            self.assertIn('"declaredComplete": <boolean>', schema)

# Producer:Betsy
