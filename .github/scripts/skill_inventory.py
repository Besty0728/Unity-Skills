#!/usr/bin/env python3
"""Skill inventory and C# <-> module-doc consistency audit (backs /skillcheck).

Reads every [UnitySkill(...)] in SkillsForUnity/Editor/Skills/*Skills.cs and every module doc under
SkillsForUnity/unity-skills~/skills/, runs the mechanical cross-checks, and reports findings by severity.
Counts are computed, never hard-coded; --sync-counts rewrites the count anchors in AGENTS.md and the READMEs.

Registries that already live in SkillDocumentationConsistencyTests.cs (advisory modules, category exceptions,
pilot modules, byte budgets, known duplicates / broken links) are parsed from that file, not copied here.

    python3 .github/scripts/skill_inventory.py [repo root] [--json] [--all] [--sync-counts]

Exit code 1 when any error-level finding exists (or --sync-counts could not fix a drift).
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass, field

SKILLS_DIR = "SkillsForUnity/Editor/Skills"
PACKAGE_DIR = "SkillsForUnity/unity-skills~"
DOCS_DIR = PACKAGE_DIR + "/skills"
ROOT_DOC = "SKILL.md"  # the only SKILL.md; discovery registers it
MODULE_DOC = "MODULE.md"  # per-module entry under skills/<module>/
INDEX_DOC = "INDEX.md"  # module index under skills/
TEST_FILE = "SkillsForUnity/Tests/Editor/Core/SkillDocumentationConsistencyTests.cs"
ROOT_DOC_BYTE_BUDGET = 8192
ATTRIBUTE_FILE = SKILLS_DIR + "/UnitySkillAttribute.cs"

SKILL_NAME_RE = re.compile(r"^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$")
STR_RE = re.compile(r'@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*"')
LINK_RE = re.compile(r'\[(?P<text>[^\]\n]*)\]\((?P<target>[^)\s]+)(?:\s+"[^"]*")?\)')
SEVERITIES = ("error", "warning", "info")
# Fields every success payload may carry without the contract naming them.
GENERIC_FIELDS = {"success", "error", "warnings", "message", "status"}


@dataclass
class Finding:
    severity: str
    code: str
    message: str
    where: str = ""


@dataclass
class Param:
    name: str
    type: str
    optional: bool


@dataclass
class CodeSkill:
    name: str
    file: str
    line: int
    description: str | None
    category: str
    operation: list[str]
    mode: str
    risk: str
    flags: dict[str, bool]
    tags: list[str]
    outputs: list[str] | None
    required_params: list[str]
    method: str
    params: list[Param]
    item_props: dict[str, str] | None
    return_fields: set[str] | None
    conditions: list[str]
    attribute_count: int = 1
    has_description: bool = True

    @property
    def never_in_semi(self) -> bool:
        return ("Delete" in self.operation or self.flags.get("MayEnterPlayMode", False)
                or self.flags.get("MayTriggerReload", False) or self.risk.lower() == "high")


@dataclass
class DocEntry:
    name: str
    module: str
    file: str
    line: int
    params: list[tuple[str, str]] | None
    item_props: list[str] | None
    returns: list[str] | None


# --------------------------------------------------------------------------------------------- C# lexing

def read_text(path: str) -> str:
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def mask_source(src: str) -> tuple[str, str]:
    """Return (no_comments, skeleton): comments blanked; skeleton also blanks string/char contents.

    Both keep the original offsets and newlines, so structure is found on the skeleton and text sliced from the other.
    """
    n = len(src)
    nc, sk = list(src), list(src)

    def blank(a: int, b: int, *arrays: list[str]) -> None:
        for arr in arrays:
            for k in range(a, b):
                if arr[k] not in "\r\n":
                    arr[k] = " "

    prefix_re = re.compile(r'(?:\$@|@\$|@|\$)?"')
    i = 0
    while i < n:
        c = src[i]
        two = src[i:i + 2]
        if two == "//":
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j, nc, sk)
            i = j
        elif two == "/*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j, nc, sk)
            i = j
        elif c == "#" and not src[src.rfind("\n", 0, i) + 1:i].strip():
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j, sk)
            i = j
        elif c in '@$"' and prefix_re.match(src, i):
            m = prefix_re.match(src, i)
            q = m.end() - 1
            verbatim = "@" in m.group(0)
            if src.startswith('"""', q):
                j = src.find('"""', q + 3)
                end = n if j < 0 else j + 3
                blank(q + 3, max(q + 3, end - 3), sk)
            elif verbatim:
                j = q + 1
                while j < n:
                    if src[j] == '"':
                        if src[j + 1:j + 2] == '"':
                            j += 2
                            continue
                        break
                    j += 1
                end = min(j + 1, n)
                blank(q + 1, j, sk)
            else:
                j = q + 1
                while j < n and src[j] != '"' and src[j] != "\n":
                    j += 2 if src[j] == "\\" else 1
                end = min(j + 1, n)
                blank(q + 1, j, sk)
            i = end
        elif c == "'":
            j = i + 1
            while j < n and src[j] != "'" and src[j] != "\n":
                j += 2 if src[j] == "\\" else 1
            blank(i + 1, j, sk)
            i = min(j + 1, n)
        else:
            i += 1
    return "".join(nc), "".join(sk)


def match_close(skel: str, i: int, open_ch: str, close_ch: str) -> int:
    depth = 0
    for k in range(i, len(skel)):
        ch = skel[k]
        if ch == open_ch:
            depth += 1
        elif ch == close_ch:
            depth -= 1
            if depth == 0:
                return k
    return -1


def split_top(skel: str, text: str, generics: bool = False) -> list[str]:
    """Split on top-level commas; the skeleton decides the structure, the pieces are sliced from text."""
    pieces, depth, start = [], 0, 0
    opens, closes = "([{" + ("<" if generics else ""), ")]}" + (">" if generics else "")
    for k, ch in enumerate(skel):
        if ch in opens:
            depth += 1
        elif ch in closes:
            depth -= 1
        elif ch == "," and depth == 0:
            pieces.append(text[start:k])
            start = k + 1
    tail = text[start:]
    if tail.strip():
        pieces.append(tail)
    return pieces


def unquote(lit: str) -> str:
    if lit.startswith("@"):
        return lit[2:-1].replace('""', '"')
    body = lit[1:-1]
    return re.sub(r"\\(u[0-9a-fA-F]{4}|.)", lambda m: {
        "n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "'": "'", "0": "\0"}.get(m.group(1), (
            chr(int(m.group(1)[1:], 16)) if len(m.group(1)) == 5 else m.group(1))), body)


def line_conditions(nc: str) -> list[list[str]]:
    """Active #if conditions at the start of each line (index = 0-based line)."""
    stack: list[str] = []
    result = []
    for raw in nc.split("\n"):
        stripped = raw.strip()
        result.append(list(stack))
        if stripped.startswith("#if"):
            stack.append(stripped[3:].strip())
        elif stripped.startswith("#elif") and stack:
            stack[-1] = stripped[5:].strip()
        elif stripped.startswith("#else") and stack:
            stack[-1] = "!(" + stack[-1].lstrip("!") + ")"
        elif stripped.startswith("#endif") and stack:
            stack.pop()
    return result


def parse_string_set(nc: str, skel: str, field_name: str) -> list[str]:
    body = block_after(nc, skel, field_name)
    return [unquote(s) for s in STR_RE.findall(body)] if body is not None else []


