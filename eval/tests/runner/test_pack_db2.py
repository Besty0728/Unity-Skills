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


class DB2ScriptCheckTests(unittest.TestCase):
    """H1's reading of the script: comments and strings removed, declarations parsed with all their declarators."""

    def check(self, source):
        from .pack_offline import load_modules
        private = load_modules("DB2")[1]
        code = private.code_of(source)
        return private.fields(code), private.INVOKE_RE.search(code) is not None

    def test_declarations_count_whatever_their_form(self):
        declared, _invokes = self.check(
            "[SerializeField, Min(1)] private int maxHealth = 100;\n"
            "[Tooltip(\"a, see https://x\")] public int currentHealth, deathCount;\n"
            "public UnityEngine.Events.UnityEvent onDied = new UnityEvent();\n")
        self.assertEqual(declared["maxHealth"], ("int", True, "100"))
        self.assertEqual(declared["currentHealth"][:2], ("int", True))
        self.assertEqual(declared["deathCount"][:2], ("int", True))
        self.assertEqual(declared["onDied"][:2], ("UnityEngine.Events.UnityEvent", True))

    def test_unserialized_declarations_do_not_count(self):
        declared, _invokes = self.check("public static int maxHealth = 100; private int currentHealth;"
                                        " [System.NonSerialized] public int deathCount; public const int cap = 1;")
        self.assertFalse(any(entry[1] for entry in declared.values()))

    def test_comments_and_strings_neither_spoof_nor_hide(self):
        declared, invokes = self.check("// public int maxHealth = 100;\n/* onDied?.Invoke(); */\n"
                                       "string s = \"public int deathCount; onDied.Invoke()\";\n")
        self.assertNotIn("maxHealth", declared)
        self.assertNotIn("deathCount", declared)
        self.assertFalse(invokes)
        _declared, invokes = self.check("void Die() { deathCount++; onDied ?. Invoke(); }")
        self.assertTrue(invokes)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
