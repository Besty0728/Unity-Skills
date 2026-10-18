"""OC6 reference, exact-failure variants and two valid extraction routes."""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative


def _duplicate(ctx, ident):
    row = ctx.c.write("gameobject_duplicate", **V.address(ident))
    return V.object_id({"instanceId": row.get("copyInstanceId"), "entityId": row.get("copyEntityId")})


def _local(ctx, ident, trs):
    for field, values in (("m_LocalPosition", trs["position"]), ("m_LocalRotation", trs["rotation"]),
                          ("m_LocalScale", trs["scale"])):
        ctx.c.write("component_set_serialized_property", componentType="UnityEngine.Transform", propertyPath=field,
                    value=",".join("%.9g" % v for v in values), **V.address(ident))


def _report(ctx):
    return {"prefabPath": ctx.handles["file"],
            "inspectedOwners": [ctx.probe.go(ident)["path"] for ident, field, obj in ctx.handles["edges"]],
            "declaredComplete": True}


def _run(ctx, duplicate_source=False, dangling=False, root_links=False, swap=False, pose=False,
         append=False, partial=False, unpack=False, omit_hinge=False, batch=False):
    gates, links = ctx.private.layout(ctx)
    for gate in gates:
        gate["info"] = ctx.probe.go(gate["id"])
        gate["local"] = V.local_trs(ctx.probe, gate["id"])
    unused = next(g for g in gates if not any(V.under(link["childPath"], g["path"]) for link in links))
    source = next(g for g in gates if not partial or g is not unused)
    seed_obj = source["id"]
    use_copy = duplicate_source or dangling or omit_hinge
    hinge = None
    if use_copy:
        seed_obj = _duplicate(ctx, seed_obj)
    if omit_hinge:
        hinge = next(k for k in ctx.probe.children(seed_obj) if k["name"].startswith("Hinge_"))
        hinge["local"] = V.local_trs(ctx.probe, hinge)
        hinge["index"] = next(i for i, k in enumerate(ctx.probe.children(seed_obj)) if V.same_id(k, hinge))
        ctx.c.write("gameobject_delete", **V.address(hinge))
    saved = ctx.c.write("prefab_create", savePath=ctx.handles["file"], **V.address(seed_obj))
    if not saved.get("connected"):
        raise ValueError("prefab_create did not connect its source")
    if use_copy:
        ctx.c.write("gameobject_delete", **V.address(seed_obj))
    else:
        if ctx.probe.go(seed_obj)["name"] != source["info"]["name"]:
            ctx.c.write("gameobject_rename", newName=source["info"]["name"], **V.address(seed_obj))
        _local(ctx, seed_obj, source["local"])
    chosen = [g for g in gates if (use_copy or g is not source) and not (partial and g is unused)]
    if dangling:
        for gate in chosen:
            ctx.c.write("gameobject_delete", **V.address(gate["id"]))
    placed = {source["path"]: source["id"]} if not use_copy else {}
    if partial:
        placed[unused["path"]] = unused["id"]
    if batch:
        import json
        items = [dict(prefabPath=ctx.handles["file"], name=g["info"]["name"] + "__new",
                      x=g["local"]["position"][0], y=g["local"]["position"][1], z=g["local"]["position"][2],
                      **V.address(g["parent"], "parent")) for g in chosen]
        result = ctx.c.write("prefab_instantiate_batch", items=json.dumps(items))
        created = [V.object_id(row) for row in result.get("results") or []]
    else:
        created = [V.object_id(ctx.c.write("prefab_instantiate", prefabPath=ctx.handles["file"],
                    name=g["info"]["name"] + "__new", **V.address(g["parent"], "parent"))) for g in chosen]
    for gate, ident in zip(chosen, created):
        _local(ctx, ident, gate["local"])
        if omit_hinge:
            child = V.object_id(ctx.c.write("gameobject_create", name=hinge["name"], **V.address(ident, "parent")))
            _local(ctx, child, hinge["local"])
            ctx.c.write("gameobject_set_sibling_index", index=hinge["index"], **V.address(child))
        placed[gate["path"]] = ident
    if not dangling:
        for i, link in enumerate(links):
            chosen_link = links[(i + 1) % len(links)] if swap else link
            gate = next(g for g in gates if V.under(chosen_link["childPath"], g["path"]))
            ident = placed[gate["path"]]
            rel = chosen_link["childPath"][len(gate["path"]):].lstrip("/")
            path = ctx.probe.go(ident)["path"] + ("/" + rel if not root_links else "")
            ctx.c.write("component_set_serialized_property", componentType="UnityEngine.MeshRenderer",
                        propertyPath=link["field"], referencePath=path, objectType="UnityEngine.Transform",
                        **V.address(link["id"]))
    for gate, ident in zip(chosen, created):
        if not dangling:
            ctx.c.write("gameobject_delete", **V.address(gate["id"]))
        ctx.c.write("gameobject_rename", newName=gate["info"]["name"], **V.address(ident))
        if not append:
            ctx.c.write("gameobject_set_sibling_index", index=gate["index"], **V.address(ident))
    if pose:
        gate = chosen[0]
        ident = placed[gate["path"]]
        p, r = V.floats(gate["info"]["position"]), V.floats(gate["info"]["rotation"])
        ctx.c.write("gameobject_set_transform", localPosX=p[0], localPosY=p[1], localPosZ=p[2], **V.address(ident))
        ctx.c.write("component_set_property", componentType="UnityEngine.Transform", propertyName="localEulerAngles",
                    value=",".join(str(v) for v in r), **V.address(ident))
    if unpack:
        for ident in placed.values():
            ctx.c.write("prefab_unpack", completely=True, **V.address(ident))
    return _report(ctx)


