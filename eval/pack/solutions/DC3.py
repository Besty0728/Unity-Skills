"""DC3 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The reference moves and renames the script with asset_move (the GUID stays), then writes the renamed class into the
moved file and imports it with asset_refresh. Between the two compilations the file name does not match the class,
so the components are briefly missing their script; Unity keeps their serialized data meanwhile.
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

NEW_SRC = """using UnityEngine;
using UnityEngine.Serialization;
{open}
    public class {cls} : MonoBehaviour
    {{{marker}
        {speed_attr}public float {speed} = {default};
        {route_attr}public Transform[] {route};
        public float {pause} = 0.5f;

        public float RouteLength()
        {{
            float total = 0f;
            for (int i = 1; i < {route}.Length; i++)
                total += Vector3.Distance({route}[i - 1].position, {route}[i].position);
            return total;
        }}

        public float LapSeconds()
        {{
            return RouteLength() / {speed}{lap};
        }}
    }}
{close}"""

HELPER_SRC = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int bonus = 1;
}}
"""


def _names(ctx):
    moved = ctx.private.names(ctx.seed)
    speed_old, route_old, pause = ctx.handles["fields"]
    speed_new, route_new = ctx.private.renames(ctx.handles["fields"])
    return moved, speed_old, route_old, pause, speed_new, route_new


def source(ctx, attributes=True, namespace=True, default="1.5f", pause_term=True, variant=None):
    moved, speed_old, route_old, pause, speed_new, route_new = _names(ctx)
    return NEW_SRC.format(
        open="\nnamespace %s\n{" % moved["namespace"] if namespace else "", close="}\n" if namespace else "",
        cls=moved["class"], marker="\n        // usk-fake: %s" % variant if variant else "",
        speed_attr='[FormerlySerializedAs("%s")] ' % speed_old if attributes else "",
        route_attr='[FormerlySerializedAs("%s")] ' % route_old if attributes else "",
        speed=speed_new, route=route_new, pause=pause, default=default,
        lap=" + %s * %s.Length" % (pause, route_new) if pause_term else "")


def _disk(ctx, asset_path):
    return os.path.join(ctx.project_path, *asset_path.split("/"))


