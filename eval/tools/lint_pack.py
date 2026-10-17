"""Traceability lint for the evaluation task pack (architecture draft 3.2; contract 5.2-5.6, 6.3). Read-only.

    python3 -B eval/tools/lint_pack.py [--pack-dir DIR | --pack-root DIR --private-root DIR] [--solutions-root DIR]
                                       [--manifest FILE] [--tasks ID[,ID...]]

Every finding names the rule it breaks:
  1  each assertion lists requirements; every R it names exists in both prompts, every G in the prompt prefix
  2  both prompts have the same R numbers and, per requirement, the same numeric literals and placeholders; the
     answer schema descriptions are bilingual with matching numbers
  3  every requirement is covered by at least one assertion
  4  weights sum to 10; a critical assertion rests only on G1/G2/G6 or on prohibition requirements
  5  handle and C0 key names avoid the role blacklist; a task whose prompts call it read-only keeps C0 to digests
  6  module hygiene: public task modules carry no trap or grading words (comments, docstrings, identifiers) and
     import only task_api, verifylib and the standard library; private verifiers import only verifylib and the
     standard library and open no files or connections; solutions never import the private verifier
  7  solutions: reference(), at least 5 negatives with known expected-failure sets, at least 1 alternative
  8  check_eval_pack: the manifest matches the public files and the private verifiers
Exit 0 without errors (warnings allowed), 1 with errors, 2 when the lint cannot run.
"""
import argparse
import ast
import io
import math
import os
import re
import string
import sys
import tokenize

sys.dont_write_bytecode = True
TOOLS = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(TOOLS))
RUNNER = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
for _entry in (RUNNER, TOOLS):
    if _entry not in sys.path:
        sys.path.insert(0, _entry)

import check_eval_pack                     # noqa: E402
from usk_eval import pack, prompts         # noqa: E402
from usk_eval.problems import ProblemError  # noqa: E402
from usk_eval.task_api import Negative     # noqa: E402

DEFAULT_PACK_ROOT = os.path.join(RUNNER, "pack")
DEFAULT_PRIVATE_ROOT = os.path.join(REPO, "eval", "pack", "private")
DEFAULT_SOLUTIONS_ROOT = os.path.join(REPO, "eval", "pack", "solutions")
LANGS = ("zh-CN", "en")
BOM = b"\xef\xbb\xbf"
TOTAL_WEIGHT = 10.0
MIN_NEGATIVES = 5
MIN_ALTERNATIVES = 1
CRITICAL_RULES = ("G1", "G2", "G6")
API_METHODS = ("build", "snapshot", "prompt_vars", "answer_schema", "teardown")

ROLE_RE = re.compile(r"(?i)expected|target|decoy|inactive|answer|correct|wrong|trap|truth|solution|reference|legacy")
TRAP_RE = re.compile(r"(?i)\b(?:traps?|decoys?|distract\w*|trick\w*|gotchas?|pitfalls?|grad(?:e|es|ed|ing)|"
                     r"scor(?:e|es|ed|ing)|assert\w*|expect\w*|correct\w*|wrong\w*|targets?|legacy|solutions?|"
                     r"cheat\w*|hints?|verif\w*|answer key)\b")
TRAP_ZH_RE = re.compile(r"陷阱|诱饵|干扰|判分|评分|得分|扣分|断言|期望|正确|错误答案|参考答案|目标对象|验收")
READ_ONLY_ZH = "只读任务"
READ_ONLY_EN = "read-only task"
PROHIBITION_ZH = ("不要", "不得", "不能", "禁止", "不允许", "保持不变", "不变", "只读")
PROHIBITION_EN = ("do not", "don't", "must not", "never", "may not", "unchanged", "read-only", "no other")
REQ_RE = re.compile(r"^[RG][1-9][0-9]*$")
NUMBER_RE = re.compile(r"-?\d+(?:\.\d+)?")
REQ_LINE_RE = re.compile(r"^R(\d+)\. ")
G_LINE_RE = re.compile(r"^G(\d+) ")
CONTINUATION = "   "
NETWORK_MODULES = ("socket", "ssl", "http", "urllib", "ftplib", "smtplib", "poplib", "imaplib", "telnetlib",
                   "subprocess", "shutil", "tempfile", "asyncio", "multiprocessing", "requests", "webbrowser")
