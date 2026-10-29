"""DC2: a door state machine (Closed / Opening / Open / Closing) whose openness moves on scaled game time (dev,
challenge).

Fixture:
- A/Scripts/<Recorder>_<word>_<run>.cs compiled at build: a MonoBehaviour that, in LateUpdate (execution order 10000),
  reads the public members `openness` and `state` of every MonoBehaviour on its `door` GameObject by reflection and
  stores them with Time.time and Time.frameCount in its own public fields.
- R/<Hall> (seeded position and yaw) > <Frame> > <Door> (Cube), <Spare> (Cube, under <Frame> or <Hall>) and
  <Monitor> (empty) carrying the recorder component with `door` = <Door>.
- Object names, their creation order, the class names and travelSeconds come from the seed.
"""
import hashlib
import os
import random

from usk_eval import verifylib as V
from usk_eval.task_api import PublicTask, address

WORDS = ("Kestrel", "Talon", "Vortex", "Ember", "Quill", "Harrow", "Juniper", "Marlow")
HALL_NAMES = ("Hangar", "Vault", "Atrium", "Depot")
FRAME_NAMES = ("Frame", "Portal", "Jamb")
DOOR_NAMES = ("Leaf", "Slab", "Panel", "Shutter")
SPARE_NAMES = ("Crate", "Pillar", "Bench")
MONITOR_NAMES = ("Monitor", "Logger", "Watch")
CLASS_STEMS = ("DoorMachine", "HatchDoor", "GateController")
RECORDER_STEMS = ("DoorRecorder", "LeafMonitor", "GateLog")
YAWS = (25, 40, 55, 70, -30, -45, -65)

RECORDER_SRC = """using System;
using System.Reflection;
using UnityEngine;

[DefaultExecutionOrder(10000)]
public class @CLS@ : MonoBehaviour
{
    public GameObject door;
    public float sampleTime = -1f;
    public int sampleFrame = -1;
    public float sampleOpenness = -1f;
    public string sampleState = "";
    public int sampleCount;

    void LateUpdate()
    {
        sampleTime = Time.time;
        sampleFrame = Time.frameCount;
        sampleCount = 0;
        sampleOpenness = -1f;
        sampleState = "";
        if (door == null) return;
        foreach (var part in door.GetComponents<MonoBehaviour>())
        {
            if (part == null) continue;
            var kind = part.GetType();
            object open = Member(part, kind, "openness");
            if (!(open is float)) continue;
            sampleCount++;
            sampleOpenness = (float)open;
            object now = Member(part, kind, "state");
            sampleState = now == null ? "" : now.ToString();
        }
    }

    static object Member(object owner, Type kind, string name)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var field = kind.GetField(name, flags);
            if (field != null) return field.GetValue(owner);
            var property = kind.GetProperty(name, flags);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                return property.GetValue(owner, null);
        }
        catch (Exception)
        {
        }
        return null;
    }
}
"""


def travel_seconds(seed):
    """travelSeconds the scene must carry, from the seed alone."""
    return random.Random("DC2:%d" % seed).choice((2.5, 3.0, 3.5))


def cs_files(project_path):
    out = []
    for base, dirs, files in os.walk(os.path.join(project_path, "Assets")):
        dirs.sort()
        out += [os.path.relpath(os.path.join(base, name), project_path).replace(os.sep, "/")
                for name in sorted(files) if name.endswith(".cs")]
    return sorted(out)


def file_sha(project_path, asset_path):
    try:
        with open(os.path.join(project_path, *asset_path.split("/")), "rb") as handle:
            return hashlib.sha256(handle.read()).hexdigest()
    except OSError:
        return None


def serialized_rows(probe, ref, component_type):
    """[[propertyPath, value]] of a component, sorted by path (rows, so Unity names never become keys)."""
    return sorted([key, value] for key, value in (probe.ser(ref, component_type) or {}).items())


