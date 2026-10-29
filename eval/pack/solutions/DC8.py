"""DC8 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

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
    public float spawnInterval = 3f;
    public int spawnsPerWave = 1;
    public int maxAlive = 1;
}}
"""

SPAWNER_SRC = """using UnityEngine;
{marker}
public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public GameObject prefab;
    public Transform container;

    private float _elapsed;
{fields}
    void Awake()
    {{
{awake}
    }}

    public void SetConfig({cfg} next)
    {{
{switch}
    }}

    void Update()
    {{
        if (config == null || prefab == null || container == null || {interval} <= 0f) return;
        _elapsed += Time.deltaTime;
        while (_elapsed >= {interval})
        {{
            _elapsed -= {interval};
            SpawnWave();
        }}
    }}

    void SpawnWave()
    {{
{wave}
    }}
}}
"""

WAVE = """        int count = Mathf.Min(config.spawnsPerWave, config.maxAlive - container.childCount);
        for (int i = 0; i < count; i++) Instantiate(prefab, container);"""

COROUTINE_SRC = """using System.Collections;
using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public GameObject prefab;
    public Transform container;

    private Coroutine _waves;

    void Start()
    {{
        Restart();
    }}

    public void SetConfig({cfg} next)
    {{
        config = next;
        Restart();
    }}

    void Restart()
    {{
        if (_waves != null) StopCoroutine(_waves);
        _waves = config != null && prefab != null && container != null ? StartCoroutine(Waves(config)) : null;
    }}

    IEnumerator Waves({cfg} active)
    {{
        while (active.spawnInterval > 0f)
        {{
            yield return new WaitForSeconds(active.spawnInterval);
            int count = Mathf.Min(active.spawnsPerWave, active.maxAlive - container.childCount);
            for (int i = 0; i < count; i++) Instantiate(prefab, container);
        }}
    }}
}}
"""

