"""Environment snapshot and drift (contract 2.8), check helpers, skill-doc scan, dedicated marker (7.4), gate keys (9)."""
import glob
import hashlib
import json
import os
import platform
import sys

from . import store

HEALTH_KEYS = ("version", "unityVersion", "instanceId", "projectName", "currentMode", "tokenLevel", "surfaceProfile",
               "dryRunPolicy", "summaryAutoTruncate", "summaryPageSize", "panelApprovalRequired", "allowlistCount")
DRIFT_KEYS = ("version", "instanceId", "currentMode", "tokenLevel", "surfaceProfile", "dryRunPolicy",
              "summaryAutoTruncate", "summaryPageSize", "panelApprovalRequired", "allowlistCount", "pipelineType", "pid")
PIPELINES = ("BuiltIn", "URP", "HDRP", "Custom")
REFERENCE_DRY_RUN = "off"
REFERENCE_TOKEN_LEVEL = "maximum"
MARKER_KIND = "UnitySkillsEvaluationProject"
PROJECT_DOC_ROOTS = (".claude", ".agents", ".cursor", ".opencode", ".kimi-code")
AGENT_CONFIG = "scripts/agent_config.json"


def snapshot(client, instance_pid, health=None):
    """Settings that must not drift during an attempt, plus the pipeline facts tasks read through ctx.env."""
    health = health if health is not None else client.health()
    snap = {key: health.get(key) for key in HEALTH_KEYS}
    pipeline = client.read("project_get_render_pipeline")
    snap.update({
        "pipelineType": pipeline.get("pipelineType"),
        "pipelineName": pipeline.get("pipelineName"),
        "defaultShader": pipeline.get("defaultShader"),
        "colorProperty": pipeline.get("colorProperty"),
        "pid": instance_pid,
    })
    return snap


def drift(before, after):
    before, after = before or {}, after or {}
    return {key: [before.get(key), after.get(key)] for key in DRIFT_KEYS if before.get(key) != after.get(key)}


def busy(health):
    comp = (health or {}).get("compilation") or {}
    return bool(comp.get("isCompiling") or comp.get("isUpdating") or (health or {}).get("domainReloadPending"))


def telemetry_enabled(client):
    """/analytics carries telemetryEnabled (30 s cache, fine for a switch); None when unreadable."""
    try:
        value = client.get("/analytics", {"window": "1h"}).get("telemetryEnabled")
    except Exception:
        return None
    return value if isinstance(value, bool) else None


def python_info():
    return {"version": platform.python_version(), "executable": sys.executable}


def os_name():
    return platform.platform()


# ---------------------------------------------------------------- hashing


def _file_digest(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read().replace(b"\r\n", b"\n")).hexdigest()


def hash_files(entries):
    """sha256 over sorted (relpath, LF-normalised content hash) pairs, the v1 env.py tree rule."""
    digest = hashlib.sha256()
    for rel, path in sorted(entries):
        digest.update(rel.encode("utf-8") + b"\0" + _file_digest(path).encode("ascii") + b"\n")
    return digest.hexdigest()


def tree_files(root, exclude=()):
    root = os.path.abspath(root)
    out = []
    for base, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if not d.startswith(".") and d != "__pycache__")
        for name in sorted(files):
            if name.startswith(".") or name.endswith((".pyc", ".pyo")):
                continue
            path = os.path.join(base, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            if rel not in exclude:
                out.append((rel, path))
    return out


def tree_hash(root, exclude=()):
    """LF-normalised path+content hash of a directory tree (v1 eval/harness/env.py:26-42)."""
    return hash_files(tree_files(root, exclude))


# ---------------------------------------------------------------- dedicated marker


def marker(marker_path):
    try:
        with open(marker_path, "rb") as handle:
            raw = handle.read()
        data = json.loads(raw.decode("utf-8-sig"))
    except (OSError, ValueError):
        return {"dedicated": False, "markerSha256": None, "environment": "user-project"}
    ok = isinstance(data, dict) and type(data.get("schema")) is int and data.get("schema") == 1 \
        and data.get("kind") == MARKER_KIND
    return {"dedicated": ok, "markerSha256": store.sha256_bytes(raw) if ok else None,
            "environment": "dedicated" if ok else "user-project"}


# ---------------------------------------------------------------- installed skill docs


def _home(environ):
    return environ.get("HOME") or environ.get("USERPROFILE") or os.path.expanduser("~")


def skill_doc_dirs(project, environ):
    home = _home(environ)
    kimi = environ.get("KIMI_CODE_HOME") or os.path.join(home, ".kimi-code")
    dirs = [os.path.join(project, root, "skills", "unity-skills") for root in PROJECT_DOC_ROOTS]
    dirs += [os.path.join(home, ".claude", "skills", "unity-skills"),
             os.path.join(home, ".gemini", "antigravity", "skills", "unity-skills"),
             os.path.join(home, ".agents", "skills", "unity-skills"),
             os.path.join(home, ".cursor", "skills", "unity-skills"),
             os.path.join(home, ".config", "opencode", "skills", "unity-skills"),
             os.path.join(kimi, "skills", "unity-skills")]
    return dirs


def skill_docs(project, environ):
    out, seen = [], set()
    for directory in skill_doc_dirs(project, environ):
        if not os.path.isdir(directory):
            continue
        key = os.path.normcase(os.path.realpath(directory))
        if key in seen:
            continue
        seen.add(key)
        config = {}
        try:
            with open(os.path.join(directory, *AGENT_CONFIG.split("/")), "r", encoding="utf-8-sig") as handle:
                loaded = json.load(handle)
            config = loaded if isinstance(loaded, dict) else {}
        except (OSError, ValueError):
            pass
        try:
            tree = tree_hash(directory, exclude=(AGENT_CONFIG,))
        except OSError:
            tree = None
        out.append({"path": directory, "agentId": config.get("agentId"), "version": config.get("version"),
                    "treeHash": tree})
    return out


# ---------------------------------------------------------------- gate keys (contract 9)


def gate_inputs_hash(repo_root, task_id, extra=()):
    """Per-task gateInputsHash over the runner (minus usage/ and report.py), the task's files and extra repo files."""
    runner = os.path.join(repo_root, "SkillsForUnity", "Evaluation~")
    entries = []

    def add(path):
        if os.path.isfile(path):
            entries.append((os.path.relpath(path, repo_root).replace(os.sep, "/"), path))

    add(os.path.join(runner, "run_eval.py"))
    for rel, path in tree_files(os.path.join(runner, "usk_eval")):
        if rel.endswith(".py") and not rel.startswith("usage/") and rel != "report.py":
            add(path)
    for _rel, path in tree_files(os.path.join(runner, "pack", "tasks", task_id)):
        add(path)
    add(os.path.join(repo_root, "eval", "pack", "private", task_id + ".py"))
    add(os.path.join(repo_root, "eval", "pack", "solutions", task_id + ".py"))
    add(os.path.join(repo_root, "eval", "cli.py"))
    for rel in extra:
        for path in sorted(glob.glob(os.path.join(repo_root, *rel.split("/")))):
            add(path)
    return hash_files(sorted(set(entries)))


def gate_key(task_id, case_version, unity_version, plugin_version, inputs_hash):
    return store.canonical_digest({"taskId": task_id, "caseVersion": case_version,
                                   "unityMajor": str(unity_version or "").split(".", 1)[0],
                                   "pluginVersion": plugin_version, "gateInputsHash": inputs_hash})

# Producer:Betsy
