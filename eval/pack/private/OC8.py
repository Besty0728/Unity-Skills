"""OC8: complete matrix, name allocation, recursive selection and preservation checks."""
from usk_eval import verifylib as V

ASSERTIONS = [
    ("A1", 1.25, False, ["R1"], "two exact names occupy distinct originally empty user slots"),
    ("A2", 0.75, True, ["R1"], "all other layer names remain unchanged"),
    ("A3", 1.25, False, ["R2"], "the three named collision pairs have the specified enabled states"),
    ("A4", 2.0, False, ["R3"], "all eligible descendants use the nearest matching ancestor's layer"),
    ("A5", 1.0, True, ["R4"], "KeepLayer subtrees and all unmatched objects keep their individual original layers"),
    ("A6", 1.25, True, ["R2", "R5"], "every other collision pair and project setting is unchanged"),
    ("A7", 1.5, True, ["R6"], "whole R object state and whole A inventory, content and metadata are preserved"),
    ("A8", 0.25, False, ["R7", "G8"], "the reported changed layer slots agree with the final layer table"),
    ("A9", 0.5, True, ["G1", "G2"], "only R/A and the two permitted settings files are touched; no other scene"),
    ("A10", 0.25, False, ["G4"], "no direct writes under A bypass the skills"),
]
SETTINGS = ("ProjectSettings/TagManager.asset", "ProjectSettings/DynamicsManager.asset")


def allowed_change(ctx, rec):
    path = V.norm_path(rec.get("assetPath"))
    if path.endswith(".meta"):
        path = path[:-5]
    if path not in SETTINGS or rec.get("kind") not in ("imported", "property_changed"):
        return False
    return not rec.get("path") and not rec.get("scenePath")


def _safe(fn, *args, **kwargs):
    try:
        return fn(*args, **kwargs)
    except Exception:
        return None


def slot(names, name):
    hits = [i for i, value in enumerate(names or []) if value == name]
    return hits[0] if len(hits) == 1 else None


def requests(names, labels):
    if len(labels or []) != 4:
        return []
    indices = [slot(names, name) for name in labels]
    if any(i is None for i in indices):
        return []
    a, b, c, d = indices
    return [(min(x, y), max(x, y), enabled) for x, y, enabled in ((a, b, False), (a, c, True), (b, d, False))]


def selection(rows, site, labels):
    """Original identity/path rows mapped to a requested name, or None for individually preserved objects."""
    home = next((path for ref, path in rows if V.same_id(ref, site)), None)
    out = {}
    for ref, path in rows:
        name = None
        if home and V.under(path, home) and path != home:
            parts = path[len(home) + 1:].split("/")
            if "KeepLayer" not in parts:
                for part in parts:
                    for label in labels[:2]:
                        if part.endswith("__" + label):
                            name = label
        out[str(V.object_id(ref))] = name
    return out


def changed_names(before, now):
    if len(before or []) != 32 or len(now or []) != 32:
        return None
    return [{"index": i, "name": name} for i, name in enumerate(now) if name != before[i]]