def block_after(nc: str, skel: str, field_name: str) -> str | None:
    m = re.search(r"\b" + re.escape(field_name) + r"\b\s*=", skel)
    if not m:
        return None
    start = skel.find("{", m.end())
    semicolon = skel.find(";", m.end())
    if start < 0 or 0 <= semicolon < start:
        return None
    end = match_close(skel, start, "{", "}")
    return nc[start + 1:end] if end > 0 else None


# --------------------------------------------------------------------------------------------- C# skills

def parse_attribute_args(nc_args: str, sk_args: str) -> tuple[list[str], dict[str, str]]:
    positional, named = [], {}
    for piece_nc, piece_sk in zip(split_top(sk_args, nc_args), split_top(sk_args, sk_args)):
        m = re.match(r"\s*([A-Za-z_]\w*)\s*=(?!=)", piece_sk)
        if m:
            named[m.group(1)] = piece_nc[m.end():].strip()
        else:
            positional.append(piece_nc.strip())
    return positional, named


def literal_string(expr: str | None) -> str | None:
    if expr is None:
        return None
    parts = STR_RE.findall(expr)
    if not parts or re.sub(r"\s|\+", "", STR_RE.sub("", expr)):
        return None
    return "".join(unquote(p) for p in parts)


def string_list(expr: str | None) -> list[str] | None:
    if expr is None:
        return None
    m = re.match(r"new\s*(?:string)?\s*\[\s*\]\s*\{(.*)\}\s*$", expr, re.S)
    return [unquote(s) for s in STR_RE.findall(m.group(1))] if m else None


def enum_values(expr: str | None, enum: str) -> list[str]:
    return re.findall(enum + r"\.(\w+)", expr) if expr else []


def parse_params(nc_params: str, sk_params: str) -> list[Param]:
    params = []
    for piece_nc, piece_sk in zip(split_top(sk_params, nc_params, True), split_top(sk_params, sk_params, True)):
        while True:
            piece_sk_s = piece_sk.lstrip()
            if not piece_sk_s.startswith("["):
                break
            start = len(piece_sk) - len(piece_sk_s)
            end = match_close(piece_sk, start, "[", "]")
            if end < 0:
                break
            piece_nc, piece_sk = piece_nc[end + 1:], piece_sk[end + 1:]
        depth, eq = 0, -1
        for k, ch in enumerate(piece_sk):
            depth += ch in "([{<"
            depth -= ch in ")]}>"
            if ch == "=" and depth == 0 and piece_sk[k + 1:k + 2] != "=":
                eq = k
                break
        decl = (piece_sk if eq < 0 else piece_sk[:eq]).strip()
        m = re.match(r"(?:(?:this|params|ref|out|in)\s+)*(.+?)\s+(\w+)$", decl, re.S)
        if m:
            params.append(Param(m.group(2), re.sub(r"\s+", " ", m.group(1)), eq >= 0))
    return params


def class_properties(skel: str, class_name: str) -> dict[str, str] | None:
    m = re.search(r"\bclass\s+" + re.escape(class_name) + r"\b[^{;]*\{", skel)
    if not m:
        return None
    end = match_close(skel, m.end() - 1, "{", "}")
    body = skel[m.end():end]
    type_re = r"\bpublic\s+(?!static\b|const\b|class\b)([\w<>\[\].? ]+?)\s+"
    props = {name.lstrip("@"): re.sub(r"\s+", " ", typ)
             for typ, name in re.findall(type_re + r"(@?\w+)\s*(?:\{|;|=)", body)}
    for typ, names in re.findall(type_re + r"(@?\w+\s*(?:=[^,;]+)?(?:,\s*@?\w+\s*(?:=[^,;]+)?)+);", body):
        props.update({n.lstrip("@"): re.sub(r"\s+", " ", typ) for n in re.findall(r"(?:^|,)\s*(@?\w+)", names)})
    return props


def return_field_sets(nc_body: str, sk_body: str) -> set[str] | None:
    """Union of top-level fields of success `return new { ... }` literals; None when not safely knowable."""
    returns = len(re.findall(r"\breturn\b", sk_body))
    if returns == 0 or "BatchExecutor" in sk_body or re.search(r"=>\s*\{|\bdelegate\b", sk_body):
        return None
    fields: set[str] = set()
    literals = 0
    for m in re.finditer(r"\breturn\s+new\s*\{", sk_body):
        end = match_close(sk_body, m.end() - 1, "{", "}")
        names = []
        for piece in split_top(sk_body[m.end():end], nc_body[m.end():end]):
            fm = re.match(r"\s*(\w+)\s*=(?!=)", piece) or re.search(r"(\w+)\s*$", piece)
            if fm:
                names.append(fm.group(1))
        literals += 1
        if "error" not in names:
            fields.update(names)
    others = returns - literals
    nonliteral_ok = len(re.findall(r"\breturn\s+(?:err\b|error\b|SkillErrorResponse\b|Validate\b|\w*Error\w*\()", sk_body))
    if not fields or others - nonliteral_ok > 0:
        return None
    return fields


def parse_skill_file(path: str, rel: str, all_classes: dict[str, dict[str, str]]) -> list[CodeSkill]:
    src = read_text(path)
    nc, sk = mask_source(src)
    conds = line_conditions(nc)
    local_classes: dict[str, dict[str, str]] = {}
    skills = []
    for m in re.finditer(r"\[\s*UnitySkill\s*\(", sk):
        open_i = m.end() - 1
        close_i = match_close(sk, open_i, "(", ")")
        if close_i < 0:
            continue
        positional, named = parse_attribute_args(nc[open_i + 1:close_i], sk[open_i + 1:close_i])
        name = literal_string(named.get("Name") or (positional[0] if positional else None))
        line = src.count("\n", 0, m.start()) + 1
        if name is None:
            skills.append(CodeSkill("<unresolved>", rel, line, None, "", [], "", "", {}, [], None, [], "", [], None, None, []))
            continue
        description_expr = named.get("Description") or (positional[1] if len(positional) > 1 else None)
        description = literal_string(description_expr)

        k = close_i + 1
        while k < len(sk) and sk[k] in " \t\r\n":
            k += 1
        k += 1 if sk[k:k + 1] == "]" else 0
        while True:
            while k < len(sk) and sk[k] in " \t\r\n":
                k += 1
            if sk[k:k + 1] != "[":
                break
            k = match_close(sk, k, "[", "]") + 1
        paren = sk.find("(", k)
        head = sk[k:paren]
        method_m = re.search(r"(\w+)\s*(?:<[^>]*>)?\s*$", head)
        pend = match_close(sk, paren, "(", ")")
        params = parse_params(nc[paren + 1:pend], sk[paren + 1:pend])
        body_start = sk.find("{", pend)
        arrow = sk.find("=>", pend)
        body_nc = body_sk = ""
        if body_start >= 0 and (arrow < 0 or body_start < arrow):
            body_end = match_close(sk, body_start, "{", "}")
            body_nc, body_sk = nc[body_start:body_end + 1], sk[body_start:body_end + 1]

        item_props = None
        item_m = re.search(r"Execute\s*<\s*(\w+)\s*>", body_sk)
        if item_m:
            cname = item_m.group(1)
            if cname not in local_classes:
                props = class_properties(sk, cname)
                if props is not None:
                    local_classes[cname] = props
            item_props = local_classes.get(cname) or all_classes.get(cname)

        flags = {f: "true" in named.get(f, "false") for f in
                 ("MutatesScene", "MutatesAssets", "MayTriggerReload", "MayEnterPlayMode", "TracksWorkflow", "ReadOnly")}
        skills.append(CodeSkill(
            name=name, file=rel, line=line, description=description,
            category=(enum_values(named.get("Category"), "SkillCategory") or [""])[0],
            operation=enum_values(named.get("Operation"), "SkillOperation"),
            mode=(enum_values(named.get("Mode"), "SkillMode") or ["FullAuto"])[0],
            risk=(literal_string(named.get("RiskLevel")) or "low"),
            flags=flags, tags=string_list(named.get("Tags")) or [],
            outputs=string_list(named.get("Outputs")),
            required_params=string_list(named.get("RequiredParams")) or [],
            method=method_m.group(1) if method_m else "", params=params, item_props=item_props,
            return_fields=return_field_sets(body_nc, body_sk) if body_sk else None,
            conditions=conds[line - 1][:] if line - 1 < len(conds) else [],
            has_description=description_expr is not None))
    return skills


