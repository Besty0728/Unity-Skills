"""Offline fake editor for DC8 (ScriptableObject difficulty configs and a spawner switched between them).

FakeEditorDC plus a frame clock for editor_run_scenario. The config assets are the base fake's ScriptableObject
records; the spawner component keeps its `config` object reference in its serialized fields. Every frame advances the
real clock by the frame time (`fps`, plus `step_cost` for a frame a scenario step costs) and the game clock by the same
time clamped like Time.maximumDeltaTime, then updates each spawner. A scenario step costs one frame, a wait_condition
polls once per frame until it matches or the scenario's timeout passes, wait_seconds runs frames for that long, and
editor_get_state reports timeSinceStartup on the real clock.

The spawner follows the behaviour its compiled C# source shows (comments stripped), not its name: Update with
Time.deltaTime or a WaitForSeconds coroutine; numbers read live from the active asset or copied into private fields
at Awake (and again in SetConfig when it reads them again); the config cloned with Instantiate; SetConfig copying
numbers into the current asset instead of switching the reference; a wave capped by the container's children, by a
counter of what was spawned, or not at all (a full wave below the cap). `// usk-fake:` markers are not read.
"""
import copy
import re

from .fake_dev_dc import FakeEditorDC, strip_comments
from .pack_fake_editor import RUNNER, Obj, _fail, _matches, _select
from usk_eval.rest import SkillError  # noqa: E402

MAX_DELTA = 1.0 / 3.0
TIME_BASE = 1000.0
START_FRAMES = 3
FIELDS = ("spawnInterval", "spawnsPerWave", "maxAlive")
SET_CONFIG_RE = re.compile(r"\bvoid\s+SetConfig\s*\([^)]*\)\s*\{(.*?)\n    \}", re.S)
AWAKE_BODY_RE = re.compile(r"\bvoid\s+(?:Awake|Start)\s*\(\s*\)\s*\{(.*?)\n    \}", re.S)


