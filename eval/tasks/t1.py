"""T1 Turret: two verbatim scripts, compile, a ScriptableObject asset and component references (composite).

Traps: three same-name turrets whose sibling order differs from their world X order, three same-name
dummies of which only one carries the Respawn tag, same-name projectile prefabs in another folder and
an "_Old" variant, and the requirement to add no code beyond the two given scripts. Class names carry
a seeded token plus the run id so a run never meets a type compiled by an earlier run.
"""
import hashlib
import json
import os
import re

from harness import checks as K
from harness import uyaml
from harness.task import Negative, Task
from harness.verify import parse_ref

TURRET_X = (-4.0, 0.0, 4.0)
DUMMY_TAGS = ("Respawn", "Untagged", "Player")
PREFAB_REF_RE = re.compile(r"^  projectilePrefab:\s*\{[^}]*?guid:\s*([0-9a-f]{32})", re.M | re.S)
WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow")

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

EDITOR_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("setup"); }}
}}
"""


def norm_src(text):
    text = (text or "").lstrip("\ufeff").replace("\r\n", "\n")
    return "\n".join(line.rstrip() for line in text.split("\n")).strip("\n")


def cs_files(project_path):
    out = set()
    root = os.path.join(project_path, "Assets")
    for base, _dirs, files in os.walk(root):
        for name in files:
            if name.endswith(".cs"):
                out.add(os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/"))
    return sorted(out)


def file_sha(project_path, rel):
    try:
        with open(os.path.join(project_path, rel), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


class T1(Task):
    id = "T1"
    title = "Turret scripts, config asset and component references"
    tier = "composite"
    timeout_s = 2400
    budget_usd = 40.0
    needs_focus = True

    def assertions(self):
        return [
            ("B1", 1.0, False, "both scripts exist at A/Scripts/<Class>.cs with exactly the given content"),
            ("B2", 0.5, False, "compilation finished without errors and both classes are loaded"),
            ("B3", 1.0, True, "no .cs file other than the two scripts was added anywhere under Assets"),
            ("B4", 1.0, False, "A/Data/MainTurretConfig.asset exists and is a TurretConfig_<k>"),
            ("B5", 1.0, False, "config asset fireRate 2.5 and burstCount 3"),
            ("B6", 1.0, False, "config projectilePrefab is A/Prefabs/Projectile_<k>.prefab (GUID on disk)"),
            ("B7", 1.0, False, "exactly one TurretAI in the scene, on the turret with the largest world X; "
                               "other turrets unchanged"),
            ("B8", 0.5, False, "TurretAI.config is the asset at A/Data/MainTurretConfig.asset"),
            ("B9", 1.0, False, "TurretAI.target is the Transform of the Respawn-tagged dummy"),
            ("B10", 0.5, False, "TurretAI.turnSpeed is 90"),
            ("B11", 0.5, True, "dummies, barrels and the three projectile prefabs unchanged"),
            ("B12", 0.5, True, "no side effects outside R/A and no scene save"),
            ("B13", 0.5, False, "answer fields match the actual state"),
        ]

    def allowed_change(self, ctx, rec):
        # script_create returns before Unity imports the file, so the import is journaled with source "editor"
        path = (rec.get("assetPath") or "")
        return rec.get("kind") == "imported" and rec.get("source") == "editor" and \
            path.startswith(ctx.A + "/") and (path.endswith(".cs") or path.endswith(".cs.meta"))

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        k = "%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)
        turret_order = list(TURRET_X)
        while turret_order == sorted(turret_order):
            rng.shuffle(turret_order)
        tag_order = list(DUMMY_TAGS)
        rng.shuffle(tag_order)

        for folder in ("Prefabs", "Legacy", "Data"):
            c.write("asset_create_folder", folderPath=ctx.asset(folder))
        prefabs = {"main": ctx.asset("Prefabs", "Projectile_%s.prefab" % k),
                   "old": ctx.asset("Prefabs", "Projectile_%s_Old.prefab" % k),
                   "legacy": ctx.asset("Legacy", "Projectile_%s.prefab" % k)}
        shapes = {"main": "Sphere", "old": "Capsule", "legacy": "Cube"}
        for key in ("main", "old", "legacy"):
            name = os.path.basename(prefabs[key])[:-len(".prefab")]
            go = c.write("gameobject_create", name=name, primitiveType=shapes[key],
                         parentInstanceId=ctx.scope.root_iid)
            c.write("prefab_create", instanceId=go["instanceId"], savePath=prefabs[key])
            c.write("gameobject_delete", instanceId=go["instanceId"])

        turrets_root = c.write("gameobject_create", name="Turrets", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        turrets, barrels = {}, []
        for x in turret_order:
            iid = c.write("gameobject_create", name="Turret", primitiveType="Cube", parentInstanceId=turrets_root,
                          x=x, y=0.5, z=0)["instanceId"]
            barrel = c.write("gameobject_create", name="Barrel", primitiveType="Cylinder", parentInstanceId=iid,
                             x=0, y=0.6, z=0.5, scaleX=0.3, scaleY=0.3, scaleZ=0.3)["instanceId"]
            turrets[x] = iid
            barrels.append(barrel)
        targets_root = c.write("gameobject_create", name="Targets", parentInstanceId=ctx.scope.root_iid,
                               z=10)["instanceId"]
        dummies, respawn = [], None
        for i, tag in enumerate(tag_order):
            iid = c.write("gameobject_create", name="Dummy", primitiveType="Capsule", parentInstanceId=targets_root,
                          x=-3 + 3 * i, y=1, z=0)["instanceId"]
            if tag != "Untagged":
                c.write("gameobject_set_tag_batch", items=json.dumps([{"instanceId": iid, "tag": tag}]))
            dummies.append(iid)
            if tag == "Respawn":
                respawn = iid
        ctx.facts.update({
            "k": k, "configClass": "TurretConfig_" + k, "aiClass": "TurretAI_" + k,
            "scriptsDir": ctx.asset("Scripts"), "configPath": ctx.asset("Data", "MainTurretConfig.asset"),
            "prefabs": prefabs, "turretIids": [turrets[x] for x in turret_order], "mainTurretIid": turrets[max(TURRET_X)],
            "barrelIids": barrels, "dummyIids": dummies, "respawnIid": respawn,
            "turretsPath": ctx.path("Turrets"), "targetsPath": ctx.path("Targets"),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["turretsC0"] = {str(i): K.go_state(p, i) for i in f["turretIids"]}
        f["fixedC0"] = {str(i): K.go_state(p, i) for i in f["dummyIids"] + f["barrelIids"]}
        f["prefabShaC0"] = {key: file_sha(ctx.project_path, path) for key, path in f["prefabs"].items()}
        f["prefabGuids"] = {key: uyaml.meta_guid(os.path.join(ctx.project_path, path))
                            for key, path in f["prefabs"].items()}
        f["csC0"] = cs_files(ctx.project_path)

    # ------------------------------------------------------------------ prompt

    def _sources(self, f):
        return (CONFIG_SRC.format(cls=f["configClass"]),
                AI_SRC.format(cls=f["aiClass"], cfg=f["configClass"]))

    def _script_paths(self, f):
        return ["%s/%s.cs" % (f["scriptsDir"], f["configClass"]), "%s/%s.cs" % (f["scriptsDir"], f["aiClass"])]

    def prompt(self, ctx):
        f = ctx.facts
        cfg, ai = self._sources(f)
        return ("在 `{scripts}` 下按原文创建下面两个脚本（文件名与类名一致，内容逐字照抄，不要增删）：\n\n"
                "`{cfg_name}.cs`：\n```csharp\n{cfg}```\n\n`{ai_name}.cs`：\n```csharp\n{ai}```\n\n"
                "编译完成后：\n"
                "1. 创建 `{cfg_name}` 资产 `{asset}`，fireRate = 2.5，burstCount = 3，projectilePrefab 指向 `{prefab}`。\n"
                "2. `{turrets}` 下有三个同名的 Turret，只给世界坐标 X 最大的那个添加 `{ai_name}` 组件："
                "config 指向上面的资产，target 指向 `{targets}` 下标签为 Respawn 的那个 Dummy 的 Transform，turnSpeed = 90。\n"
                "除这两个脚本外不要新增任何代码文件（包括编辑器脚本），不要修改其他对象和资产。").format(
            scripts=f["scriptsDir"], cfg_name=f["configClass"], ai_name=f["aiClass"], cfg=cfg, ai=ai,
            asset=f["configPath"], prefab=f["prefabs"]["main"], turrets=f["turretsPath"], targets=f["targetsPath"])

    def answer_schema(self):
        return ('{"scriptPaths": ["两个脚本的资产路径"], "configAssetPath": "配置资产路径", '
                '"turretInstanceId": 整数, "targetDummyInstanceId": 整数, "declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def _ai_holders(self, ctx):
        try:
            res = ctx.c.read("gameobject_find", component=ctx.facts["aiClass"], limit=500)
        except Exception:
            return []
        return [o.get("instanceId") for o in res.get("objects") or []]

    def verify(self, ctx, card):
        p, f, proj = ctx.probe, ctx.facts, ctx.project_path
        expected_src = self._sources(f)
        paths = self._script_paths(f)
        contents = []
        for rel in paths:
            try:
                with open(os.path.join(proj, rel), "r", encoding="utf-8") as handle:
                    contents.append(handle.read())
            except OSError:
                contents.append(None)
        src_ok = [c is not None and norm_src(c) == norm_src(e) for c, e in zip(contents, expected_src)]
        card.check("B1", all(src_ok), expected=paths, actual={"match": src_ok, "present": [c is not None for c in contents]})

        comp = ctx.c.read("debug_check_compilation")
        last = comp.get("lastCompilation") or {}
        feedback = []
        for rel in paths:
            try:
                fb = ctx.c.read("script_get_compile_feedback", scriptPath=rel)
                feedback.append(bool(fb.get("hasErrors")))
            except Exception:
                feedback.append(None)
        holders = self._ai_holders(ctx)
        cfg_info = p.asset(f["configPath"])
        loaded = bool(holders) or (cfg_info or {}).get("type") == f["configClass"]
        card.check("B2", not comp.get("isCompiling") and last.get("succeeded") is not False and
                   not last.get("errorCount") and feedback == [False, False] and loaded,
                   actual={"compilation": {k: comp.get(k) for k in ("isCompiling", "isUpdating")},
                           "last": {k: last.get(k) for k in ("succeeded", "errorCount")}, "hasErrors": feedback,
                           "loaded": loaded})

        added_cs = sorted(set(cs_files(proj)) - set(f["csC0"]))
        card.check("B3", added_cs == sorted(paths), expected=sorted(paths), actual=added_cs)

        card.check("B4", (cfg_info or {}).get("type") == f["configClass"], expected=f["configClass"], actual=cfg_info)
        so = (p.so(f["configPath"]) or {}) if cfg_info else {}
        props = {x.get("propertyPath"): x.get("value") for x in so.get("properties") or []}
        card.check("B5", K.close_tuple(props.get("fireRate"), (2.5,)) and str(props.get("burstCount")) == "3",
                   expected={"fireRate": 2.5, "burstCount": 3},
                   actual={"fireRate": props.get("fireRate"), "burstCount": props.get("burstCount")})
        disk_guid, raw = None, None
        if cfg_info:
            # Unity wraps long flow mappings onto a second line, which uyaml does not join; read the raw text
            try:
                with open(os.path.join(proj, f["configPath"]), "r", encoding="utf-8") as handle:
                    text = handle.read()
                match = PREFAB_REF_RE.search(text)
                disk_guid = match.group(1) if match else None
                raw = text[text.find("projectilePrefab"):][:160] if "projectilePrefab" in text else None
            except OSError as exc:
                raw = repr(exc)
        card.check("B6", disk_guid is not None and disk_guid == f["prefabGuids"]["main"],
                   expected=f["prefabGuids"]["main"],
                   actual={"guid": disk_guid, "prefabGuids": f["prefabGuids"], "disk": raw})

        main = f["mainTurretIid"]
        others = [i for i in f["turretIids"] if i != main]
        others_ok = all(K.same(f["turretsC0"][str(i)], K.go_state(p, i))[0] for i in others)
        main_count = p.component_count(main, f["aiClass"])
        card.check("B7", holders == [main] and main_count == 1 and others_ok,
                   expected=main, actual={"holders": holders, "countOnMain": main_count, "othersUnchanged": others_ok})

        holder = main if main in holders else (holders[0] if holders else None)
        ser = p.ser(holder, f["aiClass"]) if holder else {}
        ser = ser or {}
        cfg_ref = parse_ref(ser.get("config"))
        card.check("B8", bool(cfg_ref) and cfg_ref[1] == f["configClass"] and cfg_ref[2] == "MainTurretConfig" and
                   (cfg_info or {}).get("type") == f["configClass"],
                   expected=f["configPath"], actual={"config": ser.get("config"), "assetAtPath": bool(cfg_info)},
                   note="asset identity by type + name + an asset of that type at the expected path")
        target_owner = p.owner(ser.get("target"))
        target_ref = parse_ref(ser.get("target"))
        card.check("B9", target_owner == f["respawnIid"] and bool(target_ref) and target_ref[1] == "UnityEngine.Transform",
                   expected=f["respawnIid"], actual={"target": ser.get("target"), "owner": target_owner})
        card.check("B10", K.close_tuple(ser.get("turnSpeed"), (90,)), expected=90, actual=ser.get("turnSpeed"))

        fixed_diff = [i for i, s in f["fixedC0"].items() if not K.same(s, K.go_state(p, int(i)))[0]]
        sha_now = {key: file_sha(proj, path) for key, path in f["prefabs"].items()}
        card.check("B11", not fixed_diff and sha_now == f["prefabShaC0"],
                   actual={"changedObjects": fixed_diff, "prefabsChanged": [k for k in sha_now
                                                                            if sha_now[k] != f["prefabShaC0"][k]]})
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("B12", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

        reported = [K.norm_path(x) for x in (K.answer(ctx, "scriptPaths") or []) if isinstance(x, str)]
        rep_cfg = K.norm_path(K.answer(ctx, "configAssetPath"))
        rep_cfg_info = p.asset(rep_cfg) if rep_cfg else None
        ans_ok = (sorted(reported) == sorted(r for r in reported if os.path.isfile(os.path.join(proj, r))) and
                  len(reported) == 2 and set(reported) <= set(added_cs) and
                  (rep_cfg_info or {}).get("type") == f["configClass"] and
                  (not cfg_ref or cfg_ref[2] == (rep_cfg_info or {}).get("name")) and
                  K.as_int(K.answer(ctx, "turretInstanceId")) == holder and
                  K.as_int(K.answer(ctx, "targetDummyInstanceId")) == target_owner and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("B13", ans_ok, actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _create_script(self, ctx, name, content, folder=None):
        res = ctx.c.write("script_create", scriptName=name, folder=folder or ctx.facts["scriptsDir"], content=content)
        if res.get("jobId"):
            ctx.c.wait_job(res["jobId"])
        ctx.c.wait_idle()
        return res.get("path")

    def _scripts(self, ctx, ai_src=None):
        f = ctx.facts
        cfg, ai = self._sources(f)
        return [self._create_script(ctx, f["configClass"], cfg), self._create_script(ctx, f["aiClass"], ai_src or ai)]

    def _config(self, ctx, path=None, prefab=None):
        f, c = ctx.facts, ctx.c
        path = path or f["configPath"]
        c.write("scriptableobject_create", typeName=f["configClass"], savePath=path)
        c.write("scriptableobject_set", assetPath=path, fieldName="fireRate", value="2.5")
        c.write("scriptableobject_set", assetPath=path, fieldName="burstCount", value="3")
        c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="projectilePrefab",
                valueAssetPath=prefab or f["prefabs"]["main"])
        return path

    def _wire(self, ctx, turret, config_path, target, turn_speed=90):
        f, c = ctx.facts, ctx.c
        c.write("component_add", instanceId=turret, componentType=f["aiClass"])
        c.write("component_set_serialized_property", instanceId=turret, componentType=f["aiClass"],
                propertyPath="config", assetPath=config_path)
        c.write("component_set_serialized_property", instanceId=turret, componentType=f["aiClass"],
                propertyPath="target", referenceInstanceId=target, objectType="Transform")
        if turn_speed is not None:
            c.write("component_set_property", instanceId=turret, componentType=f["aiClass"],
                    propertyName="turnSpeed", value=str(turn_speed))

    def _answer(self, ctx, scripts, config_path, turret, target):
        return {"scriptPaths": scripts, "configAssetPath": config_path, "turretInstanceId": turret,
                "targetDummyInstanceId": target, "declaredComplete": True}

    def _solve(self, ctx, turret=None, target=None, prefab=None, config_path=None, ai_src=None, turn_speed=90):
        f = ctx.facts
        scripts = self._scripts(ctx, ai_src)
        path = self._config(ctx, config_path, prefab)
        turret = turret or f["mainTurretIid"]
        target = target or f["respawnIid"]
        self._wire(ctx, turret, path, target, turn_speed)
        return self._answer(ctx, scripts, path, turret, target)

    def reference(self, ctx):
        return self._solve(ctx)

    def negatives(self):
        def wrong_turret(ctx):
            f = ctx.facts
            first = f["turretIids"][0] if f["turretIids"][0] != f["mainTurretIid"] else f["turretIids"][1]
            return self._solve(ctx, turret=first)

        def untagged_target(ctx):
            f = ctx.facts
            other = next(i for i in f["dummyIids"] if i != f["respawnIid"])
            return self._solve(ctx, target=other)

        def legacy_prefab(ctx):
            return self._solve(ctx, prefab=ctx.facts["prefabs"]["legacy"])

        def config_wrong_path(ctx):
            return self._solve(ctx, config_path=ctx.asset("MainTurretConfig.asset"))

        def edited_script(ctx):
            f = ctx.facts
            ai = AI_SRC.format(cls=f["aiClass"], cfg=f["configClass"]).replace("30f", "45f")
            return self._solve(ctx, ai_src=ai)

        def editor_helper(ctx):
            ans = self._solve(ctx)
            self._create_script(ctx, "TurretSetup_" + ctx.facts["k"],
                                EDITOR_SRC.format(cls="TurretSetup_" + ctx.facts["k"]), folder=ctx.asset("Editor"))
            return ans

        def turnspeed_unset(ctx):
            return self._solve(ctx, turn_speed=None)

        def all_three(ctx):
            f = ctx.facts
            ans = self._solve(ctx)
            for iid in f["turretIids"]:
                if iid != f["mainTurretIid"]:
                    self._wire(ctx, iid, f["configPath"], f["respawnIid"])
            return ans

        return [
            Negative("wrong_turret", wrong_turret, {"B7"}),
            Negative("untagged_target", untagged_target, {"B9"}),
            Negative("legacy_prefab", legacy_prefab, {"B6"}),
            Negative("config_wrong_path", config_wrong_path, {"B4", "B5", "B6", "B8"}),
            Negative("edited_script", edited_script, {"B1"}),
            Negative("editor_helper", editor_helper, {"B3"}),
            Negative("turnspeed_unset", turnspeed_unset, {"B10"}),
            Negative("all_three", all_three, {"B7"}),
        ]

    def alternatives(self):
        def property_setters(ctx):
            f, c = ctx.facts, ctx.c
            scripts = self._scripts(ctx)
            path = f["configPath"]
            c.write("scriptableobject_create", typeName=f["configClass"], savePath=path)
            c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="fireRate", value="2.5")
            c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="burstCount", value="3")
            c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath="projectilePrefab",
                    valueAssetPath=f["prefabs"]["main"])
            turret = f["mainTurretIid"]
            c.write("component_add", instanceId=turret, componentType=f["aiClass"])
            c.write("component_set_property", instanceId=turret, componentType=f["aiClass"], propertyName="config",
                    assetPath=path)
            c.write("component_set_serialized_property", instanceId=turret, componentType=f["aiClass"],
                    propertyPath="target", referenceInstanceId=f["respawnIid"], objectType="Transform")
            c.write("component_set_serialized_property", instanceId=turret, componentType=f["aiClass"],
                    propertyPath="turnSpeed", value="90")
            return self._answer(ctx, scripts, path, turret, f["respawnIid"])

        return [("property_setters", property_setters)]


TASK = T1()

# Producer:Betsy
