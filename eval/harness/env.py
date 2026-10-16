"""Environment snapshot, drift detection, clean-project heuristic and the frozen docs snapshot.

Mode, tier and surface are global EditorPrefs shared by every editor on the machine and are only
writable from the panel, so the harness never sets them: it records them and treats any change
during an attempt as environment drift (plan section 5.2, global-prefs coupling).
"""
import glob
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys

HEALTH_KEYS = ("version", "unityVersion", "instanceId", "projectName", "currentMode", "tokenLevel",
               "surfaceProfile", "dryRunPolicy", "summaryAutoTruncate", "summaryPageSize",
               "panelApprovalRequired", "allowlistCount")
DRIFT_KEYS = ("version", "instanceId", "currentMode", "tokenLevel", "surfaceProfile", "dryRunPolicy",
              "summaryAutoTruncate", "summaryPageSize", "panelApprovalRequired", "allowlistCount",
              "pipelineType", "pluginHash")
PLUGIN_ID = "com.besty.unity-skills"
EVAL_ASSET_ROOT = "Assets/__UnitySkillsEval"


def tree_hash(root):
    """LF-normalised path+content hash, same rule as temp/doc-bench/round2/fingerprint.py."""
    root = os.path.abspath(root)
    digest = hashlib.sha256()
    count = 0
    for base, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if not d.startswith(".") and d != "__pycache__")
        for name in sorted(files):
            if name.startswith(".") or name.endswith((".pyc", ".pyo")):
                continue
            path = os.path.join(base, name)
            with open(path, "rb") as handle:
                data = handle.read().replace(b"\r\n", b"\n")
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            digest.update(rel.encode("utf-8") + b"\0" + hashlib.sha256(data).hexdigest().encode("ascii") + b"\n")
            count += 1
    return digest.hexdigest(), count


def plugin_lock(project_path):
    with open(os.path.join(project_path, "Packages", "packages-lock.json"), "r", encoding="utf-8") as handle:
        entry = (json.load(handle).get("dependencies") or {}).get(PLUGIN_ID) or {}
    return {"version": entry.get("version"), "source": entry.get("source"), "hash": entry.get("hash")}


def plugin_cache_dir(project_path):
    matches = sorted(glob.glob(os.path.join(project_path, "Library", "PackageCache", PLUGIN_ID + "@*")))
    if not matches:
        raise RuntimeError("installed %s not found under Library/PackageCache" % PLUGIN_ID)
    return matches[-1]


def freeze_docs(project_path, store_root):
    """Copy the installed package's unity-skills~ once per plugin build; return (dir, treeHash)."""
    source = os.path.join(plugin_cache_dir(project_path), "unity-skills~")
    source_hash, _ = tree_hash(source)
    dest = os.path.join(store_root, source_hash[:16], "docs")
    if not os.path.isdir(dest):
        tmp = dest + ".tmp"
        shutil.rmtree(tmp, ignore_errors=True)
        shutil.copytree(source, tmp)
        os.rename(tmp, dest)
    frozen_hash, files = tree_hash(dest)
    if frozen_hash != source_hash:
        raise RuntimeError("frozen docs %s differ from installed copy" % dest)
    return dest, frozen_hash, files


def _version(cmd):
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        return (out.stdout or out.stderr).strip().splitlines()[0] if (out.stdout or out.stderr) else None
    except Exception as exc:
        return "unavailable: %r" % exc


def snapshot(client, project_path, include_tools=True):
    health = client.health()
    snap = {k: health.get(k) for k in HEALTH_KEYS}
    info = client.read("project_get_info")
    pipeline = client.read("project_get_render_pipeline")
    snap.update({
        "productName": info.get("productName"),
        "platform": info.get("platform"),
        "pipelineType": pipeline.get("pipelineType"),
        "pipelineName": pipeline.get("pipelineName"),
        "defaultShader": pipeline.get("defaultShader"),
        "colorProperty": pipeline.get("colorProperty"),
        "compilation": health.get("compilation"),
    })
    lock = plugin_lock(project_path)
    snap["plugin"] = lock
    snap["pluginHash"] = lock.get("hash")
    if include_tools:
        snap["tools"] = {
            "claude": _version(["claude", "--version"]),
            "codex": _version(["codex", "--version"]),
            "unityCli": _version([os.path.expanduser("~/.unity/bin/unity"), "--version"]),
            "python": sys.version.split()[0],
            "os": platform.platform(),
        }
    return snap


def drift(before, after):
    return {k: (before.get(k), after.get(k)) for k in DRIFT_KEYS if before.get(k) != after.get(k)}


def requirement_problems(snap, require_pipeline="URP", require_mode="bypass"):
    problems = []
    if snap.get("pipelineType") != require_pipeline:
        problems.append("render pipeline is %s, the task bank requires %s (create the project from the "
                        "Universal 3D template)" % (snap.get("pipelineType"), require_pipeline))
    if snap.get("currentMode") != require_mode:
        problems.append("operating mode is %s; the calibration protocol records runs under %s only"
                        % (snap.get("currentMode"), require_mode))
    comp = snap.get("compilation") or {}
    if comp.get("isCompiling") or comp.get("isUpdating"):
        problems.append("editor is compiling or importing")
    return problems


def user_content(project_path):
    """Asset files outside the eval root, used to judge whether the project is a clean one."""
    root = os.path.join(project_path, "Assets")
    found = []
    for base, dirs, files in os.walk(root):
        rel_base = os.path.relpath(base, project_path).replace(os.sep, "/")
        if rel_base == EVAL_ASSET_ROOT or rel_base.startswith(EVAL_ASSET_ROOT + "/"):
            dirs[:] = []
            continue
        for name in files:
            if not name.endswith(".meta"):
                found.append(rel_base + "/" + name)
    return sorted(found)


def cleanliness(project_path, baseline_path):
    """Compare user content with the baseline recorded at setup; no baseline means unknown."""
    current = user_content(project_path)
    if not os.path.exists(baseline_path):
        return {"clean": None, "reason": "no baseline recorded", "files": len(current)}
    with open(baseline_path, "r", encoding="utf-8") as handle:
        baseline = set(json.load(handle)["files"])
    extra = [f for f in current if f not in baseline]
    missing = [f for f in baseline if f not in set(current)]
    return {"clean": not extra and not missing, "extra": extra[:50], "missing": missing[:50],
            "extraCount": len(extra), "missingCount": len(missing)}


def record_baseline(project_path, baseline_path):
    os.makedirs(os.path.dirname(baseline_path), exist_ok=True)
    files = user_content(project_path)
    with open(baseline_path, "w", encoding="utf-8") as handle:
        json.dump({"projectPath": project_path, "files": files}, handle, indent=1)
    return len(files)

# Producer:Betsy
