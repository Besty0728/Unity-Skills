"""DB6 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

SPAWNER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public GameObject prefab;
    public Transform container;
    public float interval = {def_interval}f;
    public int maxCount = {def_count};{marker}

    private float _elapsed;
    private int _spawned;

    void Start()
    {{{start}
    }}

    void Update()
    {{
        if (prefab == null || container == null{limit}) return;
        _elapsed += Time.deltaTime;
        while (_elapsed >= interval{limit})
        {{
            _elapsed -= interval;
            {spawn_stmt}
            _spawned++;
        }}
    }}
}}
"""

INVOKE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    [SerializeField] public GameObject prefab;
    [SerializeField] public Transform container;
    [SerializeField] public float interval = {def_interval}f;
    [SerializeField] public int maxCount = {def_count};

    private int _spawned;

    void Start()
    {{
        if (prefab != null && container != null && maxCount > 0)
            InvokeRepeating(nameof(SpawnOne), interval, interval);
    }}

    void SpawnOne()
    {{
        Instantiate(prefab, container, false);
        if (++_spawned >= maxCount) CancelInvoke(nameof(SpawnOne));
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


def _source(ctx, def_interval=None, def_count=None, variant=None):
    di = ctx.private.DEFAULT_INTERVAL if def_interval is None else def_interval
    dc = ctx.private.DEFAULT_COUNT if def_count is None else def_count
    start = ""
    limit = " || _spawned >= maxCount"
    spawn_stmt = "Instantiate(prefab, container);"
    if variant == "spawn_on_start":
        start = "\n        _elapsed = interval;"
    elif variant == "no_stop":
        limit = ""
    elif variant == "off_by_one":
        limit = " || _spawned >= maxCount - 1"
    elif variant == "parent_emitter":
        spawn_stmt = "Instantiate(prefab, transform);"
    elif variant == "keep_world_pos":
        spawn_stmt = "Instantiate(prefab, Vector3.zero, Quaternion.identity).transform.SetParent(container, true);"
    marker = "\n    // usk-fake: %s" % variant if variant else ""
    return SPAWNER_SRC.format(cls=_cls(ctx), def_interval=di, def_count=dc, marker=marker,
                              start=start, limit=limit, spawn_stmt=spawn_stmt)


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _attach(ctx, holder, prefab_path=None, container_path=None, interval=None, max_count=None):
    cls = _cls(ctx)
    at = V.address(holder)
    ctx.c.write("component_add", componentType=cls, **at)
    if prefab_path is not None:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="prefab",
                    assetPath=prefab_path, **at)
    if container_path is not None:
        ctx.c.write("component_set_serialized_property", componentType=cls, propertyPath="container",
                    referencePath=container_path, objectType="Transform", **at)
    if interval is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="interval", value=str(interval), **at)
    if max_count is not None:
        ctx.c.write("component_set_property", componentType=cls, propertyName="maxCount", value=str(max_count), **at)


def _answer(ctx, holder):
    ser = ctx.probe.ser(holder, _cls(ctx)) or {}
    container_owner = ctx.probe.owner(ser.get("container"))
    stored_interval = V.floats(ser.get("interval"))
    stored_count = V.as_int(ser.get("maxCount"))
    return {"scriptPath": ctx.handles["script"],
            "emitterInstanceId": _number(V.object_id(holder)),
            "containerInstanceId": _number(V.object_id(container_owner or ctx.handles["container"])),
            "interval": stored_interval[0] if stored_interval else None,
            "maxCount": stored_count,
            "declaredComplete": True}


def _solve(ctx, source=None, holder=None, prefab_path=None, container_path=None, interval=None, max_count=None,
           set_numbers=True):
    h, values = ctx.handles, ctx.private.spec(ctx.seed)
    _create_script(ctx, _cls(ctx), source or _source(ctx))
    holder = h["emitter"] if holder is None else holder
    prefab_path = h["prefab"] if prefab_path is None else prefab_path
    container_path = h["containerPath"] if container_path is None else container_path
    iv = (values["interval"] if interval is None else interval) if set_numbers else None
    mc = (values["maxCount"] if max_count is None else max_count) if set_numbers else None
    _attach(ctx, holder, prefab_path=prefab_path, container_path=container_path, interval=iv, max_count=mc)
    return _answer(ctx, holder)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