def load_code_skills(root: str, findings: list[Finding]) -> tuple[dict[str, CodeSkill], dict[str, int], list[str]]:
    skills_root = os.path.join(root, SKILLS_DIR)
    files = sorted(glob.glob(os.path.join(skills_root, "*Skills.cs")))
    # Item classes may live in another file than the batch skill that uses them; index them for the fallback.
    all_classes: dict[str, dict[str, str]] = {}
    for path in files:
        _, sk = mask_source(read_text(path))
        for cm in re.finditer(r"\bclass\s+(\w+Item\w*)\b", sk):
            props = class_properties(sk, cm.group(1))
            if props is not None:
                all_classes.setdefault(cm.group(1), props)
    unique: dict[str, CodeSkill] = {}
    attribute_total = 0
    for path in files:
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        for skill in parse_skill_file(path, rel, all_classes):
            attribute_total += 1
            if skill.name == "<unresolved>":
                findings.append(Finding("error", "unresolved-skill-name",
                                        "[UnitySkill] name is not a string literal", f"{rel}:{skill.line}"))
                continue
            first = unique.get(skill.name)
            if first is None:
                unique[skill.name] = skill
            else:
                first.attribute_count += 1
                if first.conditions == skill.conditions:
                    findings.append(Finding("error", "duplicate-skill-name",
                                            f"`{skill.name}` registered twice under the same #if state "
                                            f"(also {first.file}:{first.line})", f"{rel}:{skill.line}"))
    counts = {"attributes": attribute_total, "skills": len(unique), "source_files": len(files)}
    return unique, counts, [os.path.relpath(p, root).replace(os.sep, "/") for p in files]


def load_categories(root: str) -> list[str]:
    path = os.path.join(root, ATTRIBUTE_FILE)
    if not os.path.isfile(path):
        return []
    nc, sk = mask_source(read_text(path))
    m = re.search(r"\benum\s+SkillCategory\b[^{]*\{", sk)
    if not m:
        return []
    body = sk[m.end():match_close(sk, m.end() - 1, "{", "}")]
    return [x.strip().split("=")[0].strip() for x in body.split(",") if x.strip()]


# --------------------------------------------------------------------------------------------- test registries

@dataclass
class Registries:
    advisory: set[str] = field(default_factory=set)
    exceptions: dict[str, list[str]] = field(default_factory=dict)
    pilot_owner: dict[str, tuple[str, str]] = field(default_factory=dict)  # module -> ("category"|"class", value)
    pilot_budgets: dict[str, int] = field(default_factory=dict)
    module_default_budget: int | None = None
    module_budget_exceptions: dict[str, int] | None = None
    known_dupes: set[str] = field(default_factory=set)
    known_broken_links: set[str] = field(default_factory=set)
    schema_only: set[str] = field(default_factory=set)
    loaded: bool = False


def load_registries(root: str, findings: list[Finding]) -> Registries:
    path = os.path.join(root, TEST_FILE)
    reg = Registries()
    if not os.path.isfile(path):
        findings.append(Finding("error", "registry-missing", f"{TEST_FILE} not found; advisory/pilot registries unavailable"))
        return reg
    nc, sk = mask_source(read_text(path))
    reg.advisory = set(parse_string_set(nc, sk, "AdvisoryModules"))
    reg.known_dupes = set(parse_string_set(nc, sk, "KnownCrossModuleDuplicates"))
    reg.known_broken_links = set(parse_string_set(nc, sk, "KnownBrokenDocLinks"))
    reg.schema_only = set(parse_string_set(nc, sk, "SchemaOnlyModules"))
    body = block_after(nc, sk, "ModuleCategoryExceptions") or ""
    for m in re.finditer(r'\{\s*"([\w-]+)"\s*,\s*new\[\]\s*\{([^}]*)\}', body):
        reg.exceptions[m.group(1).lower()] = re.findall(r"SkillCategory\.(\w+)", m.group(2))
    body = block_after(nc, sk, "PilotEntryByteBudgets") or ""
    reg.pilot_budgets = {m.group(1): int(m.group(2)) for m in re.finditer(r'\{\s*"([\w-]+)"\s*,\s*(-?\d+)\s*\}', body)}
    m = re.search(r"\bModuleEntryDefaultByteBudget\b\s*=\s*(\d+)", sk)
    reg.module_default_budget = int(m.group(1)) if m else None
    body = block_after(nc, sk, "ModuleEntryByteBudgetExceptions")
    if body is not None and reg.module_default_budget is not None:
        reg.module_budget_exceptions = {m.group(1): int(m.group(2)) for m in re.finditer(r'\{\s*"([\w-]+)"\s*,\s*(-?\d+)\s*\}', body)}
    else:
        findings.append(Finding("error", "registry-unparsed", f"could not parse module entry budget registry from {TEST_FILE}"))
    body = block_after(nc, sk, "PilotModules") or ""
    for m in re.finditer(r'\(\s*"([\w-]+)"\s*,\s*skill\s*=>\s*([^\n]*)', body):
        cat = re.search(r"Category\s*==\s*SkillCategory\.(\w+)", m.group(2))
        cls = re.search(r'DeclaringType\??\.Name\s*==\s*"(\w+)"', m.group(2))
        if cat:
            reg.pilot_owner[m.group(1)] = ("category", cat.group(1))
        elif cls:
            reg.pilot_owner[m.group(1)] = ("class", cls.group(1))
    reg.loaded = bool(reg.advisory and reg.pilot_budgets and reg.pilot_owner)
    if not reg.loaded:
        findings.append(Finding("error", "registry-unparsed", f"could not parse advisory/pilot registries from {TEST_FILE}"))
    return reg


# --------------------------------------------------------------------------------------------- docs

def iter_lines(text: str):
    """Yield (1-based line number, line, in_code_fence)."""
    fence = False
    for no, line in enumerate(text.split("\n"), 1):
        if re.match(r"\s*(```|~~~)", line):
            fence = not fence
            yield no, line, True
            continue
        yield no, line, fence


def doc_files(module_dir: str) -> list[str]:
    files = sorted(glob.glob(os.path.join(module_dir, "*.md")))
    files += sorted(glob.glob(os.path.join(module_dir, "reference", "*.md")))
    return files


