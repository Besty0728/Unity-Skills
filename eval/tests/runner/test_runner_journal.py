"""journal.py: reading (cursor0, cursorEnd] from rotated segments and proving seq continuity (contract 7.1)."""
import json
import os
import shutil
import tempfile
import unittest

from .runner_support import journal


def entry(seq, kind="scene_changes", source="rest", agent="ClaudeCode", payload=None):
    return {"seq": seq, "type": kind, "source": source, "agent": agent, "isPlaying": False,
            "tsUtc": "2026-10-17T03:04:05.1234567Z", "payload": payload if payload is not None else {"count": 0}}


class SegmentTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir, True)

    def segment(self, name, seqs, tail="", newline="\n"):
        with open(os.path.join(self.dir, name), "w", encoding="utf-8", newline="") as handle:
            for seq in seqs:
                handle.write(json.dumps(entry(seq)) + newline)
            handle.write(tail)

    def read(self, cursor0, cursor_end, **kwargs):
        kwargs.setdefault("sleep", lambda s: None)
        return journal.read_range(self.dir, cursor0, cursor_end, **kwargs)

    def test_range_spanning_rotated_segments_is_complete(self):
        self.segment("editor_changes.jsonl", [8, 9, 10])
        self.segment("editor_changes.1.jsonl", [4, 5, 6, 7])
        self.segment("editor_changes.2.jsonl", [1, 2, 3])
        self.segment("editor_changes.3.jsonl", [])
        read = self.read(2, 10)
        self.assertTrue(read.complete)
        self.assertEqual([e["seq"] for e in read.entries], list(range(3, 11)))
        self.assertEqual(read.segments, ["editor_changes.jsonl", "editor_changes.1.jsonl", "editor_changes.2.jsonl"])

    def test_older_segments_are_not_read_once_cursor0_is_covered(self):
        self.segment("editor_changes.jsonl", [8, 9, 10])
        self.segment("editor_changes.1.jsonl", [4, 5, 6, 7])
        read = self.read(7, 10)
        self.assertTrue(read.complete)
        self.assertEqual(read.segments, ["editor_changes.jsonl"])

    def test_missing_seq_is_a_gap_after_retries(self):
        self.segment("editor_changes.jsonl", [5, 6, 8, 9, 12])
        sleeps = []
        read = self.read(4, 12, sleep=sleeps.append)
        self.assertFalse(read.complete)
        self.assertEqual(read.gaps, [[7, 7], [10, 11]])
        self.assertEqual(len(sleeps), journal.RETRIES)

    def test_retry_picks_up_an_entry_written_meanwhile(self):
        self.segment("editor_changes.jsonl", [5, 6])

        def late_write(_seconds):
            with open(os.path.join(self.dir, "editor_changes.jsonl"), "a", encoding="utf-8") as handle:
                handle.write(json.dumps(entry(7)) + "\n")

        read = self.read(4, 7, sleep=late_write)
        self.assertTrue(read.complete)
        self.assertEqual([e["seq"] for e in read.entries], [5, 6, 7])

    def test_torn_active_tail_is_ignored_but_corrupt_rotated_lines_count(self):
        self.segment("editor_changes.jsonl", [9, 10], tail='{"seq": 11, "type": "sc')
        self.segment("editor_changes.1.jsonl", [6, 7, 8], tail="not json\n")
        read = self.read(5, 10)
        self.assertTrue(read.complete)
        self.assertEqual(read.corrupt_lines, 1)

    def test_conflicting_copies_of_a_seq_make_it_incomplete(self):
        self.segment("editor_changes.jsonl", [3, 4])
        with open(os.path.join(self.dir, "editor_changes.1.jsonl"), "w", encoding="utf-8") as handle:
            handle.write(json.dumps(entry(2)) + "\n")
            handle.write(json.dumps(entry(3, payload={"count": 99})) + "\n")
        read = self.read(1, 4)
        self.assertEqual(read.conflicts, 1)
        self.assertFalse(read.complete)
        self.assertEqual(read.gaps, [])

    def test_same_entry_read_twice_during_rotation_is_not_a_conflict(self):
        self.segment("editor_changes.jsonl", [5, 6])
        self.segment("editor_changes.1.jsonl", [3, 4, 5, 6])
        read = self.read(2, 6)
        self.assertTrue(read.complete)
        self.assertEqual(read.conflicts, 0)

    def test_crlf_segments_and_empty_window(self):
        self.segment("editor_changes.jsonl", [1, 2, 3], newline="\r\n")
        self.assertTrue(self.read(0, 3).complete)
        empty = self.read(3, 3)
        self.assertTrue(empty.complete)
        self.assertEqual(empty.entries, [])
        self.assertEqual(journal.max_seq_on_disk(self.dir), 3)


class FlattenTests(unittest.TestCase):
    def test_records_carry_identity_fields_and_origin(self):
        events = [
            entry(5, payload={"count": 1, "omitted": 2, "changes": [{"kind": "gameobject_created", "path": "EV_r/X"}]}),
            entry(6, "file_changes", "rest", "UnitySkillsEval",
                  {"imported": ["Assets/a.mat"], "deleted": ["Assets/b.mat"], "moved": [{"from": "Assets/c", "to": "Assets/d"}],
                   "count": 3, "omitted": 4}),
            entry(7, "scene_saved", "editor", None, {"name": "Main", "path": "Assets/Scenes/Main.unity"}),
        ]
        records = journal.flatten(events, {"from": 5, "to": 6})
        kinds = [(r["seq"], r["kind"]) for r in records]
        self.assertEqual(kinds, [(5, "gameobject_created"), (5, "omitted"), (6, "imported"), (6, "deleted"),
                                 (6, "moved"), (6, "omitted"), (7, "scene_saved")])
        self.assertEqual(records[0]["origin"], "attempt")
        self.assertEqual(records[2]["origin"], "refresh")
        self.assertEqual(records[4]["from"], "Assets/c")
        self.assertEqual(records[4]["assetPath"], "Assets/d")
        self.assertEqual(records[6]["path"], "Assets/Scenes/Main.unity")
        for rec in records:
            for key in ("agent", "isPlaying", "tsUtc", "source", "type"):
                self.assertIn(key, rec)
        self.assertEqual(journal.agents(events), ["ClaudeCode", "UnitySkillsEval"])


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
