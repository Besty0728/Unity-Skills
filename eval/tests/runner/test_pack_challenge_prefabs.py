"""Offline checks of the prefab challenge tasks OC1, OC3 and OC5 against fake_ops_prefabs.PrefabFakeEditor.

Each task runs end to end the way the runner runs it: build, prompts, one seed one fingerprint, the untouched fixture,
and the reference, every negative (with its exact set of failed assertions) and every alternative through the private
verifier, with legacy and Unity 6000.4 ids. The fake models prefab assets as YAML files, nested instances, variants,
overrides and their propagation; native serialization, import and propagation remain for the live selftest, the gate.
"""
import unittest

from .fake_ops_prefabs import PrefabFakeEditor
from .pack_offline import OpsTaskTests, load_modules
from usk_eval import pack  # noqa: E402


class PrefabTaskTests(OpsTaskTests):
    EDITOR = PrefabFakeEditor
    TIER = "challenge"
    MINUTES = None
    PACKAGES = []

    def test_task_attributes(self):
        task = load_modules(self.TASK_ID)[0]
        self.assertEqual((task.id, task.track, task.tier, task.case_version, task.minutes),
                         (self.TASK_ID, "ops", "challenge", 1, self.MINUTES))
        requires = pack.normalize_requires(task.requires)
        self.assertEqual((requires["pipeline"], requires["mode"], requires["dedicated"], requires["packages"]),
                         ("URP", "bypass", True, self.PACKAGES))


class OC5Tests(PrefabTaskTests, unittest.TestCase):
    TASK_ID = "OC5"
    SEED = 7607
    MINUTES = 20
    PACKAGES = ["com.unity.ugui"]
    FAILS_UNTOUCHED = {"P1", "P2", "P3", "P4", "P7"}

    def test_the_asset_and_its_instance(self):
        run = self.run_for()
        c0 = run.snap["data"]
        private = load_modules(self.TASK_ID)[1]
        spec = private.layout_spec(self.SEED)
        self.assertNotEqual(private._root_children(c0["asset"]), spec["order"])
        self.assertFalse(c0["asset"]["nodes"]["Notice"]["activeSelf"])
        self.assertEqual([row[0] for row in c0["canvas"]["values"]], ["Canvas", "CanvasScaler", "GraphicRaycaster",
                                                                       "RectTransform"])
        rows = [__import__("json").loads(row) for row in c0["overrides"]]
        self.assertTrue(rows)
        self.assertEqual({(row["kind"], row["component"]) for row in rows}, {("property", "Image")})
        self.assertTrue(all(row["property"].startswith("m_Color.") for row in rows))

    def test_one_call_with_posx_posy_lands_under_the_centred_start_anchors(self):
        """Why local_position sets the anchors first: in one prefab_set_transform call posX/posY are written before
        the anchors, while localPosition and anchoredPosition still coincide, so that call cannot go wrong."""
        solutions = load_modules(self.TASK_ID)[2]

        def single_call(ctx):
            spec = solutions._spec(ctx)
            items = []
            for name in solutions.ELEMENTS:
                args = solutions._rect_args(spec["rects"][name])
                args["posX"], args["posY"] = args.pop("anchoredPosX"), args.pop("anchoredPosY")
                items.append(dict(args, prefabPath=ctx.handles["prefabPath"], gameObjectPath=name))
            ctx.c.write("prefab_set_transform_batch", items=__import__("json").dumps(items))
            solutions._asset_order(ctx, spec["order"])
            solutions._asset_active(ctx)
            solutions._asset_group(ctx, spec)
            return solutions._answer(ctx, spec["order"])

        run = self.run_for()
        card = run.verify(run.solve(single_call))
        self.assertTrue(card.strict, card.failed())


