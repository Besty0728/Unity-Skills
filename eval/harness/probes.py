"""S1 capability probes: settle every "needs a trial run" fact the task bank depends on.

Each probe runs inside its own run scope in the evaluation project, records raw evidence and a
one-line conclusion, and is cleaned up afterwards. Results go to runs/probes/<ts>.json.
"""
import datetime
import json
import os
import subprocess
import time
import traceback

from . import fixture as F
from . import journal as J
from .focus import activate
from .rest import SkillError
from .verify import Probe, parse_ref

URP_LIT = "Universal Render Pipeline/Lit"


def _scope(client):
    scope = F.Scope(F.new_run_id())
    F.enter(client, scope)
    return scope


def probe_scene_lifecycle(c, out):
    loaded = c.read("scene_get_loaded")
    scope = _scope(c)
    try:
        after = c.read("scene_get_loaded")
        info = c.read("scene_get_info")
        child = c.write("gameobject_create", name="Probe", parentInstanceId=scope.root_iid)
        dirty = c.read("scene_get_info").get("isDirty")
        F.save_run_scene(c, scope)
        saved = c.read("scene_get_info").get("isDirty")
        out.update(ok=bool(info.get("scenePath") == scope.scene_path and dirty and saved is False),
                   conclusion="scene_create activates the run scene; isDirty flips on edit and clears on save",
                   evidence={"loadedKeys": sorted(loaded.keys()), "loadedBefore": loaded, "loadedAfter": after,
                             "activeInfo": info, "childIid": child.get("instanceId"), "dirtyAfterEdit": dirty,
                             "dirtyAfterSave": saved})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_journal_shapes(c, out):
    scope = _scope(c)
    try:
        cur = J.current_cursor(c)
        a = c.write("gameobject_create", name="J_A", primitiveType="Cube", parentInstanceId=scope.root_iid)
        b = c.write("gameobject_create", name="J_B", parentInstanceId=scope.root_iid, x=1, y=2, z=3)
        c.write("gameobject_set_transform", instanceId=a["instanceId"], posX=4)
        c.write("component_add", instanceId=b["instanceId"], componentType="Rigidbody")
        c.write("component_set_serialized_property", instanceId=b["instanceId"], componentType="Rigidbody",
                propertyPath="m_Mass", value="3.5")
        mat = c.write("material_create", name="JM", savePath=scope.asset_root + "/Mats/JM.mat")
        c.write("material_assign", instanceId=a["instanceId"], materialPath=scope.asset_root + "/Mats/JM.mat")
        c.write("asset_create_folder", folderPath=scope.asset_root + "/Moved")
        c.write("asset_move", sourcePath=scope.asset_root + "/Mats/JM.mat",
                destinationPath=scope.asset_root + "/Moved/JM.mat")
        c.write("gameobject_set_parent", childInstanceId=a["instanceId"], parentInstanceId=b["instanceId"])
        F.save_run_scene(c, scope)
        time.sleep(1.0)
        col = J.Collector(c, cur)
        col.drain()
        recs = J.flatten(col.events)
        kinds = sorted({r.get("kind") for r in recs})
        sample = {}
        for r in recs:
            sample.setdefault(r.get("kind"), r)
        out.update(ok=bool(recs), conclusion="journal kinds observed: %s" % ", ".join(str(k) for k in kinds),
                   evidence={"material": mat, "kinds": kinds, "sample": sample, "count": len(recs),
                             "violationsVsScope": J.scope_violations(recs, scope.object_root, scope.asset_root,
                                                                     scope.scene_path)})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_refs(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        a = c.write("gameobject_create", name="R_A", primitiveType="Cube", parentInstanceId=scope.root_iid)
        b = c.write("gameobject_create", name="R_B", primitiveType="Cube", parentInstanceId=scope.root_iid, x=2)
        c.write("component_add", instanceId=a["instanceId"], componentType="Rigidbody")
        c.write("component_add", instanceId=b["instanceId"], componentType="HingeJoint")
        c.write("component_set_serialized_property", instanceId=b["instanceId"], componentType="HingeJoint",
                propertyPath="m_ConnectedBody", referenceInstanceId=a["instanceId"], objectType="Rigidbody")
        hinge = p.ser(b["instanceId"], "HingeJoint") or {}
        ref = hinge.get("m_ConnectedBody")
        owner = p.owner(ref)
        props = c.read("component_get_properties", instanceId=b["instanceId"], componentType="HingeJoint")
        named = [x for x in props.get("properties") or [] if x.get("name") == "connectedBody"]
        mr = p.ser(a["instanceId"], "MeshRenderer") or {}
        mesh = c.read("model_get_mesh_info", instanceId=a["instanceId"])
        mf = p.ser(a["instanceId"], "MeshFilter") or {}
        out.update(ok=True,
                   conclusion="serialized reader lists m_ConnectedBody=%s (owner resolves=%s); get_properties gives %s" % (
                       ref is not None, owner == a["instanceId"], named),
                   evidence={"connectedBody": ref, "parsed": parse_ref(ref), "owner": owner,
                             "expectedOwner": a["instanceId"],
                             "materialsSlots": {k: v for k, v in mr.items() if k.startswith("m_Materials")},
                             "meshInfo": mesh, "meshFilter": {k: v for k, v in mf.items() if "Mesh" in k}})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_urp_material(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        path = scope.asset_root + "/M/Default.mat"
        made = c.write("material_create", name="Default", savePath=path)
        default_info = p.material(path=path)
        c.write("material_set_color", path=path, r=0.1, g=0.4, b=0.8, a=1.0, propertyName="_BaseColor")
        c.write("material_set_emission", path=path, r=1, g=0.5, b=0, intensity=2, enableEmission=True)
        after = p.material(path=path)
        bad = None
        try:
            c.write("material_set_color", path=path, r=1, g=0, b=0, a=1, propertyName="_Color")
        except SkillError as exc:
            bad = exc.payload
        go = c.write("gameobject_create", name="MatHolder", primitiveType="Cube", parentInstanceId=scope.root_iid)
        c.write("material_assign", instanceId=go["instanceId"], materialPath=path)
        via_go = p.material(iid=go["instanceId"])
        out.update(ok=bool((default_info or {}).get("shader") == URP_LIT),
                   conclusion="material_create default shader=%s; _Color on URP/Lit -> %s" % (
                       (default_info or {}).get("shader"), "error" if bad else "accepted"),
                   evidence={"created": made, "default": default_info, "after": after, "wrongProperty": bad,
                             "viaGameObject": {k: via_go.get(k) for k in ("materialPath", "shader")} if via_go else None})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_prefab(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        src = c.write("gameobject_create", name="Crate", primitiveType="Cube", parentInstanceId=scope.root_iid)
        c.write("component_add", instanceId=src["instanceId"], componentType="Rigidbody")
        base = scope.asset_root + "/P/Crate.prefab"
        made = c.write("prefab_create", instanceId=src["instanceId"], savePath=base)
        inst = c.write("prefab_instantiate", prefabPath=base, parentInstanceId=scope.root_iid, x=3, name="Crate_B")
        c.write("component_set_serialized_property", instanceId=inst["instanceId"], componentType="Rigidbody",
                propertyPath="m_Mass", value="7")
        ov = c.read("prefab_get_overrides", instanceId=inst["instanceId"])
        variant = scope.asset_root + "/P/Crate_Heavy.prefab"
        var = c.write("prefab_create_variant", sourcePrefabPath=base, variantPath=variant)
        deps = p.deps(variant)
        so_prefab = p.so(variant)
        dup = c.write("gameobject_duplicate", instanceId=inst["instanceId"])
        dup_iid = dup.get("copyInstanceId")
        dup_ov = None
        try:
            dup_ov = c.read("prefab_get_overrides", instanceId=dup_iid) if dup_iid else None
        except SkillError as exc:
            dup_ov = {"error": exc.payload}
        q = p.query({"prefabSource": base, "includeInactive": True, "limit": 500})
        out.update(ok=bool(ov.get("prefabPath") == base),
                   conclusion="overrides=%s; variant deps=%s; duplicate keeps link=%s" % (
                       {k: ov.get(k) for k in ("propertyOverrides", "hasOverrides")},
                       (deps or {}).get("dependencies") if deps else None,
                       bool(dup_ov and dup_ov.get("prefabPath"))),
                   evidence={"made": made, "instance": inst, "overrides": ov, "variant": var, "deps": deps,
                             "soOnVariant": {k: (so_prefab or {}).get(k) for k in ("typeName", "propertyCount")}
                             if so_prefab else None, "duplicate": dup, "duplicateOverrides": dup_ov, "query": q})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_so_reader(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        c.write("asset_create_folder", folderPath=scope.asset_root + "/Phys")
        phys = c.write("physics_create_material", name="Rubber", savePath=scope.asset_root + "/Phys",
                       dynamicFriction=0.8, staticFriction=0.9, bounciness=0.2)
        phys2 = c.write("physics_create_material", name="Rubber", savePath=scope.asset_root + "/Phys",
                        dynamicFriction=0.1, staticFriction=0.1, bounciness=0.0)
        path = phys.get("path") or phys.get("assetPath")
        so_phys = p.so(path) if path else None
        tag = None
        try:
            tag = c.read("scriptableobject_get_serialized_properties", assetPath="ProjectSettings/TagManager.asset")
        except SkillError as exc:
            tag = {"error": exc.payload}
        out.update(ok=bool(so_phys),
                   conclusion="physic material readable via SO reader=%s; second create path=%s; TagManager read=%s" % (
                       bool(so_phys), phys2.get("path") or phys2.get("assetPath"), "error" not in (tag or {})),
                   evidence={"phys": phys, "phys2": phys2,
                             "soPhys": [x for x in (so_phys or {}).get("properties", []) if "Friction" in x.get("propertyPath", "")
                                        or "Bounc" in x.get("propertyPath", "")] if so_phys else None,
                             "tagManager": tag if "error" in (tag or {}) else
                             [x for x in tag.get("properties", []) if x.get("propertyPath", "").startswith("layers")][:12]})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_layer_write(c, out):
    before = c.read("editor_get_layers")
    result, restored = None, None
    try:
        result = c.write("scriptableobject_set_serialized_property", assetPath="ProjectSettings/TagManager.asset",
                         propertyPath="layers.Array.data[8]", value="EvalProbeLayer")
    except SkillError as exc:
        result = {"error": exc.payload}
    after = c.read("editor_get_layers")
    if "error" not in (result or {}):
        try:
            restored = c.write("scriptableobject_set_serialized_property", assetPath="ProjectSettings/TagManager.asset",
                               propertyPath="layers.Array.data[8]", value="")
        except SkillError as exc:
            restored = {"error": exc.payload}
    final = c.read("editor_get_layers")
    wrote = json.dumps(after).find("EvalProbeLayer") >= 0
    out.update(ok=True, conclusion="custom layer write via SO setter on TagManager: %s; restored=%s" % (
        wrote, json.dumps(final) == json.dumps(before)),
        evidence={"before": before, "write": result, "after": after, "restore": restored, "final": final})


def probe_ui(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        canvas = c.write("ui_create_canvas", name="ProbeCanvas")
        cv = canvas.get("instanceId")
        c.write("gameobject_set_parent", childInstanceId=cv, parentInstanceId=scope.root_iid)
        btn = c.write("ui_create_button", name="ProbeButton", parent="ProbeCanvas", text="Go")
        health = c.health()
        bi = btn.get("instanceId")
        comps = p.components(bi) if bi else None
        target = c.write("gameobject_create", name="ProbeTarget", parentInstanceId=scope.root_iid)
        decoy = c.write("gameobject_create", name="ProbeTarget", parentInstanceId=scope.root_iid, x=5)
        added = c.write("event_add_listener", instanceId=bi, componentName="Button", eventName="onClick",
                        targetObjectName="ProbeTarget", targetComponentName="GameObject", methodName="SetActive",
                        mode="RuntimeOnly", argType="bool", boolArg=False)
        lst = c.write("event_set_listener", instanceId=bi, componentName="Button", eventName="onClick", index=0,
                      targetName="ProbeTarget", targetInstanceId=target["instanceId"], targetComponentName="GameObject",
                      methodName="SetActive", mode="RuntimeOnly", argType="bool", boolArg=False)
        listeners = c.read("event_get_listeners", instanceId=bi, componentName="Button", eventName="onClick")
        ser = p.ser(bi, "Button") or {}
        calls = {k: v for k, v in ser.items() if "m_PersistentCalls" in k}
        texts = []
        for ch in p.children(bi):
            texts.append({"name": ch.get("name"), "components": (p.go(ch["instanceId"]) or {}).get("components")})
        tmp_dir = os.path.isdir(os.path.join(out["_project"], "Assets", "TextMesh Pro"))
        out.update(ok=bool(calls), conclusion="m_PersistentCalls readable=%s; label=%s; TMP essentials imported=%s; health=%s" % (
            bool(calls), texts, tmp_dir, health.get("status")),
            evidence={"canvas": canvas, "button": btn, "components": comps, "added": added, "listener": lst,
                      "listeners": listeners, "targetIid": target["instanceId"], "decoyIid": decoy["instanceId"],
                      "calls": calls,
                      "children": texts, "mainThreadIdleMs": health.get("mainThreadIdleMs")})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_compile_focus(c, out):
    scope = _scope(c)
    pid = out["_pid"]
    try:
        subprocess.run(["osascript", "-e", 'tell application "Finder" to activate'], capture_output=True, timeout=15)
        src = "using UnityEngine;\npublic class EvProbeA%s : MonoBehaviour { public float speed = 1f; }\n" % scope.run_id[-6:]
        t0 = time.time()
        made = c.write("script_create", scriptName="EvProbeA" + scope.run_id[-6:], folder=scope.asset_root + "/Scripts",
                       content=src)
        job_id = made.get("jobId")
        unfocused = None
        if job_id:
            try:
                c.retry_window_s, saved = 120, c.retry_window_s
                unfocused = c.wait_job(job_id, timeout_s=120)
            except Exception as exc:
                unfocused = {"error": repr(exc)}
            finally:
                c.retry_window_s = saved
        t_unfocused = time.time() - t0
        activate(pid)
        focused = c.wait_job(job_id, timeout_s=600) if job_id else None
        t_total = time.time() - t0
        info = c.read("script_get_info", scriptPath=(made.get("path") or ""))
        out.update(ok=bool(info.get("className") not in (None, "(unknown)")),
                   conclusion="unfocused compile finished within 120s=%s (%.0fs); after focus total %.0fs; className=%s" % (
                       bool(unfocused and unfocused.get("status") == "completed"), t_unfocused, t_total,
                       info.get("className")),
                   evidence={"created": made, "unfocused": unfocused, "focused": focused, "info": info})
    finally:
        activate(pid)
        out.setdefault("cleanup", F.exit(c, scope))


def probe_search_limits(c, out):
    p = Probe(c)
    scope = _scope(c)
    try:
        items = [{"name": "Slot_%02d" % i, "parentInstanceId": scope.root_iid} for i in range(60)]
        c.write("gameobject_create_batch", items=json.dumps(items))
        found = c.read("gameobject_find", name="Slot_", limit=50)
        found_regex = c.read("gameobject_find", name="^Slot_\\d{2}$", useRegex=True, limit=500)
        q = p.query({"namePattern": "^slot_\\d{2}$", "includeInactive": True, "limit": 500}, sample_limit=500)
        out.update(ok=True, conclusion="gameobject_find limit50 count=%s; regex limit500 count=%s; batch_query case-insensitive count=%s" % (
            found.get("count"), found_regex.get("count"), q.get("count")),
            evidence={"find": {k: found.get(k) for k in ("count", "truncated", "total")},
                      "regex": {k: found_regex.get(k) for k in ("count", "truncated", "total")},
                      "query": {k: q.get(k) for k in ("count", "returned", "sampleLimit")}})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


def probe_models(c, out):
    models = out["_models"]
    results = {}
    for key, model in models.items():
        cmd = ["claude", "-p", "Reply with exactly: OK", "--model", model, "--effort", "high",
               "--output-format", "stream-json", "--verbose", "--safe-mode", "--strict-mcp-config",
               "--disable-slash-commands", "--setting-sources", "project", "--no-session-persistence",
               "--tools", "", "--max-budget-usd", "0.50"]
        env = dict(os.environ)
        for k in ("CLAUDE_CODE_SSE_PORT", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"):
            env.pop(k, None)
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=300, env=env, cwd="/tmp")
        init_model, msg_models, result = None, set(), None
        for line in proc.stdout.splitlines():
            try:
                ev = json.loads(line)
            except ValueError:
                continue
            if ev.get("type") == "system" and ev.get("subtype") == "init":
                init_model = ev.get("model")
            elif ev.get("type") == "assistant":
                msg_models.add((ev.get("message") or {}).get("model"))
            elif ev.get("type") == "result":
                result = {k: ev.get(k) for k in ("subtype", "is_error", "total_cost_usd", "result", "modelUsage")}
        results[key] = {"requested": model, "init": init_model, "messages": sorted(m for m in msg_models if m),
                        "rc": proc.returncode, "result": result, "stderr": proc.stderr[-400:]}
    ok = all(r["init"] and r["init"].lower() == r["requested"].lower() for r in results.values())
    out.update(ok=ok, conclusion="model routing: %s" % {k: (v["init"], v["messages"]) for k, v in results.items()},
               evidence=results)


def probe_reload_stability(c, out):
    """Scene-object instanceIds and the change journal across a script compile + domain reload."""
    scope = _scope(c)
    try:
        go = c.write("gameobject_create", name="Stable", primitiveType="Cube", parentInstanceId=scope.root_iid)
        F.save_run_scene(c, scope)
        cur = J.current_cursor(c)
        col = J.Collector(c, cur).start()
        name = "EvReload" + scope.run_id[-6:]
        made = c.write("script_create", scriptName=name, folder=scope.asset_root + "/Scripts",
                       content="using UnityEngine;\npublic class %s : MonoBehaviour { public int n = 3; }\n" % name)
        job = c.wait_job(made["jobId"], timeout_s=600)
        c.wait_idle(timeout_s=600)
        add = c.write("component_add", instanceId=go["instanceId"], componentType=name)
        col.stop()
        info = c.read("gameobject_get_info", instanceId=go["instanceId"])
        recs = J.flatten(col.events)
        out.update(ok=bool(info.get("instanceId") == go["instanceId"] and col.complete),
                   conclusion="instanceId stable across reload=%s; journal complete=%s (%d records); component_add of new type ok=%s" % (
                       info.get("instanceId") == go["instanceId"], col.complete, len(recs), bool(add.get("success"))),
                   evidence={"job": {k: job.get(k) for k in ("status", "currentStage")}, "info": info,
                             "kinds": sorted({r.get("kind") for r in recs}), "gaps": col.gaps,
                             "collectorErrors": col.errors[:5], "add": add})
    finally:
        out.setdefault("cleanup", F.exit(c, scope))


PROBES = [
    ("scene_lifecycle", probe_scene_lifecycle),
    ("journal_shapes", probe_journal_shapes),
    ("refs", probe_refs),
    ("urp_material", probe_urp_material),
    ("prefab", probe_prefab),
    ("so_reader", probe_so_reader),
    ("layer_write", probe_layer_write),
    ("ui", probe_ui),
    ("compile_focus", probe_compile_focus),
    ("search_limits", probe_search_limits),
    ("reload_stability", probe_reload_stability),
    ("models", probe_models),
]


def run_probes(client, instance, project_path, only=None, models=None, out_dir=None):
    results = []
    for name, fn in PROBES:
        if only and name not in only:
            continue
        rec = {"name": name, "_project": project_path, "_pid": instance.pid, "_models": models or {}}
        started = time.time()
        try:
            fn(client, rec)
        except Exception as exc:
            rec.update(ok=False, conclusion="probe raised %s: %s" % (type(exc).__name__, exc),
                       traceback=traceback.format_exc())
        rec["seconds"] = round(time.time() - started, 1)
        for key in ("_project", "_pid", "_models"):
            rec.pop(key, None)
        results.append(rec)
        print("[%s] %s: %s" % ("ok" if rec.get("ok") else "!!", name, rec.get("conclusion")), flush=True)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
        path = os.path.join(out_dir, datetime.datetime.now().strftime("%Y%m%dT%H%M%S") + ".json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(results, handle, ensure_ascii=False, indent=1, default=str)
        print("written", path)
    return results

# Producer:Betsy
