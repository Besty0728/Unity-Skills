"""DC5: a pause menu, with buttons built by skills and a PauseController script wired through persistent onClick
listeners (dev, challenge).

Fixture: R/<Canvas> (world-space Canvas) > <Panel>, an inactive panel; R/<Archive> > <Host> and R/<Systems> [> <Group>]
> <Host>, two empty objects sharing a name (the one under <Archive> comes first in the hierarchy; the prompt names the
other by its path). The canvas comes before or after the two groups, and the named host sits one level deeper or not,
both from the seed. The script's class name carries a seeded word and the run id; the button names come from the seed.
"""
import os

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
CANVAS_NAMES = ("MenuCanvas", "HudCanvas", "OverlayCanvas", "GameCanvas")
PANEL_NAMES = ("PausePanel", "PauseMenu", "PauseOverlay")
HOST_NAMES = ("GameFlow", "PauseManager", "FlowControl", "SessionCore")
FOLDER_NAMES = (("Systems", "Archive"), ("Managers", "Backup"), ("Runtime", "Spare"), ("Services", "Storage"))
GROUP_NAMES = ("Flow", "Core", "Main")
BUTTON_NAMES = (("PauseButton", "ResumeButton"), ("BtnPause", "BtnResume"), ("Pause", "Resume"),
                ("PauseKey", "ContinueKey"))
CANVAS_SIZE = (1280, 720)


def class_name(script_path):
    return os.path.basename(script_path)[:-len(".cs")]


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def subtree_states(probe, root):
    """[[ref, go_state]] of root and every descendant, depth first."""
    out, stack = [], [root]
    while stack:
        ref = stack.pop()
        state = V.go_state(probe, ref)
        out.append([V.object_id(ref), state])
        stack.extend(reversed((state or {}).get("children") or []))
    return out


class DC5(PublicTask):
    id = "DC5"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 35
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": ["com.unity.ugui"]}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        canvas_name, panel_name, host_name = rng.choice(CANVAS_NAMES), rng.choice(PANEL_NAMES), rng.choice(HOST_NAMES)
        systems_name, archive_name = rng.choice(FOLDER_NAMES)
        pause_name, resume_name = rng.choice(BUTTON_NAMES)
        group_name = rng.choice(GROUP_NAMES) if rng.random() < 0.5 else None
        canvas_first = rng.random() < 0.5
        cls = "PauseController_%s_%s" % (rng.choice(WORDS), ctx.scope.run_id)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        def menu():
            made = V.object_id(c.write("ui_create_canvas", name=canvas_name, renderMode="WorldSpace"))
            c.write("gameobject_set_parent", **dict(address(made, "child"), **address(ctx.root, "parent")))
            c.write("ui_set_rect_transform", path=ctx.path(canvas_name), anchorMinX=0.5, anchorMinY=0.5,
                    anchorMaxX=0.5, anchorMaxY=0.5, pivotX=0.5, pivotY=0.5, anchoredPosX=0, anchoredPosY=0,
                    sizeDeltaX=CANVAS_SIZE[0], sizeDeltaY=CANVAS_SIZE[1])
            sheet = V.object_id(c.write("ui_create_panel", name=panel_name, parent=canvas_name, r=0, g=0, b=0, a=0.6))
            c.write("gameobject_set_active", active=False, **address(sheet))
            return made, sheet

        if canvas_first:
            canvas, panel = menu()
        archive = make(archive_name, ctx.root, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
        make(host_name, archive)
        systems = make(systems_name, ctx.root, x=rng.randint(-5, 5), y=0, z=rng.randint(-5, 5))
        holder = make(group_name, systems) if group_name else systems
        host = make(host_name, holder)
        if not canvas_first:
            canvas, panel = menu()
        host_path = ctx.path(*[part for part in (systems_name, group_name, host_name) if part])
        canvas_path = ctx.path(canvas_name)
        return {"canvas": canvas, "canvasPath": canvas_path, "panel": panel, "panelPath": canvas_path + "/" + panel_name,
                "host": host, "hostPath": host_path, "archive": archive, "archivePath": ctx.path(archive_name),
                "systems": systems, "pauseName": pause_name, "resumeName": resume_name,
                "script": ctx.asset("Scripts", cls + ".cs")}

    def snapshot(self, ctx):
        p, h = ctx.probe, ctx.handles
        return {"data": {"canvas": subtree_states(p, h["canvas"]), "archive": subtree_states(p, h["archive"]),
                         "systems": subtree_states(p, h["systems"]), "scripts": cs_files(ctx.project_path)},
                "digests": {}}

    def prompt_vars(self, ctx):
        h = ctx.handles
        return {"scripts": ctx.asset("Scripts"), "cls": class_name(h["script"]), "canvas": h["canvasPath"],
                "panel": h["panelPath"], "host": h["hostPath"], "pauseBtn": h["pauseName"],
                "resumeBtn": h["resumeName"]}

    def answer_schema(self):
        def path(zh, en):
            return {"type": "string", "desc": {"zh-CN": zh, "en": en}}

        return {
            "type": "object",
            "required": ["scriptPath", "hostPath", "pauseButtonPath", "resumeButtonPath"],
            "properties": {
                "scriptPath": path("脚本的资产路径", "asset path of the script"),
                "hostPath": path("挂载了控制器组件的对象的完整层级路径",
                                 "full hierarchy path of the object carrying the controller component"),
                "pauseButtonPath": path("暂停按钮的完整层级路径", "full hierarchy path of the pause button"),
                "resumeButtonPath": path("继续按钮的完整层级路径", "full hierarchy path of the resume button"),
            },
        }


TASK = DC5()

# Producer:Betsy
