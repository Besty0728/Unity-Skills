"""Offline Codex skill isolation tests; no subject, provider, or Unity calls."""
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from harness import subject as S


class CodexIsolationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name, "home")
        self.home.mkdir()
        self.cwd = Path(self.temp.name, "cwd")
        self.cwd.mkdir()
        self.out = Path(self.temp.name, "out")
        self.out.mkdir()
        self.env = {"CODEX_HOME": str(self.home)}
        self.command = S.build_command("private task", "m", "high", str(self.cwd), 1, False, "codex")
        self.system = str(self.home / "skills" / ".system")

    def prompt(self, external=True):
        text = "### Skill roots\n- `r0` = `%s`\n" % self.system
        text += "### Available skills\n- system: System skill (file: r0/base/SKILL.md)\n"
        if external:
            text += "- `r1` = `/host/.agents/skills`\n"
            text += "- unity-batch: Batch skill (file: r1/unity/skills/batch/SKILL.md)\n"
        return subprocess.CompletedProcess([], 0, json.dumps([{"content": [{"text": text}]}]), "")

    def invoke(self):
        return S._codex_isolation(self.command, str(self.cwd), self.env, str(self.out))

    def test_explicit_paths_remove_host_catalog_and_reuse_exec_config(self):
        with mock.patch.object(S.subprocess, "run", side_effect=[self.prompt(), self.prompt(False)]) as render:
            extra = self.invoke()
        self.assertIn('/host/.agents/skills/unity/skills/batch/SKILL.md', extra[1])
        self.assertIn('"enabled"=false', extra[1])
        first = render.call_args_list[0].args[0]
        second = render.call_args_list[1].args[0]
        self.assertEqual(first[:3], ["codex", "debug", "prompt-input"])
        self.assertNotIn("private task", first)
        self.assertIn("features.skip_host_skill_discovery=true", first)
        self.assertIn("project_doc_max_bytes=0", first)
        self.assertEqual(second[:-3], first[:-1])
        self.assertEqual(second[-3:-1], extra)
        evidence = json.loads((self.out / "subject.isolation.json").read_text())
        self.assertTrue(evidence["verified"])
        self.assertEqual(evidence["verifiedRoots"], {"r0": self.system})

    def test_remaining_host_root_blocks_launch_and_saves_evidence(self):
        with mock.patch.object(S.subprocess, "run", return_value=self.prompt()):
            with self.assertRaisesRegex(ValueError, "external skill roots remain"):
                self.invoke()
        evidence = json.loads((self.out / "subject.isolation.json").read_text())
        self.assertFalse(evidence["verified"])
        self.assertIn("external skill roots", evidence["error"])

    def test_missing_or_unparseable_inventory_fails_closed(self):
        for value in ("not json", "[]", '[{"text":"not a skill inventory"}]'):
            with self.subTest(value=value), mock.patch.object(S.subprocess, "run", return_value=
                    subprocess.CompletedProcess([], 0, value, "")):
                with self.assertRaises(ValueError):
                    self.invoke()

    def test_existing_user_config_is_rejected_before_debug(self):
        (self.home / "config.toml").write_text("model='host'\n")
        with mock.patch.object(S.subprocess, "run") as render:
            with self.assertRaisesRegex(ValueError, "empty user config"):
                self.invoke()
        render.assert_not_called()

    def test_render_timeout_is_bounded_failure(self):
        with mock.patch.object(S.subprocess, "run", side_effect=subprocess.TimeoutExpired("debug", 20)):
            with self.assertRaisesRegex(ValueError, "timed out"):
                self.invoke()


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
