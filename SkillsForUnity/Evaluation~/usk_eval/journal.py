"""Editor change journal read back from disk (contract 7.1 reader side) and flattened into records (5.7).

The plugin appends every entry to Library/UnitySkills/editor_changes.jsonl and rotates it into
editor_changes.1.jsonl (newest) .. editor_changes.10.jsonl. The runner reads (cursor0, cursorEnd] from the
segments, newest first, and proves the seq range is gap-free before any verifier sees it.
"""
import json
import os
import time

from . import PACKAGE_NAME, store
from .rest import RUNNER_AGENT

SEGMENTS = 10
PACKAGE_PREFIX = "Packages/" + PACKAGE_NAME      # Unity's path of the plugin package, wherever it is on disk
RETRIES = 3
RETRY_DELAY_S = 0.5
ACTIVE = "editor_changes.jsonl"


def segment_names():
    return [ACTIVE] + ["editor_changes.%d.jsonl" % n for n in range(1, SEGMENTS + 1)]


def current_cursor(client):
    """editor_get_changes since=0 limit=1 also flushes debounced property changes (contract 2.8 S5)."""
    res = client.read("editor_get_changes", since=0, limit=1)
    return int(res.get("cursor") or 0)


class JournalRead(object):
    def __init__(self):
        self.entries = []
        self.complete = True
        self.gaps = []
        self.segments = []
        self.conflicts = 0
        self.corrupt_lines = 0

    def as_state(self):
        return {"complete": self.complete, "gaps": self.gaps, "segments": self.segments,
                "conflicts": self.conflicts, "corruptLines": self.corrupt_lines}


def _canonical(entry):
    return json.dumps(entry, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def _parse_segment(path, active):
    """(entries, corrupt) of one segment; the active segment's last line may be mid-write and is ignored."""
    with open(path, "rb") as handle:
        raw = handle.read()
    lines = raw.split(b"\n")
    if lines and not lines[-1].strip():
        lines = lines[:-1]
    entries, corrupt = [], 0
    for index, line in enumerate(lines):
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line.decode("utf-8-sig"))
            seq = entry.get("seq") if isinstance(entry, dict) else None
            if not isinstance(seq, int) or isinstance(seq, bool):
                raise ValueError("no seq")
        except (ValueError, UnicodeDecodeError):
            if not (active and index == len(lines) - 1):
                corrupt += 1
            continue
        entries.append(entry)
    return entries, corrupt


def _read_once(journal_dir, cursor0, cursor_end):
    read = JournalRead()
    by_seq = {}
    for name in segment_names():
        path = os.path.join(journal_dir, name)
        try:
            entries, corrupt = _parse_segment(path, name == ACTIVE)
        except FileNotFoundError:
            continue
        except OSError:
            continue
        read.segments.append(name)
        read.corrupt_lines += corrupt
        oldest = None
        for entry in entries:
            seq = entry["seq"]
            oldest = seq if oldest is None else min(oldest, seq)
            if cursor0 < seq <= cursor_end:
                text = _canonical(entry)
                if seq in by_seq and by_seq[seq][0] != text:
                    read.conflicts += 1
                by_seq.setdefault(seq, (text, entry))
        if oldest is not None and oldest <= cursor0 + 1:
            break
    read.entries = [by_seq[seq][1] for seq in sorted(by_seq)]
    read.gaps = _gaps(set(by_seq), cursor0, cursor_end)
    read.complete = not read.gaps and read.conflicts == 0
    return read


def _gaps(present, cursor0, cursor_end):
    gaps, start = [], None
    for seq in range(cursor0 + 1, cursor_end + 1):
        if seq in present:
            if start is not None:
                gaps.append([start, seq - 1])
                start = None
        elif start is None:
            start = seq
    if start is not None:
        gaps.append([start, cursor_end])
    return gaps


def read_range(journal_dir, cursor0, cursor_end, retries=None, delay_s=None, sleep=time.sleep):
    """Entries with seq in (cursor0, cursorEnd], re-read up to `retries` times while incomplete."""
    if cursor_end <= cursor0:
        return JournalRead()
    read = _read_once(journal_dir, cursor0, cursor_end)
    for _ in range(RETRIES if retries is None else retries):
        if read.complete:
            break
        sleep(RETRY_DELAY_S if delay_s is None else delay_s)
        read = _read_once(journal_dir, cursor0, cursor_end)
    return read


def max_seq_on_disk(journal_dir):
    """Newest seq in the active segment (or the newest rotated one); fallback cursor when the editor is gone."""
    for name in segment_names():
        try:
            entries, _ = _parse_segment(os.path.join(journal_dir, name), name == ACTIVE)
        except OSError:
            continue
        if entries:
            return max(e["seq"] for e in entries)
    return 0