_STDLIB_FALLBACK = frozenset((
    "abc", "array", "ast", "base64", "bisect", "collections", "contextlib", "copy", "csv", "datetime", "decimal",
    "difflib", "enum", "fnmatch", "fractions", "functools", "glob", "hashlib", "heapq", "hmac", "io", "itertools",
    "json", "math", "numbers", "operator", "os", "pathlib", "random", "re", "secrets", "statistics", "string",
    "struct", "sys", "textwrap", "time", "types", "typing", "unicodedata", "uuid", "zlib", "socket", "ssl", "http",
    "urllib", "ftplib", "smtplib", "poplib", "imaplib", "telnetlib", "subprocess", "shutil", "tempfile", "asyncio",
    "multiprocessing", "webbrowser", "posixpath", "ntpath", "stat", "platform", "codecs", "binascii", "colorsys",
    "pprint", "reprlib", "weakref", "warnings", "traceback", "inspect", "importlib", "pickle", "shelve", "sqlite3",
    "zipfile", "tarfile", "gzip", "bz2", "lzma", "threading", "queue", "signal", "errno", "locale", "gettext",
    "calendar", "cmath", "dataclasses", "graphlib", "keyword", "token", "tokenize", "linecache", "argparse"))
STDLIB = frozenset(getattr(sys, "stdlib_module_names", ())) or _STDLIB_FALLBACK
G_RULES = {lang: {"G%s" % m.group(1) for m in (G_LINE_RE.match(line) for line in prompts.PREFIX[lang].split("\n"))
                  if m} for lang in LANGS}


class Finding(object):
    def __init__(self, task, rule, message, level="error"):
        self.task, self.rule, self.message, self.level = task, rule, message, level

    def __repr__(self):
        return "<Finding %s rule %s %s: %s>" % (self.task, self.rule, self.level, self.message)

    def line(self):
        return "%s: %s rule %d: %s" % (self.level, self.task or "pack", self.rule, self.message)


class _Collector(object):
    def __init__(self, task):
        self.task = task
        self.findings = []

    def error(self, rule, message):
        self.findings.append(Finding(self.task, rule, message))

    def warn(self, rule, message):
        self.findings.append(Finding(self.task, rule, message, "warning"))


# ---------------------------------------------------------------- loading


def _read(path):
    with open(path, "rb") as handle:
        return handle.read()


def _source(path):
    return _read(path).decode("utf-8-sig")


def _load(name, path):
    return pack.load_source_module(name, path)


# ---------------------------------------------------------------- prompts (rules 1-3)


class Prompt(object):
    """One prompt file: {n: text} of its requirements (continuation lines joined), problems found while parsing."""

    def __init__(self, lang):
        self.lang = lang
        self.reqs = {}
        self.problems = []


def parse_prompt_file(raw, task_id, lang):
    prompt = Prompt(lang)
    bad = prompt.problems.append
    if raw.startswith(BOM):
        bad("starts with a BOM (prompt files are UTF-8 without BOM)")
    if b"\r" in raw:
        bad("has CR line endings (prompt files use LF)")
    try:
        text = raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        bad("is not UTF-8")
        return prompt
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    header = "<!-- usk-eval prompt; task %s; lang %s -->" % (task_id, lang)
    if lines and lines[0].lstrip().startswith("<!--") and lines[0].strip() != header:
        bad("first-line comment should read %r" % header)
    try:
        parsed = prompts.parse_prompt(text)
    except ValueError as exc:
        bad("cannot be parsed: %s" % exc)
        return prompt
    current = None
    for line in parsed:
        match = REQ_LINE_RE.match(line)
        if match:
            current = int(match.group(1))
            prompt.reqs[current] = line[match.end():]
        elif not line.startswith(CONTINUATION) or line.lstrip().startswith("#"):
            bad("line %r is neither a requirement nor a continuation indented by 3 spaces" % line[:40])
        else:
            prompt.reqs[current] += "\n" + line.strip()
    return prompt