def _write(ctx, asset_path, text):
    with open(_disk(ctx, asset_path), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def _wait(ctx, res=None):
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()


def _refresh(ctx):
    _wait(ctx, ctx.c.write("asset_refresh"))


def _folders(ctx, folder):
    """Creates `folder` level by level through asset_create_folder."""
    parts = folder.split("/")
    for depth in range(2, len(parts) + 1):
        sub = "/".join(parts[:depth])
        if not os.path.isdir(_disk(ctx, sub)):
            ctx.c.write("asset_create_folder", folderPath=sub)


def _answer(ctx, path, namespace=True):
    moved = ctx.private.names(ctx.seed)
    info = ctx.c.read("asset_get_info", assetPath=path)
    return {"scriptPath": path, "scriptGuid": info.get("guid"),
            "className": "%s.%s" % (moved["namespace"], moved["class"]) if namespace else moved["class"],
            "declaredComplete": True}


def _move_and_write(ctx, text):
    h = ctx.handles
    _folders(ctx, os.path.dirname(h["newPath"]))
    _wait(ctx, ctx.c.write("asset_move", sourcePath=h["oldPath"], destinationPath=h["newPath"]))
    _write(ctx, h["newPath"], text)
    _refresh(ctx)
    return h["newPath"]


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _answer(ctx, _move_and_write(ctx, source(ctx)))


def filesystem_move(ctx):
    """The new text written into the old file, then the .cs and its .meta moved on disk (the folders made through
    the skills) and one asset_refresh: a single compilation, the GUID travels with the .meta."""
    h = ctx.handles
    _folders(ctx, os.path.dirname(h["newPath"]))
    _write(ctx, h["oldPath"], source(ctx))
    for suffix in ("", ".meta"):
        os.replace(_disk(ctx, h["oldPath"]) + suffix, _disk(ctx, h["newPath"]) + suffix)
    _refresh(ctx)
    return _answer(ctx, h["newPath"])


def script_move_rename(ctx):
    """script_move to the new folder, script_rename to the class name, then the new text."""
    h = ctx.handles
    moved = ctx.private.names(ctx.seed)
    _wait(ctx, ctx.c.write("script_move", scriptPath=h["oldPath"], newFolder=os.path.dirname(h["newPath"])))
    there = "%s/%s" % (os.path.dirname(h["newPath"]), os.path.basename(h["oldPath"]))
    _wait(ctx, ctx.c.write("script_rename", scriptPath=there, newName=moved["class"]))
    _write(ctx, h["newPath"], source(ctx))
    _refresh(ctx)
    return _answer(ctx, h["newPath"])


def save_run_scene(ctx):
    out = reference(ctx)
    ctx.c.write("scene_save")
    return out


# ---------------------------------------------------------------- negatives


def delete_recreate(ctx):
    """The old script deleted and a new one created at the new path: new GUID, every instance loses its script."""
    h = ctx.handles
    moved = ctx.private.names(ctx.seed)
    _wait(ctx, ctx.c.write("script_delete", scriptPath=h["oldPath"]))
    _folders(ctx, os.path.dirname(h["newPath"]))
    _wait(ctx, ctx.c.write("script_create", scriptName=moved["class"], folder=os.path.dirname(h["newPath"]),
                           content=source(ctx)))
    return _answer(ctx, h["newPath"])


def no_formerly(ctx):
    return _answer(ctx, _move_and_write(ctx, source(ctx, attributes=False)))


def _values(ctx):
    """What an agent reads before the change: each holder's and each prefab's speed, pause and waypoint targets."""
    h = ctx.handles
    old = os.path.basename(h["oldPath"])[:-len(".cs")]
    speed_old, route_old, pause = h["fields"]
    out = {"holders": [], "assets": {}}
    for ref in h["holders"]:
        values = {row["propertyPath"]: row.get("value") for row in ctx.c.read(
            "component_get_serialized_properties", componentType=old, **V.address(ref)).get("properties") or []}
        size = V.as_int(values.get(route_old + ".Array.size")) or 0
        links = [(ctx.probe.go(ctx.probe.owner(values.get("%s.Array.data[%d]" % (route_old, i)))) or {}).get("path")
                 for i in range(size)]
        out["holders"].append((ref, values.get(speed_old), values.get(pause), links))
    for key in ("basePrefab", "variantPrefab"):
        values = {row["propertyPath"]: row.get("value") for row in ctx.c.read(
            "prefab_get_serialized_properties", prefabPath=h[key], componentType=old).get("properties") or []}
        size = V.as_int(values.get(route_old + ".Array.size")) or 0
        links = [(V.parse_ref(values.get("%s.Array.data[%d]" % (route_old, i))) or (None, None, None))[2]
                 for i in range(size)]
        out["assets"][key] = (values.get(speed_old), values.get(pause), links)
    return out


def rename_and_reset(ctx):
    """No FormerlySerializedAs: the data is lost, then every value is set again through the skills."""
    h = ctx.handles
    moved, _speed_old, _route_old, pause, speed_new, route_new = _names(ctx)
    before = _values(ctx)
    path = _move_and_write(ctx, source(ctx, attributes=False))
    for ref, speed, wait, links in before["holders"]:
        for field, value in ((speed_new, speed), (pause, wait), (route_new + ".Array.size", len(links))):
            ctx.c.write("component_set_serialized_property", componentType=moved["class"], propertyPath=field,
                        value=str(value), **V.address(ref))
        for index, link in enumerate(links):
            ctx.c.write("component_set_serialized_property", componentType=moved["class"],
                        propertyPath="%s.Array.data[%d]" % (route_new, index), referencePath=link,
                        objectType="Transform", **V.address(ref))
    for key, (speed, wait, links) in before["assets"].items():
        for field, value in ((speed_new, speed), (pause, wait), (route_new + ".Array.size", len(links))):
            ctx.c.write("prefab_set_serialized_property", prefabPath=h[key], componentType=moved["class"],
                        propertyPath=field, value=str(value))
        for index, link in enumerate(links):
            ctx.c.write("prefab_set_serialized_property", prefabPath=h[key], componentType=moved["class"],
                        propertyPath="%s.Array.data[%d]" % (route_new, index), referenceGameObjectPath=link,
                        referenceComponentType="Transform")
    return _answer(ctx, path)


def not_moved(ctx):
    """Class and fields renamed, the file renamed in place (still in the old folder); reported truthfully."""
    h = ctx.handles
    moved = ctx.private.names(ctx.seed)
    _wait(ctx, ctx.c.write("script_rename", scriptPath=h["oldPath"], newName=moved["class"]))
    there = "%s/%s.cs" % (os.path.dirname(h["oldPath"]), moved["class"])
    _write(ctx, there, source(ctx))
    _refresh(ctx)
    return _answer(ctx, there)


def namespace_missing(ctx):
    return _answer(ctx, _move_and_write(ctx, source(ctx, namespace=False)), namespace=False)


def default_changed(ctx):
    return _answer(ctx, _move_and_write(ctx, source(ctx, default="2f")))


def behaviour_changed(ctx):
    """LapSeconds() drops the pause term."""
    return _answer(ctx, _move_and_write(ctx, source(ctx, pause_term=False, variant="no-pause")))


def compile_error(ctx):
    """The finished change, then an edit that does not compile (the loaded assembly stays the good one)."""
    out = reference(ctx)
    _wait(ctx, ctx.c.write("script_replace", scriptPath=ctx.handles["newPath"], find="return total;",
                           replace="return total; {"))
    return out


def extra_script(ctx):
    out = reference(ctx)
    name = "PatrolHelper_" + os.path.basename(ctx.handles["oldPath"])[:-len(".cs")].split("_", 1)[1]
    _wait(ctx, ctx.c.write("script_create", scriptName=name, folder=os.path.dirname(ctx.handles["newPath"]),
                           content=HELPER_SRC.format(cls=name)))
    return out


def renamed_squad(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_rename", newName="Squad_Moved", **V.address(ctx.handles["squad"]))
    return out


def wrong_report(ctx):
    out = reference(ctx)
    out["scriptPath"] = ctx.handles["oldPath"]
    return out


def outside_root(ctx):
    out = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayPatroller")
    return out


def direct_file(ctx):
    out = reference(ctx)
    folder = os.path.dirname(_disk(ctx, ctx.handles["newPath"]))
    with open(os.path.join(folder, "notes.txt"), "wb") as handle:
        handle.write(b"moved\n")
    return out


NEGATIVES = [
    Negative("delete_recreate", delete_recreate, {"Q1", "Q5", "Q6", "Q11"}, "new GUID: every instance loses its script"),
    Negative("no_formerly", no_formerly, {"Q4", "Q5", "Q6", "Q11"}, "fields renamed without FormerlySerializedAs"),
    Negative("rename_and_reset", rename_and_reset, {"Q4", "Q7"}, "data lost, then set again through the skills"),
    Negative("not_moved", not_moved, {"Q1"}, "renamed in place, not moved to the new path"),
    Negative("namespace_missing", namespace_missing, {"Q2"}, "class renamed without the namespace"),
    Negative("default_changed", default_changed, {"Q3"}, "the speed default changed"),
    Negative("behaviour_changed", behaviour_changed, {"Q11"}, "LapSeconds() without the pause term"),
    Negative("compile_error", compile_error, {"Q2", "Q11"}, "a final edit that does not compile"),
    Negative("extra_script", extra_script, {"Q8"}),
    Negative("renamed_squad", renamed_squad, {"Q8"}, "another object modified"),
    Negative("wrong_report", wrong_report, {"Q10"}),
    Negative("outside_root", outside_root, {"Q9"}),
    Negative("direct_file", direct_file, {"Q12"}, "a non-script file written to disk next to the script"),
]

ALTERNATIVES = [("filesystem_move", filesystem_move), ("script_move_rename", script_move_rename),
                ("save_run_scene", save_run_scene)]

# Producer:Betsy
