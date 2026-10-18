#!/usr/bin/env python3
"""Compiles every C# script a drafted task's offline test feeds to script_create, against real Unity API assemblies.

usage: csharp_check.py REPO_ROOT TASK_ID [--module runner.test_pack_xxx] [--versions 2022.3.62f2,6000.3.26f1]
                       [--editors-dir DIR] [--keep DIR] [--json]

How it works: the task's own offline test module runs in-process with a recording hook on the fake editors'
script-writing skills; scripts are grouped per fake editor (one run = one project), and each group is compiled as one
assembly with the Roslyn compiler and the .NET runtime that ship inside the Unity editor installs (no Unity license,
no editor process). Compiler language level is C# 9 (what Unity 2022.3 and 6000.x accept). UnityEngine.UI and
TextMeshPro are not available offline: a group that needs them reports "unverifiable", not "fail".

Exit 0: every group that is expected to compile does. Groups of solutions whose name says they are meant to break
(compile, broken, syntax, invalid, typo) are expected to fail on every version.
"""
import glob
import hashlib
import inspect
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import unittest

sys.dont_write_bytecode = True

SCRIPT_SKILLS = ("script_create", "script_create_batch")
BREAK_WORDS = re.compile(r"(?i)compile|broken|syntax|invalid|typo|error")
UI_TYPES = re.compile(r"CS0(234|246|400)[^\n]*(UnityEngine\.UI|TMPro|UnityEngine\.EventSystems|Text|Button|Canvas)")
_state = threading.local()


def parse_args(argv):
    editors = next((p for p in (os.path.expanduser("~/Unity/Hub/Editor"), "/data/home/Unity/Hub/Editor")
                    if os.path.isdir(p)), "/data/home/Unity/Hub/Editor")
    opts = {"module": None, "versions": "2022.3.62f2,6000.3.26f1", "editors": editors, "keep": None, "json": False}
    pos, i = [], 1
    while i < len(argv):
        arg = argv[i]
        if arg == "--module":
            opts["module"] = argv[i + 1]; i += 2
        elif arg == "--versions":
            opts["versions"] = argv[i + 1]; i += 2
        elif arg == "--editors-dir":
            opts["editors"] = argv[i + 1]; i += 2
        elif arg == "--keep":
            opts["keep"] = argv[i + 1]; i += 2
        elif arg == "--json":
            opts["json"] = True; i += 1
        else:
            pos.append(arg); i += 1
    if len(pos) != 2:
        print(__doc__)
        sys.exit(2)
    opts["root"], opts["task"] = pos
    return opts


class Recorder(object):
    def __init__(self, solutions_dir):
        self.solutions_dir = os.path.realpath(solutions_dir) + os.sep
        self.groups = {}          # editor id -> {"label": str, "scripts": {key: content}, "order": [key]}

    def label(self):
        outer = None
        for frame in inspect.stack()[2:]:
            if os.path.realpath(frame.filename).startswith(self.solutions_dir):
                outer = frame.function          # the last one seen is the outermost solutions-module frame
        return outer or "build"

    def record(self, editor, skill, args):
        items = []
        if skill == "script_create":
            items = [args]
        elif skill == "script_create_batch":
            try:
                items = json.loads(args.get("items") or "[]")
            except ValueError:
                items = []
        token = getattr(editor, "_csharp_group", None)
        if token is None:
            token = len(self.groups)
            try:
                editor._csharp_group = token
            except AttributeError:
                token = id(editor)
        group = self.groups.setdefault(token, {"labels": [], "scripts": {}})
        label = self.label()
        if label not in group["labels"]:
            group["labels"].append(label)
        for item in items:
            content = item.get("content")
            if not isinstance(content, str):
                continue
            key = "%s/%s" % (item.get("folder") or "Assets/Scripts", item.get("scriptName") or item.get("name") or "Script")
            group["scripts"][key] = content


def install_hooks(recorder, pfe):
    editor_cls, client_cls = pfe.FakeEditor, pfe.FakeClient
    original_call, original_write = editor_cls.call, client_cls.write

    def call(self, skill, args, agent):
        if skill in SCRIPT_SKILLS and not getattr(_state, "in_client", False):
            recorder.record(self, skill, args)
        return original_call(self, skill, args, agent)

    def write(self, skill, **args):
        if skill in SCRIPT_SKILLS:
            recorder.record(self.editor, skill, args)
        _state.in_client = True
        try:
            return original_write(self, skill, **args)
        finally:
            _state.in_client = False

    editor_cls.call, client_cls.write = call, write


def editor_paths(editors_dir, version):
    data = os.path.join(editors_dir, version, "Editor", "Data")
    dotnet = os.path.join(data, "NetCoreRuntime", "dotnet")
    csc = os.path.join(data, "DotNetSdkRoslyn", "csc.dll")
    netstandard = os.path.join(data, "NetStandard", "ref", "2.1.0", "netstandard.dll")
    modules = sorted(glob.glob(os.path.join(data, "Managed", "UnityEngine", "UnityEngine.*Module.dll")))
    extra = []          # the monolithic UnityEngine.dll duplicates every module type (CS0433): modules only
    editor_modules = sorted(glob.glob(os.path.join(data, "Managed", "UnityEngine", "UnityEditor.*Module.dll")))
    missing = [p for p in (dotnet, csc, netstandard) if not os.path.exists(p)] + ([] if modules else ["UnityEngine modules"])
    return {"dotnet": dotnet, "csc": csc, "netstandard": netstandard, "refs": modules + editor_modules + extra,
            "missing": missing}


