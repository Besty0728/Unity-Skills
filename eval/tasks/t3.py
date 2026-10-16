"""T3 Physics bridge: hinge chain by world X, physic material on box colliders only, layer + collision pair.

Traps: five same-name planks whose sibling order differs from their X order, a SphereCollider placed
before the BoxCollider on one plank (physics_set_material hits the first collider), child objects that
must keep their layer, a same-name decoy plank elsewhere, and one allowed ProjectSettings change (the
L1 x L2 collision pair). The fixture writes two custom layer names into free TagManager slots and
restores them, together with the collision pairs it touched, in teardown.
"""
import json
import os
import time

from harness import checks as K
from harness.rest import RestError
from harness.task import Negative, Task
from harness.verify import floats, parse_ref

TAG_MANAGER = "ProjectSettings/TagManager.asset"
SETTINGS_FILES = ("ProjectSettings/DynamicsManager.asset",)
PLANK_COUNT = 5
PLANK_X = [1.1 * (i + 1) for i in range(PLANK_COUNT)]
MASS = 2.0
FRICTION = (0.8, 0.9, 0.0)      # dynamic, static, bounciness


def _token(rng):
    return "".join(rng.choice("abcdefghjkmnpqrstuvwxyz") for _ in range(3))


def _layer_names(client):
    layers = _retry_429(client.read, "editor_get_layers").get("layers") or []
    return {int(l["index"]): l.get("name") for l in layers}


def _retry_429(fn, *args, **kwargs):
    # The server caps admission at 100 requests/s and harness.rest does not retry its 429 answers
    # (sent before execution, so a retry is safe even for writes).
    for attempt in range(20):
        try:
            return fn(*args, **kwargs)
        except RestError as exc:
            if exc.status != 429 or attempt == 19:
                raise
            time.sleep(1.0)


def _collision(client, a, b):
    time.sleep(0.02)
    return _retry_429(client.read, "physics_get_layer_collision", layer1=a, layer2=b).get("collisionEnabled")


def _pairs(l1, l2):
    keys = set()
    for layer in (l1, l2):
        for j in range(32):
            keys.add(tuple(sorted((layer, j))))
    return sorted(keys)


def _null_ref(value):
    ref = parse_ref(value)
    return ref is None or ref[0] == 0


