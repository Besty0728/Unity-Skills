"""DC7 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The assembly definitions are edited on disk (the prompt allows .asmdef files under A/Scripts like C# scripts) and
imported with asset_refresh; scripts move with asset_move, which keeps their GUIDs.
"""
import json
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int bonus = 1;
}}
"""


def asmdef_text(name, references=(), editor_only=True, **extra):
    data = {"name": name, "rootNamespace": "", "references": list(references),
            "includePlatforms": ["Editor"] if editor_only else [], "excludePlatforms": [], "allowUnsafeCode": False,
            "overrideReferences": False, "precompiledReferences": [], "autoReferenced": True, "defineConstraints": [],
            "versionDefines": [], "noEngineReferences": False}
    data.update(extra)
    return json.dumps(data, indent=4) + "\n"


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _write(ctx, asset_path, text):
    target = _disk(ctx, asset_path)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _load(ctx, asset_path):
    with open(_disk(ctx, asset_path), "r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def _guid_ref(ctx, asset_path):
    return "GUID:" + V.uyaml.meta_guid(_disk(ctx, asset_path))


def _refresh(ctx):
    res = ctx.c.write("asset_refresh")
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()


def _cls(ctx, key):
    return os.path.basename(ctx.handles[key])[:-len(".cs")]


def add_references(ctx, refs, **changes):
    """Adds `refs` to the runtime gameplay .asmdef (and applies `changes` to its other keys)."""
    path = ctx.handles["playAsmdef"]
    data = _load(ctx, path)
    data["references"] = list(data.get("references") or []) + list(refs)
    data.update(changes)
    _write(ctx, path, json.dumps(data, indent=4) + "\n")


def editor_folder(ctx):
    return ctx.handles["folder"] + "/Editor"


def make_editor_asmdef(ctx, folder, references=None, editor_only=True):
    """The new Editor .asmdef in `folder` (created through the skills when missing); returns its path."""
    h = ctx.handles
    if not os.path.isdir(_disk(ctx, folder)):
        ctx.c.write("asset_create_folder", folderPath=folder)
    path = "%s/%s.asmdef" % (folder, h["editorAsm"])
    _write(ctx, path, asmdef_text(h["editorAsm"], references or [h["coreAsm"]], editor_only=editor_only))
    return path


def move_report(ctx, folder):
    h = ctx.handles
    target = "%s/%s" % (folder, os.path.basename(h["reportScript"]))
    if not os.path.isdir(_disk(ctx, folder)):
        ctx.c.write("asset_create_folder", folderPath=folder)
    ctx.c.write("asset_move", sourcePath=h["reportScript"], destinationPath=target)
    ctx.wait_idle()
    return target


def attach(ctx, ref=None):
    ctx.c.write("component_add", componentType=_cls(ctx, "combatScript"), **V.address(ref or ctx.handles["fighter"]))
    return ref or ctx.handles["fighter"]


def answer(ctx, asmdef, script, holder=None):
    return {"editorAsmdefPath": asmdef, "editorScriptPath": script,
            "fighterPath": (ctx.probe.go(holder or ctx.handles["fighter"]) or {}).get("path"),
            "declaredComplete": True}


def _solve(ctx, refs=None, editor_refs=None, editor_only=True, attach_to=None, do_attach=True, **changes):
    h = ctx.handles
    add_references(ctx, refs if refs is not None else [h["coreAsm"], h["dataAsm"]], **changes)
    asmdef = make_editor_asmdef(ctx, editor_folder(ctx), editor_refs, editor_only)
    _refresh(ctx)
    script = move_report(ctx, editor_folder(ctx))
    _refresh(ctx)
    holder = attach(ctx, attach_to) if do_attach else None
    return answer(ctx, asmdef, script, holder)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = _disk(ctx, asset_path)
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx)


def guid_references(ctx):
    """Every new reference written as GUID:<guid> of the referenced .asmdef."""
    h = ctx.handles
    return _solve(ctx, refs=[_guid_ref(ctx, h["coreAsmdef"]), _guid_ref(ctx, h["dataAsmdef"])],
                  editor_refs=[_guid_ref(ctx, h["coreAsmdef"])])


def asmdef_in_tools(ctx):
    """The Editor .asmdef placed in the editor script's own folder: nothing moves."""
    h = ctx.handles
    add_references(ctx, [h["coreAsm"], h["dataAsm"]])
    asmdef = make_editor_asmdef(ctx, os.path.dirname(h["reportScript"]))
    _refresh(ctx)
    attach(ctx)
    return answer(ctx, asmdef, h["reportScript"])


def save_run_scene(ctx):
    out = reference(ctx)
    ctx.c.write("scene_save")
    return out


# ---------------------------------------------------------------- negatives


def deleted_asmdef(ctx):
    """The runtime .asmdef deleted: its scripts fall into Assembly-CSharp, which references every auto-referenced
    assembly, so the project compiles."""
    h = ctx.handles
    ctx.c.write("asset_delete", assetPath=h["playAsmdef"])
    asmdef = make_editor_asmdef(ctx, editor_folder(ctx))
    _refresh(ctx)
    script = move_report(ctx, editor_folder(ctx))
    _refresh(ctx)
    attach(ctx)
    return answer(ctx, asmdef, script)


