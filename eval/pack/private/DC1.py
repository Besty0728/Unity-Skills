"""DC1 private verifier: an inventory script fixed in place, its public API, and a Play Mode call sequence graded call by
call against the specification.

What the task probes: the original Add merges into the first stack without the maxStack cap, accepts counts of 0 or
less (and raises Changed for them), creates at most one uncapped new stack; Remove takes from the first stack only and
never raises Changed. Agents tend to change the public signature (uint counts, extra parameters, a field instead of
the event), fix only the prompt's example (a hard-coded 10), recreate the script (new GUID, the scene component goes
missing) or touch the watcher. Play Mode: every Add / Remove is followed by a read of the inventory and of the
watcher, so each call is graded from the state observed just before it (a wrong Add does not spoil the Remove checks),
with the maxStack and slotCount the scene stores (a runtime override of them does not move the yardstick).
"""
import hashlib
import json
import os
import random
import re

from usk_eval import verifylib as V

COMMENT_RE = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
SCRIPT_RE = re.compile(r"\.cs(\.meta)?$")
FIELDS = {"maxStack": "int", "slotCount": "int", "stacks": "List<Stack>"}
METHODS = {"CountOf": ("int", ("string",)), "Add": ("int", ("string", "int")), "Remove": ("int", ("string", "int"))}
SYSTEM_TYPES = {"string": "System.String", "int": "System.Int32"}
ITEMS = ("Ore", "Gem", "Herb", "Wood", "Iron", "Salt", "Silk", "Coal")
MAX_STACKS = (12, 15, 20)
SLOT_COUNTS = (4, 5)
FIRST_COUNTS = (3, 5, 7)
SECOND_COUNTS = (2, 4, 6)
SPEC_SALT = 0x5C01

ASSERTIONS = [
    ("K1", 0.75, False, ["R1"], "the script keeps its path and GUID and its component is still the only one, on the "
                                "given object"),
    ("K2", 1.0, False, ["R2"], "public API unchanged: class, Stack, fields, event Changed, CountOf/Add/Remove "
                               "signatures, no other public member"),
    ("K3", 0.5, False, ["R1"], "compilation finished without errors for the project and both scripts"),
    ("K4", 0.75, True, ["R7"], "the watcher script is unchanged (content and GUID) and no code file was added"),
    ("K5", 0.75, True, ["R7"], "the component's stored maxStack, slotCount and stacks, the watcher component and every "
                               "other object are unchanged in the scene"),
    ("K6", 3.0, False, ["R3", "R5", "R6", "R8"], "in Play Mode without errors every Add returns, stores and raises "
                                                 "Changed as specified (fraction of the checks)"),
    ("K7", 2.25, False, ["R4", "R5", "R6", "R8"], "in Play Mode without errors every Remove returns, removes and "
                                                  "raises Changed as specified and CountOf totals the stacks "
                                                  "(fraction of the checks)"),
    ("K8", 0.5, False, ["R9", "G8"], "the answer reports the script and the object carrying the inventory"),
    ("K9", 0.25, True, ["G1", "G2"], "no change outside R/A, no other scene saved or opened"),
    ("K10", 0.25, False, ["G4"], "no file other than C# scripts was written under A around the skills"),
]

