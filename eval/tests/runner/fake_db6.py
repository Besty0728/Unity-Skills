"""In-memory editor for the DB6 offline tests: subclasses FakeEditorDB and models TimedSpawner components in Play Mode.

A component whose class declares (prefab, container, interval, maxCount) instantiates `<prefab>(Clone)` under its
target parent every `interval` seconds up to `maxCount`; `prefab` may be the asset or a scene object (Instantiate
clones either). A `// usk-fake: <variant>` comment in the script selects the wrong behaviour described by the
solutions' negatives (spawn_on_start, no_stop, off_by_one, parent_emitter, keep_world_pos, renamed_clones).

The scenario stand-in paces steps the way RuntimeScenarioService does when `step_cost` is set: every executed step
and every wait start costs one editor tick of game time, `wait_condition` re-reads once per tick until it matches or
the scenario's timeout passes, and Play may start `start_offset` seconds before the first step. editor_get_state
reports timeSinceStartup on that clock.
"""
import copy
import math

from .fake_dev_db import FPS, FakeEditorDB
from .pack_fake_editor import RUNNER, Obj, _fail, _matches, _select
from usk_eval.rest import SkillError  # noqa: E402

TIME_BASE = 1000.0


class FakeEditorDB6(FakeEditorDB):
    step_cost = 0.0       # game seconds one executed step (or the start of a wait) costs; Unity: one editor tick
    start_offset = 0.0    # game seconds already elapsed when the scenario's first step runs
    poll = 1.0 / FPS      # wait_condition re-reads once per editor tick

    def _advance(self, seconds):
        if seconds > 0:
            self.clock += seconds
            self._simulate_spawners()

    def _skill_editor_get_state(self, agent, args):
        out = FakeEditorDB._skill_editor_get_state(self, agent, args)
        out["timeSinceStartup"] = TIME_BASE + getattr(self, "clock", 0.0)
        return out

    def _skill_prefab_instantiate(self, agent, args):
        record = self.prefabs.get(args.get("prefabPath"))
        if record is None:
            raise _fail("ASSET_NOT_FOUND", "Prefab not found: %s" % args.get("prefabPath"))
        item = {"name": args.get("name") or record["name"], "x": args.get("x", 0), "y": args.get("y", 0),
                "z": args.get("z", 0), "primitiveType": (record.get("mesh") or "").lower()}
        obj = self._create(agent, item, self.find(args, "parent", required=False))
        return {"success": True, "name": obj.name, "entityId": obj.eid, "instanceId": obj.iid, "path": self.path(obj)}

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
                if isinstance(prefab, tuple) and prefab[0] == "asset" and prefab[1] in self.prefabs:
                    base = self.prefabs[prefab[1]]["name"]
                elif isinstance(prefab, tuple) and prefab[0] == "object" and self.objects.get(prefab[1]) is not None:
                    base = self.objects[prefab[1]].name
                else:
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
                    "base": base,
                    "name": base + "(Clone)",
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
                name = "%s_%d" % (model["base"], model["made"] + 1) if "renamed_clones" in variants else model["name"]
                self._spawn_instance(model["parent"], name, world_zero="keep_world_pos" in variants)
                model["made"] += 1

    def run_scenario(self, spec, agent=RUNNER):
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        saved = copy.deepcopy((self.objects, self.roots, self.owners))
        self.playing = spec.get("executionMode", "play") == "play"
        self.clock = 0.0
        self.runtime_error = False
        deadline = float(spec.get("timeoutSeconds") or 60)
        if self.playing:
            self._init_spawners()
            self._simulate_spawners()
            self._advance(self.start_offset)
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind == "wait_frames":
                    self._advance(self.step_cost)
                    self._advance(int(step.get("frames") or 0) / FPS)
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                if kind == "wait_seconds":
                    self._advance(self.step_cost)
                    self._advance(float(step.get("seconds") or 0))
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                actual = last
                if kind == "wait_condition":
                    self._advance(self.step_cost)
                    while True:
                        try:
                            actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                        except SkillError as exc:
                            actual = exc.payload
                        selected = _select(actual, step.get("select"))
                        if _matches(selected, step.get("expected"), float(step.get("tolerance") or 0)):
                            records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                            "expected": step.get("expected"), "actual": selected, "passed": True})
                            break
                        if self.clock > deadline:
                            passed, error = False, "Scenario timeout; steps will not be replayed."
                            break
                        self._advance(max(self.poll, self.step_cost))
                    if not passed:
                        break
                    last = selected
                    continue
                if kind in ("read", "invoke") or (kind == "assert" and step.get("skill")):
                    self._advance(self.step_cost)
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except SkillError as exc:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": exc.payload, "passed": False})
                        passed, error = False, "Step refused or failed; inspect its actual response."
                        break
                if kind == "assert":
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
