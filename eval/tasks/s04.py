"""S04' Disable one of several same-name siblings (basic tier).

Traps: five siblings are all called Marker; name lookups pick an arbitrary one (and are
case-insensitive, so `marker` competes too); one Marker is already inactive; Marker_Archive,
lowercase `marker` and R/Hall/Backup/Marker also carry an AudioSource.
"""
from harness import checks as K
from harness.task import Negative, Task

MARKER_COUNT = 5


class S04(Task):
    id = "S04"
    title = "Disable the right same-name sibling"
    tier = "basic"
    timeout_s = 300
    budget_usd = 5.0

    def assertions(self):
        return [
            ("C1", 4.0, False, "the Marker under Markers that has an AudioSource is inactive (activeSelf false)"),
            ("C2", 2.0, False, "the other four Markers keep their C0 active states"),
            ("C3", 1.0, False, "decoys (Marker_Archive, marker, Backup/Marker) unchanged"),
            ("C4", 1.5, True, "ids, names, parents, sibling order and components of the Hall subtree unchanged"),
            ("C5", 0.5, False, "answer reports the target's instanceId and path"),
            ("C6", 1.0, False, "journal: only active-state changes, nothing outside R/A, no scene save"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        hall = c.write("gameobject_create", name="Hall", parentInstanceId=ctx.scope.root_iid)["instanceId"]
        markers = c.write("gameobject_create", name="Markers", parentInstanceId=hall)["instanceId"]
        backup = c.write("gameobject_create", name="Backup", parentInstanceId=hall, x=0, y=0, z=6)["instanceId"]
        # roles of the children of Markers, seeded; a Marker with no AudioSource always comes first so a
        # first-match lookup by name never lands on the target by luck
        roles = ["target", "inactive", "plain", "plain", "archive", "lower"]
        rng.shuffle(roles)
        roles.insert(0, "plain")
        nodes = {"markers": [], "plain": []}
        for i, role in enumerate(roles):
            name = {"archive": "Marker_Archive", "lower": "marker"}.get(role, "Marker")
            iid = c.write("gameobject_create", name=name, primitiveType="Sphere", parentInstanceId=markers,
                          x=i * 1.5, y=0.5, z=0, scaleX=0.4, scaleY=0.4, scaleZ=0.4)["instanceId"]
            if role in ("target", "archive", "lower"):
                c.write("component_add", instanceId=iid, componentType="AudioSource")
            if role == "inactive":
                c.write("gameobject_set_active", instanceId=iid, active=False)
            if name == "Marker":
                nodes["markers"].append(iid)
            if role in ("target", "inactive", "archive", "lower"):
                nodes[role] = iid
        spare = c.write("gameobject_create", name="Marker", primitiveType="Sphere", parentInstanceId=backup,
                        x=0, y=0.5, z=0, scaleX=0.4, scaleY=0.4, scaleZ=0.4)["instanceId"]
        c.write("component_add", instanceId=spare, componentType="AudioSource")
        ctx.facts.update({
            "hallIid": hall, "markersIid": markers, "markersPath": ctx.path("Hall", "Markers"),
            "markerIids": nodes["markers"], "targetIid": nodes["target"], "inactiveIid": nodes["inactive"],
            "decoyIids": [nodes["archive"], nodes["lower"], spare],
            "targetPath": ctx.path("Hall", "Markers", "Marker"),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["activeC0"] = {str(i): (p.go(i) or {}).get("isActive") for i in f["markerIids"]}
        f["decoysC0"] = {str(i): K.go_state(p, i) for i in f["decoyIids"]}
        f["structureC0"] = self._structure(ctx)

    def _structure(self, ctx):
        out = []

        def walk(iid):
            info = ctx.probe.go(iid) or {}
            kids = [ch["instanceId"] for ch in info.get("children") or []]
            out.append({"id": iid, "name": info.get("name"), "parentPath": info.get("parentPath"),
                        "components": info.get("components"), "children": kids})
            for k in kids:
                walk(k)

        walk(ctx.facts["hallIid"])
        return out

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("`{markers}` 下有多个同名子物体 `Marker`。只禁用其中带有 AudioSource 组件的那一个"
                "（把它的 activeSelf 设为 false）。其他对象（包括其他 Marker）的激活状态、名称、层级和组件都不要改变。"
                ).format(markers=ctx.facts["markersPath"])

    def answer_schema(self):
        return '{"disabledInstanceId": 整数, "disabledPath": "被禁用对象的完整层级路径", "declaredComplete": true 或 false}'

    # ------------------------------------------------------------------ verification

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        target = p.go(f["targetIid"])
        card.check("C1", bool(target) and target.get("isActive") is False, actual=(target or {}).get("isActive"))
        others = {str(i): (p.go(i) or {}).get("isActive") for i in f["markerIids"] if i != f["targetIid"]}
        want = {k: v for k, v in f["activeC0"].items() if k != str(f["targetIid"])}
        card.check("C2", others == want, expected=want, actual=others)
        diffs = []
        for iid in f["decoyIids"]:
            ok, diff = K.same(f["decoysC0"][str(iid)], K.go_state(p, iid))
            if not ok:
                diffs.append(diff)
        card.check("C3", not diffs, actual=diffs)
        ok, diff = K.same(f["structureC0"], self._structure(ctx))
        card.check("C4", ok, actual=diff)
        ans_ok = (target is not None and K.as_int(K.answer(ctx, "disabledInstanceId")) == f["targetIid"] and
                  K.norm_path(K.answer(ctx, "disabledPath")) == target.get("path") and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("C5", ans_ok, expected=f["targetIid"], actual=ctx.answer)
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        other = [r for r in ctx.records if r.get("type") == "scene_changes" and not self._is_activation(r)]
        card.check("C6", not bad and not saves and not other,
                   actual={"violations": bad[:10], "sceneSaves": saves[:5], "nonActivation": other[:10]})

    @staticmethod
    def _is_activation(rec):
        return rec.get("kind") == "property_changed" and rec.get("property") == "m_IsActive"

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iid):
        info = ctx.probe.go(iid) or {}
        return {"disabledInstanceId": iid, "disabledPath": info.get("path"), "declaredComplete": True}

    def _newly_inactive(self, ctx):
        f = ctx.facts
        for iid in f["markerIids"] + f["decoyIids"]:
            was = f["activeC0"].get(str(iid), True)
            if was and (ctx.probe.go(iid) or {}).get("isActive") is False:
                return iid
        return None

    def reference(self, ctx):
        f = ctx.facts
        res = ctx.probe.query({"parentPath": f["markersPath"], "componentType": "AudioSource",
                               "includeInactive": True})
        hits = [o for o in res.get("objects") or [] if o.get("name") == "Marker"]
        iid = hits[0]["instanceId"]
        ctx.c.write("gameobject_set_active", instanceId=iid, active=False)
        return self._answer(ctx, iid)

    def negatives(self):
        def by_name(ctx):
            ctx.c.write("gameobject_set_active", name="Marker", active=False)
            return self._answer(ctx, self._newly_inactive(ctx))

        def all_markers(ctx):
            for iid in ctx.facts["markerIids"]:
                ctx.c.write("gameobject_set_active", instanceId=iid, active=False)
            return self._answer(ctx, ctx.facts["targetIid"])

        def reactivate_inactive(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_set_active", instanceId=ctx.facts["inactiveIid"], active=True)
            return ans

        def rename(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_rename", instanceId=ctx.facts["targetIid"], newName="Marker_Audio")
            return self._answer(ctx, ctx.facts["targetIid"])

        def archive(ctx):
            iid = ctx.facts["decoyIids"][0]
            ctx.c.write("gameobject_set_active", instanceId=iid, active=False)
            return self._answer(ctx, iid)

        def wrong_report(ctx):
            self.reference(ctx)
            other = next(i for i in ctx.facts["markerIids"] if i not in (ctx.facts["targetIid"],
                                                                          ctx.facts["inactiveIid"]))
            return self._answer(ctx, other)

        return [
            Negative("by_name", by_name, {"C1", "C3", "C5"}, "name lookup resolves to R/Hall/Backup/Marker"),
            Negative("all", all_markers, {"C2"}),
            Negative("reactivate_inactive", reactivate_inactive, {"C2"}),
            Negative("rename", rename, {"C4", "C6"}),
            Negative("archive", archive, {"C1", "C3", "C5"}),
            Negative("wrong_report", wrong_report, {"C5"}),
        ]

    def alternatives(self):
        def find_with_component(ctx):
            f = ctx.facts
            res = ctx.c.read("gameobject_find", name="Marker", component="AudioSource", limit=500)
            hits = [o for o in res.get("objects") or []
                    if o.get("name") == "Marker" and o.get("path") == f["markersPath"] + "/Marker"]
            iid = hits[0]["instanceId"]
            ctx.c.write("gameobject_set_active", instanceId=iid, active=False)
            return self._answer(ctx, iid)

        return [("find_with_component", find_with_component)]


TASK = S04()

# Producer:Betsy
