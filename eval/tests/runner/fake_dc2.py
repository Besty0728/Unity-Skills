"""Offline fake editor for DC2 (door state machine).

FakeEditorDC plus a frame clock for editor_run_scenario: every frame advances Time.time by the frame's real time
(clamped like Time.maximumDeltaTime) times Time.timeScale, updates each door component, then lets the fixture
recorder sample it as its LateUpdate does. Scenario steps cost frames the way the plugin's ticks do: a read or an
assert one frame, an invoke one frame before and one after the call (child job), a wait one frame more than it asks.
The frame rate is `fps` (`step_cost` adds real time to the frame of each step), or random per frame with occasional hitches when `jitter_seed` is set. A door follows the
task's rules unless its source carries `// usk-fake: <variant>`: coroutine (one motion, stopped on a switch, moving in
the call itself), frame-step (1/10000 of a travel per frame), unscaled, open-restarts, restart-reverse, leak (a coroutine per switch, never stopped,
first move one frame later), double (like leak but a repeated call starts another), no-clamp, anchor (openness from a
Time.time anchor re-set on each switch) or snap (WaitForSeconds then a jump).
"""
import copy
import random
import re

from .fake_dev_dc import FakeEditorDC, strip_comments
from .pack_fake_editor import RUNNER, _fail, _matches, _select
from usk_eval.rest import SkillError  # noqa: E402

MAX_DELTA = 1.0 / 3.0
OPENNESS_RE = re.compile(r"\bpublic\s+(?:virtual\s+|override\s+|new\s+)*float\s+openness\b")
METHOD_RE = r"\bpublic\s+(?:virtual\s+)?void\s+%s\s*\(\s*\)"
START_FRAMES = 3
ATTR_RE = re.compile(r"\[SerializeField\s*,[^\]]*\]")
EXPR_PROP_RE = re.compile(r"\bpublic\s+([\w.<>]+)\s+(\w+)\s*=>\s*([^;]*);")


def move_towards(current, goal, step):
    if abs(goal - current) <= step:
        return goal
    return current + (step if goal > current else -step)


def terminal(goal):
    return "Open" if goal >= 1.0 else "Closed"