class T3(Task):
    id = "T3"
    title = "Physics bridge with hinge chain, physic material and a collision pair"
    tier = "composite"
    timeout_s = 1800
    budget_usd = 40.0

    def assertions(self):
        return [
            ("P1", 1.0, False, "each plank has exactly one Rigidbody (mass 2) and exactly one HingeJoint"),
            ("P2", 1.5, False, "hinge chain by world X: plank1 -> AnchorLeft, plank k -> plank k-1"),
            ("P3", 0.5, False, "every hinge axis is (0, 0, 1)"),
            ("P4", 1.0, False, "A/Physics/BridgeGrip_<k>.physicMaterial with dynamic 0.8, static 0.9, bounce 0"),
            ("P5", 0.5, False, "exactly one physic material asset under A"),
            ("P6", 1.5, False, "material on every plank BoxCollider, not on plank3's SphereCollider"),
            ("P7", 1.0, False, "the five planks are on L1, their children keep their layer"),
            ("P8", 1.0, False, "L1 x L2 collision disabled; every other pair with L1 or L2 unchanged"),
            ("P9", 1.0, True, "AnchorLeft, Spare/Plank, plank TRS/children/order/component order unchanged"),
            ("P10", 0.5, True, "no side effects outside R/A except the collision pair, no scene save"),
            ("P11", 0.5, False, "answer matches the actual state"),
        ]

    # ------------------------------------------------------------------ fixture

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        k = _token(rng)
        order = list(range(PLANK_COUNT))
        while order == sorted(order):
            rng.shuffle(order)

        names = _layer_names(c)
        free = [i for i in range(8, 32) if not names.get(i)]
        if len(free) < 2:
            raise RuntimeError("need two free user layer slots, have %s" % free)
        l1, l2 = free[0], free[1]
        layer1, layer2 = "Ev%sPlank" % k, "Ev%sGuard" % k
        ctx.facts.update({"k": k, "L1": l1, "L2": l2, "layer1": layer1, "layer2": layer2, "layersWritten": []})
        for slot, name in ((l1, layer1), (l2, layer2)):
            c.write("scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                    propertyPath="layers.Array.data[%d]" % slot, value=name)
            ctx.facts["layersWritten"].append(slot)
        # Snapshot the pairs the task can touch, then make them all collide so the task starts clean.
        snapshot = {}
        for a, b in _pairs(l1, l2):
            snapshot["%d,%d" % (a, b)] = _collision(c, a, b)
        ctx.facts["collisionOriginal"] = snapshot
        for key, enabled in snapshot.items():
            if not enabled:
                a, b = (int(x) for x in key.split(","))
                c.write("physics_set_layer_collision", layer1=a, layer2=b, enableCollision=True)

        root = ctx.scope.root_iid
        bridge = c.write("gameobject_create", name="Bridge", parentInstanceId=root)["instanceId"]
        anchor = c.write("gameobject_create", name="AnchorLeft", primitiveType="Cube", parentInstanceId=bridge,
                         x=0, y=1, z=0)["instanceId"]
        c.write("component_add", instanceId=anchor, componentType="Rigidbody")
        c.write("component_set_property", instanceId=anchor, componentType="Rigidbody", propertyName="isKinematic",
                value="true")
        planks_root = c.write("gameobject_create", name="Planks", parentInstanceId=bridge)["instanceId"]
        by_x = [None] * PLANK_COUNT
        rails = [None] * PLANK_COUNT
        for xi in order:
            iid = c.write("gameobject_create", name="Plank", primitiveType="Cube", parentInstanceId=planks_root,
                          x=PLANK_X[xi], y=1, z=0, scaleX=1, scaleY=0.2, scaleZ=0.8)["instanceId"]
            if xi == 2:
                c.write("component_remove", instanceId=iid, componentType="BoxCollider")
                c.write("component_add", instanceId=iid, componentType="SphereCollider")
                c.write("component_add", instanceId=iid, componentType="BoxCollider")
            rails[xi] = c.write("gameobject_create", name="Rail", parentInstanceId=iid, y=0.6)["instanceId"]
            by_x[xi] = iid
        spare_root = c.write("gameobject_create", name="Spare", parentInstanceId=bridge)["instanceId"]
        spare = c.write("gameobject_create", name="Plank", primitiveType="Cube", parentInstanceId=spare_root,
                        x=7.7, y=1, z=0, scaleX=1, scaleY=0.2, scaleZ=0.8)["instanceId"]
        c.write("asset_create_folder", folderPath=ctx.asset("Physics"))
        ctx.facts.update({
            "bridgeIid": bridge, "anchorIid": anchor, "planksIid": planks_root, "planksByX": by_x, "railsByX": rails,
            "spareIid": spare, "spareRootIid": spare_root, "siblingOrder": order,
            "planksPath": ctx.path("Bridge", "Planks"), "anchorPath": ctx.path("Bridge", "AnchorLeft"),
            "materialPath": ctx.asset("Physics", "BridgeGrip_%s.physicMaterial" % k),
        })

    def after_fixture(self, ctx):
        p, f = ctx.probe, ctx.facts
        f["planksChildrenC0"] = [ch["instanceId"] for ch in p.children(f["planksIid"])]
        f["plankC0"] = [self._plank_state(p, iid) for iid in f["planksByX"]]
        f["railC0"] = [self._rail_state(p, iid) for iid in f["railsByX"]]
        f["anchorC0"] = {"go": K.go_state(p, f["anchorIid"]), "rb": p.ser(f["anchorIid"], "Rigidbody")}
        f["spareC0"] = {"go": K.go_state(p, f["spareIid"]), "root": K.go_state(p, f["spareRootIid"])}
        f["collisionC0"] = {"%d,%d" % (a, b): _collision(ctx.c, a, b) for a, b in _pairs(f["L1"], f["L2"])}

    def teardown(self, ctx):
        # Runs from cleanup even when build or verify raised; each step is independent so one failure
        # never skips the rest, and the final read-back raises if anything is still dirty.
        c, f = ctx.c, ctx.facts
        out = {"collisionRestored": [], "layersCleared": [], "errors": []}
        for key, enabled in (f.get("collisionOriginal") or {}).items():
            a, b = (int(x) for x in key.split(","))
            try:
                if _collision(c, a, b) != enabled:
                    _retry_429(c.write, "physics_set_layer_collision", layer1=a, layer2=b,
                               enableCollision=bool(enabled))
                    out["collisionRestored"].append(key)
            except Exception as exc:
                out["errors"].append("pair %s: %r" % (key, exc))
        for slot in f.get("layersWritten") or []:
            try:
                _retry_429(c.write, "scriptableobject_set_serialized_property", assetPath=TAG_MANAGER,
                           propertyPath="layers.Array.data[%d]" % slot, value="")
                out["layersCleared"].append(slot)
            except Exception as exc:
                out["errors"].append("layer %d: %r" % (slot, exc))
        names = _layer_names(c)
        left = [s for s in f.get("layersWritten") or [] if names.get(s)]
        bad = [k for k, v in (f.get("collisionOriginal") or {}).items()
               if _collision(c, *(int(x) for x in k.split(","))) != v]
        if left or bad:
            raise RuntimeError("T3 teardown incomplete: layers %s still named, pairs %s differ (%s)"
                               % (left, bad, out["errors"]))
        out["verified"] = True
        return out

    def allowed_change(self, ctx, rec):
        path = (rec.get("assetPath") or "").replace("\\", "/")
        if path.endswith(".meta"):
            path = path[:-5]
        return path in SETTINGS_FILES

    # ------------------------------------------------------------------ prompt

    def prompt(self, ctx):
        f = ctx.facts
        return ("`{planks}` 下有 5 个同名的 `Plank`，按世界坐标 X 从小到大编号为木板 1–5（层级顺序与编号无关）。"
                "为每块木板添加 Rigidbody（质量 2）和 HingeJoint；木板 1 的 HingeJoint 的 connectedBody 设为 `{anchor}` 的 Rigidbody，"
                "木板 k（k=2..5）的 connectedBody 设为木板 k−1 的 Rigidbody；所有 HingeJoint 的轴设为 (0, 0, 1)。"
                "新建物理材质 `{mat}`（动摩擦 0.8，静摩擦 0.9，弹性 0），只把它赋给这 5 块木板的 BoxCollider，其他碰撞体不要改。"
                "把这 5 块木板本身（不含子对象）的 Layer 设为 `{l1}`（第 {i1} 层），"
                "并在物理层碰撞矩阵中关闭 `{l1}` 与 `{l2}`（第 {i2} 层）之间的碰撞；"
                "这一对碰撞设置是本题唯一允许修改的工程设置，其他层对不要改，也不要增删层。"
                "`R/Bridge/Spare` 下的 Plank 不是木板，不要修改。").format(
            planks=f["planksPath"], anchor=f["anchorPath"], mat=f["materialPath"], l1=f["layer1"], l2=f["layer2"],
            i1=f["L1"], i2=f["L2"])

    def answer_schema(self):
        return ('{"planks": [{"number": 1 到 5 的编号, "instanceId": 整数}, ...共 5 项], '
                '"materialPath": "物理材质资产路径", "plankLayerIndex": 整数, "declaredComplete": true 或 false}')

    # ------------------------------------------------------------------ verification

    @staticmethod
    def _plank_state(p, iid):
        info = p.go(iid) or {}
        state = {k: info.get(k) for k in ("name", "path", "parentPath", "isActive", "tag", "position", "rotation",
                                          "scale")}
        state["children"] = [ch.get("instanceId") for ch in info.get("children") or []]
        comps = [c for c in info.get("components") or [] if c in ("SphereCollider", "BoxCollider")]
        state["colliders"] = comps
        return state

    @staticmethod
    def _rail_state(p, iid):
        info = p.go(iid) or {}
        return {k: info.get(k) for k in ("name", "path", "layer", "isActive", "position")}

    def verify(self, ctx, card):
        p, f, c = ctx.probe, ctx.facts, ctx.c
        planks = f["planksByX"]

        good = 0
        bodies = []
        for iid in planks:
            rb = p.component_count(iid, "Rigidbody")
            hinge = p.component_count(iid, "HingeJoint")
            mass = floats((p.ser(iid, "Rigidbody") or {}).get("m_Mass"))
            bodies.append({"rb": rb, "hinge": hinge, "mass": mass})
            good += rb == 1 and hinge == 1 and len(mass) == 1 and abs(mass[0] - MASS) < 1e-3
        card.fraction("P1", good / float(PLANK_COUNT), expected="1 Rigidbody(mass 2) + 1 HingeJoint", actual=bodies)

        axes = [(p.ser(iid, "HingeJoint") or {}).get("m_Axis") for iid in planks]
        card.check("P3", all(K.close_tuple(a, (0, 0, 1)) for a in axes), expected=(0, 0, 1), actual=axes)

        path = f["materialPath"]
        so = p.so(path) or {}
        props = {x.get("propertyPath"): x.get("value") for x in so.get("properties") or []}
        values = [floats(props.get(k)) for k in ("dynamicFriction", "staticFriction", "bounciness")]
        ok_values = all(len(v) == 1 and abs(v[0] - want) < 1e-3 for v, want in zip(values, FRICTION))
        card.check("P4", bool(so) and ok_values, expected=FRICTION,
                   actual={"type": so.get("typeName"), "values": values})

        disk = self._physic_materials(ctx)
        card.check("P5", disk == [path], expected=[path], actual=disk)

        mat_name = os.path.splitext(os.path.basename(path))[0]
        rows, ok_mat = [], True
        for i, iid in enumerate(planks):
            box = parse_ref((p.ser(iid, "BoxCollider") or {}).get("m_Material"))
            row = {"box": box}
            ok = bool(box) and box[2] == mat_name and "PhysicMaterial" in box[1]
            if i == 2:
                sphere = (p.ser(iid, "SphereCollider") or {}).get("m_Material")
                row["sphere"] = sphere
                ok = ok and _null_ref(sphere)
            ok_mat = ok_mat and ok
            rows.append(row)
        card.check("P6", ok_mat, expected=mat_name, actual=rows)

        plank_layers = [(p.go(iid) or {}).get("layer") for iid in planks]
        rails_now = [self._rail_state(p, iid) for iid in f["railsByX"]]
        rails_same = K.same(f["railC0"], rails_now)
        card.check("P7", all(l == f["layer1"] for l in plank_layers) and rails_same[0],
                   expected=f["layer1"], actual={"planks": plank_layers, "railDiff": rails_same[1]})

        now = {"%d,%d" % (a, b): _collision(c, a, b) for a, b in _pairs(f["L1"], f["L2"])}
        target = "%d,%d" % tuple(sorted((f["L1"], f["L2"])))
        changed = sorted(key for key in now if key != target and now[key] != f["collisionC0"].get(key))
        card.check("P8", now.get(target) is False and not changed, expected={target: False},
                   actual={target: now.get(target), "otherChanged": changed})

        diffs = []
        for label, before, after in (
                ("anchor", f["anchorC0"], {"go": K.go_state(p, f["anchorIid"]), "rb": p.ser(f["anchorIid"], "Rigidbody")}),
                ("spare", f["spareC0"], {"go": K.go_state(p, f["spareIid"]), "root": K.go_state(p, f["spareRootIid"])}),
                ("planks", f["plankC0"], [self._plank_state(p, iid) for iid in planks]),
                ("order", f["planksChildrenC0"], [ch["instanceId"] for ch in p.children(f["planksIid"])])):
            ok, diff = K.same(before, after)
            if not ok:
                diffs.append((label, diff))
        card.check("P9", not diffs, actual=diffs)

        bad = ctx.violations(self)
        saves = ctx.saved_scenes()
        card.check("P10", not bad and not saves, actual={"violations": bad[:10], "sceneSaves": saves[:5]})

        reported = {}
        for row in K.answer(ctx, "planks") or []:
            if isinstance(row, dict):
                reported[K.as_int(row.get("number"))] = K.as_int(row.get("instanceId"))
        layer_now = (p.go(planks[0]) or {}).get("layer")
        names = _layer_names(c)
        ans_ok = (reported == {i + 1: iid for i, iid in enumerate(planks)} and
                  K.norm_path(K.answer(ctx, "materialPath")) == path and
                  names.get(K.as_int(K.answer(ctx, "plankLayerIndex"))) == layer_now and
                  K.answer(ctx, "declaredComplete") in (True, False))
        card.check("P11", ans_ok, actual=ctx.answer)

        # Last: m_ConnectedBody is not listed by the serialized reader and component_get_properties
        # reports the name only, so give every candidate a unique name first (run scene only).
        labels = {iid: "Plank@%d" % (i + 1) for i, iid in enumerate(planks)}
        labels[f["spareIid"]] = "Plank@spare"
        for iid, label in labels.items():
            c.write("gameobject_rename", instanceId=iid, newName=label)
        expected = ["AnchorLeft"] + [labels[iid] for iid in planks[:-1]]
        actual = []
        for iid in planks:
            try:
                props = c.read("component_get_properties", instanceId=iid, componentType="HingeJoint")
            except Exception:
                actual.append(None)
                continue
            body = next((x for x in props.get("properties") or [] if x.get("name") == "connectedBody"), {})
            actual.append(body.get("value"))
        hits = sum(1 for a, e in zip(actual, expected) if a == e)
        card.fraction("P2", hits / float(PLANK_COUNT), expected=expected, actual=actual)

    def _physic_materials(self, ctx):
        root = os.path.join(ctx.project_path, ctx.A)
        out = []
        for base, _dirs, files in os.walk(root):
            for name in files:
                if name.endswith(".physicMaterial") or name.endswith(".physicsMaterial"):
                    rel = os.path.relpath(os.path.join(base, name), ctx.project_path)
                    out.append(rel.replace(os.sep, "/"))
        return sorted(out)

    # ------------------------------------------------------------------ solutions

    def _answer(self, ctx, order=None):
        f = ctx.facts
        order = order or f["planksByX"]
        return {"planks": [{"number": i + 1, "instanceId": iid} for i, iid in enumerate(order)],
                "materialPath": f["materialPath"], "plankLayerIndex": f["L1"], "declaredComplete": True}

    def _create_material(self, ctx):
        res = ctx.c.write("physics_create_material", name="BridgeGrip_%s" % ctx.facts["k"],
                          savePath=ctx.asset("Physics"), dynamicFriction=FRICTION[0], staticFriction=FRICTION[1],
                          bounciness=FRICTION[2])
        return res.get("path")

    def _joints(self, ctx, order, axis="0,0,1"):
        c, f = ctx.c, ctx.facts
        prev = f["anchorIid"]
        for iid in order:
            c.write("component_add", instanceId=iid, componentType="Rigidbody")
            c.write("component_set_property", instanceId=iid, componentType="Rigidbody", propertyName="mass",
                    value=str(MASS))
            c.write("component_add", instanceId=iid, componentType="HingeJoint")
            c.write("component_set_serialized_property", instanceId=iid, componentType="HingeJoint",
                    propertyPath="m_ConnectedBody", referenceInstanceId=prev, objectType="Rigidbody")
            c.write("component_set_property", instanceId=iid, componentType="HingeJoint", propertyName="axis",
                    value=axis)
            prev = iid

    def _box_material(self, ctx, path):
        for iid in ctx.facts["planksByX"]:
            ctx.c.write("component_set_serialized_property", instanceId=iid, componentType="BoxCollider",
                        propertyPath="m_Material", assetPath=path)

    def _layers(self, ctx, recursive=False, extra=()):
        f = ctx.facts
        items = [{"instanceId": iid, "layer": f["layer1"], "recursive": recursive}
                 for iid in list(f["planksByX"]) + list(extra)]
        ctx.c.write("gameobject_set_layer_batch", items=json.dumps(items))

    def _collision_off(self, ctx, a=None, b=None):
        f = ctx.facts
        ctx.c.write("physics_set_layer_collision", layer1=f["L1"] if a is None else a,
                    layer2=f["L2"] if b is None else b, enableCollision=False)

    def _solve(self, ctx, order=None, axis="0,0,1", material=True, set_tool=False, recursive=False, extra=(),
               pair=None):
        f = ctx.facts
        path = self._create_material(ctx) if material else f["materialPath"]
        self._joints(ctx, order or f["planksByX"], axis=axis)
        if set_tool:
            for iid in f["planksByX"]:
                ctx.c.write("physics_set_material", instanceId=iid, materialPath=path)
        else:
            self._box_material(ctx, path)
        self._layers(ctx, recursive=recursive, extra=extra)
        if pair:
            self._collision_off(ctx, *pair)
        else:
            self._collision_off(ctx)
        return self._answer(ctx, order)

    def reference(self, ctx):
        return self._solve(ctx)

    def negatives(self):
        def set_material_tool(ctx):
            return self._solve(ctx, set_tool=True)

        def chain_by_sibling(ctx):
            sibling = [ch["instanceId"] for ch in ctx.probe.children(ctx.facts["planksIid"])]
            return self._solve(ctx, order=sibling)

        def wrong_pair(ctx):
            return self._solve(ctx, pair=(ctx.facts["L1"], ctx.facts["L1"]))

        def recursive_layer(ctx):
            return self._solve(ctx, recursive=True)

        def spare_edited(ctx):
            return self._solve(ctx, extra=(ctx.facts["spareIid"],))

        def material_twice(ctx):
            ans = self._solve(ctx)
            self._create_material(ctx)
            return ans

        def axis_x(ctx):
            return self._solve(ctx, axis="1,0,0")

        def wrong_report(ctx):
            ans = self._solve(ctx)
            ans["plankLayerIndex"] = ctx.facts["L2"]
            return ans

        return [
            Negative("set_material_tool", set_material_tool, {"P6"}),
            Negative("chain_by_sibling", chain_by_sibling, {"P2", "P11"}),
            Negative("wrong_pair", wrong_pair, {"P8"}),
            Negative("recursive_layer", recursive_layer, {"P7"}),
            Negative("spare_edited", spare_edited, {"P9"}),
            Negative("material_twice", material_twice, {"P5"}),
            Negative("axis_x", axis_x, {"P3"}),
            Negative("wrong_report", wrong_report, {"P11"}),
        ]

    def alternatives(self):
        def property_route(ctx):
            c, f = ctx.c, ctx.facts
            path = c.write("physics_create_material", name="BridgeGrip_%s" % f["k"],
                           savePath=ctx.asset("Physics"))["path"]
            for prop, value in zip(("dynamicFriction", "staticFriction", "bounciness"), FRICTION):
                c.write("scriptableobject_set_serialized_property", assetPath=path, propertyPath=prop, value=str(value))
            prev = f["anchorIid"]
            for iid in f["planksByX"]:
                c.write("component_add", instanceId=iid, componentType="HingeJoint")
                c.write("component_set_serialized_property", instanceId=iid, componentType="Rigidbody",
                        propertyPath="m_Mass", value=str(MASS))
                c.write("component_set_serialized_property", instanceId=iid, componentType="HingeJoint",
                        propertyPath="m_ConnectedBody", referenceInstanceId=prev, objectType="Rigidbody")
                c.write("component_set_serialized_property", instanceId=iid, componentType="HingeJoint",
                        propertyPath="m_Axis", value="0,0,1")
                c.write("component_set_property", instanceId=iid, componentType="BoxCollider",
                        propertyName="sharedMaterial", assetPath=path)
                prev = iid
            self._layers(ctx)
            self._collision_off(ctx, f["L2"], f["L1"])
            return self._answer(ctx)

        return [("property_route", property_route)]


TASK = T3()

# Producer:Betsy
