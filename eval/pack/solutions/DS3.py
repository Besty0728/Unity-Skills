"""DS3 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The C# sources differ in behaviour; each negative variant also carries a `// usk-fake: <variant>` comment, which the
offline fake editor reads to model the same wrong behaviour (a real editor ignores the comment).
"""
import os

from usk_eval import verifylib as V
from usk_eval.rest import SkillError
from usk_eval.task_api import Negative

CONFIG_SRC = """using UnityEngine;

public class {cls} : ScriptableObject
{{
    public int count = 2;
    public float interval = 2f;
}}
"""

SPAWNER_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public GameObject prefab;
    public Transform container;

    private int _spawned;
    private float _elapsed;

    void Start()
    {{
        {start}
    }}

    void Update()
    {{
        if (config == null || prefab == null || container == null{limit}) return;
        _elapsed += Time.deltaTime;
        while (_elapsed >= config.interval{limit})
        {{
            _elapsed -= config.interval;
            Instantiate(prefab, {parent});
            _spawned++;
        }}
    }}
}}
"""

INVOKE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public GameObject prefab;
    public Transform container;

    private int _spawned;

    void Start()
    {{
        if (config != null && prefab != null && container != null && config.count > 0)
            InvokeRepeating(nameof(Spawn), config.interval, config.interval);
    }}

    void Spawn()
    {{
        Instantiate(prefab, container);
        if (++_spawned >= config.count) CancelInvoke(nameof(Spawn));
    }}
}}
"""

HELPER_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("wave setup"); }}
}}
"""


def _spawner_source(cls, cfg, variant=None):
    parts = {"marker": "// usk-fake: %s" % variant if variant else "", "cls": cls, "cfg": cfg,
             "start": "", "limit": " || _spawned >= config.count", "parent": "container"}
    if variant == "runtime-instance":
        parts["start"] = "config = ScriptableObject.CreateInstance<%s>();" % cfg
    elif variant == "spawn-at-zero":
        parts["start"] = "if (config != null) _elapsed = config.interval;"
    elif variant == "no-limit":
        parts["limit"] = ""
    elif variant == "wrong-container":
        parts["parent"] = "transform"
    return SPAWNER_SRC.format(**parts)


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _scripts(ctx, variant=None, spawner_src=None):
    cfg, spawner = ctx.private.class_names(ctx.handles)
    return [_create_script(ctx, cfg, CONFIG_SRC.format(cls=cfg)),
            _create_script(ctx, spawner, spawner_src or _spawner_source(spawner, cfg, variant))]


def _assets(ctx, values=True):
    cfg, _spawner = ctx.private.class_names(ctx.handles)
    spec = ctx.private.wave_spec(ctx.seed)
    paths = []
    for key, want in (("assetA", spec["a"]), ("assetB", spec["b"])):
        path = ctx.handles[key]
        ctx.c.write("scriptableobject_create", typeName=cfg, savePath=path)
        if values:
            ctx.c.write("scriptableobject_set", assetPath=path, fieldName="count", value=str(want["count"]))
            ctx.c.write("scriptableobject_set", assetPath=path, fieldName="interval", value=str(want["interval"]))
        paths.append(path)
    return paths


def _wire(ctx, spawner_key, asset_key, container_key):
    _cfg, spawner = ctx.private.class_names(ctx.handles)
    at = V.address(ctx.handles[spawner_key])
    ctx.c.write("component_add", componentType=spawner, **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="config",
                assetPath=ctx.handles[asset_key], **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="prefab",
                assetPath=ctx.handles["prefab"], **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="container",
                referencePath=ctx.handles[container_key + "Path"], objectType="Transform", **at)


def _answer(ctx, scripts, assets):
    return {"scriptPaths": scripts, "configAssetPaths": assets,
            "spawnerPaths": [(ctx.probe.go(ctx.handles[key]) or {}).get("path") for key in ("spawnerA", "spawnerB")],
            "declaredComplete": True}


def _solve(ctx, variant=None, values=True, swap=False, spawner_src=None):
    scripts = _scripts(ctx, variant, spawner_src)
    assets = _assets(ctx, values)
    _wire(ctx, "spawnerA", "assetB" if swap else "assetA", "containerA")
    _wire(ctx, "spawnerB", "assetA" if swap else "assetB", "containerB")
    return _answer(ctx, scripts, assets)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def invoke_repeating(ctx):
    cfg, spawner = ctx.private.class_names(ctx.handles)
    return _solve(ctx, spawner_src=INVOKE_SRC.format(cls=spawner, cfg=cfg))


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def runtime_instance(ctx):
    return _solve(ctx, variant="runtime-instance")


def defaults_unset(ctx):
    return _solve(ctx, values=False)


def spawn_at_zero(ctx):
    return _solve(ctx, variant="spawn-at-zero")


def no_limit(ctx):
    return _solve(ctx, variant="no-limit")


def wrong_container(ctx):
    return _solve(ctx, variant="wrong-container")


def swapped_configs(ctx):
    return _solve(ctx, swap=True)


def extra_script(ctx):
    answer = reference(ctx)
    cfg, _spawner = ctx.private.class_names(ctx.handles)
    helper = "WaveSetup_" + cfg[len("WaveConfig_"):]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper), folder=ctx.asset("Editor"))
    return answer


def rename_container(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Holding", **V.address(ctx.handles["containerA"]))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["configAssetPaths"] = [ctx.handles["assetA"]]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StraySpawner")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "waves.txt"), b"tuned\n")
    return answer


def broken_config(ctx):
    """A config script that does not compile: no asset and no component can follow, the answer says so."""
    cfg, spawner = ctx.private.class_names(ctx.handles)
    scripts = [_create_script(ctx, cfg, CONFIG_SRC.format(cls=cfg).replace("public int count = 2;",
                                                                          "public int count = 2; {")),
               _create_script(ctx, spawner, _spawner_source(spawner, cfg))]
    assets = []
    try:
        assets = _assets(ctx)
        _wire(ctx, "spawnerA", "assetA", "containerA")
        _wire(ctx, "spawnerB", "assetB", "containerB")
    except SkillError:
        pass
    answer = _answer(ctx, scripts, assets)
    answer["declaredComplete"] = False
    return answer


NEGATIVES = [
    Negative("runtime_instance", runtime_instance, {"D10", "D11"}, "a config created at runtime replaces the asset"),
    Negative("defaults_unset", defaults_unset, {"D3", "D10", "D11"}, "asset values left at the code defaults"),
    Negative("spawn_at_zero", spawn_at_zero, {"D10"}, "the first spawn at Play start"),
    Negative("no_limit", no_limit, {"D10", "D11"}, "spawning never stops"),
    Negative("wrong_container", wrong_container, {"D10", "D11"}, "instances parented to the spawner"),
    Negative("swapped_configs", swapped_configs, {"D4", "D10", "D11"}, "each spawner references the other's asset"),
    Negative("extra_script", extra_script, {"D5"}),
    Negative("rename_container", rename_container, {"D6"}),
    Negative("wrong_report", wrong_report, {"D9"}),
    Negative("outside_root", outside_root, {"D7"}),
    Negative("direct_file", direct_file, {"D8"}, "a non-script file written to disk next to the skills"),
    Negative("broken_config", broken_config, {"D1", "D2", "D3", "D4", "D9", "D10", "D11"}, "a compile error in the "
                                                                                           "config script"),
]

ALTERNATIVES = [("invoke_repeating", invoke_repeating), ("save_run_scene", save_run_scene)]

# Producer:Betsy