class FakeEditorDC2(FakeEditorDC):
    fps = 53.0            # not a divisor of the travel grid, so an unclamped door overshoots like in a real editor
    step_cost = 0.0       # extra real seconds the frame of an executed read / invoke / assert step costs
    jitter_seed = None

    def __init__(self, *args, **kwargs):
        FakeEditorDC.__init__(self, *args, **kwargs)
        self.doors = {}
        self.game_time, self.frame_count, self.delta = 0.0, 0, 0.0
        self._jitter = None

    # ------------------------------------------------------------ door components

    def _compile(self):
        """The shared compile model understands `[SerializeField]` and `{ get ...}` only: rewrite the two C# forms it
        does not (`[SerializeField, Min(x)]` attributes and expression-bodied `public T name => expr;`) first."""
        original = self.scripts
        self.scripts = {path: EXPR_PROP_RE.sub(r"public \1 \2 { get { return \3; } }", ATTR_RE.sub("[SerializeField]", text))
                        for path, text in original.items()}
        try:
            FakeEditorDC._compile(self)
        finally:
            self.scripts = original

    def _is_door(self, cls):
        spec = self.classes.get(cls)
        return bool(spec) and OPENNESS_RE.search(strip_comments(self.compiled_sources.get(spec["path"], ""))) is not None

    def enter_play(self):
        self.doors = {}
        for obj in self.walk():
            for cls in [kind for kind, _enabled in obj.components if self._is_door(kind)]:
                try:
                    travel = float(obj.fields.get(cls, {}).get("travelSeconds"))
                except (TypeError, ValueError):
                    travel = 0.5
                self.doors[(obj.eid, cls)] = {"o": 0.0, "state": "Closed", "T": travel, "variants": self.variants(cls),
                                              "motions": [], "timer": None, "goal": None, "dir": 0, "a": 0.0, "at": 0.0}

    def _rate(self, door, step):
        return step / door["T"] if door["T"] > 0 else 1.0

    def _update(self, door, scaled, real):
        variants = door["variants"]
        if "anchor" in variants:
            if door["dir"]:
                door["o"] = min(1.0, max(0.0, door["a"] + door["dir"] * (self.game_time - door["at"]) / door["T"]))
                if door["dir"] > 0 and door["o"] >= 1.0:
                    door["state"], door["dir"] = "Open", 0
                elif door["dir"] < 0 and door["o"] <= 0.0:
                    door["state"], door["dir"] = "Closed", 0
            return
        if "leak" in variants or "double" in variants:
            alive = []
            for goal in door["motions"]:
                door["o"] = move_towards(door["o"], goal, self._rate(door, scaled))
                if door["o"] == goal:
                    door["state"] = terminal(goal)
                else:
                    alive.append(goal)
            door["motions"] = alive
            return
        if "coroutine" in variants:
            if door["goal"] is not None:
                self._coroutine_step(door)
            return
        if "snap" in variants:
            if door["timer"] is not None:
                door["timer"] -= scaled
                if door["timer"] <= 1e-12:
                    door["o"], door["state"], door["timer"] = door["goal"], terminal(door["goal"]), None
            return
        if "frame-step" in variants:
            step = 1.0 / (10000.0 * door["T"])
        elif "unscaled" in variants:
            step = self._rate(door, real)
        else:
            step = self._rate(door, scaled)
        if door["state"] == "Opening":
            door["o"] = door["o"] + step if "no-clamp" in variants else min(1.0, door["o"] + step)
            if door["o"] >= 1.0:
                door["state"] = "Open"
        elif door["state"] == "Closing":
            door["o"] = door["o"] - step if "no-clamp" in variants else max(0.0, door["o"] - step)
            if door["o"] <= 0.0:
                door["state"] = "Closed"

    def _coroutine_step(self, door):
        goal = door["goal"]
        door["o"] = move_towards(door["o"], goal, self._rate(door, self.delta))
        if door["o"] == goal:
            door["state"], door["goal"] = terminal(goal), None

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        door = self.doors.get((obj.eid, cls))
        if door is None:
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        source = strip_comments(self.compiled_sources.get(self.classes[cls]["path"], ""))
        if method not in ("Open", "Close") or not re.search(METHOD_RE % method, source):
            raise _fail("SEMANTIC_INVALID", "No method %s() on %s" % (method, cls))
        opening = method == "Open"
        moving, goal = ("Opening", 1.0) if opening else ("Closing", 0.0)
        same = ("Opening", "Open") if opening else ("Closing", "Closed")
        variants = door["variants"]
        if "open-restarts" in variants and opening:
            door["o"], door["state"] = 0.0, "Opening"
            return None
        if "double" in variants:
            if door["o"] == goal:
                door["state"] = terminal(goal)
                return None
            door["state"] = moving
            door["motions"].append(goal)
            return None
        if door["state"] in same:
            return None
        if "restart-reverse" in variants and door["state"] in ("Closing", "Opening"):
            door["o"] = 0.0 if opening else 1.0
        door["state"] = moving
        if "anchor" in variants:
            door["a"], door["at"], door["dir"] = door["o"], self.game_time, 1 if opening else -1
        elif "leak" in variants:
            door["motions"].append(goal)
        elif "coroutine" in variants:
            door["goal"] = goal
            self._coroutine_step(door)
        elif "snap" in variants:
            door["goal"], door["timer"] = goal, door["T"]
        return None

    # ------------------------------------------------------------ the recorder and the frame clock

    def _sample(self):
        for obj in self.walk():
            for cls, _enabled in obj.components:
                values = obj.fields.get(cls)
                if not isinstance(values, dict) or "sampleOpenness" not in values:
                    continue
                link = values.get("door")
                found = [door for (eid, _cls), door in sorted(self.doors.items())
                         if isinstance(link, tuple) and eid == link[1]]
                values.update(sampleTime=self.game_time, sampleFrame=self.frame_count, sampleCount=len(found),
                              sampleOpenness=found[-1]["o"] if found else -1.0,
                              sampleState=found[-1]["state"] if found else "")

    def _real_dt(self):
        if self._jitter is not None:
            return 0.5 if self._jitter.random() < 0.02 else self._jitter.uniform(1.0 / 120.0, 1.0 / 15.0)
        return 1.0 / self.fps

    def _frame(self, extra=0.0):
        real = self._real_dt() + extra
        scaled = min(real, MAX_DELTA) * self.time_scale
        self.frame_count += 1
        self.game_time += scaled
        self.delta = scaled
        for door in self.doors.values():
            self._update(door, scaled, real)
        self._sample()
        return real

    def _advance(self, frames):
        for _ in range(frames):
            self._frame()

    def _advance_seconds(self, seconds):
        spent = 0.0
        while spent < seconds:
            spent += self._frame()

    def run_scenario(self, spec, agent=RUNNER):
        """editor_run_scenario's job result on this model with the frame clock: steps in order, the first refused step
        or failed assert ends it; the scene and the clock are put back afterwards."""
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        saved = copy.deepcopy((self.objects, self.roots))
        self.playing = spec.get("executionMode", "play") == "play"
        self.time_scale, self.runtime_error = 1.0, False
        self.game_time, self.frame_count, self.delta = 0.0, 0, 0.0
        self._jitter = random.Random(self.jitter_seed) if self.jitter_seed is not None else None
        try:
            if self.playing:
                self.enter_play()
                self._advance(START_FRAMES)
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    if self.playing and kind == "wait_frames":
                        self._advance(int(step.get("frames") or 0) + 1)
                    elif self.playing:
                        self._advance_seconds(float(step.get("seconds") or 0))
                        self._advance(1)
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                if self.playing:
                    self._frame(self.step_cost)
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                    except SkillError as exc:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "actual": exc.payload, "passed": False})
                        passed, error = False, "Step refused or failed; inspect its actual response."
                        break
                    if kind == "invoke" and self.playing:
                        self._frame(self.step_cost)
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
            if passed and self.runtime_error:
                passed, error = False, "Runtime error logs were captured."
        finally:
            self.playing = False
            self.objects, self.roots = saved
            self.doors = {}
            self.time_scale = 1.0
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

# Producer:Betsy
