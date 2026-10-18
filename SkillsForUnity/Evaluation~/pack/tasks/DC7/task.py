"""DC7: assembly definitions to repair without deleting them, and editor code to take out of a runtime assembly
(dev, challenge).

Fixture, all under A/Scripts/<Game>: <CoreDir>/<Token>.<CoreDir>.asmdef with Stats_<k>.cs (a plain class with hp),
<DataDir>/<Token>.<DataDir>.asmdef with Tuning_<k>.cs (a static class with a constant factor), and
<PlayDir>/<Token>.<PlayDir>.asmdef, which references nothing, with Combat_<k>.cs (a MonoBehaviour computing power from
both in Awake) and, one or two folders deeper, Report_<k>.cs (an editor menu item that uses Stats_<k>). The files are
written to disk and imported with asset_refresh, so the project does not compile after the build. Scene:
R/<Arena> > <Fighter> and <Spectator>, two empty objects. Folder, assembly, class and object names, the folder depth,
the object order, hp and factor come from the seed; the names carry the run id.
"""
import hashlib
import json
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
GAMES = ("Forge", "Harbor", "Orchard", "Quarry")
CORE_DIRS = ("Core", "Shared", "Base", "Common")
DATA_DIRS = ("Data", "Tuning", "Config", "Settings")
PLAY_DIRS = ("Gameplay", "Combat", "Logic", "Runtime")
TOOL_DIRS = ("Tools", "Debug", "Utilities", "DevTools")
MIDDLE_DIRS = ("Internal", "Systems", "Parts")
ARENAS = (("Arena", "Fighter", "Spectator"), ("Ring", "Brawler", "Referee"), ("Yard", "Duelist", "Squire"),
          ("Pit", "Champion", "Herald"))
HPS = (8, 10, 12, 14)
FACTORS = (2, 3, 4, 5)
SPEC_SALT = 0xDC07

STATS_SRC = """public class {stats}
{{
    public int hp = {hp};
}}
"""

TUNING_SRC = """public static class {tuning}
{{
    public const int factor = {factor};
}}
"""

COMBAT_SRC = """using UnityEngine;

public class {combat} : MonoBehaviour
{{
    public int power;

    void Awake()
    {{
        power = new {stats}().hp * {tuning}.factor;
    }}
}}
"""

REPORT_SRC = """using UnityEditor;
using UnityEngine;

public static class {report}
{{
    [MenuItem("Tools/{report}")]
    public static void Run()
    {{
        Debug.Log(new {stats}().hp);
    }}
}}
"""


def spec(seed):
    """hp and factor, a pure function of the seed (shared with the private module)."""
    rng = random.Random(seed ^ SPEC_SALT)
    return {"hp": rng.choice(HPS), "factor": rng.choice(FACTORS)}


ASMDEF_SRC = """{{
    "name": "{name}",
    "rootNamespace": "",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}}
"""


def asmdef_json(name):
    """A runtime assembly definition in Unity's own layout, with no references."""
    return ASMDEF_SRC.format(name=name)


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def disk(project_path, asset_path):
    return os.path.join(project_path, *asset_path.split("/"))