def verify(ctx, card):
    task, h = ctx.task, ctx.handles or {}
    c0, dig = ctx.c0 or {}, ctx.c0_digests or {}
    rows = c0.get("nodes") or []
    labels = h.get("names") or []
    before = c0.get("layers") or []
    names = _safe(task.layer_names, ctx)
    good_names = len(before) == 32 and isinstance(names, list) and len(names) == 32 and len(labels) == 4
    indices = [slot(names, name) for name in labels[:2]]
    allocations = good_names and len(indices) == 2 and all(i is not None and 8 <= i < 32 and not before[i]
                                                                         for i in indices)
    card.check("A1", bool(allocations and len(set(indices)) == 2), actual=indices)
    unchanged = good_names and all(name == before[i] or
                                   (not before[i] and i >= 8 and name in labels[:2])
                                   for i, name in enumerate(names))
    card.check("A2", bool(unchanged), actual=changed_names(before, names))

    matrix = _safe(task.matrix, ctx)
    pairs = requests(names, labels)
    card.check("A3", len(pairs) == 3 and bool(matrix) and
               all(matrix.get("%d,%d" % (a, b)) is value for a, b, value in pairs), actual=pairs)
    picks = selection(rows, h.get("site"), labels)
    work, kept = [], []
    for ref, path in rows:
        info = _safe(ctx.probe.go, ref) or {}
        now = slot(names, info.get("layer")) if info else None
        label = picks.get(str(V.object_id(ref)))
        if label:
            index = slot(names, label)
            work.append({"path": path, "pass": now is not None and index is not None and now == index})
        else:
            kept.append({"path": path, "pass": now is not None and V.canonical_digest(now) ==
                         (dig.get("membership") or {}).get(str(V.object_id(ref)))})
    card.fraction("A4", sum(row["pass"] for row in work) / float(len(work)) if work else 0,
                  actual=[row["path"] for row in work if not row["pass"]])
    card.check("A5", bool(kept) and all(row["pass"] for row in kept),
               actual=[row["path"] for row in kept if not row["pass"]])

    permitted = {"%d,%d" % (a, b) for a, b, _value in pairs}
    initial = dig.get("matrix") or {}
    different = [key for key, digest in initial.items() if key not in permitted and
                 (not matrix or V.canonical_digest(matrix.get(key)) != digest)]
    settings = _safe(task.settings_view, ctx)
    card.check("A6", len(initial) == 528 and bool(matrix) and not different and settings is not None and
               V.canonical_digest(settings) == dig.get("settings"), actual={"pairs": different,
               "settingsMatch": settings is not None and V.canonical_digest(settings) == dig.get("settings")})

    current = _safe(task.nodes, ctx)
    inventory = current is not None and {str(V.object_id(ref)) for ref, _path in current} ==                 {str(V.object_id(ref)) for ref, _path in rows}
    altered = []
    for ref, path in rows:
        view = _safe(task.scene_view, ctx, ref)
        if view is None or V.canonical_digest(view) != (dig.get("objects") or {}).get(str(V.object_id(ref))):
            altered.append(path)
    files = _safe(task.files, ctx, ctx.A, scene=True)
    tags = _safe(lambda: [ctx.read("asset_get_labels", assetPath=path) for path in h.get("folders", [])])
    records = _safe(ctx.write_records)
    forbidden = []
    for rec in records or []:
        if rec.get("kind") in ("gameobject_created", "gameobject_destroyed") and V.under(rec.get("path"), ctx.R):
            forbidden.append(rec)
        if rec.get("kind") in ("imported", "deleted", "moved") and any(
                V.under(rec.get(key), ctx.A) for key in ("assetPath", "from")):
            forbidden.append(rec)
    card.check("A7", bool(rows) and inventory and not altered and files is not None and
               V.canonical_digest(files) == dig.get("assets") and tags is not None and
               V.canonical_digest(tags) == dig.get("labels") and records is not None and not forbidden,
               actual={"objects": altered, "inventory": inventory, "prohibitedActions": forbidden[:8],
               "assetsMatch": files is not None and V.canonical_digest(files) == dig.get("assets")})

    answer = ctx.answer if isinstance(ctx.answer, dict) else {}
    reported = answer.get("layers")
    actual = changed_names(before, names)
    report_ok = isinstance(reported, list) and actual is not None and len(reported) == len(actual)
    if report_ok:
        report_ok = all(isinstance(row, dict) and type(row.get("index")) is int and
                        isinstance(row.get("name"), str) and row["index"] == real["index"] and
                        row["name"] == real["name"] for row, real in zip(reported, actual))
    card.check("A8", bool(report_ok) and type(answer.get("declaredComplete")) is bool, actual=reported)
    bad = _safe(ctx.violations)
    other = _safe(ctx.saved_other_scenes)
    card.check("A9", bad == [] and other == [], actual={"scope": bad, "scenes": other})
    direct = _safe(ctx.direct_writes)
    card.check("A10", direct == [], actual=direct)

# Producer:Betsy
