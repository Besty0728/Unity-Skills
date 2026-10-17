"""T1 private verifier: turret scripts, config asset and component references, then a Play Mode check.

What the task probes: three same-name turrets whose sibling order differs from their world X order (the turret to
wire is the one with the largest X in C0); three same-name dummies of which only one carries the Respawn tag (found
from the C0 tags); same-name projectile prefabs in another folder and an "_Old" variant; no code beyond the two given
scripts; target is a Transform reference, not the GameObject. Class names carry a seeded token plus the run id, so a
run never meets a type compiled by an earlier run. The runtime part enters Play Mode and reads the component back
from the object that holds it.
"""
import hashlib
import os
import re

from usk_eval import verifylib as V

PREFAB_REF_RE = re.compile(r"^  projectilePrefab:\s*\{[^}]*?guid:\s*([0-9a-f]{32})", re.M | re.S)

CONFIG_SRC = """using UnityEngine;

public class {cls} : ScriptableObject
{{
    public float fireRate = 1f;
    public int burstCount = 1;
    public GameObject projectilePrefab;
}}
"""

AI_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public {cfg} config;
    public Transform target;
    public float turnSpeed = 30f;
}}
"""

ASSERTIONS = [
    ("B1", 0.75, False, ["R1"], "both scripts exist at A/Scripts/<Class>.cs with exactly the given content"),
    ("B2", 0.5, False, ["R1"], "compilation finished without errors and both classes are loaded"),
    ("B3", 1.0, True, ["R4"], "no code file other than the two scripts was added under Assets"),
    ("B4", 0.75, False, ["R2"], "A/Data/MainTurretConfig.asset exists and is a TurretConfig_<k>"),
    ("B5", 1.0, False, ["R2"], "the config asset has fireRate 2.5 and burstCount 3"),
    ("B6", 0.75, False, ["R2"], "the config's projectilePrefab is A/Prefabs/Projectile_<k>.prefab (GUID on disk)"),
    ("B7", 0.75, False, ["R3"], "exactly one TurretAI in the scene, on the turret with the largest world X; the "
                                "other turrets are unchanged"),
    ("B8", 0.5, False, ["R3"], "TurretAI.config is the asset at A/Data/MainTurretConfig.asset"),
    ("B9", 1.0, False, ["R3"], "TurretAI.target is the Transform of the Respawn-tagged Dummy"),
    ("B10", 0.5, False, ["R3"], "TurretAI.turnSpeed is 90"),
    ("B11", 0.5, True, ["R5"], "dummies, barrels and the three projectile prefabs are unchanged"),
    ("B12", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("B13", 0.5, False, ["R7", "G8"], "the answer reports the scripts, the config asset, the turret and the dummy"),
    ("B14", 1.0, False, ["R6"], "in Play Mode no error is logged and the TurretAI holds config, target and turnSpeed 90"),
    ("B15", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "B1": {"zh-CN": "两个脚本要逐字照抄，放在 A/Scripts 下，文件名与类名一致。",
           "en": "Copy both scripts verbatim into A/Scripts, each file named after its class."},
    "B2": {"zh-CN": "等编译完成并确认没有错误后再创建资产和组件。",
           "en": "Wait until compilation finishes without errors before creating the asset and the component."},
    "B3": {"zh-CN": "不要为完成任务额外写编辑器脚本或其他代码文件。",
           "en": "Do not write editor scripts or any other code file to get the job done."},
    "B4": {"zh-CN": "用 scriptableobject_create 按题目给出的路径和类型创建资产。",
           "en": "Create the asset with scriptableobject_create at the given path and type."},
    "B5": {"zh-CN": "fireRate 应为 2.5，burstCount 应为 3。", "en": "fireRate must be 2.5 and burstCount 3."},
    "B6": {"zh-CN": "A/Prefabs/Projectile_<k>_Old.prefab 和 A/Common 下的同名预制体都不是要求的那个。",
           "en": "Neither A/Prefabs/Projectile_<k>_Old.prefab nor the same-name prefab under A/Common is the one asked for."},
    "B7": {"zh-CN": "三个 Turret 同名且同级顺序与世界 X 顺序不同；按世界坐标找 X 最大的那个，用 instanceId 或 entityId 操作。",
           "en": "The three Turrets share a name and their sibling order differs from their world X order; pick the "
                 "largest X by world position and act on its instanceId or entityId."},
    "B8": {"zh-CN": "config 要指向 A/Data/MainTurretConfig.asset。", "en": "config must point at A/Data/MainTurretConfig.asset."},
    "B9": {"zh-CN": "三个 Dummy 同名，只有一个标签是 Respawn；target 要指向它的 Transform。",
           "en": "The three Dummies share a name and only one is tagged Respawn; target must be its Transform."},
    "B10": {"zh-CN": "turnSpeed 应为 90，脚本里的默认值是 30。", "en": "turnSpeed must be 90; the script default is 30."},
    "B11": {"zh-CN": "不要改动 Dummy、Barrel 和已有的预制体。", "en": "Leave the Dummies, the Barrels and the existing prefabs alone."},
    "B12": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
            "en": "Keep every change inside R and A, and save no scene but this run's."},
    "B13": {"zh-CN": "答案中的路径和 id 要与实际状态一致，并写明 declaredComplete。",
            "en": "Report the actual paths and ids, and declaredComplete."},
    "B14": {"zh-CN": "验收会进入 Play 模式读取组件；要保证运行时没有错误日志，且字段值在运行时保持设置。",
            "en": "Verification enters Play Mode and reads the component back; keep the Console free of errors and the "
                  "values set at runtime."},
    "B15": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def class_names(handles):
    def name(path):
        return os.path.basename(path)[:-len(".cs")]
    return name(handles["configScript"]), name(handles["aiScript"])


def expected_sources(handles):
    cfg, ai = class_names(handles)
    return CONFIG_SRC.format(cls=cfg), AI_SRC.format(cls=ai, cfg=cfg)


def main_turret(c0):
    """Id of the turret with the largest world X in C0 (R3)."""
    rows = [((V.floats((state or {}).get("position"))[:1] or [float("-inf")])[0], ref)
            for ref, state in c0.get("turrets") or []]
    return max(rows, key=lambda row: row[0])[1] if rows else None


def respawn_dummy(c0):
    """Id of the Dummy tagged Respawn in C0 (R3)."""
    hits = [ref for ref, state in c0.get("dummies") or [] if (state or {}).get("tag") == "Respawn"]
    return hits[0] if len(hits) == 1 else None


def norm_src(text):
    text = (text or "").lstrip("﻿").replace("\r\n", "\n")
    return "\n".join(line.rstrip() for line in text.split("\n")).strip("\n")


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _text(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "r", encoding="utf-8-sig") as handle:
            return handle.read()
    except (OSError, UnicodeDecodeError):
        return None


def _sha(ctx, asset_path):
    try:
        with open(_disk(ctx, asset_path), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def holders(ctx):
    """Ids of the GameObjects that carry the TurretAI class (empty when the class is not loaded)."""
    _cfg, ai = class_names(ctx.handles)
    found = _read(ctx, "gameobject_find", component=ai, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


def holder(ctx, found=None):
    """The object whose TurretAI is graded: the C0 main turret when it holds one, else the first holder."""
    found = holders(ctx) if found is None else found
    main = main_turret(ctx.c0)
    return next((ref for ref in found if V.same_id(ref, main)), found[0] if found else None)


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cfg_class, ai_class = class_names(h)
    paths = [h["configScript"], h["aiScript"]]
    contents = [_text(ctx, path) for path in paths]
    matches = [text is not None and norm_src(text) == norm_src(want)
               for text, want in zip(contents, expected_sources(h))]
    card.check("B1", all(matches), expected=paths, actual={"match": matches,
                                                           "present": [text is not None for text in contents]})

    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors") for path in paths]
    found = holders(ctx)
    config = p.asset(h["configAsset"])
    loaded = bool(found) or (config or {}).get("type") == cfg_class
    card.check("B2", not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")
               and feedback == [False, False] and loaded,
               actual={"isCompiling": comp.get("isCompiling"), "last": {k: last.get(k) for k in ("succeeded",
                                                                                                   "errorCount")},
                       "hasErrors": feedback, "loaded": loaded})

    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    card.check("B3", added == sorted(paths), expected=sorted(paths), actual=added)

    card.check("B4", (config or {}).get("type") == cfg_class, expected=cfg_class, actual=(config or {}).get("type"))
    so = (p.so(h["configAsset"]) or {}) if config else {}
    props = {row.get("propertyPath"): row.get("value") for row in so.get("properties") or []}
    card.check("B5", V.close_tuple(props.get("fireRate"), (2.5,)) and V.as_int(props.get("burstCount")) == 3,
               expected={"fireRate": 2.5, "burstCount": 3},
               actual={"fireRate": props.get("fireRate"), "burstCount": props.get("burstCount")})
    guids = {path: guid for path, _sha, guid in c0.get("prefabs") or []}
    text = _text(ctx, h["configAsset"]) if config else None
    match = PREFAB_REF_RE.search(text or "")
    disk_guid = match.group(1) if match else None
    card.check("B6", disk_guid is not None and disk_guid == guids.get(h["projectilePrefab"]),
               expected=guids.get(h["projectilePrefab"]), actual={"guid": disk_guid, "prefabGuids": guids})

    main = main_turret(c0)
    others = [(ref, state) for ref, state in c0.get("turrets") or [] if not V.same_id(ref, main)]
    others_ok = all(V.same(state, V.go_state(p, ref))[0] for ref, state in others)
    on_main = p.component_count(main, ai_class) if main is not None else 0
    card.check("B7", len(found) == 1 and V.same_id(found[0], main) and on_main == 1 and others_ok,
               expected=main, actual={"holders": found, "countOnMain": on_main, "othersUnchanged": others_ok})

    graded = holder(ctx, found)
    ser = (p.ser(graded, ai_class) or {}) if graded is not None else {}
    cfg_ref = V.parse_ref(ser.get("config"))
    card.check("B8", bool(cfg_ref) and cfg_ref[1] == cfg_class and cfg_ref[2] == "MainTurretConfig" and
               (config or {}).get("type") == cfg_class, expected=h["configAsset"],
               actual={"config": ser.get("config"), "assetAtPath": bool(config)},
               note="asset identity by type and name plus an asset of that type at the required path")
    target_ref = V.parse_ref(ser.get("target"))
    target_owner = p.owner(ser.get("target"))
    respawn = respawn_dummy(c0)
    card.check("B9", V.same_id(target_owner, respawn) and bool(target_ref) and target_ref[1] == "UnityEngine.Transform",
               expected=respawn, actual={"target": ser.get("target"), "owner": target_owner})
    card.check("B10", V.close_tuple(ser.get("turnSpeed"), (90,)), expected=90, actual=ser.get("turnSpeed"))

    changed = [str(ref) for ref, state in (c0.get("barrels") or []) + (c0.get("dummies") or [])
               if not V.same(state, V.go_state(p, ref))[0]]
    prefabs = [path for path, sha, _guid in c0.get("prefabs") or [] if _sha(ctx, path) != sha]
    card.check("B11", not changed and not prefabs, actual={"changedObjects": changed, "changedPrefabs": prefabs})

    bad = ctx.violations()
    card.check("B12", not bad, actual={"violations": bad[:10]})

    reported = [V.norm_path(x) for x in (V.answer(ctx, "scriptPaths") or []) if isinstance(x, str)]
    reported_config = V.norm_path(V.answer(ctx, "configAssetPath"))
    reported_info = p.asset(reported_config) if reported_config else None
    answer_ok = (len(reported) == 2 and set(reported) <= set(added) and
                 all(os.path.isfile(_disk(ctx, path)) for path in reported) and
                 (reported_info or {}).get("type") == cfg_class and
                 (not cfg_ref or cfg_ref[2] == (reported_info or {}).get("name")) and
                 graded is not None and V.same_id(V.answer(ctx, "turretInstanceId"), graded) and
                 V.same_id(V.answer(ctx, "dummyInstanceId"), target_owner) and
                 isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("B13", answer_ok, actual=ctx.answer)

    direct = [rec for rec in ctx.direct_writes()
              if not re.search(r"\.cs(\.meta)?$", str(rec.get("assetPath") or ""))]
    card.check("B15", not direct, actual={"directWrites": direct[:10]})


def SCENARIO(ctx):
    """Play Mode: read the TurretAI back from the object that holds it (the main turret when nothing does)."""
    _cfg, ai_class = class_names(ctx.handles)
    target = holder(ctx) or main_turret(ctx.c0)
    return {"executionMode": "play", "timeoutSeconds": 90, "steps": [
        {"kind": "wait_frames", "frames": 2},
        {"kind": "read", "skill": "component_get_serialized_properties",
         "args": dict({"componentType": ai_class}, **V.address(target))},
        {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True},
    ]}


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    cfg_class, _ai = class_names(ctx.handles)
    read = next((step for step in scenario.get("steps") or [] if step.get("kind") == "read"), None) or {}
    props = {row.get("propertyPath"): row.get("value")
             for row in ((read.get("actual") or {}).get("properties") or []) if isinstance(row, dict)}
    cfg_ref, target_ref = V.parse_ref(props.get("config")), V.parse_ref(props.get("target"))
    values_ok = (bool(cfg_ref) and cfg_ref[1] == cfg_class and cfg_ref[2] == "MainTurretConfig" and
                 bool(target_ref) and target_ref[1] == "UnityEngine.Transform" and
                 V.close_tuple(props.get("turnSpeed"), (90,)))
    card.check("B14", scenario.get("passed") is True and values_ok,
               actual={"passed": scenario.get("passed"), "error": scenario.get("error"),
                       "status": scenario.get("status"), "config": props.get("config"),
                       "target": props.get("target"), "turnSpeed": props.get("turnSpeed")})

# Producer:Betsy