def file_sha(project_path, asset_path):
    try:
        with open(disk(project_path, asset_path), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def meta_guid(project_path, asset_path):
    try:
        return V.uyaml.meta_guid(disk(project_path, asset_path))
    except (OSError, ValueError):
        return None


def read_json(project_path, asset_path):
    try:
        with open(disk(project_path, asset_path), "r", encoding="utf-8-sig") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def write_text(project_path, asset_path, text):
    full = disk(project_path, asset_path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def subtree_states(probe, root):
    """[[ref, go_state]] of root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append([V.object_id(ref), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


class DC7(PublicTask):
    id = "DC7"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 40
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        values = spec(ctx.seed)
        game, word = rng.choice(GAMES), rng.choice(WORDS)
        core_dir, data_dir, play_dir, tool_dir = (rng.choice(CORE_DIRS), rng.choice(DATA_DIRS), rng.choice(PLAY_DIRS),
                                                  rng.choice(TOOL_DIRS))
        middle = rng.choice(MIDDLE_DIRS) if rng.random() < 0.5 else None
        arena_name, fighter_name, other_name = rng.choice(ARENAS)
        fighter_first = rng.random() < 0.5
        k = "%s_%s" % (word, ctx.scope.run_id)
        token = "%s_%s" % (game, k)
        folder = ctx.asset("Scripts", game)
        names = {"stats": "Stats_" + k, "tuning": "Tuning_" + k, "combat": "Combat_" + k, "report": "Report_" + k}
        tools = "/".join(part for part in (folder, play_dir, middle, tool_dir) if part)
        paths = {
            "coreAsmdef": "%s/%s/%s.%s.asmdef" % (folder, core_dir, token, core_dir),
            "dataAsmdef": "%s/%s/%s.%s.asmdef" % (folder, data_dir, token, data_dir),
            "playAsmdef": "%s/%s/%s.%s.asmdef" % (folder, play_dir, token, play_dir),
            "statsScript": "%s/%s/%s.cs" % (folder, core_dir, names["stats"]),
            "tuningScript": "%s/%s/%s.cs" % (folder, data_dir, names["tuning"]),
            "combatScript": "%s/%s/%s.cs" % (folder, play_dir, names["combat"]),
            "reportScript": "%s/%s.cs" % (tools, names["report"]),
        }
        texts = {
            "coreAsmdef": asmdef_json("%s.%s" % (token, core_dir)),
            "dataAsmdef": asmdef_json("%s.%s" % (token, data_dir)),
            "playAsmdef": asmdef_json("%s.%s" % (token, play_dir)),
            "statsScript": STATS_SRC.format(stats=names["stats"], hp=values["hp"]),
            "tuningScript": TUNING_SRC.format(tuning=names["tuning"], factor=values["factor"]),
            "combatScript": COMBAT_SRC.format(combat=names["combat"], stats=names["stats"], tuning=names["tuning"]),
            "reportScript": REPORT_SRC.format(report=names["report"], stats=names["stats"]),
        }
        for key in sorted(paths):
            write_text(ctx.project_path, paths[key], texts[key])
        res = c.write("asset_refresh")
        if isinstance(res, dict) and res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        missing = [path for path in paths.values() if not ctx.probe.guid(path)]
        if missing:
            raise RuntimeError("DC7 fixture: not imported: %s" % ", ".join(missing))

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        arena = make(arena_name, ctx.root, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
        order = [fighter_name, other_name] if fighter_first else [other_name, fighter_name]
        made = {name: make(name, arena, x=1 + index, y=0, z=0) for index, name in enumerate(order)}
        return dict(paths, folder=folder, scripts=ctx.asset("Scripts"), coreAsm="%s.%s" % (token, core_dir),
                    dataAsm="%s.%s" % (token, data_dir), playAsm="%s.%s" % (token, play_dir),
                    editorAsm="%s.Editor" % token, arena=arena, arenaPath=ctx.path(arena_name),
                    fighter=made[fighter_name], fighterPath=ctx.path(arena_name, fighter_name))

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        keys = ("coreAsmdef", "dataAsmdef", "playAsmdef", "statsScript", "tuningScript", "combatScript", "reportScript")
        files = {key: {"path": h[key], "sha": file_sha(project, h[key]), "guid": meta_guid(project, h[key])}
                 for key in keys}
        asmdefs = {key: json.dumps(read_json(project, h[key]), sort_keys=True)
                   for key in ("coreAsmdef", "dataAsmdef", "playAsmdef")}
        return {"data": {"files": files, "asmdefs": asmdefs, "scripts": cs_files(project),
                         "arena": subtree_states(p, h["arena"])},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        values = spec(ctx.seed)

        def cls(key):
            return os.path.basename(h[key])[:-len(".cs")]

        return {"scripts": h["scripts"], "folder": h["folder"], "coreAsmdef": h["coreAsmdef"],
                "dataAsmdef": h["dataAsmdef"], "playAsmdef": h["playAsmdef"], "coreAsm": h["coreAsm"],
                "playAsm": h["playAsm"], "editorAsm": h["editorAsm"], "editorScript": h["reportScript"],
                "combatCls": cls("combatScript"), "statsCls": cls("statsScript"), "tuningCls": cls("tuningScript"),
                "fighter": h["fighterPath"], "arena": h["arenaPath"], "power": values["hp"] * values["factor"],
                "hp": values["hp"], "factor": values["factor"]}

    def answer_schema(self):
        def path(zh, en):
            return {"type": "string", "desc": {"zh-CN": zh, "en": en}}

        return {
            "type": "object",
            "required": ["editorAsmdefPath", "editorScriptPath", "fighterPath"],
            "properties": {
                "editorAsmdefPath": path("新建的 Editor 程序集定义文件的资产路径",
                                         "asset path of the new Editor assembly definition file"),
                "editorScriptPath": path("修复后编辑器脚本的资产路径", "asset path of the editor script after the repair"),
                "fighterPath": path("挂载了战斗组件的对象的完整层级路径",
                                    "full hierarchy path of the object carrying the combat component"),
            },
        }


TASK = DC7()

# Producer:Betsy