def compile_group(paths, sources, workdir, version):
    src_dir = os.path.join(workdir, version)
    os.makedirs(src_dir, exist_ok=True)
    files = []
    for index, (key, content) in enumerate(sorted(sources.items())):
        name = os.path.join(src_dir, "%02d_%s.cs" % (index, re.sub(r"[^A-Za-z0-9_]", "_", os.path.basename(key))))
        with io.open(name, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content)
        files.append(name)
    command = [paths["dotnet"], paths["csc"], "-nologo", "-target:library", "-nostdlib", "-langversion:9.0",
               "-out:" + os.path.join(src_dir, "out.dll"), "-r:" + paths["netstandard"]]
    command += ["-r:" + ref for ref in paths["refs"]] + files
    run = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=400, text=True)
    lines = [line for line in run.stdout.splitlines() if line.strip()]
    errors = [re.sub(r"^.*/(\d\d_[^/]+\.cs)", r"\1", line) for line in lines if " error CS" in line]
    warnings = [line for line in lines if " warning CS" in line]
    return {"ok": run.returncode == 0, "errors": errors, "warnings": len(warnings),
            "obsolete": sorted({re.search(r"warning (CS\d+): (.*?) \[", line).group(2)[:90] for line in warnings
                                if "CS0618" in line and re.search(r"warning (CS\d+): (.*?) \[", line)})[:4]}


def main(argv):
    opts = parse_args(argv)
    root, task = os.path.realpath(opts["root"]), opts["task"]
    sys.path.insert(0, os.path.join(root, "eval", "tests"))
    sys.path.insert(0, os.path.join(root, "SkillsForUnity", "Evaluation~"))
    from runner import pack_fake_editor as pfe

    module_name = opts["module"] or "runner.test_pack_%s" % task.lower()
    recorder = Recorder(os.path.join(root, "eval", "pack", "solutions"))
    install_hooks(recorder, pfe)
    suite = unittest.defaultTestLoader.loadTestsFromName(module_name)
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream, verbosity=0).run(suite)
    tests_ok = result.wasSuccessful()

    versions = [v for v in opts["versions"].split(",") if v]
    toolchains = {v: editor_paths(opts["editors"], v) for v in versions}
    usable = {v: t for v, t in toolchains.items() if not t["missing"]}
    for v, t in toolchains.items():
        if t["missing"]:
            print("WARNING: editor %s unusable, missing %s" % (v, t["missing"]))
    if not usable:
        print("csharp_check: no usable Unity editor install found")
        return 2

    scratch_parent = os.path.join(root, "eval", "runs")          # git-ignored; nothing here shows up in git status
    os.makedirs(scratch_parent, exist_ok=True)
    workdir = opts["keep"] or tempfile.mkdtemp(prefix="csharp-check-", dir=scratch_parent)
    os.makedirs(workdir, exist_ok=True)
    rows, bad = [], 0
    for number, (_editor, group) in enumerate(sorted(recorder.groups.items(), key=lambda kv: kv[1]["labels"])):
        if not group["scripts"]:
            continue
        label = "+".join(l for l in group["labels"] if l != "build") or "build"
        expect_fail = bool(BREAK_WORDS.search(label))
        normal = re.compile(r"r\d{8}T\d{6}[0-9a-f]{6}")
        digest = hashlib.sha256("\n".join("%s\n%s" % (normal.sub("<RUN>", k), normal.sub("<RUN>", v))
                                          for k, v in sorted(group["scripts"].items())).encode()).hexdigest()[:10]
        row = {"label": label, "scripts": len(group["scripts"]), "digest": digest, "expectFail": expect_fail, "versions": {}}
        group_dir = os.path.join(workdir, "g%03d_%s" % (number, re.sub(r"\W", "_", label)[:40]))
        for version, tool in usable.items():
            outcome = compile_group(tool, group["scripts"], group_dir, version)
            needs_ui = any(UI_TYPES.search(e) for e in outcome["errors"])
            outcome["status"] = ("unverifiable" if (not outcome["ok"] and needs_ui) else
                                 "ok" if outcome["ok"] else "fail")
            row["versions"][version] = outcome
        row["pass"] = all((v["status"] == "unverifiable") or (v["status"] == "fail") == expect_fail
                          for v in row["versions"].values())
        bad += 0 if row["pass"] else 1
        rows.append(row)

    seen, unique = set(), []
    for row in rows:                               # identical sources from different runs/seeds print once
        key = (row["label"], row["digest"])
        if key not in seen:
            seen.add(key)
            unique.append(row)
    if opts["json"]:
        print(json.dumps({"testsOk": tests_ok, "groups": unique, "badGroups": bad}, indent=1))
    else:
        print("tests: %s (%d run); %d script group(s), %d distinct; compile with %s" % (
            "ok" if tests_ok else "FAILED", result.testsRun, len(rows), len(unique), ", ".join(usable)))
        for row in unique:
            cells = " ".join("%s=%s" % (v.split(".")[0], row["versions"][v]["status"]) for v in row["versions"])
            flag = "ok  " if row["pass"] else "BAD "
            print("%s %-34s scripts=%d %s%s" % (flag, row["label"][:34], row["scripts"], cells,
                                                "  (expected to break)" if row["expectFail"] else ""))
            if not row["pass"] or any(v["warnings"] and v["status"] == "ok" for v in row["versions"].values()):
                for version, outcome in row["versions"].items():
                    for line in outcome["errors"][:4]:
                        print("      [%s] %s" % (version.split(".")[0], line[:230]))
                    if outcome["obsolete"]:
                        print("      [%s] obsolete API warnings: %s" % (version.split(".")[0], "; ".join(outcome["obsolete"])))
        print("csharp_check: %s (%d bad group(s))" % ("OK" if not bad and tests_ok else "FAIL", bad))
    if not opts["keep"]:
        import shutil
        shutil.rmtree(workdir, ignore_errors=True)
    return 0 if (not bad and tests_ok) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