def brace_fields(text: str) -> list[str] | None:
    """Depth-1 field names of the first {...} group in text."""
    start = text.find("{")
    if start < 0:
        return None
    end = match_close(text, start, "{", "}")
    if end < 0:
        return None
    body = text[start + 1:end]
    skel = re.sub(r"`[^`]*`", lambda m: " " * len(m.group(0)), body)
    names = []
    for piece in split_top(skel, body):
        m = re.match(r"\s*`?([A-Za-z_]\w*)`?\??\s*(?::|$)", piece) or re.match(r"\s*`?([A-Za-z_]\w*)`?\??\s*$", piece)
        if m:
            names.append(m.group(1))
    return names


def clean_names(cell: str) -> list[str]:
    raw = re.findall(r"`([^`]+)`", cell) or [cell]
    names = []
    for item in raw:
        for part in re.split(r"[,/]", item):
            part = part.strip().rstrip("*?").strip()
            if re.fullmatch(r"\w+", part):
                names.append(part)
    return names


def parse_doc_entries(path: str, module: str, rel: str) -> list[DocEntry]:
    lines = list(iter_lines(read_text(path)))
    starts = [i for i, (_, line, fenced) in enumerate(lines)
              if not fenced and re.match(r"###\s+`?([a-z][a-z0-9_]*)`?\s*$", line)]
    heading_idx = [i for i, (_, line, fenced) in enumerate(lines) if not fenced and re.match(r"#{1,3}\s", line)]
    entries = []
    for s in starts:
        end = next((h for h in heading_idx if h > s), len(lines))
        section = lines[s + 1:end]
        name = re.match(r"###\s+`?([a-z][a-z0-9_]*)`?", lines[s][1]).group(1)
        params = item_props = returns = None
        i = 0
        while i < len(section):
            _, line, fenced = section[i]
            if fenced:
                i += 1
                continue
            if params is None and line.lstrip().startswith("|") and "parameter" in line.split("|")[1].lower():
                params = []
                i += 1
                while i < len(section) and section[i][1].lstrip().startswith("|"):
                    cells = [c.strip() for c in section[i][1].strip().strip("|").split("|")]
                    if cells and not re.fullmatch(r"[-: ]+", cells[0]):
                        ctype = cells[1].lower() if len(cells) > 1 else ""
                        params += [(n, ctype) for n in clean_names(cells[0])]
                    i += 1
                continue
            if re.match(r"\*\*Item properties\*\*", line):
                j = i + 1
                while j < len(section) and section[j][1].strip():
                    j += 1
                tail = re.sub(r"^\s*\([^)]*\)", "", line[len("**Item properties**"):]).lstrip(": ")
                if "`" in tail:
                    tail = re.sub(r"\(or\s+", ", (", re.sub(r"\((?!or\s)[^)]*\)", "", tail))
                    lead = re.match(r"\s*(?:\(?`[^`]+`\)?\s*(?:,|;|/|\band\b|\bor\b)?\s*)+", tail)
                    tokens = re.findall(r"`([^`]+)`", lead.group(0) if lead else "")
                else:
                    tokens = re.findall(r"`([^`]+)`", " ".join(section[k][1] for k in range(i + 1, j)))
                item_props = [n for chunk in tokens for n in clean_names("`" + chunk + "`")]
            elif re.match(r"\*\*Returns\*\*", line):
                block = line
                j = i + 1
                while j < len(section) and section[j][1].strip():
                    block += " " + section[j][1]
                    j += 1
                returns = brace_fields(block)
            i += 1
        entries.append(DocEntry(name, module, rel, lines[s][0], params, item_props, returns))
    return entries


def section_lines(path: str, heading_re: str) -> list[tuple[int, str]]:
    out, active = [], False
    for no, line, fenced in iter_lines(read_text(path)):
        if not fenced and line.startswith("## "):
            active = bool(re.match(heading_re, line))
            continue
        if active and not fenced:
            out.append((no, line))
    return out


def do_not_names(path: str) -> list[tuple[int, str]]:
    """Skill names a DO NOT block claims do not exist (left of the arrow, next to 'does not exist')."""
    names = []
    lines = list(iter_lines(read_text(path)))
    for idx, (_, line, fenced) in enumerate(lines):
        if fenced or "**DO NOT**" not in line:
            continue
        for no, text, f in lines[idx + 1:]:
            if f or (text.strip() and not text.lstrip().startswith("-")):
                break
            m = re.search(r"(?:does not|do not|doesn't|don't) exist|不存在", text)
            if not m:
                continue
            left = re.split(r"→|->", text[:m.start()])[0]
            names += [(no, n) for n in re.findall(r"`([a-z][a-z0-9_]*)`", left)]
    return names


# --------------------------------------------------------------------------------------------- checks