class FakeEditorDC8(FakeEditorDC):
    fps = 50.0
    step_cost = 0.0       # extra real seconds one executed step (or poll) costs on top of one frame
    start_hitch = 0.0     # extra real seconds the first Play frame takes

    def __init__(self, *args, **kwargs):
        FakeEditorDC.__init__(self, *args, **kwargs)
        self.spawners = {}
        self.real_time, self.game_time = 0.0, 0.0
        self.clones = 0

    # ------------------------------------------------------------ the spawner model

    def _is_spawner(self, cls):
        spec = self.classes.get(cls)
        if not spec:
            return False
        names = {name: kind for kind, name, _default, _old in spec["fields"]}
        return names.get("config") in self.classes and "container" in names and "prefab" in names

    def _model(self, cls):
        """How the spawner class behaves, read from its source."""
        src = strip_comments(self.compiled_sources.get(self.classes[cls]["path"], ""))
        found = SET_CONFIG_RE.search(src)
        switch = found.group(1) if found else ""
        awake = "".join(AWAKE_BODY_RE.findall(src))
        cached = re.search(r"\b_interval\s*=\s*config\.spawnInterval", src) is not None
        model = {
            "coroutine": "IEnumerator" in src and "WaitForSeconds" in src,
            "cached": cached,
            "refresh": cached and (re.search(r"\bAwake\s*\(\s*\)\s*;", switch) is not None or
                                   re.search(r"\b_interval\s*=", switch) is not None),
            "clone_awake": re.search(r"\bInstantiate\s*\(\s*config\s*\)|CreateInstance", awake) is not None,
            "clone_switch": re.search(r"\bInstantiate\s*\(\s*next\s*\)|CreateInstance", switch) is not None,
            "sets_config": re.search(r"\bconfig\s*=\s*(?:next|Instantiate\s*\(\s*next\s*\))", switch) is not None,
            "writes": re.search(r"\bconfig\.(?:spawnInterval|spawnsPerWave|maxAlive)\s*=(?!=)", switch) is not None,
            "resets": "_elapsed = 0" in switch or re.search(r"\bRestart\s*\(\s*\)", switch) is not None,
            "counter": re.search(r"maxAlive\s*-\s*_spawned", src) is not None,
            "counter_reset": "_spawned = 0" in switch,
            "min": re.search(r"Mathf\.Min\s*\(", src) is not None,
            "guard": re.search(r"childCount\s*>=\s*(?:config|active)\.maxAlive", src) is not None,
        }
        return model

    def _values(self, spawner, path=None):
        record = self.sos.get(path or spawner["config"][1]) if (path or spawner["config"]) else None
        return record["fields"] if record else None

    def enter_play(self):
        self.spawners = {}
        for obj in self.walk():
            for cls, _enabled in obj.components:
                if not self._is_spawner(cls):
                    continue
                fields = obj.fields.get(cls, {})
                spawner = {"cls": cls, "obj": obj, "model": self._model(cls), "config": fields.get("config"),
                           "elapsed": 0.0, "spawned": 0, "cache": None, "co_cfg": None, "t_end": 0.0}
                self.spawners[(obj.eid, cls)] = spawner
                if spawner["model"]["clone_awake"] and spawner["config"]:
                    self._set_config_ref(spawner, spawner["config"][1], clone=True)
                self._cache(spawner)
                if spawner["model"]["coroutine"]:
                    self._restart(spawner)

    def _set_config_ref(self, spawner, path, clone):
        self.clones += clone
        spawner["config"] = ("asset", path, self.clones) if clone else ("asset", path)
        spawner["obj"].fields[spawner["cls"]]["config"] = spawner["config"]

    def _cache(self, spawner):
        values = self._values(spawner)
        spawner["cache"] = tuple(values[name] for name in FIELDS) if values else None

    def _restart(self, spawner):
        """StopCoroutine + StartCoroutine(Waves(config)): the wait begins now on the config at this moment."""
        values = self._values(spawner)
        usable = values is not None and self._wired(spawner)
        spawner["co_cfg"] = spawner["config"][1] if usable else None
        spawner["t_end"] = self.game_time + (values["spawnInterval"] if usable else 0.0)

    def _wired(self, spawner):
        fields = spawner["obj"].fields[spawner["cls"]]
        return bool(spawner["config"] and fields.get("prefab") and fields.get("container"))

    def _wave(self, spawner, path):
        fields = spawner["obj"].fields[spawner["cls"]]
        model, values = spawner["model"], self._values(spawner, path)
        if values is None:
            return
        per, cap = (spawner["cache"][1], spawner["cache"][2]) if model["cached"] and spawner["cache"] else \
            (values["spawnsPerWave"], values["maxAlive"])
        container = self.objects[fields["container"][1]]
        children = len(container.children)
        if model["counter"]:
            count = min(per, cap - spawner["spawned"])
        elif model["min"]:
            count = min(per, cap - children)
        elif model["guard"]:
            count = per if children < cap else 0
        else:
            count = per
        prefab = fields["prefab"][1].rsplit("/", 1)[-1].rsplit(".", 1)[0]
        for _ in range(max(0, count)):
            eid, iid = self._new_ids()
            clone = Obj(eid, iid, prefab + "(Clone)", container)
            container.children.append(clone)
            self.objects[eid] = clone
            self.owners[self.transform_id(clone)] = eid
            spawner["spawned"] += 1

    def _update(self, spawner, delta):
        model = spawner["model"]
        if model["coroutine"]:
            path = spawner["co_cfg"]
            if path is not None and self.game_time >= spawner["t_end"]:
                self._wave(spawner, path)
                interval = self._values(spawner, path)["spawnInterval"]
                if interval > 0:
                    spawner["t_end"] = self.game_time + interval
                else:
                    spawner["co_cfg"] = None
            return
        if not self._wired(spawner):
            return
        values = self._values(spawner)
        interval = spawner["cache"][0] if model["cached"] and spawner["cache"] else (
            values["spawnInterval"] if values else 0.0)
        if interval <= 0:
            return
        spawner["elapsed"] += delta
        while spawner["elapsed"] >= interval:
            spawner["elapsed"] -= interval
            self._wave(spawner, spawner["config"][1])

    def invoke_model(self, obj, cls, method, arguments, parameter_types=None):
        spawner = self.spawners.get((obj.eid, cls))
        if spawner is None or method != "SetConfig":
            return FakeEditorDC.invoke_model(self, obj, cls, method, arguments, parameter_types)
        if len(arguments) != 1 or not isinstance(arguments[0], dict) or \
                (parameter_types and parameter_types != [self._config_type(cls)]):
            raise _fail("SEMANTIC_INVALID", "SetConfig takes one config asset")
        path = arguments[0].get("assetPath")
        if path not in self.sos:
            raise _fail("ASSET_NOT_FOUND", "Asset not found: %s" % path)
        model = spawner["model"]
        if model["writes"] and spawner["config"]:
            self.sos[spawner["config"][1]]["fields"].update({name: self.sos[path]["fields"][name] for name in FIELDS})
        if model["sets_config"] or model["clone_switch"]:
            self._set_config_ref(spawner, path, clone=model["clone_switch"])
        if model["counter_reset"]:
            spawner["spawned"] = 0
        if model["resets"]:
            spawner["elapsed"] = 0.0
        if model["refresh"]:
            self._cache(spawner)
        if model["coroutine"]:
            self._restart(spawner)
        return None

    def _config_type(self, cls):
        return next(kind for kind, name, _d, _o in self.classes[cls]["fields"] if name == "config")

    def format_value(self, value):
        """A runtime clone of an asset answers with its own id and the name Unity gives a clone."""
        if isinstance(value, tuple) and len(value) == 3 and value[0] == "asset":
            _id, kind, name = FakeEditorDC.format_value(self, value[:2]).split(":", 2)
            return "%d:%s:%s(Clone)" % (-(900000 + value[2]), kind, name)
        return FakeEditorDC.format_value(self, value)

    # ------------------------------------------------------------ the frame clock

    def _skill_editor_get_state(self, agent, args):
        out = FakeEditorDC._skill_editor_get_state(self, agent, args)
        out["timeSinceStartup"] = TIME_BASE + self.real_time
        return out

    def _frame(self, real):
        self.real_time += real
        delta = min(real, MAX_DELTA) * self.time_scale
        self.game_time += delta
        for spawner in self.spawners.values():
            self._update(spawner, delta)

    def _tick(self):
        self._frame(1.0 / self.fps + self.step_cost)

    def _advance_seconds(self, seconds):
        end = self.real_time + seconds
        while self.real_time < end:
            self._frame(1.0 / self.fps)

    def run_scenario(self, spec, agent=RUNNER):
        """editor_run_scenario's job result on this model with the frame clock: steps in order, the first refused step,
        failed assert or timed-out wait ends it; the scene is put back afterwards, the assets are not (Play Mode edits
        of an asset stay)."""
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        if spec.get("executionMode", "play") == "play" and self.compile_errors:
            return {"status": "failed", "passed": False, "error": "Play Mode refused: compilation errors.",
                    "interrupted": False, "executionUncertain": False, "steps": [], "compilation": None,
                    "logWindow": {"complete": True, "entries": []}}
        saved = copy.deepcopy((self.objects, self.roots))
        self.playing = spec.get("executionMode", "play") == "play"
        self.time_scale, self.runtime_error = 1.0, False
        self.real_time = self.game_time = 0.0
        deadline = float(spec.get("timeoutSeconds") or 60)
        try:
            if self.playing:
                self.enter_play()
                self._frame(1.0 / self.fps + self.start_hitch)
                for _ in range(START_FRAMES - 1):
                    self._frame(1.0 / self.fps)
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    self._tick()
                    if kind == "wait_frames":
                        for _ in range(int(step.get("frames") or 0)):
                            self._frame(1.0 / self.fps)
                    else:
                        self._advance_seconds(float(step.get("seconds") or 0))
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                self._tick()
                actual = last
                while True:
                    if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                        try:
                            actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                        except SkillError as exc:
                            records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                            "actual": exc.payload, "passed": False})
                            passed, error = False, "Step refused or failed; inspect its actual response."
                            break
                    if kind not in ("assert", "wait_condition"):
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"), "actual": actual,
                                        "passed": True})
                        break
                    selected = _select(actual, step.get("select"))
                    ok = _matches(selected, step.get("expected"), float(step.get("tolerance") or 0))
                    if ok or kind == "assert":
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "expected": step.get("expected"), "actual": selected, "passed": ok})
                        if not ok:
                            passed, error = False, "Assertion failed."
                        break
                    if self.real_time > deadline:
                        records.append({"index": index, "kind": kind, "skill": step.get("skill"),
                                        "expected": step.get("expected"), "actual": selected, "passed": False})
                        passed, error = False, "Scenario timed out."
                        break
                    self._tick()
                if not passed:
                    break
                last = actual
            if passed and self.runtime_error:
                passed, error = False, "Runtime error logs were captured."
        finally:
            self.playing = False
            self.objects, self.roots = saved
            self.spawners = {}
            self.time_scale = 1.0
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": []}}

# Producer:Betsy
