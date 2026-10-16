"""T9 Rename two serialized fields of a script without losing the values already stored in the scene (composite).

Traps: renaming alone silently resets every Guard's values after the reload, re-typing the values by hand
is forbidden (the journal shows it), and a decoy script with a field also called `speed` must not be touched.
Class names carry a seeded token plus the run id so a run never meets a type compiled by an earlier run.
Deleting and recreating the script is not a trap here: Unity 2022 hands the same path its old GUID back.
"""
import hashlib
import os
import re

from harness import checks as K
from harness import uyaml
from harness.task import Negative, Task

SPEEDS = (3.25, 7.5, 11.125)
LAPS = (2, 3, 4)
ROUTE_SPEED = 5.5
WORDS = ("Heron", "Lynx", "Moth", "Otter", "Wren", "Ibex")
RENAMED = {"speed": "moveSpeed", "waypoint": "patrolTarget"}

PATROL_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float speed = 1f;
    public Transform waypoint;
    public int laps = 1;
}}
"""

PATROL_RENAMED = """using UnityEngine;
using UnityEngine.Serialization;

public class {cls} : MonoBehaviour
{{
    [FormerlySerializedAs("{old_speed}")]
    public float moveSpeed = 1f;
    [FormerlySerializedAs("{old_waypoint}")]
    public Transform patrolTarget;
    public int laps = 1;
}}
"""

PATROL_PLAIN = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float moveSpeed = 1f;
    public Transform patrolTarget;
    public int laps = 1{tail}
}}
"""

ROUTE_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public float speed = 2f;
    public bool loop = true;
}}
"""

EDITOR_SRC = """using UnityEditor;
using UnityEngine;

