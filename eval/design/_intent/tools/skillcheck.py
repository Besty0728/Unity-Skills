#!/usr/bin/env python3
"""Compares every skill name and keyword argument used by pack Python files with the [UnitySkill] declarations in C#.

usage: skillcheck.py --cs ROOT [--cs ROOT2 ...] FILE.py [FILE.py ...]
The first --cs root is authoritative; later roots only add names (reported as "older tree only").
Exit 1 on an unknown skill or an unknown keyword argument.
"""
import ast
import io
import os
import re
import sys

ROUTER_ARGS = {"verbose", "pageOffset", "pageLimit"}
CALLERS = {"write", "read", "read_raw", "call"}
SKILL_LIKE = re.compile(r"^(prefab|material|gameobject|component|scene|asset|batch|ui|uitk|physics|editor|project|light|"
                        r"camera|script|scriptableobject|event|model|audio|animator|terrain|navmesh|package|test|"
                        r"cleaner|debug|console|importer|shader|timeline|volume|postprocess|urp|xr)_[a-z0-9_]+$")


def split_top(text, sep=","):
    parts, depth, cur, i, n = [], 0, [], 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            cur.append(text[i:j + 1])
            i = j + 1
            continue
        if ch in "([{<":
            depth += 1
        elif ch in ")]}>":
            if not (ch == ">" and i > 0 and text[i - 1] == "="):
                depth -= 1
        if ch == sep and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    if "".join(cur).strip():
        parts.append("".join(cur))
    return parts


def matching_paren(text, start):
    depth, i, n = 0, start, len(text)
    while i < n:
        ch = text[i]
        if ch == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            i = j + 1
            continue
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def strip_attrs(seg):
    seg = seg.strip()
    while seg.startswith("["):
        depth, i = 0, 0
        while i < len(seg):
            ch = seg[i]
            if ch == '"':
                j = i + 1
                while j < len(seg) and seg[j] != '"':
                    j += 2 if seg[j] == "\\" else 1
                i = j + 1
                continue
            if ch == "[":
                depth += 1
            elif ch == "]":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        seg = seg[i + 1:].strip()
    return seg


def load_cs(root):
    skills = {}
    sig_re = re.compile(r"public\s+static\s+[\w<>\[\],\.\s\?]+?\s+(\w+)\s*\(")
    attr_re = re.compile(r'\[UnitySkill\(\s*"([A-Za-z0-9_]+)"')
    for dirpath, _dirs, files in os.walk(root):
        for fname in sorted(files):
            if not fname.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fname)
            text = io.open(path, encoding="utf-8-sig", errors="replace").read()
            for m in attr_re.finditer(text):
                sm = sig_re.search(text, m.end())
                if not sm:
                    continue
                open_idx = sm.end() - 1
                close_idx = matching_paren(text, open_idx)
                params = []
                for seg in split_top(text[open_idx + 1:close_idx]):
                    seg = strip_attrs(seg)
                    if not seg:
                        continue
                    left = split_top(seg, "=")[0].strip() if "=" in seg else seg
                    mm = re.findall(r"[A-Za-z_]\w*", left)
                    if mm:
                        params.append(mm[-1])
                line = text.count("\n", 0, m.start()) + 1
                skills[m.group(1)] = {"params": params, "file": fname, "line": line}
    return skills


def address_expansion(node):
    """Names produced by **address(x[, prefix]) / **V.address(...) / **dict(...) of such, or None if unknown."""
    if isinstance(node, ast.Call):
        func = node.func
        fname = func.attr if isinstance(func, ast.Attribute) else getattr(func, "id", None)
        if fname == "address":
            prefix = ""
            if len(node.args) > 1 and isinstance(node.args[1], ast.Constant) and isinstance(node.args[1].value, str):
                prefix = node.args[1].value
            for kw in node.keywords:
                if kw.arg == "prefix" and isinstance(kw.value, ast.Constant):
                    prefix = kw.value.value
            if prefix:
                return {prefix + "InstanceId", prefix + "EntityId"}
            return {"instanceId", "entityId"}
        if fname == "dict":
            names = set()
            for arg in node.args:
                sub = address_expansion(arg)
                if sub is None:
                    return None
                names |= sub
            for kw in node.keywords:
                if kw.arg is None:
                    sub = address_expansion(kw.value)
                    if sub is None:
                        return None
                    names |= sub
                else:
                    names.add(kw.arg)
            return names
    if isinstance(node, ast.Dict):
        names = set()
        for key, value in zip(node.keys, node.values):
            if key is None:
                sub = address_expansion(value)
                if sub is None:
                    return None
                names |= sub
            elif isinstance(key, ast.Constant) and isinstance(key.value, str):
                names.add(key.value)
            else:
                return None
        return names
    return None


