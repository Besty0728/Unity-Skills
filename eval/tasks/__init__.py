"""Task registry. Each task lives in tasks/<id lowercase>.py and exposes TASK (a harness.task.Task)."""
import importlib

TASK_IDS = ["S01", "S03", "S04", "S06", "S08", "D01", "T1", "T2", "T3", "T4", "T5", "T7", "T9", "T10"]
BASE_SEEDS = {tid: 7001 + i * 101 for i, tid in enumerate(TASK_IDS)}   # public seeds; reserved ones stay local


def load(task_id):
    module = importlib.import_module("tasks.%s" % task_id.lower())
    return module.TASK


def available():
    out = {}
    for tid in TASK_IDS:
        try:
            out[tid] = load(tid)
        except ImportError:
            continue
    return out


def run_seed(task_id, rep):
    """Same seed for every model in a rep, so the three subjects face identical fixtures."""
    return BASE_SEEDS[task_id] + 7919 * rep

# Producer:Betsy
