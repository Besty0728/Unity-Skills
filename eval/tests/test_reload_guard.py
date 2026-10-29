"""eval/tools/reload_guard.py: the restart verdict from an editor log's Domain Reload Profiling totals."""
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import reload_guard as G  # noqa: E402


def profile(total):
    return "Domain Reload Profiling: %dms\n\tBeginReloadAssembly (120ms)\n\tFinalizeReload (400ms)\n" % total


class ReloadGuardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.log = os.path.join(self.tmp.name, "editor.log")

    def write(self, *totals):
        with open(self.log, "w", encoding="utf-8") as fh:
            fh.write("Loading project\n")
            for t in totals:
                fh.write("Reloading assemblies after forced synchronous recompile.\n" + profile(t))

    def test_below_threshold_keeps_the_editor(self):
        self.write(800, 900, 2900, 3100, 2950)
        report, code = G.verdict(self.log, 3000)
        self.assertEqual(code, 0)
        self.assertEqual(report["reloads"], 5)
        self.assertEqual(report["recentMs"], [2900, 3100, 2950])
        self.assertFalse(report["restart"])

    def test_median_at_threshold_asks_for_a_restart(self):
        self.write(800, 3000, 9000, 3000)
        report, code = G.verdict(self.log, 3000)
        self.assertEqual(code, 3)
        self.assertEqual(report["medianMs"], 3000)
        self.assertTrue(report["restart"])

    def test_one_slow_reload_alone_does_not_restart(self):
        self.write(1000, 1100, 12000)
        self.assertEqual(G.verdict(self.log, 3000)[1], 0)

    def test_no_reload_or_no_log_is_no_data(self):
        self.write()
        self.assertEqual(G.verdict(self.log)[1], 1)
        self.assertEqual(G.verdict(os.path.join(self.tmp.name, "missing.log"))[1], 1)

    def test_cli_exit_code(self):
        self.write(4000, 4100, 4200)
        with mock.patch("builtins.print"):
            self.assertEqual(G.main([self.log]), 3)
            self.assertEqual(G.main([self.log, "--threshold-ms", "5000"]), 0)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
