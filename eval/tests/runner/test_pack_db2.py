"""Offline pack tests for DB2 (dev/basic: Health MonoBehaviour with TakeDamage, Heal, and death event)."""
import unittest

from .fake_db2 import FakeEditorDB2
from .test_pack_dev_db import DevTaskTests


class DB2Tests(DevTaskTests, unittest.TestCase):
    TASK_ID = "DB2"
    EDITOR = FakeEditorDB2
    SEED = 7608
    FAILS_UNTOUCHED = {"H1", "H2", "H3", "H6", "H7"}

    def test_seed_variation(self):
        """Different seeds vary the unit order, unit names, class name, and maxHealth."""
        seen_max = set()
        seen_paths = set()
        for seed in (10, 21, 32, 43, 54, 65):
            run = self.run_for(seed=seed)
            pv = run.task.prompt_vars(run.build_ctx)
            seen_max.add(pv["max_health"])
            seen_paths.add(pv["unit"])
        self.assertGreaterEqual(len(seen_max), 2)
        self.assertGreaterEqual(len(seen_paths), 2)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