def split_placeholders(text):
    """(literal text with placeholders removed, [placeholder names], [problems])."""
    literal, names, problems = [], [], []
    try:
        for chunk, field, spec, conversion in string.Formatter().parse(text):
            literal.append(chunk)
            if field is None:
                continue
            if not field or not field.isidentifier():
                problems.append("placeholder {%s} is not a plain name" % field)
            if spec or conversion:
                problems.append("placeholder {%s} has a format spec or conversion" % field)
            names.append(field)
    except ValueError as exc:
        problems.append("unbalanced braces (%s); write literal braces as {{ and }}" % exc)
    return "".join(literal), names, problems


def numbers(text):
    return sorted(NUMBER_RE.findall(text))


def prohibition(prompt_set, number):
    texts = [prompt_set[lang].reqs.get(number) for lang in LANGS]
    if not all(texts):
        return False
    zh, en = texts[0], texts[1].lower()
    return any(marker in zh for marker in PROHIBITION_ZH) and any(marker in en for marker in PROHIBITION_EN)


def _schema_descs(node, where, out):
    if isinstance(node, dict):
        if "desc" in node:
            out.append((where or "answer", node["desc"]))
        for key, value in (node.get("properties") or {}).items():
            _schema_descs(value, "%s.%s" % (where, key) if where else key, out)
        if node.get("items") is not None:
            _schema_descs(node["items"], "%s[]" % (where or "answer"), out)
    return out


def check_prompts(found, prompt_set, schema):
    """Rule 2."""
    for lang in LANGS:
        for problem in prompt_set[lang].problems:
            found.error(2, "prompt.%s.md %s" % (lang, problem))
    zh, en = prompt_set["zh-CN"].reqs, prompt_set["en"].reqs
    if set(zh) != set(en):
        found.error(2, "requirement numbers differ: zh-CN %s, en %s" % (_req_list(zh), _req_list(en)))
    for number in sorted(set(zh) & set(en)):
        parts = {}
        for lang, text in (("zh-CN", zh[number]), ("en", en[number])):
            literal, names, problems = split_placeholders(text)
            for problem in problems:
                found.error(2, "prompt.%s.md R%d: %s" % (lang, number, problem))
            parts[lang] = (numbers(literal), sorted(set(names)))
        if parts["zh-CN"][0] != parts["en"][0]:
            found.error(2, "R%d numeric literals differ: zh-CN %s, en %s" % (number, parts["zh-CN"][0], parts["en"][0]))
        if parts["zh-CN"][1] != parts["en"][1]:
            found.error(2, "R%d placeholders differ: zh-CN %s, en %s" % (number, parts["zh-CN"][1], parts["en"][1]))
        clash = sorted(set(parts["zh-CN"][1]) & {"requirements", "schema", "submit_cmd"})
        if clash:
            found.error(2, "R%d uses prefix-only placeholders %s" % (number, clash))
    if schema is None:
        return
    for where, desc in _schema_descs(schema, "", []):
        if not isinstance(desc, dict) or not all(isinstance(desc.get(lang), str) and desc[lang].strip()
                                                 for lang in LANGS):
            found.error(2, "answer schema %s: desc must give non-empty zh-CN and en texts" % where)
        elif numbers(desc["zh-CN"]) != numbers(desc["en"]):
            found.error(2, "answer schema %s: desc numbers differ: zh-CN %s, en %s" % (
                where, numbers(desc["zh-CN"]), numbers(desc["en"])))


def _req_list(reqs):
    return ["R%d" % n for n in sorted(reqs)]


# ---------------------------------------------------------------- assertions (rules 1, 3, 4)