class OC1Tests(PrefabTaskTests, unittest.TestCase):
    TASK_ID = "OC1"
    SEED = 7101
    MINUTES = 20
    FAILS_UNTOUCHED = {"A1", "A2", "A6", "A8"}

    def test_nested_prefabs_and_the_one_instance_with_overrides(self):
        run = self.run_for()
        c0, h = run.snap["data"], run.handles
        private = load_modules(self.TASK_ID)[1]
        docs = private.docs(run.build_ctx, h["turret"])
        nested = [doc for doc in docs if doc.type_name == "PrefabInstance"]
        self.assertEqual([doc.get("m_SourcePrefab", "guid") for doc in nested], [c0["barrelGuid"]])
        tank_docs = private.docs(run.build_ctx, h["tank"])
        self.assertEqual(len([doc for doc in tank_docs if doc.type_name == "PrefabInstance"]), 1)
        with_overrides = [key for key, value in c0["overrides"].items() if any(value["counts"].values())]
        self.assertEqual(len(with_overrides), 1)
        self.assertIn(with_overrides[0], {str(ref) for ref in h["tanks"]})
        self.assertEqual([row[1] for row in c0["addedValues"][with_overrides[0]]], ["AudioSource"])
        dome = dict(c0["turretComponents"])["Dome"]
        self.assertIn("UnityEngine.SphereCollider", [comp[0] for comp in dome])
        self.assertTrue(any(row[0].endswith("/") for row in c0["assets"]))
        self.assertTrue(any(row[0].endswith(".prefab.meta") and row[1] for row in c0["assets"]))

    def test_a_turret_instance_away_from_its_asset_root_position_has_overrides(self):
        """Why the fixture places the Turret instance at its asset root's local position: anywhere else the root
        position is a genuine override, the instance counts as the one with overrides and A8's answer flips."""
        run = self.run_for()
        h = run.handles
        turret = run.editor.find({"entityId": str(h["turrets"][0])})
        reader = run.editor.client("probe")
        self.assertFalse(reader.read("prefab_get_overrides", path=run.editor.path(turret))["hasOverrides"])
        turret.pos = (turret.pos[0], 0.0, turret.pos[2])
        self.assertTrue(reader.read("prefab_get_overrides", path=run.editor.path(turret))["hasOverrides"])


class OC3Tests(PrefabTaskTests, unittest.TestCase):
    TASK_ID = "OC3"
    SEED = 7301
    MINUTES = 30
    FAILS_UNTOUCHED = {"B1", "B2", "B5"}

    def test_slots_variants_and_the_second_common_table(self):
        run = self.run_for()
        c0, h = run.snap["data"], run.handles
        private = load_modules(self.TASK_ID)[1]
        rows = private.current(run.build_ctx, c0["lootGuid"])
        self.assertEqual(len(rows), 34)
        self.assertEqual(sum(1 for row in rows if row["componentIndex"] == 1), 1)
        self.assertEqual(sum(1 for row in rows if row["guid"]), 7)
        variants = private.variants(run.build_ctx)
        self.assertEqual(len(variants), 3)
        for path in variants:
            docs = private.docs(run.build_ctx, path)
            self.assertEqual([doc.type_name for doc in docs], ["PrefabInstance"])
        commons = [path for path in private.guid_paths(run.build_ctx).values()
                   if path.endswith("/LootTable_Common.asset")]
        self.assertEqual(len(commons), 2)
        self.assertEqual(sum(1 for count in c0["lootCounts"].values() if count == 0), 2 + len(variants))
        self.assertTrue(any(row[0].endswith(".asset.meta") and row[1] for row in c0["assets"]))

    def test_a_base_edit_reaches_its_variant_but_not_the_variant_file(self):
        run = self.run_for()
        private = load_modules(self.TASK_ID)[1]
        solutions = load_modules(self.TASK_ID)[2]
        variant = private.variants(run.build_ctx)[0]

        def content():
            with open(run.editor.disk(variant), "rb") as handle:
                return handle.read()

        before = content()
        run.solve(solutions.reference)
        self.assertEqual(content(), before)
        root = run.editor.assets[variant].root
        table = run.editor._find_comp(root, "Loot").values["table"]
        self.assertIsNotNone(table)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
