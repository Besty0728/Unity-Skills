"""store.py: timestamps, atomic writes, schema-checked reads, JSONL, runner.log format (contract 0, 4.1, 4.2)."""
import json
import os
import re
import shutil
import tempfile
import unittest

from .runner_support import store

ISO_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")


class TimeTests(unittest.TestCase):
    def test_now_iso_is_utc_millis_with_z(self):
        self.assertRegex(store.now_iso(), ISO_RE)

    def test_parse_utc_accepts_contract_forms(self):
        base = store.parse_utc("2026-10-17T03:04:05.123Z")
        self.assertEqual(store.parse_utc("2026-10-17T03:04:05.1234567Z").microsecond, 123456)   # journal ToString("o")
        self.assertEqual(store.parse_utc("2026-10-17T03:04:05.123456789Z").microsecond, 123456)
        self.assertEqual(store.parse_utc("2026-10-17T11:04:05.123+08:00"), base)
        self.assertEqual(store.parse_utc("2026-10-16T22:04:05.123-05:00"), base)
        self.assertEqual(store.parse_utc("2026-10-17T03:04:05Z").microsecond, 0)
        self.assertEqual(store.iso(base), "2026-10-17T03:04:05.123Z")

    def test_parse_utc_rejects_naive_and_garbage(self):
        for bad in ("2026-10-17T03:04:05", "yesterday", "", None, 5):
            with self.assertRaises(ValueError):
                store.parse_utc(bad)

    def test_ids_match_contract_patterns(self):
        self.assertRegex(store.stamp_id("s"), r"^s\d{8}T\d{6}[0-9a-f]{6}$")
        self.assertRegex(store.stamp_id("r"), r"^r\d{8}T\d{6}[0-9a-f]{6}$")
        self.assertRegex(store.new_attempt_id(), r"^[0-9a-f]{12}$")


class FileTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def test_atomic_json_has_schema_first_no_bom_and_leaves_no_temp(self):
        path = os.path.join(self.tmp, "a", "b.json")
        store.atomic_write_json(path, {"schema": 1, "name": "评测"})
        with open(path, "rb") as handle:
            raw = handle.read()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertTrue(raw.decode("utf-8").startswith('{\n "schema": 1'))
        self.assertIn("评测", raw.decode("utf-8"))
        self.assertEqual(os.listdir(os.path.dirname(path)), ["b.json"])

    def test_atomic_json_refuses_nan(self):
        with self.assertRaises(ValueError):
            store.atomic_write_json(os.path.join(self.tmp, "x.json"), {"schema": 1, "v": float("nan")})

    def test_bom_text(self):
        path = os.path.join(self.tmp, "p.md")
        store.atomic_write_text(path, "提示", bom=True)
        with open(path, "rb") as handle:
            self.assertEqual(handle.read(), b"\xef\xbb\xbf" + "提示".encode("utf-8"))

    def write(self, name, text):
        path = os.path.join(self.tmp, name)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def test_read_json_schema_rules(self):
        self.assertEqual(store.read_json(self.write("ok.json", '{"schema": 1}'))["schema"], 1)
        for name, text, code in (("none.json", "{}", "STATE_CORRUPT"), ("str.json", '{"schema": "1"}', "STATE_CORRUPT"),
                                 ("bool.json", '{"schema": true}', "STATE_CORRUPT"),
                                 ("bad.json", "{oops", "STATE_CORRUPT"),
                                 ("new.json", '{"schema": 2}', "SCHEMA_UNSUPPORTED")):
            with self.assertRaises(store.StoreError) as caught:
                store.read_json(self.write(name, text))
            self.assertEqual(caught.exception.code, code, name)
        self.assertIsNone(store.read_json(os.path.join(self.tmp, "missing.json"), missing_ok=True))
        with self.assertRaises(store.StoreError) as caught:
            store.read_json(os.path.join(self.tmp, "missing.json"))
        self.assertEqual(caught.exception.code, "IO_ERROR")

    def test_jsonl_append_and_torn_tail(self):
        path = os.path.join(self.tmp, "ledger.jsonl")
        store.append_jsonl(path, {"schema": 1, "n": 1})
        store.append_jsonl(path, {"schema": 1, "n": 2})
        with open(path, "a", encoding="utf-8") as handle:
            handle.write('{"schema":1,"n":3')            # a writer died mid-line
        self.assertEqual([r["n"] for r in store.read_jsonl(path)], [1, 2])
        with open(path, "rb") as handle:
            self.assertEqual(handle.read().count(b"\n"), 2)

    def test_runlog_line_format(self):
        path = os.path.join(self.tmp, "runner.log")
        log = store.RunLog("next", primary=path)
        log.error("boom\nTraceback line 1\nline 2")
        with open(path, "r", encoding="utf-8") as handle:
            lines = handle.read().splitlines()
        self.assertRegex(lines[0], r"^\d{4}-\d{2}-\d{2}T[\d:.]+Z pid=\d+ cmd=next ERROR boom$")
        self.assertEqual(lines[1:], ["    Traceback line 1", "    line 2"])

    def test_canonical_digest_is_order_independent(self):
        self.assertEqual(store.canonical_digest({"a": 1, "b": [1, "é"]}),
                         store.canonical_digest(json.loads('{"b": [1, "é"], "a": 1}')))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