def _rows(found, private):
    """Well-formed ASSERTIONS rows as (id, weight, critical, reqs, desc); malformed ones are reported (rule 1)."""
    spec = getattr(private, "ASSERTIONS", None)
    if not isinstance(spec, (list, tuple)) or not spec:
        found.error(1, "the private verifier has no ASSERTIONS list")
        return []
    rows, seen = [], set()
    for index, row in enumerate(spec):
        if isinstance(row, (str, bytes)) or not isinstance(row, (list, tuple)) or len(row) != 5:
            found.error(1, "ASSERTIONS[%d] is not (id, weight, critical, [reqIds], desc)" % index)
            continue
        aid, weight, critical, reqs, desc = row
        if not isinstance(aid, str) or not aid:
            found.error(1, "ASSERTIONS[%d] has no id" % index)
            continue
        if aid in seen:
            found.error(1, "assertion %s is defined twice" % aid)
        seen.add(aid)
        if isinstance(reqs, (str, bytes)) or not isinstance(reqs, (list, tuple)) or not reqs:
            found.error(1, "assertion %s names no requirement" % aid)
            reqs = []
        bad = [req for req in reqs if not isinstance(req, str) or not REQ_RE.match(req)]
        if bad:
            found.error(1, "assertion %s names malformed requirements %r" % (aid, bad))
        if not isinstance(critical, bool):
            found.error(1, "assertion %s: critical must be True or False" % aid)
        if not isinstance(desc, str) or not desc.strip():
            found.error(1, "assertion %s has no description" % aid)
        rows.append((aid, weight, bool(critical), [req for req in reqs if req not in bad], desc))
    return rows


def check_assertions(found, private, prompt_set):
    """Rules 1, 3 and 4."""
    rows = _rows(found, private)
    if not callable(getattr(private, "verify", None)):
        found.error(1, "the private verifier defines no verify(ctx, card)")
    reqs_by_lang = {lang: {"R%d" % n for n in prompt_set[lang].reqs} for lang in LANGS}
    for aid, _weight, _critical, reqs, _desc in rows:
        for req in reqs:
            known = reqs_by_lang if req.startswith("R") else G_RULES
            missing = [lang for lang in LANGS if req not in known[lang]]
            if missing:
                found.error(1, "assertion %s names %s, which prompt %s does not define" % (
                    aid, req, " and ".join(missing) if req.startswith("R") else "prefix " + "/".join(missing)))
    hints = getattr(private, "HINTS", None)
    ids = {row[0] for row in rows}
    if hints is not None and not isinstance(hints, dict):
        found.error(1, "HINTS must be a dict {assertion id: {zh-CN, en}}")
    for aid, hint in (hints or {}).items() if isinstance(hints, dict) else ():
        if aid not in ids:
            found.error(1, "HINTS names an unknown assertion %s" % aid)
        elif not isinstance(hint, dict) or not all(isinstance(hint.get(lang), str) and hint[lang].strip()
                                                   for lang in LANGS):
            found.error(1, "HINTS[%s] must give non-empty zh-CN and en texts" % aid)
    covered = {req for row in rows for req in row[3]}
    for req in sorted(reqs_by_lang["zh-CN"] | reqs_by_lang["en"], key=lambda r: int(r[1:])):
        if req not in covered:
            found.error(3, "%s is not covered by any assertion" % req)
    total = 0.0
    for aid, weight, critical, reqs, _desc in rows:
        if isinstance(weight, bool) or not isinstance(weight, (int, float)) or not math.isfinite(weight) or \
                weight <= 0:
            found.error(4, "assertion %s: weight must be a positive number, got %r" % (aid, weight))
            continue
        total += weight
        if critical:
            loose = [req for req in reqs if req not in CRITICAL_RULES and
                     not (req.startswith("R") and prohibition(prompt_set, int(req[1:])))]
            if loose:
                found.error(4, "critical assertion %s rests on %s, which is neither G1/G2/G6 nor a prohibition" % (
                    aid, ", ".join(loose)))
    if rows and abs(total - TOTAL_WEIGHT) > 1e-6:
        found.error(4, "assertion weights sum to %g, expected %g" % (total, TOTAL_WEIGHT))
    return rows