class Audit:
    def __init__(self, root: str, run_frontmatter: bool = True):
        self.root = root
        self.run_frontmatter = run_frontmatter
        self.findings: list[Finding] = []
        self.counts: dict[str, int] = {}
        self.stats: dict = {}

    def add(self, severity: str, code: str, message: str, where: str = "") -> None:
        self.findings.append(Finding(severity, code, message, where))

    def run(self) -> None:
        root = self.root
        self.skills, c, self.source_files = load_code_skills(root, self.findings)
        self.categories = load_categories(root)
        self.reg = load_registries(root, self.findings)
        docs_root = os.path.join(root, DOCS_DIR)
        module_dirs = sorted(d for d in glob.glob(os.path.join(docs_root, "*")) if os.path.isfile(os.path.join(d, MODULE_DOC)))
        self.modules = [os.path.basename(d) for d in module_dirs]
        self.advisory = [m for m in self.modules if m.lower() in {a.lower() for a in self.reg.advisory}]
        real_categories = [x for x in self.categories if x != "Uncategorized"]
        self.counts = {
            "total": len(self.skills), "attributes": c["attributes"], "files": c["source_files"],
            "cats": len(real_categories), "docs": len(self.modules), "advisory": len(self.advisory),
            "rest": len(self.modules) - len(self.advisory),
        }
        if not self.skills:
            self.add("error", "no-skills", "no [UnitySkill] found; scan path is wrong")
            return
        self.check_metadata()
        self.map_modules(real_categories)
        self.entries = self.load_entries(module_dirs)
        self.check_ghosts_and_duplicates()
        self.check_signatures()
        self.check_returns()
        self.check_do_not()
        self.check_overview()
        self.check_mode()
        self.check_pilots()
        self.check_entry_budgets()
        self.check_undocumented()
        self.check_root_doc()
        self.check_links()
        if self.run_frontmatter:
            self.check_frontmatter()

    # -- metadata
    def check_metadata(self) -> None:
        for s in self.skills.values():
            where = f"{s.file}:{s.line}"
            if not s.category or s.category == "Uncategorized":
                self.add("error", "meta-category", f"`{s.name}` has no Category", where)
            if not s.operation:
                self.add("error", "meta-operation", f"`{s.name}` has no Operation", where)
            if not s.has_description:
                self.add("warning", "meta-description", f"`{s.name}` has no description", where)
            if not s.tags:
                self.add("info", "meta-tags", f"`{s.name}` has no Tags", where)
            if not s.outputs:
                self.add("info", "meta-outputs", f"`{s.name}` has no Outputs", where)
            if s.mode == "SemiAuto" and s.never_in_semi:
                self.add("error", "mode-semantic-conflict",
                         f"`{s.name}` is marked SemiAuto but matches the NeverInSemi rule "
                         "(Delete / MayEnterPlayMode / MayTriggerReload / RiskLevel=high)", where)
            if s.outputs and s.return_fields is not None:
                declared = {o for o in s.outputs if not self.is_routed(o, s.outputs)}
                returned = {f for f in s.return_fields if not self.is_routed(f, s.outputs)}
                missing = sorted(declared - returned - GENERIC_FIELDS)
                if missing:
                    self.add("warning", "outputs-vs-return",
                             f"`{s.name}` Outputs {missing} not in the returned object ({sorted(returned)})", where)

    @staticmethod
    def is_routed(field_name: str, outputs: list[str]) -> bool:
        """The router adds entityId (and echoed *EntityId locators) when Outputs has instanceId."""
        return "instanceId" in outputs and field_name.lower().endswith("entityid")

    # -- modules
    def map_modules(self, real_categories: list[str]) -> None:
        lower_cats = {c.lower(): c for c in real_categories}
        self.module_cats: dict[str, list[str]] = {}
        covered: set[str] = set()
        for module in self.modules:
            key = module.lower()
            in_adv = key in {a.lower() for a in self.reg.advisory}
            cats = self.reg.exceptions.get(key) or ([lower_cats[key]] if key in lower_cats else [])
            if in_adv and cats:
                self.add("error", "module-both", f"{module}/ is registered advisory but maps to categories {cats}")
            elif not in_adv and not cats:
                self.add("error", "module-unknown",
                         f"{module}/ matches no SkillCategory and is not a registered advisory module")
            if not in_adv:
                self.module_cats[module] = cats
                covered.update(cats)
        for stale in sorted(a for a in self.reg.advisory if a.lower() not in {m.lower() for m in self.modules}):
            self.add("warning", "advisory-stale", f"advisory module `{stale}` has no skills/{stale}/ directory")
        for cat in real_categories:
            if cat not in covered:
                self.add("warning", "category-undocumented", f"SkillCategory `{cat}` has no module directory carrying docs")
        self.by_category: dict[str, list[str]] = defaultdict(list)
        for s in self.skills.values():
            self.by_category[s.category].append(s.name)
        for cat, names in self.by_category.items():
            if cat and cat not in real_categories and cat != "Uncategorized":
                self.add("error", "category-unknown", f"{len(names)} skills use Category `{cat}` not in the enum")
        self.stats["by_category"] = {c: len(v) for c, v in sorted(self.by_category.items())}

    def module_skills(self, module: str) -> set[str]:
        owner = self.reg.pilot_owner.get(module)
        if owner and owner[0] == "class":
            return {s.name for s in self.skills.values() if os.path.basename(s.file) == owner[1] + ".cs"}
        cats = {owner[1]} if owner else set(self.module_cats.get(module, []))
        return {s.name for s in self.skills.values() if s.category in cats}

    def load_entries(self, module_dirs: list[str]) -> list[DocEntry]:
        entries = []
        for d in module_dirs:
            module = os.path.basename(d)
            if module in self.advisory:
                continue
            for path in doc_files(d):
                rel = os.path.relpath(path, os.path.join(self.root, DOCS_DIR)).replace(os.sep, "/")
                entries += parse_doc_entries(path, module, rel)
        return entries

    def check_ghosts_and_duplicates(self) -> None:
        by_name: dict[str, list[DocEntry]] = defaultdict(list)
        for e in self.entries:
            by_name[e.name].append(e)
            if e.name not in self.skills:
                self.add("error", "ghost-skill", f"`{e.name}` is documented but has no C# skill", f"{e.file}:{e.line}")
        for name, group in sorted(by_name.items()):
            modules = {e.module for e in group}
            for module in sorted(modules):
                same = [e for e in group if e.module == module]
                if len(same) > 1:
                    where = ", ".join(f"{e.file}:{e.line}" for e in same)
                    self.add("error", "duplicate-in-module", f"`{name}` defined {len(same)} times in {module}/", where)
            if len(modules) > 1 and name not in self.reg.known_dupes:
                self.add("warning", "duplicate-cross-module", f"`{name}` defined in {sorted(modules)}",
                         ", ".join(f"{e.file}:{e.line}" for e in group))
        for name in sorted(self.reg.known_dupes):
            if len({e.module for e in by_name.get(name, [])}) < 2:
                self.add("warning", "known-duplicate-stale", f"KnownCrossModuleDuplicates lists `{name}` but it is no longer duplicated")
        self.by_name = by_name

    def check_signatures(self) -> None:
        for e in self.entries:
            skill = self.skills.get(e.name)
            if skill is None:
                continue
            where = f"{e.file}:{e.line}"
            code_names = {p.name.lower(): p for p in skill.params}
            is_batch = skill.item_props is not None and {p.name for p in skill.params} <= {"items"}
            if is_batch:
                if e.item_props is not None:
                    self.compare_names(e.item_props, set(skill.item_props), skill, "item-property", where)
                continue
            if e.params is None:
                continue
            doc_names = {n for n, _ in e.params}
            self.compare_names(list(doc_names), {p.name for p in skill.params}, skill, "param", where,
                               locator_twin=True)
            for n, dtype in e.params:
                p = code_names.get(n.lower())
                if p and self.type_mismatch(dtype, p.type):
                    self.add("info", "param-type", f"`{skill.name}` param `{n}` documented `{dtype}`, C# `{p.type}`", where)

    def compare_names(self, doc: list[str], code: set[str], skill: CodeSkill, kind: str, where: str,
                      locator_twin: bool = False) -> None:
        code_l = {c.lower() for c in code}
        doc_l = {d.lower() for d in doc}
        extra_doc = []
        for d in doc:
            if d.lower() in code_l:
                continue
            # The router synthesises an entityId twin for instanceId locators on custom skills.
            if locator_twin and d.lower().endswith("entityid") and d.lower().replace("entityid", "instanceid") in code_l:
                continue
            extra_doc.append(d)
        extra_code = sorted(c for c in code if c.lower() not in doc_l)
        if extra_doc:
            self.add("error", f"{kind}-doc-extra", f"`{skill.name}` documents {kind.replace('-', ' ')}s not in C#: {extra_doc}", where)
        if extra_code:
            self.add("warning", f"{kind}-undocumented", f"`{skill.name}` C# {kind.replace('-', ' ')}s not documented: {extra_code}", where)

    @staticmethod
    def type_mismatch(doc: str, cs: str) -> bool:
        def norm(t: str) -> str | None:
            t = t.lower().replace("?", "").strip()
            if t in ("int", "long", "short", "integer"):
                return "int"
            if t in ("float", "double", "number"):
                return "float"
            if t in ("bool", "boolean"):
                return "bool"
            if t == "string":
                return "string"
            return None
        a, b = norm(doc), norm(cs)
        return a is not None and b is not None and a != b and {a, b} != {"int", "float"}

    def check_returns(self) -> None:
        for e in self.entries:
            skill = self.skills.get(e.name)
            if skill is None or e.returns is None:
                continue
            where = f"{e.file}:{e.line}"
            doc = {f for f in e.returns if f not in GENERIC_FIELDS and not f.lower().endswith("entityid")}
            if skill.outputs:
                declared = {o for o in skill.outputs if not o.lower().endswith("entityid")} - GENERIC_FIELDS
                if declared - doc:
                    self.add("warning", "returns-vs-outputs",
                             f"`{skill.name}` Outputs {sorted(declared - doc)} missing from documented Returns", where)
            if skill.return_fields is not None:
                code = {f for f in skill.return_fields if not f.lower().endswith("entityid")} - GENERIC_FIELDS
                if doc - code or code - doc:
                    self.add("warning", "returns-vs-return",
                             f"`{skill.name}` Returns vs returned object: doc-only {sorted(doc - code)}, "
                             f"code-only {sorted(code - doc)}", where)

    def check_do_not(self) -> None:
        for module in self.modules:
            if module in self.advisory:
                continue
            path = os.path.join(self.root, DOCS_DIR, module, MODULE_DOC)
            for no, name in do_not_names(path):
                if name in self.skills:
                    self.add("error", "do-not-real-skill", f"DO NOT block denies `{name}` but it exists in C#",
                             f"{module}/{MODULE_DOC}:{no}")

    def check_overview(self) -> None:
        for module in self.modules:
            if module in self.advisory:
                continue
            path = os.path.join(self.root, DOCS_DIR, module, MODULE_DOC)
            lines = section_lines(path, r"##\s+Skills Overview")
            if not lines:
                continue
            listed = {}
            for no, line in lines:
                if "**DO NOT**" in line:
                    break
                for n in re.findall(r"`([a-z][a-z0-9_]*)`", line):
                    if SKILL_NAME_RE.match(n) and "_" in n:
                        listed.setdefault(n, no)
            owned = self.module_skills(module)
            for n, no in sorted(listed.items()):
                if n not in self.skills and not n.endswith("_"):
                    self.add("warning", "overview-unknown", f"Overview lists `{n}` which is not a C# skill", f"{module}/{MODULE_DOC}:{no}")
            missing = sorted(owned - set(listed))
            if missing and owned:
                self.add("info", "overview-missing",
                         f"{module}: {len(missing)} of {len(owned)} skills not in Skills Overview: {missing[:8]}"
                         + (" ..." if len(missing) > 8 else ""), f"{module}/{MODULE_DOC}")

    def check_mode(self) -> None:
        for module in self.modules:
            if module in self.advisory:
                continue
            path = os.path.join(self.root, DOCS_DIR, module, MODULE_DOC)
            for no, line, fenced in iter_lines(read_text(path)):
                if fenced or "SemiAuto" not in line:
                    continue
                for clause in re.split(r"[;；。]|\.\s", line):
                    if "SemiAuto" not in clause or re.search(r"FullAuto|NeverInSemi|forbidden|FA\b", clause):
                        continue
                    for name in re.findall(r"`([a-z][a-z0-9_]*)`", clause):
                        s = self.skills.get(name)
                        if s and s.mode != "SemiAuto":
                            self.add("error", "mode-doc-vs-code",
                                     f"`{name}` documented SemiAuto but C# Mode is {s.mode}", f"{module}/{MODULE_DOC}:{no}")

    def check_pilots(self) -> None:
        docs_root = os.path.join(self.root, DOCS_DIR)
        for module, budget in sorted(self.reg.pilot_budgets.items()):
            if not os.path.isdir(os.path.join(docs_root, module)):
                self.add("error", "pilot-missing", f"pilot module `{module}` has no docs directory")
                continue
            owned = self.module_skills(module)
            documented = {e.name for e in self.entries if e.module == module}
            if owned - documented:
                self.add("error", "pilot-missing-skill", f"{module}: C# skills without a doc section: {sorted(owned - documented)}")
            if documented - owned:
                self.add("error", "pilot-extra-skill", f"{module}: documented skills not owned by the module: {sorted(documented - owned)}")
            ref_dir = os.path.join(docs_root, module, "reference")
            ref_files = {os.path.splitext(f)[0] for f in os.listdir(ref_dir)} if os.path.isdir(ref_dir) else set()
            for stem in sorted(ref_files):
                defs = [e.name for e in self.entries if e.file == f"{module}/reference/{stem}.md"]
                if defs != [stem]:
                    self.add("error", "pilot-reference-mismatch",
                             f"{module}/reference/{stem}.md defines {defs or 'nothing'}, expected exactly [{stem}]")
            for name in sorted(owned - ref_files):
                self.add("error", "pilot-reference-missing", f"{module}: `{name}` has no reference/{name}.md")
            size = len(read_text(os.path.join(docs_root, module, MODULE_DOC)).replace("\r", "").encode("utf-8"))
            self.stats.setdefault("pilot_entry_bytes", {})[module] = {"bytes": size, "budget": budget}
            if budget < 0:
                self.add("error", "pilot-budget-pending", f"{module}/{MODULE_DOC} budget is -1 (not decided): {size} bytes")
            elif size > budget:
                self.add("error", "pilot-budget", f"{module}/{MODULE_DOC} is {size} bytes > budget {budget}")

    def check_entry_budgets(self) -> None:
        default = self.reg.module_default_budget
        exceptions = self.reg.module_budget_exceptions
        if default is None or exceptions is None:
            return
        docs_root = os.path.join(self.root, DOCS_DIR)
        for module in sorted(exceptions):
            if module in self.reg.pilot_budgets:
                self.add("error", "entry-budget-registry",
                         f"`{module}` is a pilot (PilotEntryByteBudgets) and must not also be in ModuleEntryByteBudgetExceptions")
            elif module not in self.modules:
                self.add("error", "entry-budget-registry", f"ModuleEntryByteBudgetExceptions lists `{module}`, which has no {MODULE_DOC}")
        for module in self.modules:
            if module in self.reg.pilot_budgets:
                continue
            size = len(read_text(os.path.join(docs_root, module, MODULE_DOC)).replace("\r", "").encode("utf-8"))
            limit = exceptions.get(module, default)
            self.stats.setdefault("entry_bytes", {})[module] = {"bytes": size, "budget": limit}
            if size > limit:
                self.add("error", "entry-budget", f"{module}/{MODULE_DOC} is {size} bytes > budget {limit}")

    def check_undocumented(self) -> None:
        tokens: set[str] = set()
        docs_root = os.path.join(self.root, DOCS_DIR)
        for path in glob.glob(os.path.join(docs_root, "**", "*.md"), recursive=True):
            tokens.update(re.findall(r"[a-z][a-z0-9_]+", read_text(path)))
        for s in sorted(self.skills.values(), key=lambda x: x.name):
            if s.name not in tokens:
                self.add("info", "undocumented-skill", f"`{s.name}` is not mentioned anywhere under skills/", f"{s.file}:{s.line}")

    def check_root_doc(self) -> None:
        root_doc = os.path.join(self.root, PACKAGE_DIR, ROOT_DOC)
        if os.path.isfile(root_doc):
            size = len(read_text(root_doc).replace("\r", "").encode("utf-8"))
            self.stats["root_doc_bytes"] = {"bytes": size, "budget": ROOT_DOC_BYTE_BUDGET}
            if size > ROOT_DOC_BYTE_BUDGET:
                self.add("error", "root-doc-budget", f"root {ROOT_DOC} is {size} bytes > {ROOT_DOC_BYTE_BUDGET} (LF-normalised)")
        gitattributes = os.path.join(self.root, ".gitattributes")
        if not os.path.isfile(gitattributes) or not re.search(r"^\*\.md\s+text\s+eol=lf\s*$", read_text(gitattributes), re.M):
            self.add("error", "gitattributes", ".gitattributes missing or lacks `*.md text eol=lf` (issue #59)")
        for path in glob.glob(os.path.join(self.root, PACKAGE_DIR, "**", "*.md"), recursive=True):
            with open(path, "rb") as f:
                if b"\r" in f.read():
                    self.add("warning", "crlf", "markdown file contains CR bytes",
                             os.path.relpath(path, self.root).replace(os.sep, "/"))

    # -- links
    def link_sources(self) -> list[str]:
        pkg = os.path.join(self.root, PACKAGE_DIR)
        sources = [os.path.join(pkg, ROOT_DOC)]
        sources += sorted(glob.glob(os.path.join(pkg, "references", "*.md")))
        sources.append(os.path.join(pkg, "skills", INDEX_DOC))
        for d in sorted(glob.glob(os.path.join(pkg, "skills", "*"))):
            if os.path.isdir(d):
                sources += doc_files(d)
        return [p for p in sources if os.path.isfile(p)]

    @staticmethod
    def heading_anchors(path: str) -> set[str]:
        anchors, seen = set(), defaultdict(int)
        for line in read_text(path).split("\n"):
            m = re.match(r"#{1,6}\s+(.*?)\s*$", line)
            if not m:
                continue
            text = m.group(1)
            explicit = re.search(r"\{#([^}]+)\}\s*$", text)
            if explicit:
                anchors.add(explicit.group(1).lower())
                text = text[:explicit.start()].rstrip()
            slug = "".join("-" if ch == " " else ch for ch in text.strip().lower() if ch.isalnum() or ch in "-_ ")
            if not slug:
                continue
            anchors.add(slug if seen[slug] == 0 else f"{slug}-{seen[slug]}")
            seen[slug] += 1
        return anchors

    @staticmethod
    def is_doc_link(target: str) -> bool:
        if target.lower().startswith(("http://", "https://", "mailto:")):
            return False
        if target.startswith("#"):
            return True
        file_part = target.split("#")[0]
        return not file_part or "/" in file_part or file_part.lower().endswith(".md")

    def check_links(self) -> None:
        pkg = os.path.join(self.root, PACKAGE_DIR)
        anchor_cache: dict[str, set[str]] = {}
        reported: set[str] = set()
        checked = 0
        graph: dict[str, set[str]] = defaultdict(set)
        for source in self.link_sources():
            rel_source = os.path.relpath(source, pkg).replace(os.sep, "/")
            for m in LINK_RE.finditer(read_text(source)):
                target = m.group("target")
                if not self.is_doc_link(target):
                    continue
                checked += 1
                file_part, _, anchor = target.partition("#")
                resolved = source if not file_part else os.path.normpath(os.path.join(os.path.dirname(source), file_part))
                key = f"{rel_source} -> {target}"
                if not os.path.exists(resolved):
                    reported.add(key)
                    if key not in self.reg.known_broken_links:
                        self.add("error", "link-missing-file", f"{key}: target does not exist", rel_source)
                    continue
                if os.path.isfile(resolved):
                    graph[source].add(resolved)
                if anchor and os.path.isfile(resolved):
                    anchors = anchor_cache.setdefault(resolved, self.heading_anchors(resolved))
                    if anchor.lower() not in anchors:
                        reported.add(key)
                        if key not in self.reg.known_broken_links:
                            self.add("error", "link-missing-anchor", f"{key}: no such heading anchor", rel_source)
        self.stats["links_checked"] = checked
        for stale in sorted(self.reg.known_broken_links - reported):
            self.add("warning", "known-broken-link-stale", f"KnownBrokenDocLinks entry now resolves: {stale}")
        root_doc = os.path.join(pkg, ROOT_DOC)
        seen, queue = {root_doc}, [root_doc]
        while queue:
            for nxt in graph.get(queue.pop(), ()):
                if nxt not in seen:
                    seen.add(nxt)
                    queue.append(nxt)
        for path in sorted(glob.glob(os.path.join(pkg, "references", "*.md"))):
            if path not in seen:
                self.add("warning", "reference-unreachable",
                         "references file is not reachable from the root SKILL.md via relative links",
                         os.path.relpath(path, pkg).replace(os.sep, "/"))

    def check_frontmatter(self) -> None:
        script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check_skill_frontmatter.py")
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        proc = subprocess.run([sys.executable, "-B", script, self.root], capture_output=True, text=True,
                              encoding="utf-8", env=env)
        if proc.returncode != 0 and "PyYAML" in proc.stderr:
            self.add("warning", "frontmatter-skipped", "check_skill_frontmatter.py needs PyYAML (pip install pyyaml); skipped")
            return
        out = proc.stdout.splitlines()
        for line in out:
            if line.startswith("  - "):
                self.add("error", "frontmatter", line[4:].strip())
        if proc.returncode != 0 and not any(line.startswith("  - ") for line in out):
            self.add("error", "frontmatter", (proc.stderr or proc.stdout).strip()[-300:])
        self.stats["frontmatter"] = [line for line in out if line.startswith("description")]

    # -- counts
    def count_anchors(self) -> list[tuple[str, re.Pattern]]:
        return COUNT_ANCHORS

    def check_counts(self, sync: bool) -> list[dict]:
        """Compare (and with sync, rewrite) the documented count anchors. Returns the drift records."""
        drifts = []
        for rel, patterns in COUNT_ANCHORS:
            path = os.path.join(self.root, rel)
            if not os.path.isfile(path):
                self.add("warning", "anchor-file-missing", f"{rel} not found")
                continue
            with open(path, encoding="utf-8", newline="") as f:
                text = f.read()
            new_text = text
            for pattern in patterns:
                matches = list(pattern.finditer(new_text))
                if not matches:
                    self.add("warning", "anchor-not-found", f"count anchor not found in {rel}: {pattern.pattern}")
                    continue
                for m in reversed(matches):
                    for key, value in m.groupdict().items():
                        if value is not None and int(value) != self.counts[key]:
                            drifts.append({"file": rel, "key": key, "documented": int(value), "actual": self.counts[key],
                                           "line": new_text.count("\n", 0, m.start(key)) + 1})
                            new_text = new_text[:m.start(key)] + str(self.counts[key]) + new_text[m.end(key):]
            new_text, table_drifts = self.sync_category_table(rel, new_text)
            drifts += table_drifts
            if sync and new_text != text:
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(new_text)
        for d in drifts:
            self.add("warning" if not sync else "info", "count-drift" if not sync else "count-synced",
                     f"{d['file']}:{d['line']} {d['key']}: documented {d['documented']} -> actual {d['actual']}")
        return drifts

    def sync_category_table(self, rel: str, text: str) -> tuple[str, list[dict]]:
        if not rel.startswith("README"):
            return text, []
        start = re.search(r"<summary><b>[^<]*\(\d+\)</b></summary>", text)
        if not start:
            return text, []
        end = text.find("</details>", start.end())
        section = text[start.end():end]
        lookup = {c.lower(): c for c in self.by_category}
        drifts = []

        def fix(m: re.Match) -> str:
            cat = lookup.get(re.sub(r"\s+", "", m.group("name")).lower())
            if cat is None:
                return m.group(0)
            actual = len(self.by_category[cat])
            if int(m.group("n")) != actual:
                drifts.append({"file": rel, "key": f"table:{cat}", "documented": int(m.group("n")), "actual": actual,
                               "line": text.count("\n", 0, start.end() + m.start()) + 1})
            return f"{m.group('pre')}{actual}{m.group('post')}"

        section_new = re.sub(r"(?P<pre>\|\s*\*\*(?P<name>[^*]+)\*\*\s*\|\s*)(?P<n>\d+)(?P<post>\s*\|)", fix, section)
        return text[:start.end()] + section_new + text[end:], drifts


