"""Offline lifecycle tests for partial fixture setup cleanup ownership."""
import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from harness import fixture as F  # noqa: E402
from harness import runner as R  # noqa: E402
from harness.rest import SkillError  # noqa: E402


class PartialSetupTests(unittest.TestCase):
    def test_folder_is_owned_before_scene_creation_can_fail(self):
        class Client:
            def read(self, skill, **kwargs):
                if skill == "scene_get_loaded":
                    return {"scenes": []}
                if kwargs["assetPath"] == F.EVAL_ROOT:
                    return {"path": F.EVAL_ROOT}
                raise SkillError("not found")

            def write(self, skill, **kwargs):
                if skill == "scene_create":
                    raise RuntimeError("scene creation failed")
                return {}

        scope = F.Scope("r20261011T123456abcdef")
        with self.assertRaisesRegex(RuntimeError, "scene creation failed"):
            F.enter(Client(), scope)
        self.assertTrue(scope.folder_created)
        self.assertFalse(scope.scene_created)
        self.assertIsNone(scope.root_iid)

    def test_cleanup_attempts_owned_partial_folder_without_root_instance(self):
        scope = F.Scope("r20261011T123456abcdef")
        scope.folder_created = True
        exit_call = mock.Mock(return_value=["asset_delete"])
        with mock.patch.object(R.F, "exit", exit_call):
            cleanup = R._cleanup(object(), scope, False)
        exit_call.assert_called_once()
        self.assertEqual(cleanup["actions"], ["asset_delete"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
