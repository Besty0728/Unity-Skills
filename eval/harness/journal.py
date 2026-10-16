"""Editor change journal (editor_get_changes) as the side-effect ledger of an attempt.

The journal is a 500-entry ring; a truncated read keeps only the newest entries and cannot page
backwards. Collector therefore polls in the background while the subject runs and flags any gap
(dropped/truncated) so a verifier never silently passes a side-effect check on partial evidence.
"""
import threading
import time

PAGE = 500


class Collector(object):
    def __init__(self, client, since, poll_s=3.0):
        self.client = client
        self.since = since
        self.start_cursor = since
        self.poll_s = poll_s
        self.events = []
        self.gaps = []
        self.errors = []
        self._stop = threading.Event()
        self._thread = None

    def drain(self):
        while True:
            res = self.client.read("editor_get_changes", since=self.since, limit=PAGE)
            if res.get("dropped"):
                self.gaps.append({"since": self.since, "oldestSeq": res.get("oldestSeq"), "kind": "dropped"})
            if res.get("truncated"):
                self.gaps.append({"since": self.since, "cursor": res.get("cursor"), "kind": "truncated"})
            changes = res.get("changes") or []
            self.events.extend(changes)
            cursor = res.get("cursor")
            if cursor is not None:
                self.since = max(self.since, int(cursor))
            if len(changes) < PAGE:
                return

    def _loop(self):
        while not self._stop.is_set():
            try:
                self.drain()
            except Exception as exc:  # reloads answer 503 for a while; keep the attempt alive
                self.errors.append(repr(exc))
            self._stop.wait(self.poll_s)

    def start(self):
        self._thread = threading.Thread(target=self._loop, name="journal-collector", daemon=True)
        self._thread.start()
        return self

    def stop(self):
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=60)
        self.drain()
        return self

    @property
    def complete(self):
        return not self.gaps


def current_cursor(client):
    res = client.read("editor_get_changes", since=0, limit=1)
    return int(res.get("cursor") or 0)


def flatten(events):
    """One record per atomic change: scene change entries, file paths, scene save/open events."""
    out = []
    for event in events:
        base = {"seq": event.get("seq"), "type": event.get("type"), "source": event.get("source")}
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
        else:
            rec = dict(base)
            rec.update({"kind": etype})
            rec.update(payload)
            out.append(rec)
    return out


def under(path, root):
    if not path or not root:
        return False
    path = path.replace("\\", "/")
    root = root.replace("\\", "/").rstrip("/")
    return path == root or path.startswith(root + "/")


def adopted_paths(records, object_root):
    """Paths of objects that were created or edited outside R and then moved under R: their earlier
    records are transient work on the subject's own object, not edits of anything outside R."""
    out = set()
    for rec in records:
        if rec.get("kind") == "gameobject_reparented" and under(rec.get("toPath"), object_root) \
                and not under(rec.get("fromPath"), object_root):
            out.add(rec.get("fromPath"))
    return out


def scope_violations(records, object_root, asset_root, run_scene=None, extra_allowed=None):
    """Changes outside the run's object/asset roots (meta files follow their asset)."""
    bad = []
    adopted = adopted_paths(records, object_root)
    for rec in records:
        kind = rec.get("kind")
        if extra_allowed and extra_allowed(rec):
            continue
        if kind in ("imported", "deleted", "moved"):
            path = rec.get("assetPath") or ""
            if path.endswith(".meta"):
                path = path[:-5]
            ok = under(path, asset_root) and (kind != "moved" or under(rec.get("from") or path, asset_root))
            if not ok:
                bad.append(rec)
        elif kind in ("scene_saved", "scene_opened"):
            if not run_scene or rec.get("path") != run_scene:
                bad.append(rec)
        elif kind == "omitted":
            bad.append(rec)
        elif rec.get("type") == "scene_changes":
            asset = rec.get("assetPath")
            paths = [p for p in (rec.get("path"), rec.get("fromPath"), rec.get("toPath"), rec.get("newParentPath"))
                     if p and p not in adopted]
            if asset:
                if not under(asset, asset_root):
                    bad.append(rec)
            elif not all(under(p, object_root) for p in paths) or (not paths and not adopted.intersection(
                    p for p in (rec.get("path"), rec.get("fromPath")) if p)):
                bad.append(rec)
            elif run_scene and rec.get("scenePath") and rec.get("scenePath") != run_scene:
                bad.append(rec)
        elif kind == "undo_redo":
            continue
    return bad


def non_rest(records):
    return [r for r in records if r.get("source") not in (None, "rest")]

# Producer:Betsy
