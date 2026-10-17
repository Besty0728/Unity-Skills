"""Offline checks of T5 (prefab variant replacement) against the in-memory editor of fake_ops_t5.

The common checklist comes from pack_offline.OpsTaskTests (prompts render in both languages, one seed one fingerprint,
no role labels in handles and C0, reference / negatives / alternatives on legacy ids and on Unity 6000.4 ids). The tests
below add what is specific to T5: the seeded layout, the semantic base-prefab digest, the wrapped YAML lines of real
variant files, the journal tolerance for prefab asset edits and the 6000.4 path forwarding of the router.
The live selftest against a real editor remains the gate.
"""
import os
import sys
import unittest

from .fake_ops_t5 import FakeEditorT5
from .pack_offline import OpsTaskTests, detail, load_modules
from usk_eval import verifylib as V  # noqa: E402
from usk_eval.rest import SOLVER_AGENT, SkillError  # noqa: E402


def disk(run, asset_path):
    return os.path.join(run.project, *asset_path.split("/"))


class T5Tests(OpsTaskTests, unittest.TestCase):
    TASK_ID = "T5"
    EDITOR = FakeEditorT5
    SEED = 7910
    FAILS_UNTOUCHED = {"V1", "V2", "V4", "V6", "V7", "V9"}

    def test_task_attributes(self):
        task = load_modules("T5")[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         ("T5", "ops", "standard", 2, 20))
        self.assertEqual(dict(task.requires), {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []})

    def test_seeded_layout(self):
        marked, heavy = set(), set()
        for seed in range(7910, 7930):
            run = self.run_for(seed=seed)
            children = run.snap["data"]["children"]
            crates = [(index, state) for index, (_ref, state) in enumerate(children) if state["name"] == "Crate"]
            self.assertEqual([index for index, _state in crates], [1, 2, 4])
            self.assertEqual([state["name"] for _ref, state in children if state["name"] != "Crate"],
                             ["Pallet", "Pallet2"])
            self.assertEqual(sorted(round(state["rotation"]["y"]) for _index, state in crates), [30, 75, 120])
            index, _ref, _state = run.private.replaced(run.snap["data"])
            heavy_index = next(i for i, state in crates if abs(state["scale"]["x"] - 1.2) < 1e-6)
            self.assertIn(index, (1, 2))
            self.assertNotEqual(index, heavy_index)
            marked.add(index)
            heavy.add(heavy_index)
        self.assertEqual(marked, {1, 2})
        self.assertEqual(heavy, {1, 2, 4})

    def test_replaced_crate_needs_a_crate_near_75_degrees(self):
        run = self.run_for()
        data = run.snap["data"]
        self.assertIsNotNone(run.private.replaced(data))
        self.assertIsNone(run.private.replaced({"children": []}))
        turned = {"children": [[ref, dict(state, rotation={"x": 0, "y": 30, "z": 0})]
                               for ref, state in data["children"]]}
        self.assertIsNone(run.private.replaced(turned))

    def test_fixture_uses_the_prefab_and_its_overrides(self):
        run = self.run_for()
        editor = run.editor
        links = [getattr(obj, "prefab", None) for obj in editor.walk()]
        sources = [link["source"] for link in links if link]
        self.assertEqual(sources, [run.handles["basePath"]] * 3)
        self.assertEqual(sorted(link["mass"] for link in links if link and link["mass"] is not None), [9.0])
        self.assertTrue(os.path.isfile(disk(run, run.handles["basePath"])))
        self.assertEqual(len(run.snap["digests"]["base"]), 64)

    def test_public_and_private_digest_agree_and_read_content_not_bytes(self):
        run = self.run_for()
        base = disk(run, run.handles["basePath"])
        public_digest = sys.modules[type(run.task).__module__]._prefab_digest(base)
        self.assertEqual(public_digest, run.private.prefab_digest(base))
        self.assertEqual(public_digest, run.snap["digests"]["base"])
        with open(base, "rb") as handle:
            raw = handle.read()
        with open(base, "wb") as handle:
            handle.write(b"\r\n".join(raw.split(b"\n")) + b"\r\n\r\n")
        self.assertEqual(run.private.prefab_digest(base), public_digest)
        with open(base, "wb") as handle:
            handle.write(raw.replace(b"m_Mass: 5", b"m_Mass: 6"))
        self.assertNotEqual(run.private.prefab_digest(base), public_digest)

    def test_variant_file_has_wrapped_unity_lines_and_reads_back(self):
        run = self.run_for()
        run.solve(run.solutions.reference)
        with open(disk(run, run.private.variant_path(run.build_ctx)), encoding="utf-8") as handle:
            lines = handle.read().split("\n")
        wrapped = [i for i, line in enumerate(lines) if line.endswith(",") and lines[i + 1].strip() == "type: 3}"]
        self.assertTrue(wrapped, "the fake should wrap long flow mappings like Unity")
        docs = V.uyaml.load(disk(run, run.private.variant_path(run.build_ctx)))
        self.assertEqual([doc.type_name for doc in docs], ["PrefabInstance"])

    def test_offline_only_negatives_fail_exactly_their_assertions(self):
        solutions = load_modules("T5")[2]
        for mode in ("legacy", "6000.4"):
            for negative in solutions.EXTRA_NEGATIVES:
                with self.subTest(negative=negative.name, ids=mode):
                    run = self.run_for(entity_mode=mode)
                    card = run.verify(run.solve(negative.apply))
                    self.assertEqual(set(card.failed()), negative.expect_failed, detail(card))

    def test_each_answer_field_is_checked(self):
        reference = load_modules("T5")[2].reference
        wrong = {
            "id": lambda a, run: dict(a, newInstanceId=int(a["newInstanceId"]) + 2),
            "path": lambda a, run: dict(a, variantPath=run.handles["basePath"]),
            "index": lambda a, run: dict(a, siblingIndex=a["siblingIndex"] + 1),
            "flag": lambda a, run: dict(a, declaredComplete="yes"),
            "missing": lambda a, run: {key: value for key, value in a.items() if key != "newInstanceId"},
        }
        for mode in ("legacy", "6000.4"):
            for name, change in wrong.items():
                with self.subTest(field=name, ids=mode):
                    run = self.run_for(entity_mode=mode)
                    card = run.verify(change(run.solve(reference), run))
                    self.assertEqual(set(card.failed()), {"V9"}, detail(card))

    def test_objects_left_under_root_or_depot_change_the_other_objects(self):
        reference = load_modules("T5")[2].reference
        for parent in ("root", "depot"):
            with self.subTest(parent=parent):
                run = self.run_for()
                answer = run.solve(reference)
                holder = run.build_ctx.root if parent == "root" else run.handles["depot"]
                run.editor.call("gameobject_create", dict({"name": "TempHelper"}, **V.address(holder, "parent")),
                                SOLVER_AGENT)
                card = run.verify(answer)
                self.assertEqual(set(card.failed()), {"V5"}, detail(card))

    def test_unreadable_prefab_files_fail_the_assertions_instead_of_the_verifier(self):
        for garbage in (b"", b"not yaml at all\n", b"--- !u!1001 &1\nPrefabInstance:\n  m_Modification\n", b"\xff\xfe\x00"):
            with self.subTest(garbage=garbage[:12]):
                run = self.run_for()
                answer = run.solve(run.solutions.reference)
                for asset in (run.private.variant_path(run.build_ctx), run.handles["basePath"]):
                    with open(disk(run, asset), "wb") as handle:
                        handle.write(garbage)
                card = run.verify(answer)
                self.assertTrue({"V1", "V2", "V3"} <= set(card.failed()), detail(card))

    def test_asset_edit_records_need_the_journal_tolerance(self):
        run = self.run_for()
        answer = run.solve(run.solutions.reference)
        kept = run.private.allowed_change
        run.private.allowed_change = None
        try:
            card = run.verify(answer)
        finally:
            run.private.allowed_change = kept
        self.assertEqual(set(card.failed()), {"V8"}, detail(card))

    def test_journal_tolerance_does_not_cover_scene_objects(self):
        run = self.run_for()
        answer = run.solve(run.solutions.reference)
        run.editor.call("gameobject_create", {"name": "Crate_Heavy"}, SOLVER_AGENT)
        card = run.verify(answer)
        self.assertEqual(set(card.failed()), {"V8"}, detail(card))

    def test_router_forwards_an_entity_id_only_through_a_unique_path(self):
        for mode, ambiguous in (("legacy", False), ("6000.4", True)):
            run = self.run_for(entity_mode=mode)
            client = run.editor.client(SOLVER_AGENT)
            crate = run.handles["children"][1]
            call = lambda: client.write("component_set_property", componentType="Rigidbody", propertyName="mass",
                                        value="3", **V.address(crate))
            if ambiguous:
                with self.assertRaises(SkillError):
                    call()
            else:
                call()
            client.write("gameobject_rename", newName="CrateEdit", **V.address(crate))
            call()


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