# ---------------------------------------------------------------- public module (rules 5, 6)


def _keys(tree):
    """(key, line) of every string used as a dict key, dict() keyword or stored subscript in the module."""
    out = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Dict):
            out += [(key.value, key.lineno) for key in node.keys
                    if isinstance(key, ast.Constant) and isinstance(key.value, str)]
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "dict":
            out += [(kw.arg, node.lineno) for kw in node.keywords if kw.arg]
        elif isinstance(node, ast.Subscript) and isinstance(node.ctx, ast.Store):
            if isinstance(node.slice, ast.Constant) and isinstance(node.slice.value, str):
                out.append((node.slice.value, node.lineno))
    return out


def _function(tree, name):
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name == name:
            return node
    return None


def _digest_only(func):
    """Problems with a read-only task's snapshot(): each return must be a dict literal whose "data" is an empty dict
    literal and whose "digests" values are calls (canonical_digest(...))."""
    if func is None:
        return ["defines no snapshot()"]
    returns = [node for node in ast.walk(func) if isinstance(node, ast.Return)]
    problems = []
    for node in returns or [None]:
        value = getattr(node, "value", None)
        if not isinstance(value, ast.Dict):
            problems.append("snapshot() must return a dict literal so its C0 can be checked")
            continue
        entries = {key.value: item for key, item in zip(value.keys, value.values)
                   if isinstance(key, ast.Constant) and isinstance(key.value, str)}
        data = entries.get("data")
        if data is not None and not (isinstance(data, ast.Dict) and not data.keys):
            problems.append("snapshot() stores C0 data (line %d); a read-only task keeps only digests" % data.lineno)
        digests = entries.get("digests")
        if isinstance(digests, ast.Dict):
            if any(not isinstance(item, ast.Call) for item in digests.values):
                problems.append("snapshot() digests must be canonical_digest(...) calls")
        elif digests is not None:
            problems.append("snapshot() digests must be a dict literal of canonical_digest(...) calls")
    return problems


def _texts(source, tree):
    """(line, text) of the comments and docstrings of a module."""
    out = []
    try:
        for token in tokenize.generate_tokens(io.StringIO(source).readline):
            if token.type == tokenize.COMMENT:
                out.append((token.start[0], token.string))
    except (tokenize.TokenError, IndentationError):
        pass
    for node in [tree] + [n for n in ast.walk(tree) if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef,
                                                                       ast.ClassDef))]:
        doc = ast.get_docstring(node, clean=False)
        if doc:
            out.append((getattr(node, "lineno", 1), doc))
    return out


def _identifiers(tree):
    """(name, line) of every name the module defines: functions, classes, arguments, assigned names and attributes."""
    out = []
    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            out.append((node.name, node.lineno))
        elif isinstance(node, ast.arg):
            out.append((node.arg, node.lineno))
        elif isinstance(node, ast.Name) and isinstance(node.ctx, ast.Store):
            out.append((node.id, node.lineno))
        elif isinstance(node, ast.Attribute) and isinstance(node.ctx, ast.Store):
            out.append((node.attr, node.lineno))
        elif isinstance(node, ast.alias) and node.asname:
            out.append((node.asname, getattr(node, "lineno", 0)))
    return out


def _imports(tree):
    """(module, names, line) of every import; relative imports report module '.' + name."""
    out = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            out += [(alias.name, (), node.lineno) for alias in node.names]
        elif isinstance(node, ast.ImportFrom):
            module = ("." * node.level) + (node.module or "")
            out.append((module, tuple(alias.name for alias in node.names), node.lineno))
    return out


def _stdlib(module):
    return module.split(".")[0] in STDLIB


