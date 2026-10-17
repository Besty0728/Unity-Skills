"""T10 private verifier: materials moved into one folder per initial letter with their GUIDs kept.

What the task probes: a copy followed by a delete looks like a move but gives every material a new GUID and breaks the
renderer slots that used it; six names occur in several Incoming folders (Brick twice, Tile three times) and Bolt
clashes with Materials/B/Bolt.mat, so the _2/_3 suffixes depend on the ordinal order of the original full paths and on
what already sits in the destination; some names start with a lower-case letter (the folder is the upper-case one);
Vendor/Old is a nested folder; Misc/Grip.physicMaterial is no material and keeps Misc and Incoming alive;
Elsewhere/Brick.mat and Materials/B/Bolt.mat are not to be touched even though they are used by renderers.

The run state holds neutral facts only: the files and folders under A at issue with their GUIDs, the state of the
materials outside Incoming and the slot references of the 12 renderers. The intended destinations are derived here
from those facts and the rules of the prompt.
"""
import os

from usk_eval import verifylib as V

ASSERTIONS = [
    ("H1", 2.0, False, ["R1"], "every material whose name does not clash sits at A/Materials/<L>/<name>.mat with its "
                               "original GUID (fraction)"),
    ("H2", 1.5, True, ["R3"], "the GUID set of all .mat files under A is unchanged (moved, not copied, recreated, "
                              "deleted or overwritten)"),
    ("H3", 1.0, False, ["R2"], "clashing names sit at the first free _2/_3 name, assigned in ordinal order of the "
                               "original paths, with their original GUIDs"),
    ("H4", 1.5, False, ["R3"], "every renderer slot still references the same Material object"),
    ("H5", 0.5, False, ["R4"], "the materials that were under Materials or outside Incoming are unchanged"),
    ("H6", 1.0, False, ["R4"], "non-material files (Misc/Grip.physicMaterial) stay where they were with their GUID"),
    ("H7", 1.0, False, ["R5"], "the Incoming folders that are empty after the moves are deleted; the ones that still "
                               "hold files are kept"),
    ("H8", 0.5, False, ["R6", "G8"], "the answer lists every moved material with its original and new path"),
    ("H9", 0.5, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("H10", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "H1": {"zh-CN": "用 asset_move 或 asset_move_batch 移动资产，GUID 才会保留；目标文件夹是文件名首字母的大写，"
                    "不存在要先建好（asset_create_folder_batch）。",
           "en": "Move the assets with asset_move or asset_move_batch so their GUIDs survive; the destination folder "
                 "is the upper-case first letter of the file name and must be created first "
                 "(asset_create_folder_batch)."},
    "H2": {"zh-CN": "只能移动：asset_duplicate 复制后删除原件、重建或覆盖都会改变 GUID 集合。",
           "en": "Only move: duplicating and deleting the original, recreating or overwriting changes the GUID set "
                 "(asset_duplicate makes a new GUID)."},
    "H3": {"zh-CN": "重名依次用 _2、_3……；先按原完整路径的序数（逐字符码值）升序处理，目标位置已有的文件"
                    "（如 Materials/B/Bolt.mat）也算占用。",
           "en": "Clashing names take _2, _3, ...; process the materials in ascending ordinal order of their original "
                 "full paths, and count what already sits in the destination (e.g. Materials/B/Bolt.mat)."},
    "H4": {"zh-CN": "移动资产不会断开渲染器的材质槽；复制后删除原件会让引用断开。",
           "en": "Moving an asset keeps the renderers' material slots; copying and deleting the original breaks them."},
    "H5": {"zh-CN": "Materials 下原有的资产和 Incoming 以外的资产不能移动、改名、覆盖或删除。",
           "en": "Assets that were under Materials, and assets outside Incoming, must not be moved, renamed, "
                 "overwritten or deleted."},
    "H6": {"zh-CN": "Misc/Grip.physicMaterial 不是材质，要留在原处并保持 GUID。",
           "en": "Misc/Grip.physicMaterial is not a material: leave it where it is, with its GUID."},
    "H7": {"zh-CN": "移动后变空的子文件夹（Batch01、Batch02、Vendor、Vendor/Old）要删除；仍有文件的 Misc 和 Incoming 要保留。",
           "en": "Delete the subfolders that are empty after the moves (Batch01, Batch02, Vendor, Vendor/Old); keep "
                 "Misc and Incoming, which still hold a file."},
    "H8": {"zh-CN": "答案要列出每个被移动材质的原路径和新路径，并与实际结果一致，还要写明 declaredComplete。",
           "en": "The answer must list the original and new path of every material that was moved, matching the "
                 "result, and declaredComplete."},
    "H9": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "H10": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def destinations(sources, taken, materials_root, reverse=False):
    """{source: destination} by the rules of R1/R2: Materials/<upper-case initial>/<name>.mat, the first free
    <name>_2.mat, <name>_3.mat ... when the name is taken (before or by an earlier move), sources taken in ascending
    ordinal order of their full paths (`reverse` flips that order)."""
    used = set(taken)
    out = {}
    for source in sorted(sources, reverse=reverse):
        stem = os.path.basename(source)[:-len(".mat")]
        folder = "%s/%s" % (materials_root, stem[0].upper())
        dest, number = "%s/%s.mat" % (folder, stem), 1
        while dest in used:
            number += 1
            dest = "%s/%s_%d.mat" % (folder, stem, number)
        used.add(dest)
        out[source] = dest
    return out


def plan(ctx):
    """The intended {source: destination} of this run, from the files recorded at issue."""
    files = ctx.c0.get("files") or {}
    incoming, materials = ctx.handles["incoming"], ctx.handles["materials"]
    sources = [path for path in files if path.endswith(".mat") and V.under(path, incoming)]
    taken = [path for path in files if V.under(path, materials)]
    return destinations(sources, taken, materials)


def _meta_guid(path):
    try:
        return V.uyaml.meta_guid(path)
    except OSError:
        return None


def _disk(ctx):
    """({asset path: guid}, {guid: [asset paths]}) of every file under A on disk, without .meta files and the run
    scene. The disk is read after the runner's refresh, so it is what the editor imported."""
    root = os.path.join(ctx.project_path, *ctx.A.split("/"))
    now, where = {}, {}
    for base, _dirs, names in os.walk(root):
        rel = os.path.relpath(base, ctx.project_path).replace(os.sep, "/")
        for name in sorted(names):
            path = rel + "/" + name
            if name.endswith(".meta") or name.startswith(".") or path == ctx.scene:
                continue
            guid = _meta_guid(os.path.join(base, name))
            now[path] = guid
            if guid:
                where.setdefault(guid, []).append(path)
    return now, where


def _slots(probe, ref):
    ser = probe.ser(ref, "MeshRenderer") or {}
    size = V.as_int(ser.get("m_Materials.Array.size")) or 0
    return [ser.get("m_Materials.Array.data[%d]" % index) for index in range(size)]


def _exists(ctx, path):
    return bool(ctx.probe.asset(path)) or os.path.isdir(os.path.join(ctx.project_path, *path.split("/")))


def _actual_moves(files, where):
    """(original path, current path) of every material of the run state whose GUID now sits at one other path."""
    out = []
    for path, guid in files.items():
        if path.endswith(".mat") and guid:
            here = where.get(guid) or []
            if len(here) == 1 and here[0] != path:
                out.append((path, here[0]))
    return out


def _stays(files, incoming):
    """Folders (from Incoming itself down) that still hold a non-material file, hence must be kept."""
    stay = set()
    for path in files:
        if V.under(path, incoming) and not path.endswith(".mat"):
            folder = path.rsplit("/", 1)[0]
            while V.under(folder, incoming):
                stay.add(folder)
                folder = folder.rsplit("/", 1)[0]
    return stay


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    files0 = c0.get("files") or {}
    wanted = plan(ctx)
    now, where = _disk(ctx)

    plain = sorted(s for s, d in wanted.items() if os.path.basename(s) == os.path.basename(d))
    clash = sorted(s for s in wanted if s not in plain)
    placed = [s for s in plain if where.get(files0.get(s)) == [wanted[s]]]
    card.fraction("H1", len(placed) / float(len(plain)) if plain else 0.0, expected=len(plain),
                  actual={"ok": len(placed), "wrong": [(s, wanted[s]) for s in plain if s not in placed][:6]})
    before = sorted(guid or "" for path, guid in files0.items() if path.endswith(".mat"))
    after = sorted(guid or "" for path, guid in now.items() if path.endswith(".mat"))
    card.check("H2", after == before, expected=len(before),
               actual={"count": len(after), "lost": sorted(set(before) - set(after))[:6],
                       "new": sorted(set(after) - set(before))[:6]})
    wrong = [(s, wanted[s]) for s in clash if where.get(files0.get(s)) != [wanted[s]]]
    card.check("H3", bool(clash) and not wrong, expected=len(clash), actual={"clashes": len(clash), "wrong": wrong[:6]})

    slots0 = c0.get("slots") or {}
    broken = []
    for ref in h["renderers"]:
        key = str(V.object_id(ref))
        before_slots, after_slots = slots0.get(key), _slots(p, ref)
        ids0 = [(V.parse_ref(value) or (None,))[0] for value in before_slots or []]
        ids1 = [(V.parse_ref(value) or (None,))[0] for value in after_slots]
        if before_slots is None or ids1 != ids0 or None in ids1:
            broken.append({"renderer": key, "before": before_slots, "after": after_slots})
    card.check("H4", bool(h["renderers"]) and not broken, actual=broken[:4])

    states0 = c0.get("states") or {}
    changed = []
    for path, state in sorted(states0.items()):
        same, diff = V.same(state, V.material_state(p, path))
        if not same:
            changed.append({path: diff})
    card.check("H5", bool(states0) and not changed, actual=changed[:4])
    keep = sorted(path for path in files0 if V.under(path, h["incoming"]) and not path.endswith(".mat"))
    lost = [path for path in keep if where.get(files0[path]) != [path]]
    card.check("H6", bool(keep) and not lost, expected=keep, actual={"notInPlace": lost})

    folders0 = [folder for folder in c0.get("folders") or [] if V.under(folder, h["incoming"])]
    stay = _stays(files0, h["incoming"])
    gone = [folder for folder in folders0 if folder not in stay]
    kept = [folder for folder in folders0 if folder in stay]
    left = [folder for folder in gone if _exists(ctx, folder)]
    missing = [folder for folder in kept if not _exists(ctx, folder)]
    card.check("H7", bool(gone) and not left and not missing, expected={"deleted": gone, "kept": kept},
               actual={"notDeleted": left, "missing": missing})

    rows = V.answer(ctx, "moves")
    shaped = isinstance(rows, list) and all(isinstance(row, dict) for row in rows)
    reported = sorted((V.norm_path(row.get("from")), V.norm_path(row.get("to"))) for row in rows) if shaped else None
    actual = sorted(_actual_moves(files0, where))
    card.check("H8", shaped and reported == actual and isinstance(V.answer(ctx, "declaredComplete"), bool),
               expected=actual[:5], actual={"reported": (reported or [])[:5], "count": len(reported or []),
                                            "declaredComplete": V.answer(ctx, "declaredComplete")})
    bad = ctx.violations()
    card.check("H9", not bad, actual={"violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("H10", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