def write_entries(path, entries):
    """Frozen copy in seq order (attempts/<aid>/journal.jsonl); written once, atomically."""
    data = "".join(json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n" for e in entries)
    store.atomic_write_bytes(path, data.encode("utf-8"))


def load_entries(path):
    return sorted(store.read_jsonl(path), key=lambda e: e.get("seq") or 0)


# ---------------------------------------------------------------- flatten


def _origin(seq, refresh_window):
    if refresh_window and isinstance(seq, int) and refresh_window["from"] < seq <= refresh_window["to"]:
        return "refresh"
    return "attempt"


def flatten(events, refresh_window=None):
    """One record per atomic change (v1 eval/harness/journal.py:70-104) plus agent/isPlaying/tsUtc/origin."""
    out = []
    for event in events:
        seq = event.get("seq")
        base = {"seq": seq, "type": event.get("type"), "source": event.get("source"), "agent": event.get("agent"),
                "isPlaying": event.get("isPlaying"), "tsUtc": event.get("tsUtc"),
                "origin": _origin(seq, refresh_window)}
        payload = event.get("payload") or {}
        etype = event.get("type")
        if etype == "scene_changes":
            for change in payload.get("changes") or []:
                rec = dict(base)
                rec.update(change)
                out.append(rec)
            if payload.get("omitted"):
                rec = dict(base)
                rec.update({"kind": "omitted", "count": payload.get("omitted")})
                out.append(rec)
        elif etype == "file_changes":
            for key in ("imported", "deleted"):
                for path in payload.get(key) or []:
                    rec = dict(base)
                    rec.update({"kind": key, "assetPath": path})
                    out.append(rec)
            for move in payload.get("moved") or []:
                rec = dict(base)
                if isinstance(move, dict):
                    rec.update({"kind": "moved", "from": move.get("from"), "assetPath": move.get("to")})
                else:
                    rec.update({"kind": "moved", "assetPath": str(move)})
                out.append(rec)
            if payload.get("omitted"):
                rec = dict(base)
                rec.update({"kind": "omitted", "count": payload.get("omitted")})
                out.append(rec)
        else:
            rec = dict(base)
            rec.update({"kind": etype})
            rec.update(payload)
            out.append(rec)
    return out


def package_path(rec):
    """Package-relative path of an import record under Packages/com.besty.unity-skills ('' for the package folder);
    None for any other record."""
    if rec.get("kind") != "imported":
        return None
    path = str(rec.get("assetPath") or "").replace("\\", "/")
    if path == PACKAGE_PREFIX:
        return ""
    return path[len(PACKAGE_PREFIX) + 1:] if path.startswith(PACKAGE_PREFIX + "/") else None


def split_package_reimports(records, judged):
    """(kept, reimports) of flattened records. `judged` is state.packageTree.atSubmit (S6): when it found the plugin
    package unchanged since issue, imports of its files that still exist are Unity rebuilding the plugin and no evidence
    of anything (contract 5.7); otherwise every record is kept."""
    if not (judged or {}).get("unchanged"):
        return list(records), []
    absent = set(judged.get("absent") or [])
    kept, reimports = [], []
    for rec in records:
        path = package_path(rec)
        (reimports if path is not None and path not in absent else kept).append(rec)
    return kept, reimports


def asset_reimport_path(rec, asset_root):
    """Path relative to the run's asset folder A of the file that an import record of the editor or of the runner's own
    calls names, '.meta' dropped; None for every other record. An import made by the agent's own REST call is never
    judged this way: it is its write, whatever the bytes."""
    if rec.get("kind") != "imported" or (rec.get("source") == "rest" and rec.get("agent") != RUNNER_AGENT):
        return None
    prefix = str(asset_root or "").replace("\\", "/").rstrip("/") + "/"
    path = str(rec.get("assetPath") or "").replace("\\", "/")
    if path.endswith(".meta"):
        path = path[:-5]
    return path[len(prefix):] if len(prefix) > 1 and path.startswith(prefix) and len(path) > len(prefix) else None


def split_asset_reimports(records, judged, asset_root):
    """(kept, reimports) of flattened records. `judged` is state.assetTree.atSubmit (S6): imports by the editor or the
    runner of files under A that existed at issue and have the same bytes at submit are Unity importing again what
    depends on a change (a script whose type changed re-imports the prefabs that use it), no write by anyone. Created,
    changed, deleted and moved files, and every record that names no such file, are kept."""
    unchanged = set((judged or {}).get("unchanged") or ())
    if not unchanged:
        return list(records), []
    kept, reimports = [], []
    for rec in records:
        (reimports if asset_reimport_path(rec, asset_root) in unchanged else kept).append(rec)
    return kept, reimports


def split_reimports(records, state):
    """(kept, package_reimports, asset_reimports): flattened records minus the re-imports that the S6 judgments in
    state.json (packageTree, assetTree) found to be no evidence (contract 2.9, 5.7)."""
    kept, package = split_package_reimports(records, (state.get("packageTree") or {}).get("atSubmit"))
    kept, assets = split_asset_reimports(kept, (state.get("assetTree") or {}).get("atSubmit"),
                                         (state.get("scope") or {}).get("assetRoot"))
    return kept, package, assets


def agents(events):
    return sorted({e.get("agent") for e in events if isinstance(e.get("agent"), str)})

# Producer:Betsy