def check_public(found, source, tree, read_only):
    """Rules 5 and 6 for pack/tasks/<ID>/task.py."""
    for key, line in _keys(tree):
        if ROLE_RE.search(key):
            found.error(5, "task.py line %d: key %r matches the role blacklist" % (line, key))
    if read_only:
        for problem in _digest_only(_function(tree, "snapshot")):
            found.error(5, "read-only task: %s" % problem)
    for line, text in _texts(source, tree):
        match = TRAP_RE.search(text) or TRAP_ZH_RE.search(text)
        if match:
            found.error(6, "task.py line %d: comment or docstring mentions %r" % (line, match.group(0)))
    for name, line in _identifiers(tree):
        if name not in API_METHODS and ROLE_RE.search(name):
            found.error(6, "task.py line %d: identifier %r matches the role blacklist" % (line, name))
    for module, names, line in _imports(tree):
        if module == "usk_eval":
            bad = [name for name in names if name not in ("task_api", "verifylib")]
            if bad:
                found.error(6, "task.py line %d imports usk_eval.%s" % (line, ", ".join(bad)))
        elif module in ("usk_eval.task_api", "usk_eval.verifylib") or module.startswith("usk_eval.verifylib."):
            continue
        elif module.startswith(".") or not _stdlib(module):
            found.error(6, "task.py line %d imports %s (only usk_eval.task_api, usk_eval.verifylib and the standard "
                           "library)" % (line, module))


def check_private(found, tree):
    """Rule 6 for eval/pack/private/<ID>.py."""
    for module, names, line in _imports(tree):
        if module == "usk_eval":
            bad = [name for name in names if name != "verifylib"]
            if bad:
                found.error(6, "private verifier line %d imports usk_eval.%s" % (line, ", ".join(bad)))
        elif module == "usk_eval.verifylib" or module.startswith("usk_eval.verifylib."):
            continue
        elif module.startswith(".") or not _stdlib(module):
            found.error(6, "private verifier line %d imports %s (only usk_eval.verifylib and the standard library)" % (
                line, module))
        elif module.split(".")[0] in NETWORK_MODULES:
            found.error(6, "private verifier line %d imports %s (no files or connections)" % (line, module))
    for node in ast.walk(tree):
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in ("open", "exec", "eval",
                                                                                               "__import__"):
            found.error(6, "private verifier line %d calls %s()" % (node.lineno, node.func.id))


def check_solutions_imports(found, tree, private_file):
    """Rule 6 for eval/pack/solutions/<ID>.py: the private verifier only ever arrives as ctx.private."""
    stem = os.path.splitext(private_file)[0]
    for module, _names, line in _imports(tree):
        parts = module.lstrip(".").split(".")
        if "private" in parts or stem in parts:
            found.error(6, "solutions line %d imports %s; use ctx.private" % (line, module))


# ---------------------------------------------------------------- solutions (rule 7)


def check_solutions(found, solutions, ids):
    """Rule 7; `ids` are the assertion ids, None when the private verifier could not be loaded."""
    if not callable(getattr(solutions, "reference", None)):
        found.error(7, "solutions define no reference(ctx)")
    negatives = getattr(solutions, "NEGATIVES", None)
    alternatives = getattr(solutions, "ALTERNATIVES", None)
    if not isinstance(negatives, (list, tuple)) or len(negatives) < MIN_NEGATIVES:
        found.error(7, "solutions need at least %d NEGATIVES, found %d" % (
            MIN_NEGATIVES, len(negatives) if isinstance(negatives, (list, tuple)) else 0))
    expected_somewhere = set()
    names = set()
    for index, negative in enumerate(negatives if isinstance(negatives, (list, tuple)) else ()):
        if not isinstance(negative, Negative):
            found.error(7, "NEGATIVES[%d] is not a task_api.Negative" % index)
            continue
        if not isinstance(negative.name, str) or not negative.name or negative.name in names:
            found.error(7, "NEGATIVES[%d] needs a unique name, got %r" % (index, negative.name))
        names.add(negative.name)
        if not callable(negative.apply):
            found.error(7, "negative %s has no callable apply" % negative.name)
        if not negative.expect_failed:
            found.error(7, "negative %s expects no failed assertion" % negative.name)
        unknown = sorted(set(negative.expect_failed) - ids) if ids is not None else []
        if unknown:
            found.error(7, "negative %s expects unknown assertions %s" % (negative.name, ", ".join(unknown)))
        expected_somewhere |= set(negative.expect_failed)
    if not isinstance(alternatives, (list, tuple)) or len(alternatives) < MIN_ALTERNATIVES:
        found.error(7, "solutions need at least %d ALTERNATIVES" % MIN_ALTERNATIVES)
    seen = set()
    for index, item in enumerate(alternatives if isinstance(alternatives, (list, tuple)) else ()):
        if not (isinstance(item, (list, tuple)) and len(item) == 2 and isinstance(item[0], str) and item[0] and
                callable(item[1])):
            found.error(7, "ALTERNATIVES[%d] is not (name, fn)" % index)
            continue
        if item[0] in seen:
            found.error(7, "alternative %s is listed twice" % item[0])
        seen.add(item[0])
    for aid in sorted((ids or set()) - expected_somewhere, key=_natural):
        found.warn(7, "no negative is expected to fail %s" % aid)