HINTS = {
    "K1": {"zh-CN": "就地修改原脚本（保持路径和 GUID），不要删除重建；场景里的组件要保持挂载。",
           "en": "Edit the original script in place (same path and GUID), do not delete and recreate it; the scene "
                 "component must stay attached."},
    "K2": {"zh-CN": "公开接口保持原样：类名、Stack、三个字段、Changed 事件和三个方法的签名都不能变，也不要新增 public 成员。",
           "en": "Keep the public API as it is: class name, Stack, the three fields, the Changed event and the three "
                 "method signatures; add no public member."},
    "K3": {"zh-CN": "等编译完成并确认没有错误。", "en": "Wait for compilation to finish without errors."},
    "K4": {"zh-CN": "不要修改观察者脚本，也不要新增代码文件。",
           "en": "Do not modify the watcher script and do not add code files."},
    "K5": {"zh-CN": "不要改动场景里保存的 maxStack、slotCount、stacks，也不要改动其他对象。",
           "en": "Do not change the stored maxStack, slotCount and stacks, or any other object."},
    "K6": {"zh-CN": "Add：count 不大于 0 时什么都不做；先按顺序补满已有堆叠（上限 maxStack），再在有空位时追加新堆叠；返回放不下的数量；存入至少 1 个时 Changed 恰好一次。要用场景中的 maxStack/slotCount，而不是示例里的数字。",
           "en": "Add: nothing for counts of 0 or less; top up the item's stacks in order to maxStack, then append "
                 "new stacks while slots are free; return what did not fit; raise Changed exactly once when anything "
                 "was stored. Use the component's maxStack/slotCount, not the example's numbers."},
    "K7": {"zh-CN": "Remove：从该物品最后一个堆叠向前移除，删除变为 0 的堆叠，返回实际移除量，移除了东西就触发 Changed 恰好一次；CountOf 返回总数。",
           "en": "Remove: take from the item's last stack backwards, delete stacks that reach 0, return what was "
                 "removed and raise Changed exactly once when anything was removed; CountOf returns the total."},
    "K8": {"zh-CN": "答案中的路径和 id 要与实际状态一致，并写明 declaredComplete。",
           "en": "Report the actual path and id, and declaredComplete."},
    "K9": {"zh-CN": "所有修改都必须在 R 与 A 之内，且只能保存本轮场景。",
           "en": "Keep every change inside R and A, and save no scene but this run's."},
    "K10": {"zh-CN": "除 C# 脚本外，不要绕过技能直接在 A 下写文件。",
            "en": "Apart from C# scripts, do not write files under A directly; use the skills."},
}


def spec(seed):
    """Verbatim copy of the public task's spec(seed) (a test pins the two together)."""
    rng = random.Random(seed ^ SPEC_SALT)
    items = rng.sample(ITEMS, 4)
    initial = [[items[0], rng.choice(FIRST_COUNTS)]]
    if rng.random() < 0.5:
        second = [items[3], rng.choice(SECOND_COUNTS)]
        initial = [second] + initial if rng.random() < 0.5 else initial + [second]
    return {"maxStack": rng.choice(MAX_STACKS), "slotCount": rng.choice(SLOT_COUNTS), "initial": initial,
            "main": items[0], "fresh": items[1:3]}


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def _read(ctx, skill, **args):
    """Result of a read-only skill, or None when it answers with a business error."""
    envelope = ctx.probe.c.read_raw(skill, **args)
    if not isinstance(envelope, dict) or envelope.get("status") in ("error", "partial") or envelope.get("errorCode"):
        return None
    result = envelope.get("result", envelope)
    return result if isinstance(result, dict) and result.get("success") is not False else None


def script_text(ctx, path):
    content = (_read(ctx, "script_read", scriptPath=path) or {}).get("content")
    return content if isinstance(content, str) else None


def compiled(ctx):
    """True when no compilation is running and the last one ended without errors."""
    comp = _read(ctx, "debug_check_compilation") or {}
    last = comp.get("lastCompilation") or {}
    return not comp.get("isCompiling") and last.get("succeeded") is not False and not last.get("errorCount")


def holders(ctx, cls):
    found = _read(ctx, "gameobject_find", component=cls, limit=500) or {}
    return [V.object_id(obj) for obj in found.get("objects") or []]


# ---------------------------------------------------------------- the specification as a model


