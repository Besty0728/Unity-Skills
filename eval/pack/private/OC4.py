"""OC4 verifier: compare every original renderer slot with its C0 digest.

Material identities and shader digests reconstruct the original classes independently of the answer. Missing and
empty entries have distinct tokens even though their SerializedProperty values both read as null.
"""
import os
import re

from usk_eval import verifylib as V

ARRAY = "m_Materials"
SLOT = re.compile(r"^m_Materials\.Array\.data\[([0-9]+)\]$")

ASSERTIONS = [
    ("A1", 2.0, False, ["R1", "R2"], "every original Standard slot on all renderers uses the exact Fallback asset"),
    ("A2", 2.0, False, ["R1", "R3"], "every original missing slot on all renderers uses the exact Fallback asset"),
    ("A3", 1.0, False, ["R4"], "every originally empty slot remains genuinely empty at its original index"),
    ("A4", 1.5, False, ["R4"], "every other assigned slot keeps its original material identity at its index"),
    ("A5", 1.0, True, ["R5"], "objects, hierarchy, component properties and array sizes are otherwise unchanged"),
    ("A6", 1.0, True, ["R6"], "all existing material assets, their paths and GUIDs remain unchanged"),
    ("A7", 0.5, False, ["R7", "G8"], "the report gives the six original classification counts and a completion flag"),
    ("A8", 0.5, True, ["G1", "G2"], "no changes outside R/A and no other scene touched"),
    ("A9", 0.5, False, ["G4"], "no direct file writes under A around the skills"),
]


def _read(ctx, skill, **args):
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    if not isinstance(result, dict) or result.get("success") is False or result.get("error"):
        return None
    return result


def fields(result, omit=False):
    if not isinstance(result, dict) or not isinstance(result.get("properties"), list):
        return None
    return sorted([[row.get("propertyPath"), row.get("value"), row.get("referenceState")]
                   for row in result["properties"] if isinstance(row, dict)
                   and not (omit and (row.get("propertyPath") == ARRAY or
                                      str(row.get("propertyPath")).startswith(ARRAY + ".")))], key=lambda row: row[0])


def token(row):
    if not isinstance(row, dict):
        return None
    state = row.get("referenceState")
    parsed = V.parse_ref(row.get("value"))
    ident = V.object_id(str(parsed[0])) if parsed else None
    if state == "assigned" and ident is not None:
        return [state, str(ident)]
    if state in ("none", "missing") and row.get("value") == "null":
        return [state, None]
    return None


def serialized(ctx, ref, kind):
    try:
        args = V.address(ref)
    except ValueError:
        return None
    return _read(ctx, "component_get_serialized_properties", componentType=kind,
                 includeChildren=True, includeHidden=True, limit=5000, **args)


def slots(result):
    if not isinstance(result, dict) or not isinstance(result.get("properties"), list):
        return None
    props = {row.get("propertyPath"): row for row in result["properties"] if isinstance(row, dict)}
    size = V.as_int((props.get(ARRAY + ".Array.size") or {}).get("value"))
    if size is None or size < 0 or size > 5000:
        return None
    entries = {int(match.group(1)): row for path, row in props.items()
               for match in [SLOT.match(path or "")] if match}
    if set(entries) != set(range(size)):
        return None
    values = [token(entries[index]) for index in range(size)]
    return values if all(value is not None for value in values) else None


def asset_state(ctx, path):
    info, props = ctx.probe.asset(path), ctx.probe.material(path=path)
    if not info or not props:
        return None
    try:
        disk = os.path.join(ctx.project_path, *path.split("/"))
        docs = V.uyaml.load(disk)
        guid = V.uyaml.meta_guid(disk)
    except (OSError, ValueError):
        return None
    return {"info": info, "properties": props,
            "yaml": [[doc.class_id, doc.file_id, doc.stripped, doc.type_name, doc.body] for doc in docs],
            "guid": guid}


def original_slots(ctx, ref):
    digests = ctx.c0_digests
    candidates = [("emptySlots", ["none", None]), ("missingSlots", ["missing", None])]
    for asset in ctx.c0.get("assets") or []:
        path, ident = asset.get("path"), V.object_id(asset.get("id"))
        if not path or ident is None:
            continue
        kind = "standardSlots" if digests.get("shader/" + path) == V.canonical_digest("Standard") else "otherSlots"
        candidates.append((kind, ["assigned", str(ident)]))
    lookup = {V.canonical_digest(value): (kind, value) for kind, value in candidates}
    out = []
    prefix = "slot/" + str(V.object_id(ref)) + "/"
    while prefix + str(len(out)) in digests:
        item = lookup.get(digests[prefix + str(len(out))])
        if item is None:
            return None
        out.append(item)
    return out if out and digests.get("size/" + str(V.object_id(ref))) == V.canonical_digest(len(out)) else None


def verify(ctx, card):
    h, digests = ctx.handles, ctx.c0_digests
    failed = {key: [] for key in ("standardSlots", "missingSlots", "emptySlots", "otherSlots")}
    counts = {key: 0 for key in failed}
    counts.update(renderers=0, slots=0)
    fallback = next((asset.get("id") for asset in ctx.c0.get("assets") or []
                     if asset.get("path") == h.get("material")), None)
    replacement = ["assigned", str(V.object_id(fallback))] if V.object_id(fallback) is not None else None
    shape = []
    readable = True
    renderers = h.get("renderers") or []
    for ref in renderers:
        old = original_slots(ctx, ref)
        result = serialized(ctx, ref, "MeshRenderer")
        current = slots(result)
        if old is None:
            readable = False
            old = []
        counts["renderers"] += 1
        counts["slots"] += len(old)
        for index, (kind, value) in enumerate(old):
            counts[kind] += 1
            wanted = replacement if kind in ("standardSlots", "missingSlots") else value
            actual = current[index] if current is not None and index < len(current) else None
            if wanted is None or actual != wanted:
                failed[kind].append([V.object_id(ref), index])
        if (current is None or len(current) != len(old) or
                digests.get("MeshRenderer/" + str(V.object_id(ref))) != V.canonical_digest(fields(result, True))):
            shape.append(V.object_id(ref))
    for aid, kind in (("A1", "standardSlots"), ("A2", "missingSlots"),
                      ("A3", "emptySlots"), ("A4", "otherSlots")):
        card.check(aid, readable and counts[kind] > 0 and not failed[kind],
                   actual={"originalSlots": counts[kind], "differentSlots": failed[kind][:15]})
    for ref in h.get("nodes") or []:
        ident = V.object_id(ref)
        info = ctx.probe.go(ref)
        transform = serialized(ctx, ref, "Transform")
        if (ident is None or not info or transform is None or
                V.canonical_digest(info) != digests.get("object/" + str(ident)) or
                V.canonical_digest(fields(transform)) != digests.get("Transform/" + str(ident))):
            shape.append(ident)
    card.check("A5", bool(h.get("nodes")) and bool(renderers) and not shape, actual={"changed": shape[:15]})
    assets = h.get("materials") or []
    changed = [path for path in assets if V.canonical_digest(asset_state(ctx, path)) != digests.get("asset/" + path)]
    card.check("A6", bool(assets) and not changed, actual={"changed": changed})
    report = ctx.answer if isinstance(ctx.answer, dict) else {}
    report_ok = readable and bool(renderers) and all(type(report.get(key)) is int and report[key] == value
                                                    for key, value in counts.items())
    card.check("A7", report_ok and isinstance(report.get("declaredComplete"), bool), expected=counts, actual=ctx.answer)
    bad = ctx.violations()
    card.check("A8", not bad and not ctx.saved_other_scenes(), actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("A9", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
