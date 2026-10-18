"""usk_eval.verifylib.snapshot: digests, subtree structure, hierarchy signature, asset listing and inventory, component
values, fingerprint."""
import hashlib
import json
import os
import tempfile
import unittest

try:
    from . import test_verifylib_support as support
except ImportError:
    import test_verifylib_support as support

from usk_eval import fixture, store, submodule
from usk_eval import verifylib as V
from usk_eval.task_api import BuildContext
from usk_eval.verifylib import Probe


class CanonicalDigestTests(unittest.TestCase):
    def test_formula_of_contract_5_8(self):
        obj = {"b": [1, 2, {"z": None, "a": "x"}], "a": 1.5}
        text = json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
        self.assertEqual(V.canonical_digest(obj), hashlib.sha256(text.encode("utf-8")).hexdigest())

    def test_one_definition_for_runner_and_verifiers(self):
        self.assertIs(V.canonical_digest, store.canonical_digest)

    def test_key_order_does_not_matter_but_list_order_does(self):
        self.assertEqual(V.canonical_digest({"a": 1, "b": 2}), V.canonical_digest({"b": 2, "a": 1}))
        self.assertNotEqual(V.canonical_digest([1, 2]), V.canonical_digest([2, 1]))

    def test_text_is_hashed_as_utf8_not_escaped(self):
        self.assertEqual(V.canonical_digest({"k": "测试"}),
                         hashlib.sha256('{"k":"测试"}'.encode("utf-8")).hexdigest())

    def test_digest_is_lowercase_hex_sha256(self):
        digest = V.canonical_digest({})
        self.assertEqual(len(digest), 64)
        self.assertEqual(digest, digest.lower())


class StructureTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene()
        self.probe = Probe(self.editor)

    def test_depth_first_nodes_in_the_contract_shape(self):
        nodes = V.structure(self.probe, [100])
        self.assertEqual([n["id"] for n in nodes], [100, 101, 102, 103])
        crate = nodes[2]
        self.assertEqual(list(crate), ["id", "entityId", "instanceId", "name", "active", "tag", "layer", "path",
                                       "components", "children"])
        self.assertEqual((crate["id"], crate["entityId"], crate["instanceId"]), (102, "102", 102))
        self.assertEqual(crate["name"], "Crate")
        self.assertEqual(crate["path"], support.R + "/Props/Crate")
        self.assertEqual(crate["components"], [["Transform", None], ["BoxCollider", True]])
        self.assertEqual(crate["children"], [])
        self.assertEqual(nodes[0]["children"], [101])
        self.assertEqual(nodes[1]["children"], [102, 103])
        self.assertFalse(nodes[3]["active"])
        self.assertEqual(nodes[3]["components"], [["Transform", None], ["Light", False]])

    def test_it_is_json_serialisable(self):
        json.dumps(V.structure(self.probe, [100]), allow_nan=False)

    def test_several_roots_keep_their_order(self):
        self.editor.add(200, "Reserve")
        self.assertEqual([n["id"] for n in V.structure(self.probe, [200, 101])], [200, 101, 102, 103])

    def test_one_root_may_be_given_as_a_number(self):
        self.assertEqual(V.structure(self.probe, 101), V.structure(self.probe, [101]))

    def test_siblings_are_walked_in_sibling_order(self):
        self.editor.add(104, "Barrel", parent=101)
        self.editor.objects[101]["children"] = [104, 103, 102]
        self.assertEqual([n["id"] for n in V.structure(self.probe, [101])], [101, 104, 103, 102])

    def test_a_missing_object_keeps_a_node_with_empty_fields(self):
        nodes = V.structure(self.probe, [999])
        self.assertEqual(nodes, [{"id": 999, "entityId": None, "instanceId": None, "name": None, "active": None,
                                  "tag": None, "layer": None, "path": None, "components": [], "children": []}])

    def test_a_deleted_object_differs_from_its_snapshot_instead_of_aborting(self):
        before = V.structure(self.probe, [100])
        self.editor.remove(102)
        after = V.structure(self.probe, [100])
        self.assertFalse(V.same(before, after)[0])
        self.assertNotEqual(V.canonical_digest(before), V.canonical_digest(after))
        self.editor.objects[100]["children"].append(102)           # stale child id: an empty node, not an error
        again = V.structure(self.probe, [100])
        self.assertEqual([n["id"] for n in again], [100, 101, 103, 102])
        self.assertIsNone(again[-1]["name"])

    def test_every_kind_of_edit_changes_the_digest(self):
        base = V.canonical_digest(V.structure(self.probe, [100]))
        edits = [("rename", lambda: self.editor.objects[102].update(name="Box")),
                 ("deactivate", lambda: self.editor.objects[102].update(active=False)),
                 ("tag", lambda: self.editor.objects[102].update(tag="Respawn")),
                 ("layer", lambda: self.editor.objects[102].update(layer="UI")),
                 ("component", lambda: self.editor.objects[102]["components"].append("Rigidbody")),
                 ("disable", lambda: self.editor.objects[102]["disabled"].add("BoxCollider")),
                 ("reorder", lambda: self.editor.objects[101]["children"].reverse()),
                 ("reparent", lambda: (self.editor.objects[101]["children"].remove(102),
                                       self.editor.objects[100]["children"].append(102)))]
        for label, edit in edits:
            with self.subTest(edit=label):
                editor = support.sample_scene()
                probe = Probe(editor)
                self.editor, self.probe = editor, probe
                edit()
                self.assertNotEqual(V.canonical_digest(V.structure(probe, [100])), base)

    def test_unchanged_scene_gives_the_same_digest(self):
        self.assertEqual(V.canonical_digest(V.structure(self.probe, [100])),
                         V.canonical_digest(V.structure(Probe(support.sample_scene()), [100])))

    def test_a_cycle_in_the_answers_terminates(self):
        self.editor.objects[102]["children"].append(100)
        ids = [n["id"] for n in V.structure(self.probe, [100])]
        self.assertEqual(ids, [100, 101, 102, 103])

    def pose(self, iid, position="0,0,0", rotation="0,0,0,1", scale="1,1,1"):
        self.editor.serialized[(iid, "Transform")] = {"m_LocalPosition": position, "m_LocalRotation": rotation,
                                                      "m_LocalScale": scale}

    def test_poses_add_the_local_pose_of_every_node(self):
        for iid in (100, 101, 103):
            self.pose(iid)
        self.pose(102, position="0,0.5,0", scale="2,1,1")
        nodes = V.structure(self.probe, [100], poses=True)
        self.assertEqual(list(nodes[2])[-1], "local")
        self.assertEqual(nodes[2]["local"], {"position": [0.0, 0.5, 0.0], "rotation": [0.0, 0.0, 0.0, 1.0],
                                             "scale": [2.0, 1.0, 1.0]})
        self.assertNotIn("local", V.structure(self.probe, [100])[2])
        json.dumps(nodes, allow_nan=False)

    def test_with_poses_a_move_turn_or_rescale_changes_the_digest(self):
        for iid in (100, 101, 102, 103):
            self.pose(iid)
        base = V.canonical_digest(V.structure(self.probe, [100], poses=True))
        self.assertEqual(V.canonical_digest(V.structure(self.probe, [100])),
                         V.canonical_digest(V.structure(Probe(support.sample_scene()), [100])))
        for label, values in (("move", {"position": "0,0,1"}), ("turn", {"rotation": "0,0.7071068,0,0.7071068"}),
                              ("rescale", {"scale": "1,1,0.5"})):
            with self.subTest(edit=label):
                self.pose(102, **values)
                self.assertNotEqual(V.canonical_digest(V.structure(self.probe, [100], poses=True)), base)
                self.pose(102)

    def test_with_poses_a_missing_object_has_no_pose(self):
        nodes = V.structure(self.probe, [999], poses=True)
        self.assertIsNone(nodes[0]["local"])
        self.assertIsNone(nodes[0]["name"])