def apply_call(stacks, max_stack, slots, op, item, count):
    """(return value, stacks after, Changed raises) of one call on `stacks` ([[item, count]]) as R3-R5 specify."""
    stacks = [[name, number] for name, number in stacks]
    if op == "CountOf":
        return sum(number for name, number in stacks if name == item), stacks, 0
    if op == "Add":
        if count <= 0:
            return 0, stacks, 0
        left = count
        for row in stacks:
            if left and row[0] == item and row[1] < max_stack:
                take = min(max_stack - row[1], left)
                row[1] += take
                left -= take
        while left and len(stacks) < slots:
            take = min(max_stack, left)
            stacks.append([item, take])
            left -= take
        return left, stacks, 1 if left < count else 0
    if count <= 0 or not any(name == item for name, _number in stacks):
        return 0, stacks, 0
    left, touched = count, set()
    for index in reversed(range(len(stacks))):
        if left and stacks[index][0] == item:
            take = min(stacks[index][1], left)
            stacks[index][1] -= take
            left -= take
            touched.add(index)
    kept = [row for index, row in enumerate(stacks) if not (index in touched and row[1] == 0)]
    return count - left, kept, 1 if left < count else 0


def plan(ctx):
    """The calls of the scenario ([(op, item, count)]), chosen on the reference trajectory from the stored state so that
    each one exercises its rule: top-up plus a new stack, zero and negative counts, topping up the second stack,
    filling every free slot with leftover, a full inventory, partial and spanning removals from the back, removals of
    nothing, and new stacks after removals; then three CountOf calls."""
    values = spec(ctx.seed)
    size, slots = values["maxStack"], values["slotCount"]
    main, (second, third) = values["main"], values["fresh"]
    stacks = [list(row) for row in values["initial"]]
    first = next(number for name, number in stacks if name == main)
    calls = [("Add", main, size - first + 3), ("Add", second, 0), ("Add", main, -4), ("Add", main, 2)]
    for op, item, count in calls:
        stacks = apply_call(stacks, size, slots, op, item, count)[1]
    free = slots - len(stacks)
    fill = [("Add", second, free * size + 1 + (size + slots) % 4), ("Add", third, 6), ("Add", main, size),
            ("Remove", main, 3), ("Remove", main, size), ("Remove", third, 5), ("Remove", second, 0)]
    for op, item, count in fill:
        stacks = apply_call(stacks, size, slots, op, item, count)[1]
    held = apply_call(stacks, size, slots, "CountOf", second, 0)[0]
    tail = [("Remove", second, held + 7), ("Add", third, 2 * size + 1)]
    return calls + fill + tail + [("CountOf", main, 0), ("CountOf", third, 0), ("CountOf", second, 0)]


def _layout(ctx):
    """[(op, item, count, invoke step index, inventory read index or None, watcher read index or None)] and the
    scenario steps; the first inventory and watcher reads are steps 1 and 2."""
    h = ctx.handles
    cls, watcher = class_name(h["script"]), class_name(h["watcherScript"])
    where = V.address(h["chest"])
    read_inventory = {"kind": "read", "skill": "component_get_serialized_properties",
                      "args": dict({"componentType": cls}, **where)}
    read_watcher = {"kind": "read", "skill": "component_get_serialized_properties",
                    "args": dict({"componentType": watcher}, **where)}
    steps, rows = [{"kind": "wait_frames", "frames": 2}, read_inventory, read_watcher], []
    for op, item, count in plan(ctx):
        arguments = [item] if op == "CountOf" else [item, count]
        types = [SYSTEM_TYPES["string"]] if op == "CountOf" else [SYSTEM_TYPES["string"], SYSTEM_TYPES["int"]]
        steps.append({"kind": "invoke", "skill": "component_invoke_method",
                      "args": dict({"componentType": cls, "methodName": op, "arguments": json.dumps(arguments),
                                    "parameterTypes": json.dumps(types)}, **where)})
        if op == "CountOf":
            rows.append((op, item, count, len(steps) - 1, None, None))
            continue
        steps += [dict(read_inventory), dict(read_watcher)]
        rows.append((op, item, count, len(steps) - 3, len(steps) - 2, len(steps) - 1))
    return rows, steps


def SCENARIO(ctx):
    """Play Mode: the inventory and watcher reads, then every planned call followed by both reads (CountOf calls
    alone). With compile errors Unity refuses Play Mode, so the scenario is then a single edit-mode check that fails
    at once."""
    check = {"kind": "assert", "skill": "editor_get_state", "select": "isPlaying", "expected": True}
    if not compiled(ctx):
        return {"executionMode": "edit", "timeoutSeconds": 30, "steps": [check]}
    _rows, steps = _layout(ctx)
    return {"executionMode": "play", "timeoutSeconds": 180, "steps": steps + [check]}