def if_guard(ctx):
    """The editor script wrapped in #if UNITY_EDITOR and left in the runtime assembly; no Editor .asmdef."""
    h = ctx.handles
    add_references(ctx, [h["coreAsm"], h["dataAsm"]])
    _refresh(ctx)
    res = ctx.c.write("script_replace", scriptPath=h["reportScript"], find="using UnityEditor;",
                      replace="#if UNITY_EDITOR\nusing UnityEditor;")
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    with open(_disk(ctx, h["reportScript"]), "a", encoding="utf-8", newline="\n") as handle:
        handle.write("#endif\n")
    _refresh(ctx)
    attach(ctx)
    return answer(ctx, "", h["reportScript"])


def editor_no_platform(ctx):
    """The new .asmdef without the Editor platform restriction: still a runtime assembly."""
    return _solve(ctx, editor_only=False)


def editor_folder_only(ctx):
    """The editor script moved into a folder named Editor inside the runtime assembly's folder; no new .asmdef."""
    h = ctx.handles
    add_references(ctx, [h["coreAsm"], h["dataAsm"]])
    _refresh(ctx)
    script = move_report(ctx, os.path.dirname(h["playAsmdef"]) + "/Editor")
    _refresh(ctx)
    attach(ctx)
    return answer(ctx, "", script)


def copied_report(ctx):
    """The editor script deleted and created again in the Editor folder: same content, new GUID."""
    h = ctx.handles
    with open(_disk(ctx, h["reportScript"]), "r", encoding="utf-8") as handle:
        content = handle.read()
    add_references(ctx, [h["coreAsm"], h["dataAsm"]])
    asmdef = make_editor_asmdef(ctx, editor_folder(ctx))
    _refresh(ctx)
    ctx.c.write("script_delete", scriptPath=h["reportScript"])
    ctx.wait_idle()
    res = ctx.c.write("script_create", scriptName=_cls(ctx, "reportScript"), folder=editor_folder(ctx),
                      content=content)
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    attach(ctx)
    return answer(ctx, asmdef, "%s/%s" % (editor_folder(ctx), os.path.basename(h["reportScript"])))


def missing_data_ref(ctx):
    """Only the core reference added: the combat script still does not compile, nothing can be attached."""
    h = ctx.handles
    return _solve(ctx, refs=[h["coreAsm"]], do_attach=False)


def not_attached(ctx):
    return _solve(ctx, do_attach=False)


def changed_settings(ctx):
    """Besides the references, another setting of the runtime .asmdef changed."""
    return _solve(ctx, allowUnsafeCode=True)


def wrong_fighter(ctx):
    """The component on the arena's other object (reported truthfully)."""
    h = ctx.handles
    other = next(V.object_id(c) for c in ctx.probe.children(h["arena"]) if not V.same_id(c, h["fighter"]))
    return _solve(ctx, attach_to=other)


def extra_script(ctx):
    out = reference(ctx)
    name = "CombatHelper_" + _cls(ctx, "combatScript").split("_", 1)[1]
    res = ctx.c.write("script_create", scriptName=name, folder=os.path.dirname(ctx.handles["combatScript"]),
                      content=HELPER_SRC.format(cls=name))
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()
    return out


def wrong_report(ctx):
    out = reference(ctx)
    out["fighterPath"] = ctx.handles["arenaPath"]
    return out


def outside_root(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayFighter")
    return out


def direct_file(ctx):
    out = reference(ctx)
    _write_file(ctx, ctx.handles["scripts"] + "/notes.txt", b"assemblies fixed\n")
    return out


NEGATIVES = [
    Negative("deleted_asmdef", deleted_asmdef, {"A2"}, "the runtime .asmdef deleted so that everything compiles"),
    Negative("if_guard", if_guard, {"A3", "A4"}, "#if UNITY_EDITOR instead of an Editor assembly"),
    Negative("editor_no_platform", editor_no_platform, {"A3"}, "the new .asmdef is not Editor-only"),
    Negative("editor_folder_only", editor_folder_only, {"A3"}, "an Editor folder inside the runtime asmdef folder"),
    Negative("copied_report", copied_report, {"A3", "A4"}, "delete + create instead of a move: new GUID"),
    Negative("missing_data_ref", missing_data_ref, {"A1", "A5", "A10"}, "one of the two references missing"),
    Negative("not_attached", not_attached, {"A5", "A10"}),
    Negative("changed_settings", changed_settings, {"A2"}, "allowUnsafeCode switched on as well"),
    Negative("wrong_fighter", wrong_fighter, {"A5", "A6"}, "the component on the other object"),
    Negative("extra_script", extra_script, {"A4"}),
    Negative("wrong_report", wrong_report, {"A9"}),
    Negative("outside_root", outside_root, {"A7"}),
    Negative("direct_file", direct_file, {"A8"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("guid_references", guid_references), ("asmdef_in_tools", asmdef_in_tools),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
