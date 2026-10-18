"""DC1 solutions: reference, negatives (each with the exact set of assertions it must fail) and alternatives.

The reference replaces the Add and Remove methods of the fixture script with script_replace. Each wrong variant also
carries a `// usk-fake: <variant>` comment, which the offline fake editor reads to model the same wrong behaviour (a
real editor ignores the comment).
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import Negative

BLOCK_START = "    public int Add(string item, int count)\n"
BLOCK_END = "    void RaiseChanged()\n"

ADD_SRC = """    public int Add(string item, int count)
    {{{marker}
        if (count <= 0) return 0;
        int left = count;{merge}
        foreach (var stack in stacks)
        {{
            if (left == 0) break;
            if (stack.item != item || stack.count >= {cap}) continue;
            int take = Math.Min({cap} - stack.count, left);
            stack.count += take;
            left -= take;{each_add}
        }}
        while (left > 0 && stacks.Count < slotCount)
        {{
            int take = Math.Min({cap}, left);
            stacks.Add(new Stack {{ item = item, count = take }});
            left -= take;{each_add}
        }}
        {once_add}return left;
    }}

"""

REMOVE_SRC = """    public int Remove(string item, int count)
    {{
        if (count <= 0) return 0;
        int left = count;
{loop}
        {once_remove}return count - left;
    }}

"""

BACKWARDS = """        for (int i = stacks.Count - 1; i >= 0 && left > 0; i--)
        {{
            var stack = stacks[i];
            if (stack.item != item) continue;
            int taken = Math.Min(left, stack.count);
            stack.count -= taken;
            left -= taken;{each_remove}
            if (stack.count == 0) stacks.RemoveAt(i);
        }}"""

FORWARDS = """        int i = 0;
        while (i < stacks.Count && left > 0)
        {{
            var stack = stacks[i];
            if (stack.item != item) {{ i++; continue; }}
            int taken = Math.Min(left, stack.count);
            stack.count -= taken;
            left -= taken;{each_remove}
            if (stack.count == 0) stacks.RemoveAt(i);
            else i++;
        }}"""

MERGE = """
        var first = stacks.Find(s => s.item == item);
        if (first != null)
        {
            first.count += count;
            RaiseChanged();
            return 0;
        }"""

HELPERS_SRC = """    public int Add(string item, int count)
    {
        if (count <= 0) return 0;
        int left = Append(item, TopUp(item, count));
        if (left < count) RaiseChanged();
        return left;
    }

    public int Remove(string item, int count)
    {
        if (count <= 0) return 0;
        int left = TakeFromBack(item, count);
        if (left < count) RaiseChanged();
        return count - left;
    }

    private int TopUp(string item, int left)
    {
        foreach (var stack in stacks)
        {
            if (left == 0) break;
            if (stack.item != item || stack.count >= maxStack) continue;
            int take = Math.Min(maxStack - stack.count, left);
            stack.count += take;
            left -= take;
        }
        return left;
    }

    private int Append(string item, int left)
    {
        while (left > 0 && stacks.Count < slotCount)
        {
            int take = Math.Min(maxStack, left);
            stacks.Add(new Stack { item = item, count = take });
            left -= take;
        }
        return left;
    }

    private int TakeFromBack(string item, int left)
    {
        for (int i = stacks.Count - 1; i >= 0 && left > 0; i--)
        {
            if (stacks[i].item != item) continue;
            int taken = Math.Min(left, stacks[i].count);
            stacks[i].count -= taken;
            left -= taken;
            if (stacks[i].count == 0) stacks.RemoveAt(i);
        }
        return left;
    }

"""

HELPER_SCRIPT = """using UnityEngine;