def _anchors(*patterns: str) -> list[re.Pattern]:
    return [re.compile(p) for p in patterns]


# (file, patterns): named groups are count keys (total, files, cats, docs, rest, advisory).
COUNT_ANCHORS: list[tuple[str, list[re.Pattern]]] = [
    ("AGENTS.md", _anchors(
        r"(?P<files>\d+) \*Skills\.cs / (?P<cats>\d+) SkillCategory / (?P<total>\d+) skills",
        r"(?P<files>\d+) vs (?P<cats>\d+):",
        r"`\*Skills\.cs` ×(?P<files>\d+)",
        r"(?P<docs>\d+) module docs: (?P<rest>\d+) REST \+ (?P<advisory>\d+) advisory",
        r"(?P<advisory>\d+) advisory modules under")),
    ("README.md", _anchors(
        r"Skills-(?P<total>\d+)-green",
        r"\*\*(?P<total>\d+) REST Skills Toolkit\*\*: (?P<files>\d+) source files across (?P<cats>\d+) categories, plus (?P<advisory>\d+) advisory",
        r"Skills Category Overview \((?P<total>\d+)\)",
        r"\((?P<files>\d+) \*Skills\.cs files → (?P<cats>\d+) SkillCategory categories, (?P<total>\d+) Skills\)",
        r"# (?P<total>\d+) Skills source code",
        r"(?P<docs>\d+) module docs \((?P<rest>\d+) REST/module docs \+ (?P<advisory>\d+) documentation-only docs\)",
        r"(?P<advisory>\d+) advisory design modules")),
    ("README_CN.md", _anchors(
        r"Skills-(?P<total>\d+)-green",
        r"\*\*(?P<total>\d+) REST Skills 全能库\*\*：(?P<files>\d+) 个源文件、(?P<cats>\d+) 个分类，另有 (?P<advisory>\d+) 个 advisory",
        r"Skills 分类概要 \((?P<total>\d+)\)",
        r"\((?P<files>\d+) 个 \*Skills\.cs → (?P<cats>\d+) 个 SkillCategory 分类，共 (?P<total>\d+) Skills\)",
        r"# (?P<total>\d+) Skills 源码",
        r"(?P<docs>\d+) 个模块文档（(?P<rest>\d+) 个 REST/模块文档 \+ (?P<advisory>\d+) 个 advisory 文档）",
        r"(?P<advisory>\d+) 个 advisory 设计模块")),
]


