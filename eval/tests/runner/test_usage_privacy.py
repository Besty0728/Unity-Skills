"""Privacy and source-hygiene guards for usk_eval.usage (contract 8.4): no log content in any output, no side effects."""
import ast
import contextlib
import gc
import io
import json
import logging
import os
import re
import shutil
import tempfile
import unittest
import warnings

try:
    from . import test_usage_support as support
except ImportError:
    import test_usage_support as support

from usk_eval import usage

SID = support.CLAUDE_SID
ROOT_THREAD = support.CODEX_ROOT_THREAD
SENTINELS = re.compile(r"ZZ_SENTINEL_[A-Z0-9_]+")
IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def fixture_sentinels():
    found = set()
    for path in support.FIXTURES.rglob("*"):
        if path.is_file():
            found.update(SENTINELS.findall(path.read_text(encoding="utf-8")))
    return found


class _Records(logging.Handler):
    def __init__(self):
        logging.Handler.__init__(self, logging.DEBUG)
        self.messages = []

    def emit(self, record):
        self.messages.append(record.getMessage())


class NoContentLeakTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = tmp.name
        self.fixtures = os.path.join(self.tmp, "fixtures")
        shutil.copytree(str(support.FIXTURES), self.fixtures)
        self.env = {"USK_EVAL_CLAUDE_ROOT": os.path.join(self.fixtures, "claude"),
                    "USK_EVAL_CODEX_ROOT": os.path.join(self.fixtures, "codex"), "HOME": os.path.join(self.tmp, "home")}

    def corrupt(self):
        """Append hostile lines that carry sentinels in every kind of place to a Claude log and a Codex rollout."""
        long_text = "ZZ_SENTINEL_LONG_FIELD_" + "x" * 300
        claude_main = os.path.join(self.fixtures, "claude", "-work-alpha", SID + ".jsonl")
        hostile = [
            '{"type":"assistant","message":{"id":"msg_cut","content":"ZZ_SENTINEL_CUT_LINE',
            json.dumps({"type": "assistant", "timestamp": "2026-10-17T03:03:00.000Z", "effort": long_text,
                        "message": {"id": "msg_hostile", "model": long_text,
                                    "usage": {"input_tokens": {"ZZ_SENTINEL_NESTED_USAGE": 1},
                                              "output_tokens": "ZZ_SENTINEL_TEXT_COUNT"}}}),
            json.dumps(["assistant", "ZZ_SENTINEL_JSON_ARRAY"]),
            json.dumps("ZZ_SENTINEL_JSON_STRING assistant"),
            '{"type":"assistant","x":' + "[" * 100000,
        ]
        with open(claude_main, "ab") as handle:
            handle.write(("\n".join(hostile) + "\n").encode("utf-8"))
            handle.write(b'{"type":"assistant","note":"ZZ_SENTINEL_BAD_BYTES \xff\xfe"}\n')
        rollout = os.path.join(self.fixtures, "codex", "2026", "10", "17", "rollout-2026-10-17T11-30-00-%s.jsonl" % ROOT_THREAD)
        hostile = [
            '{"timestamp":"2026-10-17T03:07:00.000Z","type":"event_msg","payload":{"type":"token_count",'
            '"info":{"total_token_usage":"ZZ_SENTINEL_CUT_COUNTER',
            json.dumps({"timestamp": "2026-10-17T03:07:00.000Z", "type": "turn_context",
                        "payload": {"model": long_text, "effort": long_text, "cwd": "ZZ_SENTINEL_CWD_TEXT"}}),
            json.dumps({"timestamp": "ZZ_SENTINEL_TIME_TEXT", "type": "event_msg",
                        "payload": {"type": "token_count",
                                    "info": {"total_token_usage": {"input_tokens": "ZZ_SENTINEL_STRING_COUNT"}}}}),
            json.dumps(["token_count", "ZZ_SENTINEL_JSON_ARRAY"]),
        ]
        with open(rollout, "ab") as handle:
            handle.write(("\n".join(hostile) + "\n").encode("utf-8"))
            handle.write(b'{"type":"turn_context","note":"ZZ_SENTINEL_BAD_BYTES \xff\xfe"}\n')

    def outputs(self):
        windows = [(support.T0, support.T1), ("2026-10-17T00:00:00.000Z", "2026-10-17T23:00:00.000Z"),
                   ("2026-10-18T00:00:00.000Z", "2026-10-18T01:00:00.000Z")]
        hosts = [support.host_claude(SID), support.host_claude("c1a2b3c4-0000-4000-8000-0000000000ff"),
                 support.host_codex(ROOT_THREAD), support.host_codex(), support.host_codex(cwd="/elsewhere"),
                 support.host_codex("01a10000-0000-7000-8000-0000000000ff"), {"kind": "none"}]
        ambiguous = dict(self.env, USK_EVAL_CODEX_ROOT=os.path.join(self.fixtures, "codex-ambiguous"))
        results = []
        for t0, t1 in windows:
            for host in hosts:
                results.append(usage.read_usage(host, t0, t1, env=self.env, now=support.NOW))
                results.append(usage.detect_declared(host, t0, t1, env=self.env, now=support.NOW))
            results.append(usage.read_usage(hosts[0], t0, t1, enabled=False, env=self.env, now=support.NOW))
            results.append(usage.read_usage(support.host_codex(), t0, t1, env=ambiguous, now=support.NOW))
        return results

    def check_clean(self, results, forbidden):
        self.assertTrue(results)
        for index, result in enumerate(results):
            text = json.dumps(result, ensure_ascii=False) + repr(result)
            for needle in forbidden:
                self.assertNotIn(needle, text, "result %d leaks %s" % (index, needle))
            self.assertNotIn(support.SENTINEL_CWD, text)
            self.assertNotIn("zz-sentinel", text)

    def test_fixtures_carry_enough_sentinels_for_the_checks_to_mean_something(self):
        self.assertGreaterEqual(len(fixture_sentinels()), 15)

    def test_no_sentinel_reaches_any_output(self):
        self.check_clean(self.outputs(), fixture_sentinels())

    def test_hostile_lines_do_not_leak_either(self):
        self.corrupt()
        hostile = {"ZZ_SENTINEL_LONG_FIELD", "ZZ_SENTINEL_CUT", "ZZ_SENTINEL_BAD_BYTES", "ZZ_SENTINEL_JSON",
                   "ZZ_SENTINEL_NESTED_USAGE", "ZZ_SENTINEL_TEXT_COUNT", "ZZ_SENTINEL_TIME_TEXT",
                   "ZZ_SENTINEL_STRING_COUNT", "ZZ_SENTINEL_CWD_TEXT"}
        self.check_clean(self.outputs(), fixture_sentinels() | hostile)

    def test_errors_hold_only_a_file_name_a_line_number_and_an_exception_type(self):
        self.corrupt()
        seen = 0
        for result in self.outputs():
            for error in result.get("errors", []) if isinstance(result, dict) else []:
                seen += 1
                self.assertEqual(sorted(error), ["error", "file", "line"])
                self.assertRegex(error["error"], IDENTIFIER)
                self.assertTrue(isinstance(error["line"], int) and error["line"] >= 0)
                self.assertEqual(os.path.basename(error["file"]), error["file"])
        self.assertGreater(seen, 0)

    def test_hostile_lines_are_survived_and_reported_by_line_number(self):
        self.corrupt()
        record = usage.read_usage(support.host_claude(SID), support.T0, support.T1, env=self.env, now=support.NOW)
        self.assertEqual(record["tokens"]["processed"], 14955)  # the hostile assistant line adds a message but no tokens
        self.assertEqual((record["messages"], record["model"]["values"][-1]), (8, "claude-sonnet-5-5"))
        self.assertEqual([e["line"] for e in record["errors"] if e["line"] > 17], [18, 22])  # cut line, runaway nesting
        record = usage.read_usage(support.host_codex(ROOT_THREAD), support.T0, support.T1, env=self.env, now=support.NOW)
        self.assertEqual(record["tokens"]["processed"], 20700)
        self.assertEqual([e["line"] for e in record["errors"]], [22, 24])  # cut counter line, unparsable time stamp

    def test_nothing_is_printed_logged_or_warned_and_every_file_is_closed(self):
        self.corrupt()
        handler, out, err = _Records(), io.StringIO(), io.StringIO()
        root = logging.getLogger()
        old_level = root.level
        root.addHandler(handler)
        root.setLevel(logging.DEBUG)
        try:
            with warnings.catch_warnings(record=True) as caught, contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                warnings.simplefilter("always")
                self.outputs()
                gc.collect()
        finally:
            root.removeHandler(handler)
            root.setLevel(old_level)
        self.assertEqual((handler.messages, out.getvalue(), err.getvalue()), ([], "", ""))
        self.assertEqual([str(item.message) for item in caught], [])