class HierarchySignatureTests(unittest.TestCase):
    def test_signature_has_names_order_and_values_but_no_ids(self):
        sig = V.hierarchy_signature(Probe(support.sample_scene()), 100)
        self.assertEqual(sig["name"], support.R)
        self.assertEqual([c["name"] for c in sig["children"][0]["children"]], ["Crate", "Lamp"])
        crate = sig["children"][0]["children"][0]
        self.assertEqual(crate["pos"], {"x": 0, "y": 0.5, "z": 0})
        self.assertEqual(crate["components"], ["Transform", "BoxCollider"])
        self.assertEqual(sorted(crate), ["active", "children", "components", "layer", "name", "pos", "rot", "scale",
                                         "tag"])
        self.assertNotIn("instanceId", json.dumps(sig))

    def test_same_scene_with_other_ids_has_the_same_signature(self):
        def scene(offset):
            editor = support.FakeEditor()
            editor.add(offset, support.R)
            editor.add(offset + 1, "A", parent=offset, position=(1, 2, 3))
            editor.add(offset + 2, "B", parent=offset + 1)
            return Probe(editor), offset
        (probe_a, root_a), (probe_b, root_b) = scene(100), scene(9000)
        self.assertEqual(V.hierarchy_signature(probe_a, root_a), V.hierarchy_signature(probe_b, root_b))

    def test_values_are_rounded_to_four_decimals_and_negative_zero_is_zero(self):
        editor = support.FakeEditor()
        editor.add(1, "Root", position=(-0.0, 0.123456789, -0.00001), rotation=(0, 90.00004, 0), scale=(1, 1, 1))
        sig = V.hierarchy_signature(Probe(editor), 1)
        self.assertEqual(sig["pos"], {"x": 0.0, "y": 0.1235, "z": 0.0})
        self.assertEqual(json.dumps(sig["pos"]), '{"x": 0.0, "y": 0.1235, "z": 0.0}')
        self.assertEqual(sig["rot"]["y"], 90.0)

    def test_a_visible_move_changes_the_signature(self):
        editor = support.sample_scene()
        before = V.hierarchy_signature(Probe(editor), 100)
        editor.objects[102]["position"] = (0, 0.51, 0)
        self.assertNotEqual(V.hierarchy_signature(Probe(editor), 100), before)

    def test_depth_limit(self):
        editor = support.FakeEditor()
        editor.add(1, "n0")
        for i in range(1, 12):
            editor.add(i + 1, "n%d" % i, parent=i)
        sig = V.hierarchy_signature(Probe(editor), 1)
        depth = 0
        while "children" in sig:
            sig = sig["children"][0]
            depth += 1
        self.assertEqual((depth, sig["name"]), (8, "n8"))
        shallow = V.hierarchy_signature(Probe(editor), 1, limit=2)
        self.assertEqual(shallow["children"][0]["children"][0]["name"], "n2")
        self.assertNotIn("children", shallow["children"][0]["children"][0])

    def test_a_missing_root_is_an_empty_node(self):
        sig = V.hierarchy_signature(Probe(support.FakeEditor()), 5)
        self.assertIsNone(sig["name"])
        self.assertEqual(sig["children"], [])