public static class {cls}
{{
    [MenuItem("Tools/{cls}")]
    public static void Run() {{ Debug.Log("migrate"); }}
}}
"""

FIELD_RE = r"^\s*(?:\[[^\]]*\]\s*)*public\s+{type}\s+{name}\b"
ATTR_RE = r"\[\s*(?:UnityEngine\.Serialization\.)?FormerlySerializedAs(?:Attribute)?\s*\(\s*\"{old}\"\s*\)\s*\]" \
          r"[\s\S]{{0,200}}?\b{type}\s+{new}\b"


def cs_files(project_path):
    out = set()
    for base, _dirs, files in os.walk(os.path.join(project_path, "Assets")):
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


def read_text(project_path, rel):
    try:
        with open(os.path.join(project_path, rel), "r", encoding="utf-8-sig") as handle:
            return handle.read()
    except OSError:
        return None


class T9(Task):
    id = "T9"
    title = "Rename serialized fields keeping the stored values"
    tier = "composite"
    timeout_s = 2400
    budget_usd = 40.0
    needs_focus = True

    def assertions(self):
        return [
            ("C1", 1.0, False, "Patrol script declares moveSpeed/patrolTarget/laps, no speed/waypoint, same class"),
            ("C2", 1.0, False, "FormerlySerializedAs(\"speed\") on moveSpeed and (\"waypoint\") on patrolTarget"),
            ("C3", 0.5, False, "compilation clean and the loaded component exposes the new fields"),
            ("C4", 2.0, False, "each Guard keeps its moveSpeed value (3.25 / 7.5 / 11.125)"),
            ("C5", 2.0, False, "each Guard's patrolTarget still references its own waypoint"),
            ("C6", 0.5, False, "laps values unchanged"),
            ("C7", 1.0, False, "no property writes to the renamed fields through skills (journal)"),
            ("C8", 1.0, True, "Patrol script keeps its GUID; no other code file added or changed"),
            ("C9", 0.5, True, "guards, waypoints and the decoy route unchanged; no side effects outside R/A"),
            ("C10", 0.5, False, "answer fields match the actual state"),
        ]

    def allowed_change(self, ctx, rec):
        # script edits return before Unity imports the file, so the import is journaled with source "editor"
        path = rec.get("assetPath") or ""
        return rec.get("kind") == "imported" and rec.get("source") == "editor" and \
            path.startswith(ctx.A + "/") and (path.endswith(".cs") or path.endswith(".cs.meta"))

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        word = rng.choice(WORDS)
        k = "%s_%s" % (word, ctx.scope.run_id)
        patrol, route = "Patrol_" + k, "PatrolRoute_" + k
        scripts = ctx.asset("Scripts")
        for name, src in ((patrol, PATROL_SRC), (route, ROUTE_SRC)):
            res = c.write("script_create", scriptName=name, folder=scripts, content=src.format(cls=name))
            if res.get("jobId"):
                c.wait_job(res["jobId"])
            c.wait_idle()

        wp_root = c.write("gameobject_create", name="Waypoints", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        waypoints = []
        for i in range(3):
            waypoints.append(c.write("gameobject_create", name="W%d" % (i + 1), parentInstanceId=wp_root,
                                     x=-6 + 6 * i, y=0, z=8)["instanceId"])
        mapping = list(range(3))
        rng.shuffle(mapping)
        guards_root = c.write("gameobject_create", name="Guards", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        guards = []
        for i in range(3):
            iid = c.write("gameobject_create", name="Guard_%d" % (i + 1), primitiveType="Capsule",
                          parentInstanceId=guards_root, x=-4 + 4 * i, y=1, z=0)["instanceId"]
            c.write("component_add", instanceId=iid, componentType=patrol)
            c.write("component_set_property", instanceId=iid, componentType=patrol, propertyName="speed",
                    value=repr(SPEEDS[i]))
            c.write("component_set_serialized_property", instanceId=iid, componentType=patrol,
                    propertyPath="waypoint", referenceInstanceId=waypoints[mapping[i]], objectType="Transform")
            c.write("component_set_property", instanceId=iid, componentType=patrol, propertyName="laps",
                    value=str(LAPS[i]))
            guards.append(iid)
        route_go = c.write("gameobject_create", name="Route", parentInstanceId=ctx.scope.root_iid, x=0, y=0, z=12)
        c.write("component_add", instanceId=route_go["instanceId"], componentType=route)
        c.write("component_set_property", instanceId=route_go["instanceId"], componentType=route,
                propertyName="speed", value=repr(ROUTE_SPEED))
        ctx.facts.update({
            "k": k, "patrolClass": patrol, "routeClass": route,
            "patrolPath": "%s/%s.cs" % (scripts, patrol), "routePath": "%s/%s.cs" % (scripts, route),
            "guardIids": guards, "waypointIids": waypoints, "guardWaypoint": [waypoints[m] for m in mapping],
            "routeIid": route_go["instanceId"], "guardsPath": ctx.path("Guards"),
        })

    def after_fixture(self, ctx):
        p, f, proj = ctx.probe, ctx.facts, ctx.project_path
        ids = f["guardIids"] + f["waypointIids"] + [f["routeIid"]]
        f["objectsC0"] = {str(i): K.go_state(p, i) for i in ids}
        f["routeSerC0"] = p.ser(f["routeIid"], f["routeClass"])
        f["patrolGuid"] = uyaml.meta_guid(os.path.join(proj, f["patrolPath"]))
        f["routeSha"] = file_sha(proj, f["routePath"])
        f["csC0"] = {rel: file_sha(proj, rel) for rel in cs_files(proj)}

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("脚本 `{path}`（类 `{cls}`）已挂在 `{guards}` 下的 Guard_1、Guard_2、Guard_3 上，每个 Guard 都设置了 "
                "speed、waypoint、laps。请把字段 `speed` 改名为 `moveSpeed`、`waypoint` 改名为 `patrolTarget`，"
                "类名、文件名、其他字段及其类型和默认值保持不变。\n"
                "要求：三个 Guard 上原有的速度数值和路点引用在改名后必须保留，而且不得在改名后通过技能重新写入这些值；"
                "不要修改其他脚本，不要新增任何代码文件（包括编辑器脚本），不要修改其他对象和资产。").format(
            path=f["patrolPath"], cls=f["patrolClass"], guards=f["guardsPath"])

    def answer_schema(self):
        return ('{"scriptPath": "修改后的脚本路径", "guards": [{"instanceId": 整数, "moveSpeed": 数值, '
                '"patrolTargetInstanceId": 路点 GameObject 的 instanceId}], "declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def verify(self, ctx, card):
        p, f, proj = ctx.probe, ctx.facts, ctx.project_path
        text = read_text(proj, f["patrolPath"]) or ""
        code = re.sub(r"//[^\n]*", "", text)

        def field(type_name, name):
            return re.search(FIELD_RE.format(type=type_name, name=name), code, re.M) is not None

        declared = {"moveSpeed": field("float", "moveSpeed"), "patrolTarget": field("Transform", "patrolTarget"),
                    "laps": field("int", "laps"), "speed": field("float", "speed"),
                    "waypoint": field("Transform", "waypoint")}
        class_ok = re.search(r"\bclass\s+%s\s*:\s*MonoBehaviour\b" % re.escape(f["patrolClass"]), code) is not None
        card.check("C1", class_ok and declared["moveSpeed"] and declared["patrolTarget"] and declared["laps"] and
                   not declared["speed"] and not declared["waypoint"], actual=dict(declared, classOk=class_ok))
        attrs = {"speed": re.search(ATTR_RE.format(old="speed", type="float", new="moveSpeed"), code) is not None,
                 "waypoint": re.search(ATTR_RE.format(old="waypoint", type="Transform", new="patrolTarget"),
                                       code) is not None}
        card.check("C2", all(attrs.values()), actual=attrs)

        comp = ctx.c.read("debug_check_compilation")
        last = comp.get("lastCompilation") or {}
        try:
            fb = ctx.c.read("script_get_compile_feedback", scriptPath=f["patrolPath"])
        except Exception:
            fb = {}
        sers = [p.ser(g, f["patrolClass"]) or {} for g in f["guardIids"]]
        exposes = all("moveSpeed" in s and "patrolTarget" in s and "speed" not in s and "waypoint" not in s
                      for s in sers)
        card.check("C3", not comp.get("isCompiling") and last.get("succeeded") is not False and
                   not last.get("errorCount") and not fb.get("hasErrors") and exposes,
                   actual={"last": {k: last.get(k) for k in ("succeeded", "errorCount")},
                           "hasErrors": fb.get("hasErrors"), "exposesNewFields": exposes})

        speeds = [s.get("moveSpeed") for s in sers]
        card.check("C4", all(K.close_tuple(v, (want,)) for v, want in zip(speeds, SPEEDS)),
                   expected=SPEEDS, actual=speeds)
        owners = [p.owner(s.get("patrolTarget")) for s in sers]
        card.check("C5", owners == f["guardWaypoint"], expected=f["guardWaypoint"],
                   actual={"owners": owners, "raw": [s.get("patrolTarget") for s in sers]})
        laps = [s.get("laps") for s in sers]
        card.check("C6", [str(v) for v in laps] == [str(v) for v in LAPS], expected=LAPS, actual=laps)

        watched = set(RENAMED) | set(RENAMED.values())
        manual = [r for r in ctx.records if r.get("kind") == "property_changed" and r.get("source") == "rest" and
                  (r.get("property") or "").split(".")[0] in watched]
        card.check("C7", not manual, actual=manual[:10])

        now = {rel: file_sha(proj, rel) for rel in cs_files(proj)}
        changed = sorted(rel for rel in set(now) | set(f["csC0"])
                         if rel != f["patrolPath"] and now.get(rel) != f["csC0"].get(rel))
        guid_now = uyaml.meta_guid(os.path.join(proj, f["patrolPath"])) if f["patrolPath"] in now else None
        card.check("C8", guid_now == f["patrolGuid"] and not changed,
                   expected=f["patrolGuid"], actual={"guid": guid_now, "otherCodeChanged": changed})

        objects = [i for i, s in f["objectsC0"].items() if not K.same(s, K.go_state(p, int(i)))[0]]
        route_ok = K.same(f["routeSerC0"], p.ser(f["routeIid"], f["routeClass"]))[0]
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("C9", not objects and route_ok and not bad and not saves,
                   actual={"changedObjects": objects, "routeUnchanged": route_ok, "violations": bad[:10],
                           "sceneSaves": saves[:5]})

        rows = K.answer(ctx, "guards") or []
        actual_rows = {g: (v, o) for g, v, o in zip(f["guardIids"], speeds, owners)}
        rows_ok = isinstance(rows, list) and len(rows) == 3 and all(
            isinstance(r, dict) and K.as_int(r.get("instanceId")) in actual_rows and
            K.close_tuple(r.get("moveSpeed"), (float(actual_rows[K.as_int(r.get("instanceId"))][0] or 0),)) and
            actual_rows[K.as_int(r.get("instanceId"))][0] is not None and
            K.as_int(r.get("patrolTargetInstanceId")) == actual_rows[K.as_int(r.get("instanceId"))][1]
            for r in rows)
        card.check("C10", rows_ok and K.norm_path(K.answer(ctx, "scriptPath")) == f["patrolPath"] and
                   K.answer(ctx, "declaredComplete") in (True, False), actual=ctx.answer)

    # ------------------------------------------------------------------ solutions

    def _rewrite(self, ctx, content, path=None):
        res = ctx.c.write("script_replace", scriptPath=path or ctx.facts["patrolPath"], find=r"(?s).+",
                          replace=content, isRegex=True)
        if res.get("jobId"):
            ctx.c.wait_job(res["jobId"])
        ctx.c.wait_idle()

    def _renamed(self, ctx, old_speed="speed", old_waypoint="waypoint"):
        return PATROL_RENAMED.format(cls=ctx.facts["patrolClass"], old_speed=old_speed, old_waypoint=old_waypoint)

    def _answer(self, ctx):
        f, p = ctx.facts, ctx.probe
        rows = []
        for g in f["guardIids"]:
            s = p.ser(g, f["patrolClass"]) or {}
            rows.append({"instanceId": g, "moveSpeed": float(s["moveSpeed"]) if s.get("moveSpeed") else None,
                         "patrolTargetInstanceId": p.owner(s.get("patrolTarget"))})
        return {"scriptPath": f["patrolPath"], "guards": rows, "declaredComplete": True}

    def reference(self, ctx):
        self._rewrite(ctx, self._renamed(ctx))
        return self._answer(ctx)

    def negatives(self):
        def no_attribute(ctx):
            self._rewrite(ctx, PATROL_PLAIN.format(cls=ctx.facts["patrolClass"], tail=";"))
            return self._answer(ctx)

        def rename_and_reset(ctx):
            f = ctx.facts
            self._rewrite(ctx, PATROL_PLAIN.format(cls=f["patrolClass"], tail=";"))
            for g, speed, wp in zip(f["guardIids"], SPEEDS, f["guardWaypoint"]):
                ctx.c.write("component_set_property", instanceId=g, componentType=f["patrolClass"],
                            propertyName="moveSpeed", value=repr(speed))
                ctx.c.write("component_set_serialized_property", instanceId=g, componentType=f["patrolClass"],
                            propertyPath="patrolTarget", referenceInstanceId=wp, objectType="Transform")
            return self._answer(ctx)

        def claims_preserved(ctx):
            # renames without the attribute but reports the fixture values as if they had survived
            f = ctx.facts
            self._rewrite(ctx, PATROL_PLAIN.format(cls=f["patrolClass"], tail=";"))
            rows = [{"instanceId": g, "moveSpeed": speed, "patrolTargetInstanceId": wp}
                    for g, speed, wp in zip(f["guardIids"], SPEEDS, f["guardWaypoint"])]
            return {"scriptPath": f["patrolPath"], "guards": rows, "declaredComplete": True}

        def compile_error(ctx):
            self._rewrite(ctx, self._renamed(ctx).replace("public int laps = 1;", "public int laps = 1"))
            return {"scriptPath": ctx.facts["patrolPath"], "guards": [], "declaredComplete": False}

        def attribute_typo(ctx):
            self._rewrite(ctx, self._renamed(ctx, old_speed="sped"))
            return self._answer(ctx)

        def editor_helper(ctx):
            self._rewrite(ctx, self._renamed(ctx))
            name = "PatrolMigration_" + ctx.facts["k"]
            res = ctx.c.write("script_create", scriptName=name, folder=ctx.asset("Editor"),
                              content=EDITOR_SRC.format(cls=name))
            if res.get("jobId"):
                ctx.c.wait_job(res["jobId"])
            ctx.c.wait_idle()
            return self._answer(ctx)

        def rename_decoy_too(ctx):
            f = ctx.facts
            self._rewrite(ctx, self._renamed(ctx))
            self._rewrite(ctx, ROUTE_SRC.format(cls=f["routeClass"]).replace(
                "    public float speed", "    [UnityEngine.Serialization.FormerlySerializedAs(\"speed\")]\n"
                                         "    public float moveSpeed"), path=f["routePath"])
            return self._answer(ctx)

        return [
            Negative("no_attribute", no_attribute, {"C2", "C4", "C5"}),
            Negative("rename_and_reset", rename_and_reset, {"C2", "C7"}),
            Negative("claims_preserved", claims_preserved, {"C2", "C4", "C5", "C10"}),
            Negative("compile_error", compile_error, {"C3", "C4", "C5", "C10"}),
            Negative("attribute_typo", attribute_typo, {"C2", "C4"}),
            Negative("editor_helper", editor_helper, {"C8"}),
            Negative("rename_decoy_too", rename_decoy_too, {"C8", "C9"}),
        ]

    def alternatives(self):
        def edit_file_and_refresh(ctx):
            path = os.path.join(ctx.project_path, ctx.facts["patrolPath"])
            with open(path, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(self._renamed(ctx))
            res = ctx.c.write("asset_refresh")
            if res.get("jobId"):
                ctx.c.wait_job(res["jobId"])
            ctx.c.wait_idle()
            return self._answer(ctx)

        return [("edit_file_and_refresh", edit_file_and_refresh)]


TASK = T9()

# Producer:Betsy
