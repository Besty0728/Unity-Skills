#!/usr/bin/env python3
"""Tests for the unity_skills.py CLI parameter sources: --params-file (UTF-8 JSON object, BOM or not, overridden
by key=value arguments) and --batch files written with a BOM, as Windows PowerShell 5.1 writes them.

    python3 -m unittest discover -s .github/scripts/tests -t .github/scripts/tests
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import types
import unittest
from unittest import mock

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
CLI = os.path.join(REPO, "SkillsForUnity", "unity-skills~", "scripts", "unity_skills.py")


def load_cli():
    # The client imports requests at module level; the CLI paths tested here never touch it.
    try:
        import requests  # noqa: F401
    except ImportError:
        sys.modules["requests"] = types.ModuleType("requests")
    spec = importlib.util.spec_from_file_location("unity_skills_under_test", CLI)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ParamsFileTests(unittest.TestCase):
    def setUp(self):
        self.cli = load_cli()
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name: str, data: bytes) -> str:
        path = os.path.join(self.tmp.name, name)
        with open(path, "wb") as f:
            f.write(data)
        return path

    def run_main(self, argv, **patches):
        out = io.StringIO()
        with mock.patch.object(sys, "argv", ["unity_skills.py"] + argv), contextlib.redirect_stdout(out):
            with contextlib.ExitStack() as stack:
                for name, value in patches.items():
                    stack.enter_context(mock.patch.object(self.cli, name, value))
                try:
                    self.cli.main()
                    code = 0
                except SystemExit as e:
                    code = e.code
        return code, out.getvalue()

    def test_reads_an_object_with_and_without_bom(self):
        body = json.dumps({"name": "Cube", "x": 1.5}).encode("utf-8")
        for data in (body, b"\xef\xbb\xbf" + body):
            params, error = self.cli._read_params_file(self.write("p.json", data))
            self.assertIsNone(error)
            self.assertEqual(params, {"name": "Cube", "x": 1.5})

    def test_rejects_non_objects_invalid_json_and_missing_files(self):
        for data in (b"[1, 2]", b"\"text\"", b"{not json"):
            params, error = self.cli._read_params_file(self.write("bad.json", data))
            self.assertIsNone(params)
            self.assertIn("--params-file", error)
        params, error = self.cli._read_params_file(os.path.join(self.tmp.name, "missing.json"))
        self.assertIsNone(params)
        self.assertIn("Cannot read --params-file", error)

    def test_key_value_arguments_override_file_keys(self):
        path = self.write("p.json", b"\xef\xbb\xbf" + json.dumps({"name": "FromFile", "y": 2}).encode("utf-8"))
        calls = []
        code, _ = self.run_main(["--params-file", path, "gameobject_create", "name=FromArgs"],
                                call_skill=lambda skill, **kw: calls.append((skill, kw)) or {"status": "success"})
        self.assertEqual(code, 0)
        self.assertEqual(calls, [("gameobject_create", {"dry_run_token": None, "name": "FromArgs", "y": 2})])

    def test_a_bad_params_file_fails_without_calling_the_skill(self):
        path = self.write("p.json", b"[]")
        calls = []
        code, out = self.run_main(["--params-file", path, "gameobject_create"],
                                  call_skill=lambda skill, **kw: calls.append(skill))
        self.assertEqual(code, 1)
        self.assertEqual(calls, [])
        self.assertIn("JSON object", json.loads(out)["error"])

    def test_batch_file_with_bom_is_read(self):
        steps = {"steps": [{"skill": "gameobject_find", "args": {"name": "A"}}]}
        path = self.write("b.json", b"\xef\xbb\xbf" + json.dumps(steps).encode("utf-8"))
        batches = []
        code, _ = self.run_main(["--batch", path],
                                execute_batch=lambda s, **kw: batches.append(s) or {"status": "completed"})
        self.assertEqual(code, 0)
        self.assertEqual(batches, [steps["steps"]])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