class AssetListingTests(unittest.TestCase):
    def test_relative_sorted_paths_without_meta_and_run_scene(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = os.path.join(tmp, "Assets", "__UnitySkillsEval", "r1")
            for rel in ["b.mat", "a.mat", "a.mat.meta", "Eval_r1.unity", "Eval_r1.unity.meta", "Sub/z.txt",
                        "Sub/Deep/y.cs", "Sub/Deep/y.cs.meta", "Sub/Eval_x.txt", "Eval.txt"]:
                path = os.path.join(root, *rel.split("/"))
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w") as handle:
                    handle.write("x")
            os.makedirs(os.path.join(root, "EmptyFolder"))
            listing = V.asset_listing(tmp, "Assets/__UnitySkillsEval/r1")
            self.assertEqual(listing, ["Eval.txt", "Sub/Deep/y.cs", "Sub/z.txt", "a.mat", "b.mat"])

    def test_a_missing_folder_lists_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(V.asset_listing(tmp, "Assets/Nope"), [])


class AssetInventoryTests(unittest.TestCase):
    ROOT = "Assets/__UnitySkillsEval/r1"

    def make(self, tmp, files, folders=()):
        root = os.path.join(tmp, *self.ROOT.split("/"))
        for rel, text in files.items():
            path = os.path.join(root, *rel.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w") as handle:
                handle.write(text)
        for rel in folders:
            os.makedirs(os.path.join(root, *rel.split("/")), exist_ok=True)
        return root

    @staticmethod
    def sha(text):
        return hashlib.sha256(text.encode("utf-8")).hexdigest()

    def test_folders_files_and_meta_files_with_their_hashes(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.make(tmp, {"a.mat": "m", "a.mat.meta": "guid: 1", "Sub.meta": "folderAsset: yes",
                            "Sub/z.txt": "z", "Eval_r1.unity": "s", "Eval_r1.unity.meta": "guid: 2"},
                      folders=("Empty",))
            self.assertEqual(V.asset_inventory(tmp, self.ROOT),
                             [["Empty/", None], ["Sub.meta", self.sha("folderAsset: yes")], ["Sub/", None],
                              ["Sub/z.txt", self.sha("z")], ["a.mat", self.sha("m")],
                              ["a.mat.meta", self.sha("guid: 1")]])

    def test_an_empty_folder_a_meta_edit_and_a_content_edit_all_show(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = self.make(tmp, {"T.asset": "tier: 1", "T.asset.meta": "guid: 1\nlabels: []"})
            base = V.asset_inventory(tmp, self.ROOT)
            self.assertEqual(V.asset_listing(tmp, self.ROOT), ["T.asset"])
            os.makedirs(os.path.join(root, "Spare"))
            self.assertNotEqual(V.asset_inventory(tmp, self.ROOT), base)
            self.assertEqual(V.asset_listing(tmp, self.ROOT), ["T.asset"])
            os.rmdir(os.path.join(root, "Spare"))
            self.assertEqual(V.asset_inventory(tmp, self.ROOT), base)
            for rel, text in (("T.asset.meta", "guid: 1\nlabels:\n- Loot"), ("T.asset", "tier: 2")):
                with self.subTest(edit=rel):
                    with open(os.path.join(root, rel), "r") as handle:
                        before = handle.read()
                    with open(os.path.join(root, rel), "w") as handle:
                        handle.write(text)
                    self.assertNotEqual(V.asset_inventory(tmp, self.ROOT), base)
                    with open(os.path.join(root, rel), "w") as handle:
                        handle.write(before)
            self.assertEqual(V.asset_inventory(tmp, self.ROOT), base)

    def test_a_file_the_predicate_turns_down_keeps_its_row_without_a_hash(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = self.make(tmp, {"P.prefab": "a", "P.prefab.meta": "guid: 3"})
            keep = V.asset_inventory(tmp, self.ROOT, hashed=lambda rel: not rel.endswith(".prefab"))
            self.assertEqual(keep, [["P.prefab", None], ["P.prefab.meta", self.sha("guid: 3")]])
            with open(os.path.join(root, "P.prefab"), "w") as handle:
                handle.write("b")
            self.assertEqual(V.asset_inventory(tmp, self.ROOT, hashed=lambda rel: not rel.endswith(".prefab")), keep)
            os.remove(os.path.join(root, "P.prefab"))
            self.assertNotEqual(V.asset_inventory(tmp, self.ROOT, hashed=lambda rel: not rel.endswith(".prefab")),
                                keep)

    def test_a_missing_folder_lists_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(V.asset_inventory(tmp, "Assets/Nope"), [])


class ComponentValuesTests(unittest.TestCase):
    def setUp(self):
        self.editor = support.sample_scene()
        self.probe = Probe(self.editor)
        self.editor.serialized[(102, "Transform")] = {"m_LocalPosition": "0,0.5,0"}
        self.editor.serialized[(102, "BoxCollider")] = {"m_Size": "1,1,1", "m_IsTrigger": "False"}

    def test_every_component_type_with_its_values(self):
        self.assertEqual(V.component_values(self.probe, 102),
                         {"Transform": {"m_LocalPosition": "0,0.5,0"},
                          "BoxCollider": {"m_Size": "1,1,1", "m_IsTrigger": "False"}})

    def test_types_and_skip_narrow_the_set(self):
        self.assertEqual(V.component_values(self.probe, 102, types=("BoxCollider",)),
                         {"BoxCollider": {"m_Size": "1,1,1", "m_IsTrigger": "False"}})
        self.assertEqual(V.component_values(self.probe, 102, skip=("Transform",)),
                         {"BoxCollider": {"m_Size": "1,1,1", "m_IsTrigger": "False"}})

    def test_a_value_change_shows_and_an_unreadable_component_is_none(self):
        before = V.component_values(self.probe, 102)
        self.editor.serialized[(102, "BoxCollider")]["m_IsTrigger"] = "True"
        self.assertNotEqual(V.component_values(self.probe, 102), before)
        self.assertEqual(V.component_values(self.probe, 103), {"Transform": None, "Light": None})

    def test_a_missing_object_has_no_values(self):
        self.assertEqual(V.component_values(self.probe, 999), {})


class FingerprintTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="usk-eval-fp-")
        self.addCleanup(__import__("shutil").rmtree, self.tmp, True)

    def build(self, run_id, base, crate_pos=(0, 0.5, 0), files=("Mat.mat", "Mat.mat.meta", "Sub/data.txt"),
              components=("Transform", "BoxCollider"), project=None):
        editor = support.FakeEditor()
        editor.add(base, "EV_" + run_id)
        editor.add(base + 1, "Props", parent=base)
        editor.add(base + 2, "Crate", parent=base + 1, position=crate_pos, components=components)
        scope = fixture.Scope(run_id, base)
        project = project or tempfile.mkdtemp(prefix="p-", dir=self.tmp)
        for rel in tuple(files) + ("Eval_%s.unity" % run_id,):
            path = os.path.join(project, *(scope.asset_root.split("/") + rel.split("/")))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w") as handle:
                handle.write(rel)
        return BuildContext(editor, scope, 7001, project, {}, "en", "ops", "basic", None)

    def fingerprint(self, ctx):
        return submodule("verifylib").fingerprint(ctx)

    def test_two_builds_of_one_seed_agree_whatever_their_run_ids_and_object_ids(self):
        first = self.build(support.RUN_ID, 100)
        second = self.build(support.OTHER_RUN_ID, 5000)
        self.assertEqual(self.fingerprint(first), self.fingerprint(second))
        self.assertRegex(self.fingerprint(first), r"^[0-9a-f]{64}$")

    def test_the_digest_covers_hierarchy_and_assets(self):
        base = self.fingerprint(self.build(support.RUN_ID, 100))
        variants = {
            "moved": dict(crate_pos=(0, 0.6, 0)),
            "component": dict(components=("Transform", "BoxCollider", "Rigidbody")),
            "missing asset": dict(files=("Mat.mat", "Mat.mat.meta")),
            "extra asset": dict(files=("Mat.mat", "Mat.mat.meta", "Sub/data.txt", "More.txt")),
            "renamed asset": dict(files=("Mat2.mat", "Mat.mat.meta", "Sub/data.txt")),
        }
        for label, kwargs in variants.items():
            with self.subTest(variant=label):
                self.assertNotEqual(self.fingerprint(self.build(support.OTHER_RUN_ID, 5000, **kwargs)), base)

    def test_the_run_id_is_masked_everywhere(self):
        ctx = self.build(support.RUN_ID, 100)
        ctx.probe.c.objects[102]["name"] = "Crate_" + support.RUN_ID
        other = self.build(support.OTHER_RUN_ID, 5000)
        other.probe.c.objects[5002]["name"] = "Crate_" + support.OTHER_RUN_ID
        self.assertEqual(self.fingerprint(ctx), self.fingerprint(other))

    def test_negative_zero_and_rounding_noise_do_not_split_builds(self):
        a = self.build(support.RUN_ID, 100, crate_pos=(-0.0, 0.5, 0.0))
        b = self.build(support.OTHER_RUN_ID, 5000, crate_pos=(0.0, 0.50000001, -0.00001))
        self.assertEqual(self.fingerprint(a), self.fingerprint(b))

    def test_run_scene_and_meta_files_are_not_part_of_it(self):
        base = self.fingerprint(self.build(support.RUN_ID, 100))
        ctx = self.build(support.OTHER_RUN_ID, 5000, files=("Mat.mat", "Sub/data.txt"))
        extra = os.path.join(ctx.project_path, *(ctx.A.split("/") + ["Sub", "data.txt.meta"]))
        with open(extra, "w") as handle:
            handle.write("guid: x")
        self.assertEqual(self.fingerprint(ctx), base)

    def test_it_reads_the_context_as_attempt_py_hands_it_over(self):
        ctx = self.build(support.RUN_ID, 100)
        self.assertEqual(self.fingerprint(ctx), V.fingerprint(ctx))
        for needed in ("probe", "scope", "project_path", "A"):
            self.assertTrue(hasattr(ctx, needed))

    def test_a_vanished_root_still_has_a_fingerprint(self):
        ctx = self.build(support.RUN_ID, 100)
        ctx.scope.root_iid = 31337
        self.assertRegex(self.fingerprint(ctx), r"^[0-9a-f]{64}$")


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