def _natural(text):
    return [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", text)]


# ---------------------------------------------------------------- one task


def lint_task(task_id, pack_root, private_root, solutions_root, private_file=None):
    found = _Collector(task_id)
    task_dir = os.path.join(pack_root, "tasks", task_id)
    private_file = private_file or task_id + ".py"
    prompt_set = {}
    for lang in LANGS:
        path = os.path.join(task_dir, "prompt.%s.md" % lang)
        try:
            prompt_set[lang] = parse_prompt_file(_read(path), task_id, lang)
        except OSError:
            prompt_set[lang] = Prompt(lang)
            prompt_set[lang].problems.append("is missing")
    task, schema = None, None
    task_path = os.path.join(task_dir, "task.py")
    try:
        task = pack.load_task_file(task_id, task_path)
        schema = task.answer_schema()
    except ProblemError as exc:
        found.error(6, "task.py cannot be loaded: %s" % exc.problems[0]["message"])
    except Exception as exc:
        found.error(2, "answer_schema() failed: %s: %s" % (type(exc).__name__, exc))
    check_prompts(found, prompt_set, schema)
    private, private_tree = None, None
    private_path = os.path.join(private_root, private_file)
    try:
        private_tree = ast.parse(_source(private_path), private_path)
        private = _load("usk_lint_private_" + task_id, private_path)
    except (OSError, SyntaxError) as exc:
        found.error(1, "private verifier %s cannot be read: %s" % (private_file, type(exc).__name__))
    except Exception as exc:
        found.error(1, "private verifier %s fails to import: %s: %s" % (private_file, type(exc).__name__, exc))
    rows = check_assertions(found, private, prompt_set) if private is not None else []
    if private_tree is not None:
        check_private(found, private_tree)
    declared = {lang: any(marker in text.lower() for text in prompt_set[lang].reqs.values())
                for lang, marker in (("zh-CN", READ_ONLY_ZH), ("en", READ_ONLY_EN))}
    if declared["zh-CN"] != declared["en"]:
        found.error(5, "only the %s prompt declares the task read-only" % ("zh-CN" if declared["zh-CN"] else "en"))
    read_only = any(declared.values())
    try:
        source = _source(task_path)
        check_public(found, source, ast.parse(source, task_path), read_only)
    except (OSError, SyntaxError) as exc:
        found.error(6, "task.py cannot be read: %s" % type(exc).__name__)
    solutions_path = os.path.join(solutions_root, task_id + ".py")
    try:
        check_solutions_imports(found, ast.parse(_source(solutions_path), solutions_path), private_file)
        solutions = _load("usk_lint_solutions_" + task_id, solutions_path)
    except (OSError, SyntaxError) as exc:
        found.error(7, "solutions %s.py cannot be read: %s" % (task_id, type(exc).__name__))
    except Exception as exc:
        found.error(7, "solutions %s.py fail to import: %s: %s" % (task_id, type(exc).__name__, exc))
    else:
        check_solutions(found, solutions, {row[0] for row in rows} if private is not None else None)
    return found.findings


# ---------------------------------------------------------------- the pack


def _task_ids(pack_root, manifest):
    if manifest is not None:
        return pack.all_task_ids(manifest)
    root = os.path.join(pack_root, "tasks")
    try:
        names = sorted(os.listdir(root))
    except OSError:
        return []
    return [name for name in names if os.path.isfile(os.path.join(root, name, "task.py"))]


def lint(pack_root=None, private_root=None, solutions_root=None, manifest_path=None, tasks=None,
         package_version=None):
    """Every finding for the pack (rule 8 first, then rules 1-7 per task); `tasks` limits the per-task rules."""
    pack_root = os.path.abspath(pack_root or DEFAULT_PACK_ROOT)
    private_root = os.path.abspath(private_root or DEFAULT_PRIVATE_ROOT)
    solutions_root = os.path.abspath(solutions_root or DEFAULT_SOLUTIONS_ROOT)
    manifest_path = os.path.abspath(manifest_path or os.path.join(pack_root, pack.MANIFEST_NAME))
    findings = []
    report, manifest = check_eval_pack.check_pack(pack_root, private_root, manifest_path, package_version)
    findings += [Finding(None, 8, text) for text in report.errors]
    findings += [Finding(None, 8, text, "warning") for text in report.warnings]
    ids = _task_ids(pack_root, manifest)
    for task_id in [t for t in ids if not tasks or t in tasks]:
        entry = ((manifest or {}).get("tasks") or {}).get(task_id) or {}
        findings += lint_task(task_id, pack_root, private_root, solutions_root,
                              (entry.get("private") or {}).get("file"))
    for task_id in tasks or ():
        if task_id not in ids:
            findings.append(Finding(task_id, 8, "task %s is not in the pack" % task_id))
    return findings


def _parser():
    parser = argparse.ArgumentParser(description="Traceability lint for the evaluation task pack.")
    parser.add_argument("--pack-dir", help="development pack folder: manifest.json, tasks/, private/")
    parser.add_argument("--pack-root", help="folder holding manifest.json and tasks/ (default: the package pack)")
    parser.add_argument("--private-root", help="folder holding the private verifiers (default: eval/pack/private)")
    parser.add_argument("--solutions-root", help="folder holding the solutions (default: eval/pack/solutions)")
    parser.add_argument("--manifest", help="manifest file (default: <pack root>/manifest.json)")
    parser.add_argument("--tasks", help="comma-separated task ids (default: every task in the pack)")
    return parser


def main(argv=None, out=None):
    out = out or sys.stdout
    args = _parser().parse_args(argv)
    if args.pack_dir and (args.pack_root or args.private_root):
        print("lint_pack: --pack-dir excludes --pack-root and --private-root", file=out)
        return 2
    pack_root = os.path.abspath(args.pack_dir or args.pack_root or DEFAULT_PACK_ROOT)
    private_root = os.path.join(pack_root, "private") if args.pack_dir else args.private_root
    manifest = args.manifest or os.path.join(pack_root, pack.MANIFEST_NAME)
    if not os.path.isfile(manifest):
        print("lint_pack: no manifest at %s" % manifest, file=out)
        return 2
    tasks = [t.strip() for t in args.tasks.split(",") if t.strip()] if args.tasks else None
    findings = lint(pack_root, private_root, args.solutions_root, manifest, tasks,
                    check_eval_pack._package_version(check_eval_pack.PACKAGE_JSON))
    errors = [f for f in findings if f.level == "error"]
    warnings = [f for f in findings if f.level == "warning"]
    for finding in errors + warnings:
        print(finding.line(), file=out)
    print("lint_pack: %s (%d errors, %d warnings)" % ("FAILED" if errors else "OK", len(errors), len(warnings)),
          file=out)
    return 1 if errors else 0


if __name__ == "__main__":
    for _stream in (sys.stdout, sys.stderr):
        try:
            _stream.reconfigure(errors="backslashreplace")
        except (AttributeError, ValueError):
            pass
    sys.exit(main())

# Producer:Betsy