def accepted(skill, name):
    if name in skill["params"] or name in ROUTER_ARGS:
        return True
    if name == "entityId" and "instanceId" in skill["params"]:
        return True
    if name.endswith("EntityId") and name[:-8] + "InstanceId" in skill["params"]:
        return True
    return False


def check_file(path, primary, secondary, report):
    src = io.open(path, encoding="utf-8-sig").read()
    tree = ast.parse(src, path)
    problems = 0
    calls = []

    def lookup(name):
        if name in primary:
            return primary[name], ""
        if name in secondary:
            return secondary[name], "older tree only"
        return None, ""

    def note(skill_name, kwargs_names, line, unknown_star):
        nonlocal problems
        skill, flag = lookup(skill_name)
        if skill is None:
            report.append("%s:%d UNKNOWN SKILL %s" % (path, line, skill_name))
            problems += 1
            return
        if flag:
            report.append("%s:%d skill %s exists in the %s" % (path, line, skill_name, flag))
            problems += 1
        bad = [k for k in sorted(kwargs_names) if not accepted(skill, k)]
        if bad:
            report.append("%s:%d skill %s: unknown parameter(s) %s (declared: %s)" % (
                path, line, skill_name, ", ".join(bad), ", ".join(skill["params"])))
            problems += 1
        calls.append((skill_name, skill["file"], skill["line"], line, unknown_star))

    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and node.func.attr in CALLERS:
            if node.args and isinstance(node.args[0], ast.Constant) and isinstance(node.args[0].value, str):
                if not re.fullmatch(r"[a-z][a-z0-9]*(?:_[a-z0-9]+)+", node.args[0].value):
                    continue  # file.write("text\n") and the like: not a skill name (every skill name has an underscore)
                names, unknown = set(), False
                for kw in node.keywords:
                    if kw.arg is None:
                        sub = address_expansion(kw.value)
                        if sub is None:
                            unknown = True
                        else:
                            names |= sub
                    else:
                        names.add(kw.arg)
                note(node.args[0].value, names, node.lineno, unknown)
            elif node.func.attr in ("write", "read", "read_raw") and node.args:
                report.append("%s:%d dynamic skill name (not checked): %s" % (
                    path, node.lineno, ast.get_source_segment(src, node.args[0])))
        elif isinstance(node, ast.Dict):
            keys = {k.value: v for k, v in zip(node.keys, node.values) if isinstance(k, ast.Constant)}
            if "skill" in keys and isinstance(keys["skill"], ast.Constant) and isinstance(keys["skill"].value, str):
                names, unknown = set(), False
                args_node = keys.get("args")
                if isinstance(args_node, ast.Dict):
                    sub = address_expansion(args_node)
                    if sub is None:
                        unknown = True
                    else:
                        names |= sub
                elif args_node is not None:
                    unknown = True
                note(keys["skill"].value, names, node.lineno, unknown)
    known_used = {c[0] for c in calls}
    for node in ast.walk(tree):
        if isinstance(node, ast.Constant) and isinstance(node.value, str) and SKILL_LIKE.match(node.value):
            if node.value not in primary and node.value not in secondary and node.value not in known_used:
                report.append("%s:%d note: skill-like string %r is not a declared skill (journal kind? check by hand)"
                              % (path, node.lineno, node.value))
    return problems, calls


def main(argv):
    roots, files, i = [], [], 1
    while i < len(argv):
        if argv[i] == "--cs":
            roots.append(argv[i + 1])
            i += 2
        else:
            files.append(argv[i])
            i += 1
    if not roots or not files:
        print(__doc__)
        return 2
    primary = load_cs(roots[0])
    secondary = {}
    for root in roots[1:]:
        secondary.update(load_cs(root))
    print("declared skills: %d primary (%s), %d in older trees" % (len(primary), roots[0], len(secondary)))
    total, used = 0, {}
    for path in files:
        report = []
        problems, calls = check_file(path, primary, secondary, report)
        total += problems
        for line in report:
            print(line)
        for name, cs_file, cs_line, _line, _unknown in calls:
            used[name] = (cs_file, cs_line)
    print("skills used (%d):" % len(used))
    for name in sorted(used):
        print("  %s  <- %s:%d" % (name, used[name][0], used[name][1]))
    print("skillcheck: %s (%d problem(s))" % ("OK" if total == 0 else "FAIL", total))
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
