"""In-memory editor for the DB4 offline tests: fake_dev_db.FakeEditorDB plus what the follow-camera task touches.

Added to the dev model: Vector3 fields (code defaults such as `new Vector3(0f, 3f, -6f)` or `new(0f, 3f, -6f)` and
values such as "2.5,5,-8", reported as Unity's G9 "x,y,z"), [SerializeField] private fields, Transform.set_position
through component_invoke_method in Play Mode (UnityEngine.Transform only), and a frame-stepped Play Mode stand-in for
editor_run_scenario: one player frame runs when Play starts and before every step, wait_frames runs its frames, and
the scene is put back afterwards, as leaving Play Mode does. In each frame a component whose class declares target,
offset, smoothing and smoothSpeed does what the solutions' scripts say: the point target.position + offset, set as the
world position when smoothing is false, else lerped toward with smoothSpeed * deltaTime. A `// usk-fake: <variant>`
comment selects a wrong behaviour the negatives describe (local_offset, local_position); following in Update instead
of LateUpdate behaves the same here as in Unity, where the vehicle only moves between frames. This models the
solutions, not C#.
"""
import copy
import json
import re

from .fake_dev_db import FPS, FakeEditorDB
from .pack_fake_editor import RUNNER, _fail, _matches, _select, q_rotate
from usk_eval.rest import SkillError  # noqa: E402

PRIVATE_FIELD_RE = re.compile(r"(\[[^\]]*SerializeField[^\]]*\]\s*)private\s+")
TRANSFORM_TYPE = "UnityEngine.Transform"
FOLLOW_FIELDS = frozenset(("target", "offset", "smoothing", "smoothSpeed"))


def vector_text(text):
    """Canonical "x,y,z" (G9) of a Vector3 initializer or value text; ValueError when it is not three numbers."""
    raw = str(text)
    if "(" in raw and ")" in raw:
        raw = raw[raw.index("(") + 1:raw.rindex(")")]
    values = [float(part.strip().rstrip("fFdD")) for part in raw.split(",")]
    if len(values) != 3:
        raise ValueError("not a Vector3: %r" % (text,))
    return ",".join("%.9g" % value for value in values)


def vector(value):
    try:
        return tuple(float(part) for part in vector_text(value).split(","))
    except (TypeError, ValueError):
        return (0.0, 0.0, 0.0)


class FakeEditorDB4(FakeEditorDB):
    fps = FPS                    # player frames per second of game time

    def _compile(self):
        """The base parser reads public fields only; [SerializeField] private ones serialize too."""
        sources = self.scripts
        self.scripts = {path: PRIVATE_FIELD_RE.sub(r"\1public ", text) for path, text in sources.items()}
        try:
            super(FakeEditorDB4, self)._compile()
        finally:
            self.scripts = sources

    def _parse_value(self, kind, text):
        if kind in ("Vector3", "UnityEngine.Vector3"):
            try:
                return vector_text(text)
            except ValueError:
                return str(text)
        return super(FakeEditorDB4, self)._parse_value(kind, text)

    def _skill_component_invoke_method(self, agent, args):
        if args.get("componentType") != TRANSFORM_TYPE or args.get("methodName") != "set_position":
            return super(FakeEditorDB4, self)._skill_component_invoke_method(agent, args)
        if (args.get("executionMode") or "play") == "play" and not self.playing:
            raise _fail("SEMANTIC_INVALID", "executionMode does not match the current Editor mode")
        obj = self.find(args)
        supplied = json.loads(args.get("arguments") or "[]")
        if len(supplied) != 1:
            raise _fail("SEMANTIC_INVALID", "set_position takes one Vector3 argument")
        self._set_world(obj, pos=vector(supplied[0]))
        return {"returnValue": None, "refOut": {}, "signature": "set_position(UnityEngine.Vector3)"}

    # ------------------------------------------------------------ Play Mode

    def _followers(self):
        for obj in self.walk():
            if not self.active_in_hierarchy(obj):
                continue
            for kind, enabled in obj.components:
                names = {name for _k, name, _d, _o in (self.classes.get(kind) or {}).get("fields", ())}
                if enabled is not False and kind in obj.fields and FOLLOW_FIELDS <= names:
                    yield obj, kind, obj.fields[kind]

    def _frame(self):
        """One player frame: every follow component's LateUpdate."""
        dt = 1.0 / self.fps
        for obj, cls, fields in list(self._followers()):
            ref = fields.get("target")
            followed = self.objects.get(ref[1]) if isinstance(ref, tuple) and ref[0] == "object" else None
            if followed is None:
                continue
            variants = self._variants(cls)
            position, rotation, _scale = self.world(followed)
            offset = vector(fields.get("offset"))
            if "local_offset" in variants:
                offset = q_rotate(rotation, offset)
            point = tuple(a + b for a, b in zip(position, offset))
            local = "local_position" in variants
            if fields.get("smoothing"):
                current = obj.pos if local else self.world(obj)[0]
                t = max(0.0, min(1.0, float(fields.get("smoothSpeed") or 0.0) * dt))
                point = tuple(a + (b - a) * t for a, b in zip(current, point))
            if local:
                obj.pos = point
            else:
                self._set_world(obj, pos=point)

    def _tick(self, frames=1):
        for _ in range(frames if self.playing else 0):
            self.clock += 1.0 / self.fps
            self._frame()

    def run_scenario(self, spec, agent=RUNNER):
        steps, records, passed, error, last = spec.get("steps") or [], [], True, None, None
        saved = copy.deepcopy((self.objects, self.roots, self.owners))
        self.playing = spec.get("executionMode", "play") == "play"
        self.clock = 0.0
        budget = int(float(spec.get("timeoutSeconds") or 60) * self.fps)
        logged = self.play_mode_errors() if self.playing else []
        try:
            self._tick()
            for index, step in enumerate(steps):
                kind = step.get("kind")
                if kind in ("wait_frames", "wait_seconds"):
                    count = int(step.get("frames") or 0) if kind == "wait_frames" else \
                        int(round(float(step.get("seconds") or 0) * self.fps))
                    self._tick(max(1, count))
                    records.append({"index": index, "kind": kind, "passed": True, "actual": None})
                    continue
                self._tick()
                actual = last
                if kind in ("read", "invoke", "wait_condition") or (kind == "assert" and step.get("skill")):
                    try:
                        actual = self.call(step["skill"], dict(step.get("args") or {}), agent)
                        while kind == "wait_condition" and budget > 0 and not _matches(
                                _select(actual, step.get("select")), step.get("expected"),
                                float(step.get("tolerance") or 0)):
                            budget -= 1
                            self._tick()
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
                        passed, error = False, ("Scenario timeout; steps will not be replayed."
                                                if kind == "wait_condition" else "Assertion failed.")
                        break
                else:
                    records.append({"index": index, "kind": kind, "skill": step.get("skill"), "actual": actual,
                                    "passed": True})
                last = actual
        finally:
            self.playing = False
            self.objects, self.roots, self.owners = saved
        if passed and logged:
            passed, error = False, "Runtime error logs were captured."
        return {"status": "completed" if passed else "failed", "passed": passed, "error": error,
                "interrupted": False, "executionUncertain": False, "steps": records, "compilation": None,
                "logWindow": {"complete": True, "entries": logged}}

# Producer:Betsy
