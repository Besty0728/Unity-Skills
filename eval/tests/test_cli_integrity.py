"""Offline checks for calibration slot identity and source-bound selftest gates."""
import json
import os
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import cli
from harness.classify import Ledger


class CliIntegrityTests(unittest.TestCase):
    def test_suite_stops_after_first_failed_isolation_preflight(self):
        from harness import runner as R
        args = SimpleNamespace(tasks=["S01"], models=["luna-max"], plan="calibration",
                               suite="isolated", host="codex", force=True, dry_run=False,
                               project="project", effort="high", cold=False)
        report = {"status": "invalid", "subject": {"isolationVerified": False},
                  "reasons": ["host_error: no result event"], "cleanup": {}}
        with tempfile.TemporaryDirectory() as tmp, \
             patch.object(R, "RUNS_DIR", tmp), \
             patch.object(R, "run_attempt", return_value=report) as attempt, \
             patch.object(cli, "_gate_ok", return_value=True), \
             patch.object(cli, "_check_cohort"), patch.object(cli, "_estimate", return_value={}), \
             patch("tasks.load", return_value=object()), patch("builtins.print"):
            self.assertEqual(cli.cmd_suite(args), 2)
        attempt.assert_called_once()

    def test_slot_separates_effort_and_host(self):
        with tempfile.TemporaryDirectory() as tmp:
            ledger = Ledger(os.path.join(tmp, "ledger.jsonl"))
            ledger.append(dict(suite="s", task="S01", model="gpt-6-luna", kind="model", rep=0,
                               effort="max", host="codex", status="pass"))
            self.assertEqual(cli._slot_state(ledger,"s","S01","gpt-6-luna",0,"max","codex")[0], "done")
            self.assertEqual(cli._slot_state(ledger,"s","S01","gpt-6-luna",0,"high","codex")[0], "todo")
            self.assertEqual(cli._slot_state(ledger,"s","S01","gpt-6-luna",0,"max","claude")[0], "todo")

    def test_old_or_stale_gate_does_not_authorize_runs(self):
        with tempfile.TemporaryDirectory() as tmp:
            gate = os.path.join(tmp, "gate.json")
            with patch.object(cli,"GATE",gate), patch.object(cli,"_gate_hash",return_value="current"):
                for entry, expected in [(dict(green=True),False),
                                        (dict(green=True,sourceHash="old"),False),
                                        (dict(green=True,sourceHash="current"),True)]:
                    with open(gate,"w") as handle:
                        json.dump({"S01":entry},handle)
                    self.assertEqual(cli._gate_ok("S01"),expected)

    def test_luna_max_is_effort_not_model_suffix(self):
        self.assertEqual(cli._model_id("luna-max"),"gpt-6-luna")
        self.assertEqual(cli._effort("luna-max"),"max")
        self.assertEqual(cli._effort("terra"),"high")

    def test_calibration_prefix_is_pilot(self):
        tasks,models=["S01","T1"],["astra","sol","terra","luna-max"]
        pilot=cli._slots(tasks,models,1)
        self.assertEqual(cli._slots(tasks,models,3)[:len(pilot)],pilot)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