def _properties(step):
    actual = (step or {}).get("actual")
    rows = actual.get("properties") if isinstance(actual, dict) else None
    return {row.get("propertyPath"): row.get("value") for row in rows or [] if isinstance(row, dict)} if rows else None


def inventory_state(step):
    """{maxStack, slotCount, stacks: [[item, count]]} from a serialized read, or None."""
    values = _properties(step)
    if values is None:
        return None
    size = V.as_int(values.get("stacks.Array.size"))
    if size is None:
        return None
    stacks = []
    for index in range(size):
        item = values.get("stacks.Array.data[%d].item" % index)
        count = V.as_int(values.get("stacks.Array.data[%d].count" % index))
        stacks.append([item if isinstance(item, str) else None, count])
    return {"maxStack": V.as_int(values.get("maxStack")), "slotCount": V.as_int(values.get("slotCount")),
            "stacks": stacks}


def notifications(step):
    values = _properties(step)
    return V.as_int(values.get("notifications")) if values is not None else None


def grade(ctx, scenario):
    """{"Add": [...], "Remove": [...]} check rows for the recorded steps: each call against the state read just before
    it, with the maxStack and slotCount stored in the scene (spec of the seed, as at issue)."""
    values = spec(ctx.seed)
    size, slots = values["maxStack"], values["slotCount"]
    rows, _steps = _layout(ctx)
    by_index = {step.get("index"): step for step in scenario.get("steps") or [] if isinstance(step, dict)}
    state, seen = inventory_state(by_index.get(1)), notifications(by_index.get(2))
    out = {"Add": [], "Remove": []}
    for op, item, count, call, after, counter in rows:
        group = "Add" if op == "Add" else "Remove"
        names = ("value",) if op == "CountOf" else ("value", "stacks", "changed")
        if state is None:
            out[group] += [{"call": [op, item, count], "check": name, "pass": False} for name in names]
            continue
        record = by_index.get(call)
        actual = record.get("actual") if isinstance(record, dict) and isinstance(record.get("actual"), dict) else {}
        value = V.as_int(actual.get("returnValue")) if "returnValue" in actual else None
        want, stacks, raised = apply_call(state["stacks"], size, slots, op, item, count)
        out[group].append({"call": [op, item, count], "check": "value", "pass": value is not None and value == want,
                           "expected": want, "actual": value})
        if op == "CountOf":
            continue
        now, total = inventory_state(by_index.get(after)), notifications(by_index.get(counter))
        out[group].append({"call": [op, item, count], "check": "stacks",
                           "pass": now is not None and now["stacks"] == stacks, "expected": stacks,
                           "actual": now["stacks"] if now else None})
        delta = total - seen if total is not None and seen is not None else None
        out[group].append({"call": [op, item, count], "check": "changed", "pass": delta == raised,
                           "expected": raised, "actual": delta})
        state, seen = now, total
    return out