HELPER_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("difficulty setup"); }}
}}
"""


def _spawner_source(cls, cfg, variant=None):
    parts = {"marker": "// usk-fake: %s" % variant if variant else "", "cls": cls, "cfg": cfg, "fields": "",
             "awake": "", "switch": "        config = next;\n        _elapsed = 0f;", "interval": "config.spawnInterval",
             "wave": WAVE}
    if variant == "runtime-clone":
        parts["awake"] = "        if (config != null) config = Instantiate(config);"
        parts["switch"] = "        config = Instantiate(next);\n        _elapsed = 0f;"
    elif variant == "writes-asset":
        parts["switch"] = ("        config.spawnInterval = next.spawnInterval;\n"
                           "        config.spawnsPerWave = next.spawnsPerWave;\n"
                           "        config.maxAlive = next.maxAlive;\n"
                           "        _elapsed = 0f;")
    elif variant == "cached-start":
        parts["fields"] = "    private float _interval;\n    private int _perWave;\n    private int _cap;\n"
        parts["awake"] = ("        if (config == null) return;\n"
                          "        _interval = config.spawnInterval;\n"
                          "        _perWave = config.spawnsPerWave;\n"
                          "        _cap = config.maxAlive;")
        parts["switch"] = "        config = next;"
        parts["interval"] = "_interval"
        parts["wave"] = ("        int count = Mathf.Min(_perWave, _cap - container.childCount);\n"
                         "        for (int i = 0; i < count; i++) Instantiate(prefab, container);")
    elif variant == "overshoot":
        parts["wave"] = ("        if (container.childCount >= config.maxAlive) return;\n"
                         "        for (int i = 0; i < config.spawnsPerWave; i++) Instantiate(prefab, container);")
    elif variant == "cap-per-config":
        parts["fields"] = "    private int _spawned;\n"
        parts["switch"] = "        config = next;\n        _elapsed = 0f;\n        _spawned = 0;"
        parts["wave"] = ("        int count = Mathf.Min(config.spawnsPerWave, config.maxAlive - _spawned);\n"
                         "        for (int i = 0; i < count; i++) { Instantiate(prefab, container); _spawned++; }")
    return SPAWNER_SRC.format(**parts)


def _create_script(ctx, name, content, folder=None):
    res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.asset("Scripts"), content=content)
    if res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return res.get("path")


def _scripts(ctx, variant=None, spawner_src=None, config_src=None):
    cfg, spawner = ctx.private.class_names(ctx.handles)
    return [_create_script(ctx, cfg, config_src or CONFIG_SRC.format(cls=cfg)),
            _create_script(ctx, spawner, spawner_src or _spawner_source(spawner, cfg, variant))]


def _assets(ctx, values=True):
    cfg, _spawner = ctx.private.class_names(ctx.handles)
    spec = ctx.private.difficulty_spec(ctx.seed)
    paths = []
    for level, key in ctx.private.LEVELS:
        path = ctx.handles[key]
        ctx.c.write("scriptableobject_create", typeName=cfg, savePath=path)
        if values:
            for name in ctx.private.FIELDS:
                ctx.c.write("scriptableobject_set", assetPath=path, fieldName=name, value=str(spec[level][name]))
        paths.append(path)
    return paths


def _wire(ctx, asset_key="assetNormal"):
    _cfg, spawner = ctx.private.class_names(ctx.handles)
    at = V.address(ctx.handles["spawner"])
    ctx.c.write("component_add", componentType=spawner, **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="config",
                assetPath=ctx.handles[asset_key], **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="prefab",
                assetPath=ctx.handles["prefab"], **at)
    ctx.c.write("component_set_serialized_property", componentType=spawner, propertyPath="container",
                referencePath=ctx.handles["containerPath"], objectType="Transform", **at)


def _answer(ctx, scripts, assets):
    return {"scriptPaths": scripts, "configAssetPaths": assets,
            "spawnerPath": (ctx.probe.go(ctx.handles["spawner"]) or {}).get("path"), "declaredComplete": True}


def _solve(ctx, variant=None, values=True, asset_key="assetNormal", spawner_src=None):
    scripts = _scripts(ctx, variant, spawner_src)
    assets = _assets(ctx, values)
    _wire(ctx, asset_key)
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


def coroutine_waves(ctx):
    cfg, spawner = ctx.private.class_names(ctx.handles)
    return _solve(ctx, spawner_src=COROUTINE_SRC.format(cls=spawner, cfg=cfg))


def cached_refresh(ctx):
    """Numbers copied into private fields in Awake and again in SetConfig: still read from the active asset."""
    cfg, spawner = ctx.private.class_names(ctx.handles)
    source = _spawner_source(spawner, cfg, "cached-start").replace("// usk-fake: cached-start", "").replace(
        "        config = next;\n    }",
        "        config = next;\n        _elapsed = 0f;\n        Awake();\n    }")
    return _solve(ctx, spawner_src=source)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def runtime_clone(ctx):
    return _solve(ctx, variant="runtime-clone")


def writes_asset(ctx):
    return _solve(ctx, variant="writes-asset")


def cached_start(ctx):
    return _solve(ctx, variant="cached-start")


def overshoot(ctx):
    return _solve(ctx, variant="overshoot")


def cap_per_config(ctx):
    return _solve(ctx, variant="cap-per-config")


def wrong_initial(ctx):
    return _solve(ctx, asset_key="assetHard")


def defaults_unset(ctx):
    return _solve(ctx, values=False)


def extra_script(ctx):
    answer = reference(ctx)
    cfg, _spawner = ctx.private.class_names(ctx.handles)
    helper = "DifficultySetup_" + cfg.split("_", 1)[1]
    _create_script(ctx, helper, HELPER_SRC.format(cls=helper), folder=ctx.asset("Editor"))
    return answer


def rename_container(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Holding_Area", **V.address(ctx.handles["container"]))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["configAssetPaths"] = answer["configAssetPaths"][:2]
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StraySpawner")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "difficulty.txt"), b"tuned\n")
    return answer


def broken_config(ctx):
    """A config script that does not compile: no asset and no component can follow, the answer says so."""
    cfg, spawner = ctx.private.class_names(ctx.handles)
    scripts = _scripts(ctx, config_src=CONFIG_SRC.format(cls=cfg).replace("public int maxAlive = 1;",
                                                                          "public int maxAlive = 1; {"))
    assets = []
    try:
        assets = _assets(ctx)
        _wire(ctx)
    except SkillError:
        pass
    answer = _answer(ctx, scripts, assets)
    answer["declaredComplete"] = False
    return answer


NEGATIVES = [
    Negative("runtime_clone", runtime_clone, {"C12"}, "the config is cloned with Instantiate at Awake and on switch"),
    Negative("writes_asset", writes_asset, {"C12", "C13"}, "SetConfig copies the numbers into the current asset"),
    Negative("cached_start", cached_start, {"C14"}, "the numbers are read once at Awake; SetConfig only sets the field"),
    Negative("overshoot", overshoot, {"C10", "C14"}, "a wave spawns in full whenever the container is below maxAlive"),
    Negative("cap_per_config", cap_per_config, {"C11", "C14"}, "maxAlive caps the instances spawned since the switch"),
    Negative("wrong_initial", wrong_initial, {"C4", "C10", "C12", "C14"}, "the scene references the high-cap asset"),
    Negative("defaults_unset", defaults_unset, {"C3", "C10", "C11", "C12", "C13", "C14"},
             "asset values left at the code defaults"),
    Negative("extra_script", extra_script, {"C5"}),
    Negative("rename_container", rename_container, {"C6"}),
    Negative("wrong_report", wrong_report, {"C9"}),
    Negative("outside_root", outside_root, {"C7"}),
    Negative("direct_file", direct_file, {"C8"}, "a non-script file written to disk next to the skills"),
    Negative("broken_config", broken_config, {"C1", "C2", "C3", "C4", "C9", "C10", "C11", "C12", "C13", "C14", "C15"},
             "a compile error in the config script: Play Mode cannot start"),
]

ALTERNATIVES = [("coroutine_waves", coroutine_waves), ("cached_refresh", cached_refresh),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