def reference(ctx):
    return _run(ctx)


def dangling_references(ctx):
    return _run(ctx, dangling=True)


def instance_root(ctx):
    return _run(ctx, root_links=True)


def swapped_references(ctx):
    return _run(ctx, swap=True)


def local_as_world(ctx):
    return _run(ctx, pose=True)


def appended_instances(ctx):
    return _run(ctx, append=True)


def partial_replacement(ctx):
    return _run(ctx, partial=True)


def unconnected_duplicates(ctx):
    return _run(ctx, unpack=True)


def prefab_without_hinge(ctx):
    return _run(ctx, omit_hinge=True)


def _bystander(ctx):
    gates, links = ctx.private.layout(ctx)
    paths = [g["path"] for g in gates]
    return next(row for row in V.structure(ctx.probe, [ctx.probe.go_path(ctx.R)])
                if not any(V.under(row["path"], path) for path in paths) and
                ctx.probe.component_count(row["id"], "UnityEngine.BoxCollider"))


def bystander_property(ctx):
    row = _bystander(ctx)
    result = reference(ctx)
    ctx.c.write("component_set_serialized_property", componentType="UnityEngine.BoxCollider", propertyPath="m_Size",
                value="7,8,9", **V.address(row["id"]))
    return result


def bystander_depth(ctx):
    row = _bystander(ctx)
    result = reference(ctx)
    pos = V.local_trs(ctx.probe, row["id"])["position"]
    ctx.c.write("gameobject_set_transform", localPosZ=pos[2] + 0.25, **V.address(row["id"]))
    return result


def extra_object(ctx):
    result = reference(ctx)
    ctx.c.write("gameobject_create", name="Spare", **V.address(ctx.probe.go_path(ctx.R), "parent"))
    return result


def extra_folder(ctx):
    result = reference(ctx)
    ctx.c.write("asset_create_folder", folderPath=ctx.asset("Spare"))
    return result


def asset_label(ctx):
    result = reference(ctx)
    ctx.c.write("asset_set_labels", assetPath=ctx.handles["bindings"][0][1], labels="edited")
    return result


def wrong_report(ctx):
    result = reference(ctx)
    result["inspectedOwners"].pop()
    return result


def outside_root(ctx):
    result = reference(ctx)
    ctx.c.write("gameobject_create", name="Spare")
    return result


def direct_file(ctx):
    result = reference(ctx)
    with open(os.path.join(ctx.project_path, *ctx.asset("direct.txt").split("/")), "wb") as stream:
        stream.write(b"bypassed skills\n")
    return result


def replace_all(ctx):
    return _run(ctx, duplicate_source=True)


def batch_instances(ctx):
    return _run(ctx, batch=True)


def save_run_scene(ctx):
    result = reference(ctx)
    ctx.c.write("scene_save")
    return result


NEGATIVES = [
    Negative("dangling_references", dangling_references, {"A5"}, "delete originals before repointing"),
    Negative("instance_root", instance_root, {"A5"}),
    Negative("swapped_references", swapped_references, {"A5"}),
    Negative("local_as_world", local_as_world, {"A3"}),
    Negative("appended_instances", appended_instances, {"A4"}),
    Negative("partial_replacement", partial_replacement, {"A2"}),
    Negative("unconnected_duplicates", unconnected_duplicates, {"A2"}),
    Negative("prefab_without_hinge", prefab_without_hinge, {"A1"}, "Hinges exist only as scene additions"),
    Negative("bystander_property", bystander_property, {"A6"}),
    Negative("bystander_depth", bystander_depth, {"A6"}),
    Negative("extra_object", extra_object, {"A6", "A8"}),
    Negative("extra_folder", extra_folder, {"A8"}),
    Negative("asset_label", asset_label, {"A7"}),
    Negative("wrong_report", wrong_report, {"A9"}),
    Negative("outside_root", outside_root, {"A10"}),
    Negative("direct_file", direct_file, {"A8", "A11"}),
]
ALTERNATIVES = [("replace_all", replace_all), ("batch_instances", batch_instances), ("save_run_scene", save_run_scene)]

# Producer:Betsy
