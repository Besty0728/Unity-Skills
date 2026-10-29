"""DC2 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources compile on Unity 2022.3 and 6000.x. A negative's source carries a `// usk-fake: <variant>` comment that
the offline fake editor reads to model its behaviour; a real editor ignores the comment and runs the C#.
"""
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

UPDATE_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    public float travelSeconds = 0.5f;
    public float openness;
    public DoorState state = DoorState.Closed;

    void Awake()
    {{
        openness = 0f;
        state = DoorState.Closed;
    }}

    public void Open()
    {{
{open_body}
    }}

    public void Close()
    {{
{close_body}
    }}

    void Update()
    {{
        float step = travelSeconds > 0f ? {step} : 1f;
        if (state == DoorState.Opening)
        {{
{open_move}
        }}
        else if (state == DoorState.Closing)
        {{
{close_move}
        }}
    }}
}}
"""

OPEN_BODY = "        if (state == DoorState.Closed || state == DoorState.Closing) state = DoorState.Opening;"
CLOSE_BODY = "        if (state == DoorState.Open || state == DoorState.Opening) state = DoorState.Closing;"
STEP = "Time.deltaTime / travelSeconds"
OPEN_MOVE = ("            openness = Mathf.Min(1f, openness + step);\n"
             "            if (openness >= 1f) state = DoorState.Open;")
CLOSE_MOVE = ("            openness = Mathf.Max(0f, openness - step);\n"
              "            if (openness <= 0f) state = DoorState.Closed;")

VARIANT_PARTS = {
    "frame-step": {"step": "1f / (10000f * travelSeconds)"},
    "no-clamp": {"open_move": "            openness += step;\n            if (openness >= 1f) state = DoorState.Open;",
                 "close_move": "            openness -= step;\n            if (openness <= 0f) state = DoorState.Closed;"},
    "unscaled": {"step": "Time.unscaledDeltaTime / travelSeconds"},
    "open-restarts": {"open_body": "        openness = 0f;\n        state = DoorState.Opening;"},
    "restart-reverse": {
        "open_body": "        if (state == DoorState.Opening || state == DoorState.Open) return;\n"
                     "        if (state == DoorState.Closing) openness = 0f;\n"
                     "        state = DoorState.Opening;",
        "close_body": "        if (state == DoorState.Closing || state == DoorState.Closed) return;\n"
                      "        if (state == DoorState.Opening) openness = 1f;\n"
                      "        state = DoorState.Closing;"},
}

COROUTINE_SRC = """using System.Collections;
using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    [SerializeField] private float travelSeconds = 0.5f;

    public float openness {{ get; private set; }}
    public DoorState state {{ get; private set; }}

    Coroutine motion;

    void Awake()
    {{
        openness = 0f;
        state = DoorState.Closed;
    }}

    public void Open()
    {{
        if (state == DoorState.Opening || state == DoorState.Open) return;
        Begin(DoorState.Opening, 1f);
    }}

    public void Close()
    {{
        if (state == DoorState.Closing || state == DoorState.Closed) return;
        Begin(DoorState.Closing, 0f);
    }}

    void Begin(DoorState moving, float goal)
    {{
        if (motion != null) StopCoroutine(motion);
        state = moving;
        motion = StartCoroutine(Run(goal));
    }}

    IEnumerator Run(float goal)
    {{
        while (true)
        {{
            openness = Mathf.MoveTowards(openness, goal, Time.deltaTime / travelSeconds);
            if (openness == goal)
            {{
                state = goal >= 1f ? DoorState.Open : DoorState.Closed;
                yield break;
            }}
            yield return null;
        }}
    }}
}}
"""

LEAK_SRC = """using System.Collections;
using UnityEngine;
// usk-fake: leak
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    public float travelSeconds = 0.5f;
    public float openness;
    public DoorState state = DoorState.Closed;

    void Awake()
    {{
        openness = 0f;
        state = DoorState.Closed;
    }}

    public void Open()
    {{
        if (state == DoorState.Opening || state == DoorState.Open) return;
        state = DoorState.Opening;
        StartCoroutine(Move(1f));
    }}

    public void Close()
    {{
        if (state == DoorState.Closing || state == DoorState.Closed) return;
        state = DoorState.Closing;
        StartCoroutine(Move(0f));
    }}

    IEnumerator Move(float goal)
    {{
        while (openness != goal)
        {{
            yield return null;
            openness = Mathf.MoveTowards(openness, goal, Time.deltaTime / travelSeconds);
        }}
        state = goal >= 1f ? DoorState.Open : DoorState.Closed;
    }}
}}
"""

DOUBLE_SRC = """using System.Collections;
using UnityEngine;
// usk-fake: double
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    public float travelSeconds = 0.5f;
    public float openness;
    public DoorState state = DoorState.Closed;

    void Awake()
    {{
        openness = 0f;
        state = DoorState.Closed;
    }}

    public void Open()
    {{
        state = DoorState.Opening;
        StartCoroutine(Move(1f));
    }}

    public void Close()
    {{
        state = DoorState.Closing;
        StartCoroutine(Move(0f));
    }}

    IEnumerator Move(float goal)
    {{
        while (openness != goal)
        {{
            yield return null;
            openness = Mathf.MoveTowards(openness, goal, Time.deltaTime / travelSeconds);
        }}
        state = goal >= 1f ? DoorState.Open : DoorState.Closed;
    }}
}}
"""

ANCHOR_SRC = """using UnityEngine;
// usk-fake: anchor
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    [SerializeField, Min(0.05f)] private float travelSeconds = .5f;

    float cur;
    DoorState mode = DoorState.Closed;
    float anchorValue;
    float anchorTime;
    float direction;

    public float openness => cur;
    public DoorState state => mode;

    public void Open()
    {{
        if (mode == DoorState.Opening || mode == DoorState.Open) return;
        Switch(DoorState.Opening, 1f);
    }}

    public void Close()
    {{
        if (mode == DoorState.Closing || mode == DoorState.Closed) return;
        Switch(DoorState.Closing, -1f);
    }}

    void Switch(DoorState next, float sign)
    {{
        anchorValue = cur;
        anchorTime = Time.time;
        direction = sign;
        mode = next;
    }}

    void Update()
    {{
        if (direction == 0f) return;
        cur = Mathf.Clamp01(anchorValue + direction * (Time.time - anchorTime) / travelSeconds);
        if (direction > 0f && cur >= 1f) {{ mode = DoorState.Open; direction = 0f; }}
        else if (direction < 0f && cur <= 0f) {{ mode = DoorState.Closed; direction = 0f; }}
    }}
}}
"""

SNAP_SRC = """using System.Collections;
using UnityEngine;
// usk-fake: snap
public class {cls} : MonoBehaviour
{{
    public enum DoorState {{ Closed, Opening, Open, Closing }}

    public float travelSeconds = 0.5f;
    public float openness;
    public DoorState state = DoorState.Closed;

    void Awake()
    {{
        openness = 0f;
        state = DoorState.Closed;
    }}

    public void Open()
    {{
        if (state == DoorState.Opening || state == DoorState.Open) return;
        StopAllCoroutines();
        state = DoorState.Opening;
        StartCoroutine(Snap(1f));
    }}

    public void Close()
    {{
        if (state == DoorState.Closing || state == DoorState.Closed) return;
        StopAllCoroutines();
        state = DoorState.Closing;
        StartCoroutine(Snap(0f));
    }}

    IEnumerator Snap(float goal)
    {{
        yield return new WaitForSeconds(travelSeconds);
        openness = goal;
        state = goal >= 1f ? DoorState.Open : DoorState.Closed;
    }}
}}
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float delay = 0f;
}}
"""


def door_source(cls, variant=None):
    parts = {"open_body": OPEN_BODY, "close_body": CLOSE_BODY, "step": STEP, "open_move": OPEN_MOVE,
             "close_move": CLOSE_MOVE}
    parts.update(VARIANT_PARTS.get(variant, {}))
    marker = "// usk-fake: %s" % variant if variant else ""
    return UPDATE_SRC.format(marker=marker, cls=cls, **parts)


def string_state_source(cls):
    """The reference behaviour with `public string state` and no DoorState enum."""
    text = door_source(cls)
    text = text.replace("    public enum DoorState { Closed, Opening, Open, Closing }\n\n", "")
    text = text.replace("public DoorState state = DoorState.Closed;", "public string state = \"Closed\";")
    for name in ("Closed", "Opening", "Open", "Closing"):
        text = text.replace("DoorState.%s" % name, "\"%s\"" % name)
    return text


def _cls(ctx):
    return ctx.private.class_name(ctx.handles)


def _wait(ctx, res):
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()


def _create_script(ctx, name, content):
    _wait(ctx, ctx.c.write("script_create", scriptName=name, folder=ctx.asset("Scripts"), content=content))


def _attach(ctx, ref, travel=True):
    cls, at = _cls(ctx), V.address(ref)
    ctx.c.write("component_add", componentType=cls, **at)
    if travel:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="travelSeconds",
                    value=repr(float(ctx.private.travel_seconds(ctx.seed))), **at)


def _answer(ctx, ref=None):
    h = ctx.handles
    info = ctx.probe.go(ref if ref is not None else h["door"]) or {}
    return {"scriptPath": h["script"], "doorPath": info.get("path"), "declaredComplete": True}


def _solve(ctx, source, ref=None, travel=True):
    _create_script(ctx, _cls(ctx), source)
    _attach(ctx, ref if ref is not None else ctx.handles["door"], travel)
    return _answer(ctx, ref)


def _write_file(ctx, asset_path, data):
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx, door_source(_cls(ctx)))


def coroutine_properties(ctx):
    """One coroutine stopped on every switch, moving in the call itself; [SerializeField] private travelSeconds and
    openness / state as read-only properties."""
    return _solve(ctx, COROUTINE_SRC.format(marker="// usk-fake: coroutine", cls=_cls(ctx)))


def time_anchor(ctx):
    """[SerializeField, Min] private travelSeconds = .5f; expression-bodied properties; openness computed from a
    Time.time anchor re-set on every switch."""
    return _solve(ctx, ANCHOR_SRC.format(cls=_cls(ctx)))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def default_only(ctx):
    """travelSeconds left at the code default 0.5 in the scene."""
    return _solve(ctx, door_source(_cls(ctx)), travel=False)


def frame_step(ctx):
    """A fixed step per frame instead of Time.deltaTime (as if the game ran at 10000 frames per second)."""
    return _solve(ctx, door_source(_cls(ctx), "frame-step"))


def no_clamp(ctx):
    """Reaches the end by `openness += step` and never clamps: openness overshoots 1 and drops below 0."""
    return _solve(ctx, door_source(_cls(ctx), "no-clamp"))


def double_speed(ctx):
    """A coroutine per call with no state guard and no StopCoroutine: a repeated call doubles the speed."""
    return _solve(ctx, DOUBLE_SRC.format(cls=_cls(ctx)))


def unscaled_time(ctx):
    """Time.unscaledDeltaTime: moves while Time.timeScale is 0 and too fast at 0.5."""
    return _solve(ctx, door_source(_cls(ctx), "unscaled"))


def coroutine_leak(ctx):
    """A coroutine per switch, never stopped: after a reversal two motions fight."""
    return _solve(ctx, LEAK_SRC.format(cls=_cls(ctx)))


def restart_on_reverse(ctx):
    """A reversal restarts from the far end (Open while closing from 0, Close while opening from 1)."""
    return _solve(ctx, door_source(_cls(ctx), "restart-reverse"))


def open_restarts(ctx):
    """Open() always restarts from 0, also on an open door."""
    return _solve(ctx, door_source(_cls(ctx), "open-restarts"))


def snap_after_wait(ctx):
    """WaitForSeconds(travelSeconds), then a jump to the end: no motion in between."""
    return _solve(ctx, SNAP_SRC.format(cls=_cls(ctx)))


def wrong_object(ctx):
    """The component sits on the spare object instead of the door."""
    return _solve(ctx, door_source(_cls(ctx)), ref=ctx.handles["spare"])


def state_as_string(ctx):
    """`state` is a public string; no DoorState enum."""
    return _solve(ctx, string_state_source(_cls(ctx)))


def compile_error(ctx):
    """A syntax error: the class never loads, so the component cannot be added."""
    broken = door_source(_cls(ctx)).replace("void Update()", "void Update() {")
    _create_script(ctx, _cls(ctx), broken)
    try:
        _attach(ctx, ctx.handles["door"])
    except SkillError:
        pass
    out = _answer(ctx)
    out["declaredComplete"] = False
    return out


def extra_script(ctx):
    """A second C# file under A/Scripts."""
    answer = reference(ctx)
    name = "DoorHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def moved_spare(ctx):
    """The spare object is moved."""
    answer = reference(ctx)
    position = V.floats((ctx.probe.go(ctx.handles["spare"]) or {}).get("position"))
    ctx.c.write("gameobject_set_transform", posX=position[0] + 1.0, **V.address(ctx.handles["spare"]))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["doorPath"] = ctx.handles["sparePath"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayDoor")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "door-notes.txt"), b"notes\n")
    return answer


NEGATIVES = [
    Negative("default_only", default_only, {"C3", "X1", "X3"}, "travelSeconds not set in the scene"),
    Negative("frame_step", frame_step, {"X1", "X2", "X3"}, "a fixed step per frame instead of Time.deltaTime"),
    Negative("no_clamp", no_clamp, {"X1", "X3"}, "openness is never clamped to [0, 1]"),
    Negative("double_speed", double_speed, {"X2", "X3"}, "a repeated call starts a second coroutine"),
    Negative("unscaled_time", unscaled_time, {"X2"}, "unscaled time ignores Time.timeScale"),
    Negative("coroutine_leak", coroutine_leak, {"X3"}, "the old coroutine keeps running after a reversal"),
    Negative("restart_on_reverse", restart_on_reverse, {"X3"}, "a reversal restarts from the far end"),
    Negative("open_restarts", open_restarts, {"X3"}, "Open() restarts from 0 on an open door"),
    Negative("snap_after_wait", snap_after_wait, {"X1", "X2", "X3"}, "waits, then jumps to the end"),
    Negative("wrong_object", wrong_object, {"C3", "C5", "X1", "X2", "X3"}, "component on the spare object"),
    Negative("state_as_string", state_as_string, {"C1"}, "state is a string, no DoorState enum"),
    Negative("compile_error", compile_error, {"C2", "C3", "X1", "X2", "X3"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"C4"}, "an extra C# file"),
    Negative("moved_spare", moved_spare, {"C5"}, "the spare object moved"),
    Negative("wrong_report", wrong_report, {"C8"}, "the answer names the spare object"),
    Negative("outside_root", outside_root, {"C6"}, "an object created outside R"),
    Negative("direct_file", direct_file, {"C7"}, "a non-script file written directly under A"),
]

ALTERNATIVES = [
    ("coroutine_properties", coroutine_properties),
    ("time_anchor", time_anchor),
    ("save_run_scene", save_run_scene),
]

# Producer:Betsy
