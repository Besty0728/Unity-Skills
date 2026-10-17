"""DB5 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

COUNTER_SRC = """using UnityEngine;
using UnityEngine.UI;

public class {cls} : MonoBehaviour
{{
    public Text {field};{marker}

    private int _count;
{start}
    public void OnClick()
    {{
        _count += 1;{write}
    }}
{extra}}}
"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int bonus = 1;
}}
"""

RUNTIME_START = """
    void Start()
    {
        GetComponent<Button>().onClick.AddListener(OnClick);
    }
"""
NOOP = """
    public void Noop()
    {
    }
"""


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def _source(ctx, field="label", start="", write=None, extra="", variant=None):
    """The ClickCounter source; `variant` names the wrong behaviour for the offline model (a `// usk-fake:` comment)."""
    write = "\n        %s.text = _count.ToString();" % field if write is None else write
    return COUNTER_SRC.format(cls=_cls(ctx), field=field, start=start, write=write, extra=extra,
                              marker="\n    // usk-fake: %s" % variant if variant else "")


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _path(ctx, ref):
    return (ctx.probe.go(ref) or {}).get("path")


def _attach(ctx, ref, field="label", with_label=True):
    cls = _cls(ctx)
    ctx.c.write("component_add", componentType=cls, **V.address(ref))
    if with_label:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath=field,
                    referencePath=_path(ctx, ctx.handles["label"]), objectType="Text", **V.address(ref))


def _wire(ctx, button, target=None, method="OnClick", mode="RuntimeOnly"):
    path = _path(ctx, button)
    ctx.c.write("event_add_listener", path=path, componentName="Button", eventName="onClick",
                targetPath=_path(ctx, button if target is None else target), targetComponentName=_cls(ctx),
                methodName=method, mode=mode)


def _answer(ctx, holder):
    return {"scriptPath": ctx.handles["script"], "buttonInstanceId": _number(V.object_id(holder)),
            "labelInstanceId": _number(V.object_id(ctx.handles["label"])), "declaredComplete": True}


def _solve(ctx, source=None, holder=None, field="label", with_label=True, wire=True, method="OnClick",
           mode="RuntimeOnly"):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), source or _source(ctx, field=field))
    holder = h["button"] if holder is None else holder
    _attach(ctx, holder, field=field, with_label=with_label)
    if wire:
        _wire(ctx, holder, method=method, mode=mode)
    return _answer(ctx, holder)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def runtime_add_listener(ctx):
    return _solve(ctx, source=_source(ctx, start=RUNTIME_START, variant="runtime_listener"), wire=False)


def wrong_button(ctx):
    other = next(ref for ref in ctx.handles["buttons"] if not V.same_id(ref, ctx.handles["button"]))
    return _solve(ctx, holder=other)


def no_label(ctx):
    return _solve(ctx, with_label=False)


def off_state(ctx):
    return _solve(ctx, mode="Off")


def wrong_method(ctx):
    return _solve(ctx, source=_source(ctx, extra=NOOP), method="Noop")


def text_not_updated(ctx):
    return _solve(ctx, source=_source(ctx, write="", variant="no_text_write"))


def both_buttons(ctx):
    answer = reference(ctx)
    other = next(ref for ref in ctx.handles["buttons"] if not V.same_id(ref, ctx.handles["button"]))
    _wire(ctx, other, target=ctx.handles["button"])
    return answer


def wrong_field_name(ctx):
    return _solve(ctx, field="counterLabel")


def compile_error(ctx):
    h = ctx.handles
    _create_script(ctx, _cls(ctx), _source(ctx).replace("public void OnClick()", "public void OnClick() {"))
    return {"scriptPath": h["script"], "buttonInstanceId": _number(V.object_id(h["button"])),
            "labelInstanceId": _number(V.object_id(h["label"])), "declaredComplete": True}


def extra_script(ctx):
    answer = reference(ctx)
    name = "ClickHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["labelInstanceId"] = answer["buttonInstanceId"]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayButton")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "notes.txt"), b"wired\n")
    return answer


def editor_and_runtime(ctx):
    return _solve(ctx, mode="EditorAndRuntime")


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("runtime_add_listener", runtime_add_listener, {"E4"}, "AddListener at Start instead of a persistent listener"),
    Negative("wrong_button", wrong_button, {"E3", "E4", "E5", "E6"}, "the other button is wired"),
    Negative("no_label", no_label, {"E3", "E6"}, "label left unassigned"),
    Negative("off_state", off_state, {"E4", "E6"}, "listener call state Off"),
    Negative("wrong_method", wrong_method, {"E4", "E6"}, "the listener calls another method"),
    Negative("text_not_updated", text_not_updated, {"E6"}, "the count never reaches the label"),
    Negative("both_buttons", both_buttons, {"E5"}, "the other button's onClick gets a listener too"),
    Negative("wrong_field_name", wrong_field_name, {"E1", "E3"}, "the Text field is not called label"),
    Negative("compile_error", compile_error, {"E2", "E3", "E4", "E6", "E7"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"E5"}),
    Negative("wrong_report", wrong_report, {"E7"}),
    Negative("outside_root", outside_root, {"E8"}),
    Negative("direct_file", direct_file, {"E9"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("editor_and_runtime", editor_and_runtime), ("save_run_scene", save_run_scene)]

# Producer:Betsy