class SourceHygieneTests(unittest.TestCase):
    ALLOWED_IMPORTS = {"json", "os", "re", "fnmatch", "collections", "datetime"}

    def sources(self):
        return sorted(support.USAGE_SOURCES.glob("*.py"))

    def test_the_usage_package_exists_with_the_contract_modules(self):
        self.assertEqual([path.name for path in self.sources()], ["__init__.py", "claude.py", "codex.py", "common.py"])

    def test_files_are_utf8_with_bom_ascii_only_and_end_with_the_producer_tag(self):
        here = [path for path in support.FIXTURES.parents[1].glob("test_usage*.py")]
        for path in self.sources() + here:
            with self.subTest(file=path.name):
                raw = path.read_bytes()
                self.assertTrue(raw.startswith(b"\xef\xbb\xbf") and not raw.startswith(b"\xef\xbb\xbf\xef\xbb\xbf"))
                body = raw[3:].decode("utf-8")
                self.assertTrue(all(ord(char) < 128 for char in body), "comments and strings stay English/ASCII")
                self.assertTrue(body.endswith("\n\n# Producer:Betsy\n"))
                self.assertNotIn("\r", body)

    def test_python_3_9_syntax_and_standard_library_only(self):
        for path in self.sources():
            with self.subTest(file=path.name):
                tree = ast.parse(path.read_text(encoding="utf-8-sig"), feature_version=(3, 9))
                modules = set()
                for node in ast.walk(tree):
                    if isinstance(node, ast.Import):
                        modules.update(alias.name.split(".")[0] for alias in node.names)
                    elif isinstance(node, ast.ImportFrom) and node.level == 0:
                        modules.add((node.module or "").split(".")[0])
                self.assertLessEqual(modules, self.ALLOWED_IMPORTS)

    def test_the_package_prints_nothing_logs_nothing_and_writes_nothing(self):
        for path in self.sources():
            with self.subTest(file=path.name):
                tree = ast.parse(path.read_text(encoding="utf-8-sig"))
                for node in ast.walk(tree):
                    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
                        self.assertNotIn(node.func.id, ("print", "exec", "eval", "compile", "__import__"))
                        if node.func.id == "open" and len(node.args) > 1 and isinstance(node.args[1], ast.Constant):
                            self.assertEqual(set(node.args[1].value) & set("wax+"), set())
                    if isinstance(node, ast.Attribute):
                        self.assertNotIn(node.attr, ("write", "writelines", "Popen"))
                        if isinstance(node.value, ast.Name) and node.value.id == "os":
                            self.assertNotIn(node.attr, ("remove", "unlink", "rename", "replace", "makedirs", "mkdir", "rmdir",
                                                         "system", "popen", "putenv"))
                    if isinstance(node, (ast.Import, ast.ImportFrom)):
                        names = [alias.name for alias in node.names] + [getattr(node, "module", None) or ""]
                        self.assertFalse({"logging", "subprocess", "socket", "urllib", "shutil", "tempfile"} & set(names))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