public class {cls} : MonoBehaviour
{{
    public int bonus = 1;
}}
"""


def _cls(ctx):
    return ctx.private.class_name(ctx.handles["script"])


def methods(variant=None, cap="maxStack", merge="", each_add="", once_add="if (left < count) RaiseChanged();\n        ",
            loop=None, each_remove="", once_remove="if (left < count) RaiseChanged();\n        "):
    """The Add and Remove methods; the defaults are the specified behaviour."""
    marker = "\n        // usk-fake: %s" % variant if variant else ""
    add = ADD_SRC.format(marker=marker, merge=merge, cap=cap, each_add=each_add, once_add=once_add)
    remove = REMOVE_SRC.format(loop=(loop or BACKWARDS).format(each_remove=each_remove), once_remove=once_remove)
    return add + remove


def _current(ctx):
    return ctx.c.read("script_read", scriptPath=ctx.handles["script"]).get("content")


def _wait(ctx, res):
    if isinstance(res, dict) and res.get("jobId"):
        ctx.wait_job(res["jobId"])
    ctx.wait_idle()


def _replace_block(ctx, block):
    """script_replace of the fixture's Add and Remove methods (everything from Add up to RaiseChanged) by `block`."""
    text = _current(ctx)
    start, end = text.index(BLOCK_START), text.index(BLOCK_END)
    _wait(ctx, ctx.c.write("script_replace", scriptPath=ctx.handles["script"], find=text[start:end], replace=block))


def _answer(ctx):
    return {"scriptPath": ctx.handles["script"], "chestInstanceId": _number(V.object_id(ctx.handles["chest"])),
            "declaredComplete": True}


def _number(ident):
    return int(ident) if isinstance(ident, str) else ident


def _solve(ctx, block):
    _replace_block(ctx, block)
    return _answer(ctx)


def _write_file(ctx, asset_path, data):
    """Writes a file under A on disk, bypassing the skills (the runner's refresh imports it at submit)."""
    target = os.path.join(ctx.project_path, *asset_path.split("/"))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as handle:
        handle.write(data)


# ---------------------------------------------------------------- reference and alternatives


def reference(ctx):
    return _solve(ctx, methods())


def rewrite_file(ctx):
    """The whole fixed file written straight to disk (same path, the .meta untouched, so the GUID stays), then an
    asset_refresh that compiles it."""
    text = _current(ctx)
    start, end = text.index(BLOCK_START), text.index(BLOCK_END)
    _write_file(ctx, ctx.handles["script"], (text[:start] + methods() + text[end:]).encode("utf-8"))
    _wait(ctx, ctx.c.write("asset_refresh"))
    return _answer(ctx)


def private_helpers(ctx):
    """The same behaviour split into private helper methods (private members may be added)."""
    return _solve(ctx, HELPERS_SRC)


def save_run_scene(ctx):
    answer = reference(ctx)
    ctx.c.write("scene_save")
    return answer


# ---------------------------------------------------------------- negatives


def overflow_merge(ctx):
    """Merges into the item's first stack without the cap, like the original."""
    return _solve(ctx, methods("overflow-merge", merge=MERGE))


def negative_add(ctx):
    """Only 0 is refused: a negative count runs through the top-up loop and drains the first stack below maxStack."""
    return _solve(ctx, methods("negative-add").replace("if (count <= 0) return 0;\n        int left = count;\n"
                                                       "        foreach",
                                                       "if (count == 0) return 0;\n        int left = count;\n"
                                                       "        foreach", 1))


def example_only(ctx):
    """The cap is the prompt example's 10 instead of maxStack."""
    return _solve(ctx, methods("example-only", cap="10"))


def remove_silent(ctx):
    return _solve(ctx, methods("remove-silent", once_remove=""))


def remove_front(ctx):
    return _solve(ctx, methods("remove-front", loop=FORWARDS))


def event_per_stack(ctx):
    """Changed once per stack touched instead of once per call."""
    return _solve(ctx, methods("event-per-stack", each_add="\n            RaiseChanged();", once_add="",
                               each_remove="\n            if (taken > 0) RaiseChanged();", once_remove=""))