class DC2(PublicTask):
    id = "DC2"
    track = "dev"
    tier = "challenge"
    case_version = 1
    minutes = 40
    requires = {"pipeline": "URP", "mode": "bypass", "dedicated": True, "packages": []}
    needs_focus = True

    def build(self, ctx):
        c, rng = ctx.c, ctx.rng
        hall_name, frame_name = rng.choice(HALL_NAMES), rng.choice(FRAME_NAMES)
        door_name, spare_name, monitor_name = rng.choice(DOOR_NAMES), rng.choice(SPARE_NAMES), rng.choice(MONITOR_NAMES)
        word = rng.choice(WORDS)
        cls = "%s_%s_%s" % (rng.choice(CLASS_STEMS), word, ctx.scope.run_id)
        recorder = "%s_%s_%s" % (rng.choice(RECORDER_STEMS), word, ctx.scope.run_id)
        spare_in_frame = rng.random() < 0.5
        monitor_first = rng.random() < 0.5
        scripts_dir = ctx.asset("Scripts")

        res = c.write("script_create", scriptName=recorder, folder=scripts_dir,
                      content=RECORDER_SRC.replace("@CLS@", recorder))
        if isinstance(res, dict) and res.get("jobId"):
            ctx.wait_job(res["jobId"])
        ctx.wait_idle()
        recorder_script = "%s/%s.cs" % (scripts_dir, recorder)
        if not ctx.probe.guid(recorder_script):
            raise RuntimeError("DC2 fixture: the recorder script was not imported: %s" % recorder_script)

        def make(name, parent, **args):
            args.update(address(parent, "parent"))
            return V.object_id(c.write("gameobject_create", name=name, **args))

        hall = make(hall_name, ctx.root, x=rng.randint(-4, 4), y=0, z=rng.randint(-4, 4), rotY=rng.choice(YAWS))
        monitor = make(monitor_name, hall, x=rng.choice((-2.0, 2.0)), y=0.0, z=-1.5) if monitor_first else None
        frame = make(frame_name, hall, x=0.0, y=0.0, z=rng.choice((0.5, 1.0, 1.5)))
        door = make(door_name, frame, primitiveType="Cube", x=0.0, y=1.25, z=0.0, scaleX=1.2, scaleY=2.5, scaleZ=0.15)
        spare_parent = frame if spare_in_frame else hall
        spare = make(spare_name, spare_parent, primitiveType="Cube", x=rng.choice((-1.5, 1.5)), y=0.5, z=-0.5)
        if monitor is None:
            monitor = make(monitor_name, hall, x=rng.choice((-2.0, 2.0)), y=0.0, z=-1.5)
        door_path = ctx.path(hall_name, frame_name, door_name)

        c.write("component_add", componentType=recorder, **address(monitor))
        c.write("component_set_serialized_property", componentType=recorder, propertyPath="door",
                referencePath=door_path, **address(monitor))
        ctx.wait_idle()

        return {
            "hall": hall, "hallPath": ctx.path(hall_name),
            "frame": frame, "framePath": ctx.path(hall_name, frame_name),
            "door": door, "doorPath": door_path,
            "spare": spare, "sparePath": ctx.path(*([hall_name, frame_name, spare_name] if spare_in_frame
                                                    else [hall_name, spare_name])),
            "monitor": monitor, "monitorPath": ctx.path(hall_name, monitor_name),
            "recorder": recorder, "recorderScript": recorder_script,
            "script": "%s/%s.cs" % (scripts_dir, cls),
        }

    def snapshot(self, ctx):
        p, h, project = ctx.probe, ctx.handles, ctx.project_path
        return {
            "data": {
                "hall": V.go_state(p, h["hall"]),
                "frame": V.go_state(p, h["frame"]),
                "door": V.go_state(p, h["door"]),
                "spare": V.go_state(p, h["spare"]),
                "monitor": V.go_state(p, h["monitor"]),
                "recorderRows": serialized_rows(p, h["monitor"], h["recorder"]),
                "scripts": [[path, file_sha(project, path)] for path in cs_files(project)],
            },
            "digests": {},
        }

    def prompt_vars(self, ctx):
        h = ctx.handles
        travel = travel_seconds(ctx.seed)
        return {
            "scripts": ctx.asset("Scripts"),
            "cls": os.path.basename(h["script"])[:-len(".cs")],
            "door": h["doorPath"],
            "monitor": h["monitorPath"],
            "spare": h["sparePath"],
            "travel": int(travel) if float(travel).is_integer() else travel,
        }

    def answer_schema(self):
        return {
            "type": "object",
            "required": ["scriptPath", "doorPath"],
            "properties": {
                "scriptPath": {"type": "string", "desc": {"zh-CN": "新建脚本的资产路径",
                                                          "en": "asset path of the new script"}},
                "doorPath": {"type": "string", "desc": {"zh-CN": "挂载了门状态机组件的对象的完整层级路径",
                                                        "en": "full hierarchy path of the object carrying the door "
                                                              "component"}},
            },
        }


TASK = DC2()

# Producer:Betsy