def verify(ctx, card):
    p, h, c0 = ctx.probe, ctx.handles, ctx.c0
    cls, watcher = class_name(h["script"]), class_name(h["watcherScript"])
    guids = c0.get("guids") or {}

    found = holders(ctx, cls)
    guid = p.guid(h["script"])
    card.check("K1", guid is not None and guid == guids.get(h["script"]) and len(found) == 1 and
               V.same_id(found[0], h["chest"]) and p.component_count(h["chest"], watcher) == 1,
               expected={"guid": guids.get(h["script"]), "holders": [h["chest"]]},
               actual={"guid": guid, "holders": found})

    text = COMMENT_RE.sub("", script_text(ctx, h["script"]) or "")
    api = _read(ctx, "script_analyze", scriptName=cls) or {}
    fields = {row.get("name"): row.get("type") for row in api.get("fields") or [] if isinstance(row, dict)}
    methods = {}
    for row in api.get("methods") or []:
        if isinstance(row, dict):
            params = tuple(part.strip().split()[0] for part in str(row.get("parameters") or "").split(",")
                           if part.strip())
            methods.setdefault(row.get("name"), []).append((row.get("returnType"), params))
    shape = {"fullName": api.get("fullName") == cls and api.get("kind") == "MonoBehaviour",
             "fields": fields == FIELDS, "properties": not api.get("properties"),
             "methods": methods == {name: [sig] for name, sig in METHODS.items()},
             "event": re.search(r"\bpublic\s+event\s+(?:System\.)?Action\s+Changed\s*[;=]", text) is not None,
             "stack": re.search(r"\[\s*(?:System\.)?Serializable\s*\]\s*public\s+class\s+Stack\b", text) is not None and
             re.search(r"\bpublic\s+string\s+item\s*;", text) is not None and
             re.search(r"\bpublic\s+int\s+count\s*;", text) is not None}
    card.check("K2", all(shape.values()), expected={"fields": FIELDS, "methods": METHODS},
               actual=dict(shape, analyzed={"fields": fields, "methods": methods,
                                            "properties": api.get("properties")}))

    feedback = [(_read(ctx, "script_get_compile_feedback", scriptPath=path) or {}).get("hasErrors")
                for path in (h["script"], h["watcherScript"])]
    card.check("K3", compiled(ctx) and feedback == [False, False], actual={"compiled": compiled(ctx),
                                                                            "hasErrors": feedback})

    watcher_text = script_text(ctx, h["watcherScript"])
    digest = hashlib.sha256(watcher_text.encode("utf-8")).hexdigest() if isinstance(watcher_text, str) else None
    added = sorted(set(cs_files(ctx.project_path)) - set(c0.get("scripts") or []))
    watcher_guid = p.guid(h["watcherScript"])
    card.check("K4", digest is not None and digest == c0.get("watcherHash") and
               watcher_guid == guids.get(h["watcherScript"]) and not added,
               expected={"watcherHash": c0.get("watcherHash"), "added": []},
               actual={"watcherHash": digest, "watcherGuid": watcher_guid, "added": added})

    stored = {key: value for key, value in (p.ser(h["chest"], cls) or {}).items()
              if key in ("maxStack", "slotCount") or key.startswith("stacks.")}
    watched = {key: value for key, value in (p.ser(h["chest"], watcher) or {}).items()
               if key in ("inventory", "notifications")}
    diffs = []
    for ref, state in c0.get("states") or []:
        ok, diff = V.same(state, V.go_state(p, ref))
        if not ok:
            diffs.append({"ref": ref, "diff": diff})
    same_values = V.same(c0.get("inventory"), stored)[0] and V.same(c0.get("watcher"), watched)[0]
    card.check("K5", same_values and not diffs, expected={"inventory": c0.get("inventory"),
                                                          "watcher": c0.get("watcher")},
               actual={"inventory": stored, "watcher": watched, "diffs": diffs[:5]})

    carrier = found[0] if len(found) == 1 else h["chest"]
    card.check("K8", V.norm_path(V.answer(ctx, "scriptPath")) == h["script"] and
               V.same_id(V.answer(ctx, "chestInstanceId"), carrier) and
               isinstance(V.answer(ctx, "declaredComplete"), bool), actual=ctx.answer)
    bad = ctx.violations()
    card.check("K9", not bad, actual={"violations": bad[:10]})
    direct = [rec for rec in ctx.direct_writes() if not SCRIPT_RE.search(str(rec.get("assetPath") or ""))]
    card.check("K10", not direct, actual={"directWrites": direct[:10]})


def verify_runtime(ctx, card):
    scenario = ctx.scenario or {}
    passed = scenario.get("passed") is True
    checks = grade(ctx, scenario)
    for aid, group in (("K6", "Add"), ("K7", "Remove")):
        rows = checks[group]
        good = sum(1 for row in rows if row["pass"])
        card.fraction(aid, good / float(len(rows)) if passed and rows else 0.0,
                      actual={"passed": passed, "error": scenario.get("error"), "status": scenario.get("status"),
                              "failed": [row for row in rows if not row["pass"]][:8]})

# Producer:Betsy
