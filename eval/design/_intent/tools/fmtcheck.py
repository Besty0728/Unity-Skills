#!/usr/bin/env python3
"""BOM / tail / line-ending / Python 3.9 compatibility checks for pack files.

usage: fmtcheck.py FILE [FILE ...]; exit 1 on a problem.
The 3.9 check parses with feature_version=(3, 9) and flags runtime-evaluated `X | Y` annotations and a few
3.10+ stdlib names; it is a static stand-in for running the files on a 3.9 interpreter.
"""
import ast
import re
import sys

BOM = b"\xef\xbb\xbf"
NEW_STDLIB = re.compile(r"\b(tomllib|itertools\.pairwise|StrEnum|datetime\.UTC|TypeAlias|ExceptionGroup|aiter|anext)\b"
                        r"|zip\([^)]*strict\s*=|dataclass\([^)]*slots\s*=|\.bit_count\(")


def _bitor_annotations(tree):
    found = []

    def has_bitor(node):
        return node is not None and any(isinstance(n, ast.BinOp) and isinstance(n.op, ast.BitOr)
                                        for n in ast.walk(node))

    for node in ast.walk(tree):
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            args = node.args
            every = args.posonlyargs + args.args + args.kwonlyargs + [a for a in (args.vararg, args.kwarg) if a]
            for arg in every:
                if has_bitor(arg.annotation):
                    found.append(arg.lineno)
            if has_bitor(node.returns):
                found.append(node.lineno)
        elif isinstance(node, ast.AnnAssign) and has_bitor(node.annotation):
            found.append(node.lineno)
    return found


def check(path):
    problems = []
    raw = open(path, "rb").read()
    if b"\r" in raw:
        problems.append("contains CR")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as exc:
        return ["not UTF-8: %s" % exc]
    if path.endswith(".py"):
        if not raw.startswith(BOM):
            problems.append("missing UTF-8 BOM")
        elif raw[3:6] == BOM:
            problems.append("double BOM")
        if not raw.endswith(b"\n\n# Producer:Betsy\n"):
            problems.append("tail is not exactly '\\n\\n# Producer:Betsy\\n'")
        elif raw.endswith(b"\n\n\n# Producer:Betsy\n"):
            problems.append("more than one blank line before the producer line")
        if b"\t" in raw:
            problems.append("contains tab characters")
        try:
            tree = ast.parse(text.lstrip("﻿"), path, feature_version=(3, 9))
        except SyntaxError as exc:
            problems.append("not valid Python 3.9 syntax: %s (line %s)" % (exc.msg, exc.lineno))
        else:
            lines = _bitor_annotations(tree)
            if lines:
                problems.append("runtime-evaluated `X | Y` annotation at line(s) %s" % lines[:5])
        for number, line in enumerate(text.splitlines(), 1):
            if NEW_STDLIB.search(line):
                problems.append("3.10+ stdlib use at line %d: %s" % (number, line.strip()[:80]))
    elif path.endswith(".md"):
        if raw.startswith(BOM):
            problems.append("md has a BOM")
        if raw and not raw.endswith(b"\n"):
            problems.append("md lacks a final newline")
    return problems


def main(argv):
    bad = 0
    for path in argv[1:]:
        problems = check(path)
        print("%s: %s" % (path, "ok" if not problems else "; ".join(problems)))
        bad += bool(problems)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
