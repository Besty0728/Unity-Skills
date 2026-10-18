"""DC5 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources differ in behaviour; each negative variant also carries a `// usk-fake: <variant>` comment, which the
offline fake editor reads to model the same wrong behaviour (a real editor ignores the comment).
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

CONTROLLER_SRC = """using UnityEngine;
{usings}
public class {cls} : MonoBehaviour
{{{marker}
    {field}

    private bool _paused;
{start}
    public void TogglePause()
    {{
        if (_paused) Resume();
        else Pause();
    }}

    public void Resume()
    {{
        {flag}
        Time.timeScale = 1f;
        {hide}
    }}

    private void Pause()
    {{
        _paused = true;
        Time.timeScale = 0f;
        {show}
    }}
}}
"""

RUNTIME_START = """
    void Start()
    {{
        GameObject.Find("{button}").GetComponent<Button>().onClick.AddListener(TogglePause);
    }}
"""

PAUSED_START = """
    void Start()
    {
        Pause();
    }
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int bonus = 1;
}}
"""


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def _source(ctx, variant=None, start="", usings="", panel=True, field_name="panel", private_field=False,
            reset_flag=True):
    show = "if (%s != null) %s.SetActive(true);" % (field_name, field_name) if panel else ""
    hide = "if (%s != null) %s.SetActive(false);" % (field_name, field_name) if panel else ""
    field = ("[SerializeField] private GameObject %s;" if private_field else "public GameObject %s;") % field_name
    return CONTROLLER_SRC.format(cls=_cls(ctx), marker="\n    // usk-fake: %s" % variant if variant else "",
                                 start=start, usings=usings, show=show, hide=hide, field=field,
                                 flag="_paused = false;" if reset_flag else "")


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _path(ctx, ref):
    return (ctx.probe.go(ref) or {}).get("path")


def _attach(ctx, ref, with_panel=True, field_name="panel"):
    cls = _cls(ctx)
    ctx.c.write("component_add", componentType=cls, **V.address(ref))
    if with_panel:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath=field_name,
                    referencePath=_path(ctx, ctx.handles["panel"]), **V.address(ref))


def _buttons(ctx, resume_parent=None):
    """(pause button id, resume button id): the pause button under the canvas, the resume button under the panel
    (or `resume_parent`), through ui_create_button which parents by name."""
    h = ctx.handles
    canvas_name = os.path.basename(h["canvasPath"])
    panel_name = os.path.basename(h["panelPath"]) if resume_parent is None else resume_parent
    pause = V.object_id(ctx.c.write("ui_create_button", name=h["pauseName"], parent=canvas_name, text="Pause"))
    resume = V.object_id(ctx.c.write("ui_create_button", name=h["resumeName"], parent=panel_name, text="Resume"))
    return pause, resume


def _wire(ctx, button, target, method, mode="RuntimeOnly"):
    ctx.c.write("event_add_listener", path=_path(ctx, button), componentName="Button", eventName="onClick",
                targetPath=_path(ctx, target), targetComponentName=_cls(ctx), methodName=method, mode=mode)


def _answer(ctx, host, pause, resume):
    return {"scriptPath": ctx.handles["script"], "hostPath": _path(ctx, host), "pauseButtonPath": _path(ctx, pause),
            "resumeButtonPath": _path(ctx, resume), "declaredComplete": True}


def _solve(ctx, source=None, host=None, with_panel=True, wire=True, methods=("TogglePause", "Resume"),
           resume_parent=None, field_name="panel"):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), source or _source(ctx))
    host = h["host"] if host is None else host
    _attach(ctx, host, with_panel=with_panel, field_name=field_name)
    pause, resume = _buttons(ctx, resume_parent)
    if wire:
        _wire(ctx, pause, host, methods[0])
        _wire(ctx, resume, host, methods[1])
    return _answer(ctx, host, pause, resume)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def by_ids(ctx):
    """A [SerializeField] private panel field and listeners bound through targetEntityId / targetInstanceId in
    EditorAndRuntime state."""
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx, private_field=True))
    _attach(ctx, h["host"])
    pause, resume = _buttons(ctx)
    for button, method in ((pause, "TogglePause"), (resume, "Resume")):
        locator = V.address(h["host"], "target")
        ctx.c.write("event_add_listener", path=_path(ctx, button), componentName="Button", eventName="onClick",
                    targetComponentName=_cls(ctx), methodName=method, mode="EditorAndRuntime", **locator)
    return _answer(ctx, h["host"], pause, resume)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def runtime_listener(ctx):
    """The pause button wired at runtime only (AddListener in Start); the resume listener stays persistent."""
    h = ctx.handles
    source = _source(ctx, variant="runtime-listener", start=RUNTIME_START.format(button=h["pauseName"]),
                     usings="using UnityEngine.UI;")
    _create_script(ctx, _cls(ctx), source)
    _attach(ctx, h["host"])
    pause, resume = _buttons(ctx)
    _wire(ctx, resume, h["host"], "Resume")
    return _answer(ctx, h["host"], pause, resume)


def on_archive(ctx):
    """The component and the listeners on the same-named object under the archive (reported truthfully)."""
    other = next(ref for ref in (V.object_id(c) for c in ctx.probe.children(ctx.handles["archive"]))
                 if (ctx.probe.go(ref) or {}).get("name") == os.path.basename(ctx.handles["hostPath"]))
    return _solve(ctx, host=other)


def swapped_methods(ctx):
    return _solve(ctx, methods=("Resume", "TogglePause"))


def panel_unset(ctx):
    return _solve(ctx, with_panel=False, source=_source(ctx, variant="no-panel-ref"))


def paused_start(ctx):
    return _solve(ctx, source=_source(ctx, variant="paused-start", start=PAUSED_START))


def timescale_only(ctx):
    return _solve(ctx, source=_source(ctx, variant="timescale-only", panel=False))


def stale_flag(ctx):
    """Resume() restores timeScale and the panel but leaves the private flag set: the next TogglePause resumes."""
    return _solve(ctx, source=_source(ctx, variant="stale-flag", reset_flag=False))


def resume_unwired(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx))
    _attach(ctx, h["host"])
    pause, resume = _buttons(ctx)
    _wire(ctx, pause, h["host"], "TogglePause")
    return _answer(ctx, h["host"], pause, resume)


def resume_on_canvas(ctx):
    return _solve(ctx, resume_parent=os.path.basename(ctx.handles["canvasPath"]))


def panel_activated(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_set_active", active=True, **V.address(ctx.handles["panel"]))
    return answer


def field_renamed(ctx):
    """The GameObject field is called pausePanel instead of panel (set and used consistently)."""
    return _solve(ctx, source=_source(ctx, field_name="pausePanel"), field_name="pausePanel")


def compile_error(ctx):
    """The script does not compile; the buttons are made, nothing can be attached or wired."""
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx).replace("public void Resume()", "public void Resume() {"))
    pause, resume = _buttons(ctx)
    return _answer(ctx, h["host"], pause, resume)


def extra_script(ctx):
    answer = reference(ctx)
    helper = "PauseHelper_" + _cls(ctx)[len("PauseController_"):]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["hostPath"] = ctx.handles["archivePath"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayMenu")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "menu.txt"), b"paused\n")
    return answer


NEGATIVES = [
    Negative("runtime_listener", runtime_listener, {"P5"}, "AddListener at runtime instead of a persistent listener"),
    Negative("on_archive", on_archive, {"P3", "P5", "P6"}, "the same-named object under the archive used as host"),
    Negative("swapped_methods", swapped_methods, {"P5", "P10"}),
    Negative("panel_unset", panel_unset, {"P3", "P10"}, "panel left unassigned"),
    Negative("paused_start", paused_start, {"P10"}, "the game starts paused"),
    Negative("timescale_only", timescale_only, {"P10"}, "the panel never shown"),
    Negative("stale_flag", stale_flag, {"P10"}, "Resume() keeps the paused flag, so the next TogglePause resumes"),
    Negative("resume_unwired", resume_unwired, {"P5", "P10"}, "the resume button has no listener"),
    Negative("resume_on_canvas", resume_on_canvas, {"P4"}, "the resume button under the canvas"),
    Negative("panel_activated", panel_activated, {"P6", "P10"}, "the panel activated in the scene"),
    Negative("field_renamed", field_renamed, {"P1", "P3"}, "the panel field has another name"),
    Negative("compile_error", compile_error, {"P2", "P3", "P5", "P10"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"P6"}),
    Negative("wrong_report", wrong_report, {"P9"}),
    Negative("outside_root", outside_root, {"P7"}),
    Negative("direct_file", direct_file, {"P8"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("by_ids", by_ids), ("save_run_scene", save_run_scene)]

# Producer:Betsy