def reference(ctx):
    return _solve(ctx)


def wrong_emitter(ctx):
    other = next(ref for ref in ctx.handles["emitters"] if not V.same_id(ref, ctx.handles["emitter"]))
    return _solve(ctx, holder=other)


def wrong_container(ctx):
    return _solve(ctx, container_path=ctx.handles["otherContainerPath"])


def wrong_prefab(ctx):
    return _solve(ctx, prefab_path=ctx.handles["otherPrefab"])


def default_only(ctx):
    return _solve(ctx, set_numbers=False)


def spawn_on_start(ctx):
    return _solve(ctx, source=_source(ctx, variant="spawn_on_start"))


def no_stop(ctx):
    return _solve(ctx, source=_source(ctx, variant="no_stop"))


def off_by_one(ctx):
    return _solve(ctx, source=_source(ctx, variant="off_by_one"))


def parent_emitter(ctx):
    return _solve(ctx, source=_source(ctx, variant="parent_emitter"))


def keep_world_pos(ctx):
    return _solve(ctx, source=_source(ctx, variant="keep_world_pos"))


def wrong_default(ctx):
    return _solve(ctx, source=_source(ctx, def_interval=1.5, def_count=3))


def compile_error(ctx):
    h, values = ctx.handles, ctx.private.spec(ctx.seed)
    _create_script(ctx, _cls(ctx), _source(ctx).replace("void Update()", "void Update() {"))
    return {"scriptPath": h["script"],
            "emitterInstanceId": _number(V.object_id(h["emitter"])),
            "containerInstanceId": _number(V.object_id(h["container"])),
            "interval": values["interval"], "maxCount": values["maxCount"], "declaredComplete": True}


def extra_script(ctx):
    answer = reference(ctx)
    name = "SpawnHelper_" + _cls(ctx).split("_", 1)[1]
    _create_script(ctx, name, HELPER_SRC.format(cls=name))
    return answer


def rename_bystander(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="RenamedHolder", path=ctx.handles["otherContainerPath"])
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["maxCount"] = ctx.private.DEFAULT_COUNT
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StraySpawner")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Scripts", "notes.txt"), b"spawning\n")
    return answer


def invoke_repeating(ctx):
    return _solve(ctx, source=INVOKE_SRC.format(cls=_cls(ctx), def_interval=ctx.private.DEFAULT_INTERVAL,
                                                def_count=ctx.private.DEFAULT_COUNT))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


NEGATIVES = [
    Negative("wrong_emitter", wrong_emitter, {"F3", "F5"}, "the other emitter carries the component"),
    Negative("wrong_container", wrong_container, {"F3", "F6"}, "instances spawn into the other container"),
    Negative("wrong_prefab", wrong_prefab, {"F3", "F6"}, "the component references the other prefab"),
    Negative("default_only", default_only, {"F3", "F6"}, "the scene keeps the code defaults"),
    Negative("spawn_on_start", spawn_on_start, {"F6"}, "an instance spawns immediately at Start"),
    Negative("no_stop", no_stop, {"F6"}, "spawning continues past maxCount"),
    Negative("off_by_one", off_by_one, {"F6"}, "spawning stops at maxCount - 1"),
    Negative("parent_emitter", parent_emitter, {"F6"}, "instances are parented to the emitter instead of the container"),
    Negative("keep_world_pos", keep_world_pos, {"F6"}, "instances keep world origin instead of container local origin"),
    Negative("wrong_default", wrong_default, {"F1"}, "the code defaults differ from the required defaults"),
    Negative("compile_error", compile_error, {"F2", "F3", "F6", "F7"}, "the script does not compile"),
    Negative("extra_script", extra_script, {"F4"}, "an extra C# script was added"),
    Negative("rename_bystander", rename_bystander, {"F5"}, "the other container was renamed"),
    Negative("wrong_report", wrong_report, {"F7"}, "the answer reports the wrong maxCount"),
    Negative("outside_root", outside_root, {"F8"}, "a GameObject was created outside the run root"),
    Negative("direct_file", direct_file, {"F9"}, "a non-script file was written directly to disk"),
]

ALTERNATIVES = [("invoke_repeating", invoke_repeating), ("save_run_scene", save_run_scene)]

# Producer:Betsy