# --------------------------------------------------------------------------------------------- reporting

def build_audit(root: str, sync: bool = False, run_frontmatter: bool = True) -> tuple[Audit, list[dict]]:
    audit = Audit(root, run_frontmatter)
    audit.run()
    drifts = audit.check_counts(sync) if audit.skills else []
    return audit, drifts


def render_text(audit: Audit, drifts: list[dict], sync: bool, show_all: bool, limit: int = 15) -> str:
    c = audit.counts
    out = ["UnitySkills inventory",
           f"  skills (unique): {c['total']}  ([UnitySkill] attributes: {c['attributes']}, "
           f"#if/#else stubs deduped: {c['attributes'] - c['total']})",
           f"  *Skills.cs files: {c['files']}   SkillCategory (excl. Uncategorized): {c['cats']}",
           f"  module docs: {c['docs']}  (REST {c['rest']}, advisory {c['advisory']})"]
    if "root_doc_bytes" in audit.stats:
        r = audit.stats["root_doc_bytes"]
        out.append(f"  root SKILL.md: {r['bytes']} / {r['budget']} bytes (margin {r['budget'] - r['bytes']})")
    for module, p in audit.stats.get("pilot_entry_bytes", {}).items():
        out.append(f"  pilot {module}: {p['bytes']} / {p['budget']} bytes")
    if audit.stats.get("entry_bytes"):
        eb = audit.stats["entry_bytes"]
        within = sum(1 for p in eb.values() if p["bytes"] <= p["budget"])
        big_name, big = max(eb.items(), key=lambda kv: kv[1]["bytes"])
        out.append(f"  module entries (non-pilot): {within} / {len(eb)} within budget, largest {big_name} {big['bytes']} / {big['budget']} bytes")
    if "links_checked" in audit.stats:
        out.append(f"  relative doc links checked: {audit.stats['links_checked']}")
    out.extend("  frontmatter: " + line for line in audit.stats.get("frontmatter", []))
    nis = [s.name for s in audit.skills.values() if s.never_in_semi]
    semi = [s.name for s in audit.skills.values() if s.mode == "SemiAuto"]
    out.append(f"  Mode=SemiAuto: {len(semi)}   NeverInSemi (by metadata rule): {len(nis)}")
    out.append(f"  count anchors: " + (("synced " if sync else "DRIFT ") + f"{len(drifts)}" if drifts else "in sync"))
    grouped: dict[str, dict[str, list[Finding]]] = {s: defaultdict(list) for s in SEVERITIES}
    for f in audit.findings:
        grouped[f.severity][f.code].append(f)
    totals = {s: sum(len(v) for v in grouped[s].values()) for s in SEVERITIES}
    for sev in SEVERITIES:
        if not grouped[sev]:
            continue
        out.append("")
        out.append(f"{sev.upper()} ({totals[sev]})")
        for code, items in sorted(grouped[sev].items()):
            out.append(f"  [{code}] x{len(items)}")
            shown = items if show_all else items[:limit]
            for f in shown:
                out.append(f"    - {f.message}" + (f"  ({f.where})" if f.where and f.where not in f.message else ""))
            if len(shown) < len(items):
                out.append(f"    ... {len(items) - len(shown)} more (use --all or --json)")
    out.append("")
    out.append(f"{totals['error']} errors, {totals['warning']} warnings, {totals['info']} info")
    return "\n".join(out)


def to_json(audit: Audit, drifts: list[dict]) -> str:
    return json.dumps({
        "counts": audit.counts, "stats": audit.stats, "count_drift": drifts,
        "advisory_modules": audit.advisory,
        "semi_auto": sorted(s.name for s in audit.skills.values() if s.mode == "SemiAuto"),
        "never_in_semi": sorted(s.name for s in audit.skills.values() if s.never_in_semi),
        "findings": [f.__dict__ for f in audit.findings],
    }, ensure_ascii=False, indent=2)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("root", nargs="?", default=".", help="repository root (default: cwd)")
    ap.add_argument("--json", action="store_true", help="machine-readable output")
    ap.add_argument("--all", action="store_true", help="list every finding instead of the first 15 per code")
    ap.add_argument("--sync-counts", action="store_true", help="rewrite the count anchors in AGENTS.md and the READMEs")
    args = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    audit, drifts = build_audit(os.path.abspath(args.root), args.sync_counts)
    print(to_json(audit, drifts) if args.json else render_text(audit, drifts, args.sync_counts, args.all))
    failed = any(f.severity == "error" for f in audit.findings)
    if drifts and not args.sync_counts:
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
