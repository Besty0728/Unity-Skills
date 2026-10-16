"""S03' Reparent keeping the LOCAL pose (basic tier).

Traps: gameobject_set_parent keeps the world pose, and the new parent (Props) is offset, rotated
and scaled, so the local numbers drift unless they are written back; set_transform rotX/Y/Z are
world angles; a decoy also named Lamp sits under Props/Shelf; the Lamp must end up as Props'
last child with its own child and components untouched.
"""
from harness import checks as K
from harness.task import Negative, Task
from harness.verify import quat_close, quat_from_euler

LAMP_POS = (0.5, 1.2, -0.3)
LAMP_ROT = (10.0, 20.0, 0.0)
LAMP_SCALE = 0.8
BULB_POS = (0.0, 0.4, 0.0)


class S03(Task):
    id = "S03"
    title = "Reparent keeping the local pose"
    tier = "basic"
    timeout_s = 420
    budget_usd = 6.0

    def assertions(self):
        return [
            ("B1", 2.0, False, "the original Lamp (same instanceId) is now a child of Props"),
            ("B2", 2.0, False, "Lamp m_LocalPosition equals the old local position"),
            ("B3", 2.0, False, "Lamp local rotation equals the old local rotation (10, 20, 0)"),
            ("B4", 1.0, False, "Lamp m_LocalScale equals the old local scale"),
            ("B5", 1.0, False, "Bulb (same id) keeps parent and local TRS; Lamp components/order/enabled unchanged"),
            ("B6", 1.0, True, "other nodes incl. the decoy Lamp unchanged; Props keeps child order, Lamp last"),
            ("B7", 0.5, False, "answer fields match the actual state"),
            ("B8", 0.5, False, "no side effects outside R/A and no scene save"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        theta = rng.randint(25, 65)
        wh = c.write("gameobject_create", name="Warehouse", parentInstanceId=ctx.scope.root_iid, x=2, y=0, z=1)["instanceId"]
        loose = c.write("gameobject_create", name="Loose", parentInstanceId=wh, x=-3, y=1, z=0)["instanceId"]
        props = c.write("gameobject_create", name="Props", parentInstanceId=wh, x=5, y=2, z=-1, rotY=theta,
                        scaleX=1.5, scaleY=1.5, scaleZ=1.5)["instanceId"]
        lamp = c.write("gameobject_create", name="Lamp", parentInstanceId=loose, x=LAMP_POS[0], y=LAMP_POS[1],
                       z=LAMP_POS[2], rotX=LAMP_ROT[0], rotY=LAMP_ROT[1], rotZ=LAMP_ROT[2],
                       scaleX=LAMP_SCALE, scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE)["instanceId"]
        c.write("component_add", instanceId=lamp, componentType="AudioSource")
        c.write("component_add", instanceId=lamp, componentType="BoxCollider")
        c.write("component_set_enabled", instanceId=lamp, componentType="BoxCollider", enabled=False)
        bulb = c.write("gameobject_create", name="Bulb", primitiveType="Sphere", parentInstanceId=lamp,
                       x=BULB_POS[0], y=BULB_POS[1], z=BULB_POS[2], scaleX=0.2, scaleY=0.2, scaleZ=0.2)["instanceId"]
        rope = c.write("gameobject_create", name="Rope", parentInstanceId=loose, x=0, y=2, z=0)["instanceId"]
        shelf = c.write("gameobject_create", name="Shelf", primitiveType="Cube", parentInstanceId=props,
                        x=-1, y=0.5, z=0)["instanceId"]
        crate = c.write("gameobject_create", name="Crate", primitiveType="Cube", parentInstanceId=props,
                        x=1, y=0.5, z=0.5)["instanceId"]
        decoy = c.write("gameobject_create", name="Lamp", parentInstanceId=shelf, x=0, y=0.8, z=0)["instanceId"]
        c.write("component_add", instanceId=decoy, componentType="AudioSource")
        ctx.facts.update({
            "theta": theta, "warehouseIid": wh, "looseIid": loose, "propsIid": props, "lampIid": lamp,
            "bulbIid": bulb, "ropeIid": rope, "shelfIid": shelf, "crateIid": crate, "decoyIid": decoy,
            "lampPath": ctx.path("Warehouse", "Loose", "Lamp"), "propsPath": ctx.path("Warehouse", "Props"),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["lampCompsC0"] = self._comps(ctx, f["lampIid"])
        f["bulbC0"] = K.go_state(p, f["bulbIid"], with_children=False)
        f["bulbTrsC0"] = K.local_trs(p, f["bulbIid"])
        f["fixedC0"] = {str(i): K.go_state(p, f[k]) for k, i in self._fixed(f)}
        f["propsC0"] = K.go_state(p, f["propsIid"], with_children=False)
        f["looseC0"] = K.go_state(p, f["looseIid"], with_children=False)
        f["propsChildrenC0"] = [ch["instanceId"] for ch in p.children(f["propsIid"])]
        f["looseChildrenC0"] = [ch["instanceId"] for ch in p.children(f["looseIid"])]

    @staticmethod
    def _fixed(f):
        keys = ("warehouseIid", "ropeIid", "shelfIid", "crateIid", "decoyIid")
        return [(k, f[k]) for k in keys]

    @staticmethod
    def _comps(ctx, iid):
        try:
            comps = ctx.probe.components(iid)
        except Exception:
            return None
        return [(c.get("type"), c.get("enabled")) for c in comps]

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        return ("把 `{lamp}` 移到 `{props}` 下，作为它的最后一个子物体。移动后 Lamp 相对新父物体的本地位置、本地旋转、"
                "本地缩放的数值必须与移动前相对旧父物体的本地数值完全相同（保持本地数值，而不是保持世界位姿）。"
                "Lamp 的子物体和组件保持不变，不要修改其他任何对象。").format(lamp=ctx.facts["lampPath"],
                                                         props=ctx.facts["propsPath"])

    def answer_schema(self):
        return ('{"newPath": "Lamp 移动后的完整层级路径", "localPosition": [x, y, z], '
                '"localEulerAngles": [x, y, z], "localScale": [x, y, z], "declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    def verify(self, ctx, card):
        p, f = ctx.probe, ctx.facts
        lamp = p.go(f["lampIid"])
        card.check("B1", bool(lamp) and lamp.get("name") == "Lamp" and lamp.get("parentPath") == f["propsPath"],
                   expected=f["propsPath"], actual=(lamp or {}).get("parentPath"))
        moved = self._moved(ctx, lamp)
        trs = K.local_trs(p, moved["instanceId"]) if moved else {}
        card.check("B2", bool(trs) and K.close_tuple(trs.get("position"), LAMP_POS),
                   expected=LAMP_POS, actual=trs.get("position"))
        card.check("B3", bool(trs) and quat_close(trs.get("rotation"), quat_from_euler(*LAMP_ROT)),
                   expected=quat_from_euler(*LAMP_ROT), actual=trs.get("rotation"))
        card.check("B4", bool(trs) and K.close_tuple(trs.get("scale"), (LAMP_SCALE,) * 3),
                   expected=(LAMP_SCALE,) * 3, actual=trs.get("scale"))

        bulb = K.go_state(p, f["bulbIid"], with_children=False)
        bulb_trs = K.local_trs(p, f["bulbIid"]) if bulb else None
        comps = self._comps(ctx, f["lampIid"]) if lamp else None
        lamp_kids = [ch["instanceId"] for ch in (lamp or {}).get("children") or []]
        bulb_ok = bool(bulb) and bulb.get("name") == "Bulb" and K.same(f["bulbTrsC0"], bulb_trs)[0] and \
            lamp_kids == [f["bulbIid"]]
        card.check("B5", bulb_ok and comps == [tuple(x) for x in f["lampCompsC0"]],
                   actual={"bulb": bulb, "bulbTrs": bulb_trs, "lampChildren": lamp_kids, "components": comps,
                           "componentsC0": f["lampCompsC0"]})

        diffs = []
        for key, iid in self._fixed(f):
            ok, diff = K.same(f["fixedC0"][str(iid)], K.go_state(p, iid))
            if not ok:
                diffs.append({key: diff})
        for key, iid in (("props", f["propsIid"]), ("loose", f["looseIid"])):
            ok, diff = K.same(f[key + "C0"], K.go_state(p, iid, with_children=False))
            if not ok:
                diffs.append({key: diff})
        props_kids = [ch for ch in p.children(f["propsIid"])]
        kept = [ch["instanceId"] for ch in props_kids if ch["instanceId"] in f["propsChildrenC0"]]
        last_ok = len(props_kids) == len(f["propsChildrenC0"]) + 1 and props_kids[-1].get("name") == "Lamp" and \
            props_kids[-1]["instanceId"] not in f["propsChildrenC0"]
        loose_kids = [ch["instanceId"] for ch in p.children(f["looseIid"])]
        loose_ok = loose_kids == [i for i in f["looseChildrenC0"] if i != f["lampIid"]]
        card.check("B6", not diffs and kept == f["propsChildrenC0"] and last_ok and loose_ok,
                   actual={"diffs": diffs[:5], "propsChildren": [(c.get("name"), c["instanceId"]) for c in props_kids],
                           "looseChildren": loose_kids})

        card.check("B7", self._answer_ok(ctx, moved, trs), actual=ctx.answer)
        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("B8", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

    def _moved(self, ctx, lamp):
        """The Lamp now under Props: the original if it moved, else a newly added Props child named Lamp,
        so B2-B4 grade the pose of whatever was placed there and B1/B5 grade identity."""
        f = ctx.facts
        if lamp and lamp.get("parentPath") == f["propsPath"]:
            return lamp
        added = [ch for ch in ctx.probe.children(f["propsIid"])
                 if ch.get("name") == "Lamp" and ch["instanceId"] not in f["propsChildrenC0"]]
        return ctx.probe.go(added[-1]["instanceId"]) if added else None

    def _answer_ok(self, ctx, lamp, trs):
        if not lamp or not trs:
            return False
        try:
            rot = quat_from_euler(*K.floats(K.answer(ctx, "localEulerAngles")))
        except TypeError:
            return False
        return (K.norm_path(K.answer(ctx, "newPath")) == lamp.get("path") and
                K.close_tuple(K.answer(ctx, "localPosition"), trs["position"], 1e-2) and
                quat_close(rot, trs["rotation"], 0.5) and
                K.close_tuple(K.answer(ctx, "localScale"), trs["scale"], 1e-2) and
                K.answer(ctx, "declaredComplete") in (True, False))

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, iid):
        info = ctx.probe.go(iid) or {}
        props = ctx.c.read("component_get_properties", instanceId=iid, componentType="Transform")
        vals = {pr["name"]: K.floats(pr.get("value")) for pr in props.get("properties") or []}
        return {"newPath": info.get("path"), "localPosition": vals.get("localPosition"),
                "localEulerAngles": vals.get("localEulerAngles"), "localScale": vals.get("localScale"),
                "declaredComplete": True}

    @staticmethod
    def _local_pose(ctx, iid, world_rot=False):
        c = ctx.c
        c.write("gameobject_set_transform", instanceId=iid, localPosX=LAMP_POS[0], localPosY=LAMP_POS[1],
                localPosZ=LAMP_POS[2], scaleX=LAMP_SCALE, scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE)
        if world_rot:
            c.write("gameobject_set_transform", instanceId=iid, rotX=LAMP_ROT[0], rotY=LAMP_ROT[1], rotZ=LAMP_ROT[2])
        else:
            c.write("component_set_property", instanceId=iid, componentType="Transform",
                    propertyName="localEulerAngles", value="%g,%g,%g" % LAMP_ROT)

    def reference(self, ctx):
        f = ctx.facts
        ctx.c.write("gameobject_set_parent", childInstanceId=f["lampIid"], parentInstanceId=f["propsIid"])
        self._local_pose(ctx, f["lampIid"])
        return self._answer(ctx, f["lampIid"])

    def negatives(self):
        def only_reparent(ctx):
            f = ctx.facts
            ctx.c.write("gameobject_set_parent", childInstanceId=f["lampIid"], parentInstanceId=f["propsIid"])
            return self._answer(ctx, f["lampIid"])

        def world_rot(ctx):
            f = ctx.facts
            ctx.c.write("gameobject_set_parent", childInstanceId=f["lampIid"], parentInstanceId=f["propsIid"])
            self._local_pose(ctx, f["lampIid"], world_rot=True)
            return self._answer(ctx, f["lampIid"])

        def recreate(ctx):
            f, c = ctx.facts, ctx.c
            c.write("gameobject_delete", instanceId=f["lampIid"])
            lamp = c.write("gameobject_create", name="Lamp", parentInstanceId=f["propsIid"], x=LAMP_POS[0],
                           y=LAMP_POS[1], z=LAMP_POS[2], rotX=LAMP_ROT[0], rotY=LAMP_ROT[1], rotZ=LAMP_ROT[2],
                           scaleX=LAMP_SCALE, scaleY=LAMP_SCALE, scaleZ=LAMP_SCALE)["instanceId"]
            c.write("component_add", instanceId=lamp, componentType="AudioSource")
            c.write("component_add", instanceId=lamp, componentType="BoxCollider")
            c.write("component_set_enabled", instanceId=lamp, componentType="BoxCollider", enabled=False)
            c.write("gameobject_create", name="Bulb", primitiveType="Sphere", parentInstanceId=lamp,
                    x=BULB_POS[0], y=BULB_POS[1], z=BULB_POS[2], scaleX=0.2, scaleY=0.2, scaleZ=0.2)
            return self._answer(ctx, lamp)

        def first_child(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_set_sibling_index", instanceId=ctx.facts["lampIid"], index=0)
            return ans

        def edit_bulb(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_set_transform", instanceId=ctx.facts["bulbIid"], localPosY=0.6)
            return ans

        def move_decoy(ctx):
            f = ctx.facts
            ctx.c.write("gameobject_set_parent", childInstanceId=f["decoyIid"], parentInstanceId=f["propsIid"])
            self._local_pose(ctx, f["decoyIid"])
            return self._answer(ctx, f["decoyIid"])

        def wrong_report(ctx):
            ans = self.reference(ctx)
            ans["localScale"] = [0.5333, 0.5333, 0.5333]
            return ans

        def outside_root(ctx):
            ans = self.reference(ctx)
            ctx.c.write("gameobject_create", name="StrayLamp")
            return ans

        return [
            Negative("only_reparent", only_reparent, {"B2", "B3", "B4"}),
            Negative("world_rot", world_rot, {"B3"}),
            Negative("recreate", recreate, {"B1", "B5"}),
            Negative("first_child", first_child, {"B6"}),
            Negative("edit_bulb", edit_bulb, {"B5"}),
            Negative("move_decoy", move_decoy, {"B1", "B6"}),
            Negative("wrong_report", wrong_report, {"B7"}),
            Negative("outside_root", outside_root, {"B8"}),
        ]

    def alternatives(self):
        def serialized_quaternion(ctx):
            f, c = ctx.facts, ctx.c
            c.write("gameobject_set_parent", childInstanceId=f["lampIid"], parentInstanceId=f["propsIid"])
            q = quat_from_euler(*LAMP_ROT)
            c.write("component_set_serialized_property", instanceId=f["lampIid"], componentType="Transform",
                    propertyPath="m_LocalPosition", value="%g,%g,%g" % LAMP_POS)
            c.write("component_set_serialized_property", instanceId=f["lampIid"], componentType="Transform",
                    propertyPath="m_LocalRotation", value="%.9f,%.9f,%.9f,%.9f" % q)
            c.write("component_set_serialized_property", instanceId=f["lampIid"], componentType="Transform",
                    propertyPath="m_LocalScale", value="%g,%g,%g" % ((LAMP_SCALE,) * 3))
            return self._answer(ctx, f["lampIid"])

        return [("serialized_quaternion", serialized_quaternion)]


TASK = S03()

# Producer:Betsy
