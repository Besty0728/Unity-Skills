"""DB3 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

BLINKER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float interval = {default}f;{marker}

    private Light _light;
    private float _elapsed;

    void Start()
    {{
        _light = GetComponent<Light>();{start}
    }}

    void Update()
    {{
        _elapsed += Time.deltaTime;
        if (_elapsed >= {period})
        {{
            _elapsed -= {period};
            _light.enabled = !_light.enabled;
        }}
    }}
}}
"""

INVOKE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float interval = {default}f;

    private Light _light;

    void Start()
    {{
        _light = GetComponent<Light>();
        InvokeRepeating(nameof(Toggle), interval, interval);
    }}

    void Toggle()
    {{
        _light.enabled = !_light.enabled;
    }}
}}
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float bonus = 1f;
}}
"""


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def _source(ctx, default=None, period="interval", start="", variant=None):
    """The Blinker source; `variant` names the wrong behaviour for the offline model (a `// usk-fake:` comment)."""
    return BLINKER_SRC.format(cls=_cls(ctx), default=ctx.private.DEFAULT_INTERVAL if default is None else default,
                              period=period, start=start,
                              marker="\n    // usk-fake: %s" % variant if variant else "")


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _attach(ctx, ref, interval=None):
    cls = _cls(ctx)
    ctx.c.write("component_add", componentType=cls, **V.address(ref))
    if interval is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="interval", value=str(interval),
                    **V.address(ref))


def _answer(ctx, holder):
    ser = ctx.probe.ser(holder, _cls(ctx)) or {}
    stored = V.floats(ser.get("interval"))
    return {"scriptPath": ctx.handles["script"], "lampInstanceId": _number(V.object_id(holder)),
            "interval": stored[0] if stored else None, "declaredComplete": True}


def _solve(ctx, source=None, holder=None, interval=None):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), source or _source(ctx))
    holder = h["lamp"] if holder is None else holder
    _attach(ctx, holder, ctx.private.spec(ctx.seed)["interval"] if interval is None else interval)
    return _answer(ctx, holder)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def wrong_lamp(ctx):
    other = next(ref for ref in ctx.handles["lamps"] if not V.same_id(ref, ctx.handles["lamp"]))
    return _solve(ctx, holder=other)


def default_only(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx))
    _attach(ctx, h["lamp"])
    return _answer(ctx, h["lamp"])


def toggle_on_start(ctx):
    return _solve(ctx, source=_source(ctx, start="\n        _light.enabled = !_light.enabled;",
                                      variant="toggle_on_start"))


def half_period(ctx):
    return _solve(ctx, source=_source(ctx, period="interval * 0.5f", variant="half_period"))


def wrong_default(ctx):
    return _solve(ctx, source=_source(ctx, default=2))


def compile_error(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx).replace("void Update()", "void Update() {"))
    return {"scriptPath": h["script"], "lampInstanceId": _number(V.object_id(h["lamp"])),
            "interval": ctx.private.spec(ctx.seed)["interval"], "declaredComplete": True}


def extra_script(ctx):
    answer = reference(ctx)
    name = "BlinkHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def two_components(ctx):
    answer = reference(ctx)
    _attach(ctx, ctx.handles["lamp"], ctx.private.spec(ctx.seed)["interval"])
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["interval"] = ctx.private.DEFAULT_INTERVAL
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayLamp")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "notes.txt"), b"blinking\n")
    return answer


def invoke_repeating(ctx):
    return _solve(ctx, source=INVOKE_SRC.format(cls=_cls(ctx), default=ctx.private.DEFAULT_INTERVAL))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("wrong_lamp", wrong_lamp, {"D3", "D5", "D6"}, "the other lamp carries the component"),
    Negative("default_only", default_only, {"D3", "D6"}, "the scene keeps the code default"),
    Negative("toggle_on_start", toggle_on_start, {"D6"}, "the light is toggled at Start"),
    Negative("half_period", half_period, {"D6"}, "a toggle every half interval"),
    Negative("wrong_default", wrong_default, {"D1"}),
    Negative("compile_error", compile_error, {"D2", "D3", "D6", "D7"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"D4"}),
    Negative("two_components", two_components, {"D3", "D6"}, "two components cancel each other's toggles"),
    Negative("wrong_report", wrong_report, {"D7"}),
    Negative("outside_root", outside_root, {"D8"}),
    Negative("direct_file", direct_file, {"D9"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("invoke_repeating", invoke_repeating), ("save_run_scene", save_run_scene)]

# Producer:Betsy
