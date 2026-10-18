"""In-memory editor for the DB6 offline tests: subclasses FakeEditorDB and models TimedSpawner components in Play Mode.

A component whose class declares (prefab, container, interval, maxCount) instantiates `<prefab>(Clone)` under its
target parent every `interval` seconds up to `maxCount`. A `// usk-fake: <variant>` comment in the script selects the
wrong behaviour described by the solutions' negatives (spawn_on_start, no_stop, off_by_one, parent_emitter,
keep_world_pos).
"""
import copy
import math

from .fake_dev_db import FPS, FakeEditorDB
from .pack_fake_editor import RUNNER, Obj, _matches, _select
from usk_eval.rest import SkillError  # noqa: E402


class FakeEditorDB6(FakeEditorDB):
    def _spawn_instance(self, parent, name, world_zero=False):
        eid, iid = self._new_ids()
        child = Obj(eid, iid, name, parent)
        if parent is not None:
            parent.children.append(child)
        else:
            self.roots.append(child)
        self.objects[eid] = child
        self.owners[self.transform_id(child)] = eid
        if world_zero:
            self._set_world(child, pos=(0.0, 0.0, 0.0))
        else:
            child.pos = (0.0, 0.0, 0.0)
        return child

    def _init_spawners(self):
        self.spawners = []
        required = {"prefab", "container", "interval", "maxCount"}
        for obj in list(self.walk()):
            for kind, enabled in obj.components:
                if enabled is False or kind not in obj.fields:
                    continue
                spec = self.classes.get(kind) or {}
                fnames = {name for _k, name, _d, _o in spec.get("fields", ())}
                if not (required <= fnames):
                    continue
                fields = obj.fields[kind]
                prefab = fields.get("prefab")
                container = fields.get("container")
                if not (isinstance(prefab, tuple) and prefab[0] == "asset" and prefab[1] in self.prefabs):
                    continue
                parent = self.objects.get(container[1]) if isinstance(container, tuple) and container[0] == "object" \
                    else None
                variants = self._variants(kind)
                if "parent_emitter" in variants:
                    parent = obj
                if parent is None:
                    continue
                interval = float(fields.get("interval") or 0)
                max_count = int(fields.get("maxCount") or 0)
                if "off_by_one" in variants:
                    max_count = max(0, max_count - 1)
                self.spawners.append({
                    "parent": parent,
                    "name": self.prefabs[prefab[1]]["name"] + "(Clone)",
                    "interval": interval,
                    "maxCount": max_count,
                    "variants": variants,
                    "made": 0,
                })

    def _simulate_spawners(self):
        for model in getattr(self, "spawners", []):
            interval = model["interval"]
            if interval <= 0:
                continue
            variants = model["variants"]
            due = int(math.floor(self.clock / interval + 1e-6))
            if "spawn_on_start" in variants:
                due += 1
            if "no_stop" not in variants:
                due = min(due, model["maxCount"])
            while model["made"] < due:
                self._spawn_instance(model["parent"], model["name"], world_zero="keep_world_pos" in variants)
                model["made"] += 1

    def run_scenario(self, spec, agent=RUNNER):
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        saved = copy.deepcopy((self.objects, self.roots, self.owners))
        self.playing = spec.get("executionMode", "play") == "play"
        self.clock = 0.0
        self.runtime_error = False
        if self.playing:
            self._init_spawners()
            self._simulate_spawners()
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind == "wait_frames":
                    self.clock += int(step.get("frames") or 0) / FPS
                    self._simulate_spawners()
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                if kind == "wait_seconds":
                    self.clock += float(step.get("seconds") or 0)
                    self._simulate_spawners()
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except SkillError as exc:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": exc.payload, "passed": False})
                        passed, error = False, "Step refused or failed; inspect its actual response."
                        break
                if kind in ("assert", "wait_condition"):
                    selected = _select(actual, step.get("select"))
                    ok = _matches(selected, step.get("expected"), float(step.get("tolerance") or 0))
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                    "expected": step.get("expected"), "actual": selected, "passed": ok})
                    if not ok:
                        passed, error = False, "Assertion failed."
                        break
                else:
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"), "actual": actual,
                                    "passed": True})
                last = actual
        finally:
            self.playing = False
            self.objects, self.roots, self.owners = saved
        logged = [{"type": "Exception", "message": "NullReferenceException"}] if self.runtime_error else []
        if passed and logged:
            passed, error = False, "Runtime error logs were captured."
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": logged}}

# Producer:Betsy