def signature_changed(ctx):
    """Add takes a uint count (to refuse negatives by type): the public signature changes."""
    block = methods().replace("    public int Add(string item, int count)\n    {\n",
                              "    public int Add(string item, uint amount)\n    {\n        int count = (int)amount;\n", 1)
    return _solve(ctx, block)


def event_as_field(ctx):
    """The event becomes a plain public delegate field (the watcher still compiles)."""
    answer = reference(ctx)
    _wait(ctx, ctx.c.write("script_replace", scriptPath=ctx.handles["script"], find="public event Action Changed;",
                           replace="public Action Changed;"))
    return answer


def watcher_edited(ctx):
    answer = reference(ctx)
    _wait(ctx, ctx.c.write("script_replace", scriptPath=ctx.handles["watcherScript"], find="    void Count()\n",
                           replace="    // counts Changed\n    void Count()\n"))
    return answer


def scene_values_changed(ctx):
    """The scene's maxStack set to the example's 10."""
    answer = reference(ctx)
    ctx.c.write("component_set_property", componentType=_cls(ctx), propertyName="maxStack", value="10",
                **V.address(ctx.handles["chest"]))
    return answer


def compile_error(ctx):
    return _solve(ctx, methods().replace("        if (count <= 0) return 0;\n        int left = count;\n",
                                         "        if (count <= 0) return 0; {\n        int left = count;\n", 1))


def extra_script(ctx):
    answer = reference(ctx)
    name = "StackHelper_" + _cls(ctx).split("_", 1)[1]
    _wait(ctx, ctx.c.write("script_create", scriptName=name, folder=os.path.dirname(ctx.handles["script"]),
                           content=HELPER_SCRIPT.format(cls=name)))
    return answer


def wrong_report(ctx):
    answer = reference(ctx)
    answer["chestInstanceId"] = _number(V.object_id(ctx.handles["area"]))
    return answer


def outside_root(ctx):
    answer = reference(ctx)
    ctx.c.write("gameobject_create", name="StrayCrate")
    return answer


def direct_file(ctx):
    answer = reference(ctx)
    _write_file(ctx, ctx.asset("Data", "stacks.txt"), b"fixed\n")
    return answer


# There is no delete-and-create negative: Unity gives a script created again at the path of the one just deleted the
# GUID the old file had, within the same session, so the component stays attached and nothing breaks (the live gate on
# 6000.3 passed it). DC3's delete_recreate creates the file at another path, which is a new asset with a new GUID.
NEGATIVES = [
    Negative("overflow_merge", overflow_merge, {"K6"}, "merging into the first stack ignores maxStack"),
    Negative("negative_add", negative_add, {"K6"}, "a negative Add drains a stack"),
    Negative("example_only", example_only, {"K6"}, "the cap is the example's 10"),
    Negative("remove_silent", remove_silent, {"K7"}, "Remove never raises Changed"),
    Negative("remove_front", remove_front, {"K7"}, "Remove takes from the first stack"),
    Negative("event_per_stack", event_per_stack, {"K6", "K7"}, "Changed once per stack touched"),
    Negative("signature_changed", signature_changed, {"K2", "K6", "K7"}, "Add(string, uint): no Add(string, int)"),
    Negative("event_as_field", event_as_field, {"K2"}, "Changed is a public delegate field, no longer an event"),
    Negative("watcher_edited", watcher_edited, {"K4"}, "the watcher script edited"),
    Negative("scene_values_changed", scene_values_changed, {"K5", "K6"}, "maxStack in the scene set to 10"),
    Negative("compile_error", compile_error, {"K3", "K6", "K7"}, "the edit does not compile"),
    Negative("extra_script", extra_script, {"K4"}),
    Negative("wrong_report", wrong_report, {"K8"}),
    Negative("outside_root", outside_root, {"K9"}),
    Negative("direct_file", direct_file, {"K10"}, "a non-script file written to disk next to the skills"),
]

ALTERNATIVES = [("rewrite_file", rewrite_file), ("private_helpers", private_helpers), ("save_run_scene", save_run_scene)]

# Producer:Betsy
