"""In-memory editor for the offline tests of DS1 and DS3: a virtual clock for Play Mode scenarios plus behaviour models
for the two classes the agent writes.

pack_fake_editor "compiles" scripts by parsing their public fields and cannot run C#, so DevFakeEditor models the
behaviour the task asks for from the serialized field values: a patrol (route, speed, pauseSeconds) walks its object
along the route's children, a spawner (config, prefab, container) fills its container on schedule. A script that
carries a `// usk-fake: <variant>` comment is modelled with that wrong behaviour instead (the solutions' negative
variants carry one; a real editor ignores the comment). wait_seconds / wait_frames advance the clock, every other step
sees the state at the current clock.
"""
import math
import re

from .pack_fake_editor import CLASS_RE, RUNNER, Obj, _matches, _select
from .pack_offline import OpsFakeEditor
from usk_eval.rest import SkillError  # noqa: E402

MARKER_RE = re.compile(r"//\s*usk-fake:\s*([\w-]+)")
FRAME_S = 1.0 / 60.0


def _distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def _lerp(a, b, share):
    return tuple(x + (y - x) * share for x, y in zip(a, b))


class DevFakeEditor(OpsFakeEditor):
    extra_reads = frozenset()

    def __init__(self, *args, **kwargs):
        OpsFakeEditor.__init__(self, *args, **kwargs)
        self.variants = {}
        self.clock = 0.0

    # ------------------------------------------------------------ scripts

    def _skill_script_create(self, agent, args):
        out = OpsFakeEditor._skill_script_create(self, agent, args)
        self._note_variants(args.get("content") or "")
        return out

    def _note_variants(self, source):
        marker = MARKER_RE.search(source)
        for match in CLASS_RE.finditer(source):
            if marker:
                self.variants[match.group(1)] = marker.group(1)

    def field_names(self, cls):
        return {name for _kind, name, _default, _old in (self.classes.get(cls) or {}).get("fields", ())}

    def holders(self, required):
        """(object, class) pairs whose class declares every field in `required` and sits on the object."""
        out = []
        for obj in self.walk():
            for cls in list(obj.fields):
                if required <= self.field_names(cls) and self.component(obj, cls):
                    out.append((obj, cls))
        return out

    def spawn(self, parent, name):
        eid, iid = self._new_ids()
        child = Obj(eid, iid, name, parent)
        parent.children.append(child)
        self.objects[eid] = child
        return child

    # ------------------------------------------------------------ Play Mode model

    def play_start(self):
        """Called once when a scenario enters Play Mode, before any step."""

    def simulate(self):
        """Brings the modelled objects to the state they have at self.clock."""

    def run_scenario(self, spec, agent=RUNNER):
        """editor_run_scenario's job result on this model: waits advance the virtual clock and the first failed step
        or assert ends the run; `passed` needs every assert to pass (the model logs no errors)."""
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        self.playing = spec.get("executionMode", "play") == "play"
        self.clock = 0.0
        self.play_start()
        self.simulate()
        try:
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    self.clock += float(step.get("seconds") or 0) if kind == "wait_seconds" \
                        else int(step.get("frames") or 0) * FRAME_S
                    self.simulate()
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
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}


# ---------------------------------------------------------------- DS1: patrol


def patrol_position(model, t):
    """Where a modelled patrol is t seconds after Play starts: from its start towards the second waypoint, pausing
    after each leg unless `no-pause`, looping unless `no-loop`."""
    waypoints, speed, pause, variant = model["waypoints"], model["speed"], model["pause"], model["variant"]
    if len(waypoints) < 2 or speed <= 0:
        return model["start"]
    pos, index, remaining = model["start"], 1, t
    while True:
        goal = waypoints[index]
        travel = _distance(pos, goal) / speed
        if remaining <= travel:
            return _lerp(pos, goal, remaining / travel if travel > 0 else 1.0)
        remaining -= travel
        pos = goal
        if variant != "no-pause":
            if remaining <= pause:
                return goal
            remaining -= pause
        if variant == "no-loop" and index == len(waypoints) - 1:
            return goal
        index = (index + 1) % len(waypoints)


class FakeEditorDS1(DevFakeEditor):
    def play_start(self):
        self.patrols = []
        for obj, cls in self.holders({"route", "speed", "pauseSeconds"}):
            fields = obj.fields[cls]
            route = fields.get("route")
            route_obj = self.objects.get(route[1]) if isinstance(route, tuple) and route[0] == "object" else None
            if route_obj is None:
                continue
            variant = self.variants.get(cls)
            waypoints = [tuple(child.pos) if variant == "local-waypoints" else tuple(self.world(child)[0])
                         for child in route_obj.children]
            start = tuple(obj.pos) if variant == "local-position" else tuple(self.world(obj)[0])
            self.patrols.append({"obj": obj, "waypoints": waypoints, "start": start,
                                 "speed": float(fields.get("speed") or 0), "pause": float(fields.get("pauseSeconds")
                                                                                            or 0),
                                 "variant": variant})

    def simulate(self):
        for model in getattr(self, "patrols", []):
            pos = patrol_position(model, self.clock)
            if model["variant"] == "local-position":
                model["obj"].pos = pos
            else:
                self._set_world(model["obj"], pos=pos)


# ---------------------------------------------------------------- DS3: spawner


class FakeEditorDS3(DevFakeEditor):
    def play_start(self):
        self.spawners = []
        for obj, cls in self.holders({"config", "prefab", "container"}):
            fields, variant = obj.fields[cls], self.variants.get(cls)
            config, prefab, container = fields.get("config"), fields.get("prefab"), fields.get("container")
            if variant == "runtime-instance":
                kind = next((k for k, name, _d, _o in self.classes[cls]["fields"] if name == "config"), None)
                values = {name: default for _k, name, default, _o in (self.classes.get(kind) or {}).get("fields", ())}
            elif isinstance(config, tuple) and config[0] == "asset" and config[1] in self.sos:
                values = self.sos[config[1]]["fields"]
            else:
                continue
            if not (isinstance(prefab, tuple) and prefab[0] == "asset" and prefab[1] in self.prefabs):
                continue
            parent = self.objects.get(container[1]) if isinstance(container, tuple) and container[0] == "object" \
                else None
            if variant == "wrong-container":
                parent = obj
            if parent is None:
                continue
            self.spawners.append({"parent": parent, "name": self.prefabs[prefab[1]]["name"] + "(Clone)",
                                  "count": int(values.get("count") or 0), "interval": float(values.get("interval")
                                                                                           or 0),
                                  "variant": variant, "made": 0})

    def simulate(self):
        for model in getattr(self, "spawners", []):
            interval = model["interval"]
            if interval <= 0:
                continue
            due = int(math.floor(self.clock / interval + 1e-6))
            if model["variant"] == "spawn-at-zero":
                due += 1
            if model["variant"] != "no-limit":
                due = min(due, model["count"])
            while model["made"] < due:
                self.spawn(model["parent"], model["name"])
                model["made"] += 1

# Producer:Betsy
