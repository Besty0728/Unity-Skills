"""S04 private verifier: disable the right one of several same-name siblings.

What the task probes: five children of Markers are named Marker, so a lookup by name picks an arbitrary one (and name
lookups ignore case unless asked, so `marker` competes too); one Marker is already inactive and must stay so;
Marker_Archive, `marker` and Backup/Marker also carry an AudioSource. The disabled object is derived from C0: the one
direct child of Markers named exactly Marker that has an AudioSource.
"""
from usk_eval import verifylib as V

ASSERTIONS = [
    ("C1", 4.0, False, ["R1"], "the Marker under Markers that has an AudioSource is inactive (activeSelf false)"),
    ("C2", 2.0, False, ["R2"], "every other object without an AudioSource (the other Markers and the containers) keeps "
                               "its active state"),
    ("C3", 1.0, False, ["R2", "R3"], "the other objects with an AudioSource (Marker_Archive, marker, Backup/Marker) "
                                     "are unchanged"),
    ("C4", 1.5, True, ["R3"], "ids, names, parents, child order and components of the Hall subtree are unchanged"),
    ("C5", 0.5, False, ["R4", "G8"], "the answer reports the disabled Marker's id and path"),
    ("C6", 0.5, False, ["R3", "G1", "G2"], "the journal holds only active-state changes; no change outside R/A, no "
                                           "other scene saved or opened"),
    ("C7", 0.5, False, ["G4"], "no file under A was written around the skills"),
]

HINTS = {
    "C1": {"zh-CN": "按名称查找只会命中同名对象中的某一个；先按 AudioSource 组件筛选，再按 instanceId 或 entityId 操作。",
           "en": "A lookup by name hits just one of the same-name objects; filter by the AudioSource component, then "
                 "act on the instanceId or entityId of the one you found."},
    "C2": {"zh-CN": "只改这一个 Marker；其他 Marker（包括原本未激活的那个）的激活状态都不要动。",
           "en": "Change only that Marker; leave the active state of the other Markers (the inactive one too) alone."},
    "C3": {"zh-CN": "Marker_Archive、marker 和 Backup/Marker 虽然也带 AudioSource，但名称或位置不符合要求。",
           "en": "Marker_Archive, marker and Backup/Marker carry an AudioSource too, but their name or place does not "
                 "fit."},
    "C4": {"zh-CN": "不要改名、移动、删除或增减组件。", "en": "Do not rename, move, delete or add or remove components."},
    "C5": {"zh-CN": "答案要给出被禁用 Marker 的 id 与路径，并写明 declaredComplete。",
           "en": "Report the disabled Marker's id and path, and declaredComplete."},
    "C6": {"zh-CN": "除了激活状态之外不要产生其他修改，所有修改都要在 R 内。",
           "en": "Make no change other than the active state, and keep every change inside R."},
    "C7": {"zh-CN": "不要绕过技能直接在 A 下写文件。", "en": "Do not write files under A directly; use the skills."},
}


def _key(ref):
    ident = V.object_id(ref)
    return None if ident is None else str(ident)


def _table(nodes):
    return {_key(ref): (ref, state or {}) for ref, state in nodes or []}


def audio_marker(nodes, markers):
    """Id of the direct child of Markers named exactly Marker with an AudioSource (R1); None unless exactly one."""
    table = _table(nodes)
    children = (table.get(_key(markers)) or (None, {}))[1].get("children") or []
    hits = [ref for ref in children
            if table.get(_key(ref), (None, {}))[1].get("name") == "Marker"
            and "AudioSource" in (table.get(_key(ref), (None, {}))[1].get("components") or [])]
    return hits[0] if len(hits) == 1 else None


def audio_others(nodes, markers):
    """Ids of the other objects of the Hall subtree that carry an AudioSource."""
    chosen = _key(audio_marker(nodes, markers))
    return [ref for ref, state in nodes or [] if _key(ref) != chosen
            and "AudioSource" in ((state or {}).get("components") or [])]


def _activation(rec):
    return rec.get("kind") == "property_changed" and rec.get("property") == "m_IsActive"


def verify(ctx, card):
    p, h = ctx.probe, ctx.handles
    nodes = ctx.c0.get("nodes") or []
    chosen = audio_marker(nodes, h["markers"])
    others = {_key(ref) for ref in audio_others(nodes, h["markers"])}
    now = p.go(chosen) if chosen is not None else None
    card.check("C1", bool(now) and now.get("isActive") is False, expected=False, actual=(now or {}).get("isActive"),
               note=None if chosen is not None else "C0 does not single out one Marker with an AudioSource")
    want, got = {}, {}
    for ref, state in nodes:
        if _key(ref) == _key(chosen) or _key(ref) in others:
            continue
        want[_key(ref)] = (state or {}).get("isActive")
        got[_key(ref)] = (p.go(ref) or {}).get("isActive")
    card.check("C2", bool(want) and got == want, expected=want, actual=got)
    diffs = []
    for ref, state in nodes:
        if _key(ref) in others:
            ok, diff = V.same(state, V.go_state(p, ref))
            if not ok:
                diffs.append({_key(ref): diff})
    card.check("C3", bool(others) and not diffs, actual=diffs[:5])
    structure = ("name", "parentPath", "components", "children")
    changed = []
    for ref, state in nodes:
        current = V.go_state(p, ref) or {}
        ok, diff = V.same({k: (state or {}).get(k) for k in structure}, {k: current.get(k) for k in structure})
        if not ok:
            changed.append({_key(ref): diff})
    card.check("C4", bool(nodes) and not changed, actual=changed[:5])
    reported_ok = (now is not None and V.same_id(V.answer(ctx, "disabledInstanceId"), chosen) and
                   V.norm_path(V.answer(ctx, "disabledPath")) == now.get("path") and
                   isinstance(V.answer(ctx, "declaredComplete"), bool))
    card.check("C5", reported_ok, expected=(now or {}).get("path"), actual=ctx.answer)
    extra = [rec for rec in ctx.write_records() if rec.get("type") == "scene_changes" and not _activation(rec)]
    bad = ctx.violations()
    card.check("C6", not extra and not bad, actual={"otherChanges": extra[:10], "violations": bad[:10]})
    direct = ctx.direct_writes()
    card.check("C7", not direct, actual={"directWrites": direct[:10]})

# Producer:Betsy
