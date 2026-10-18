"""Environment snapshot and drift (contract 2.8), check helpers, skill-doc scan, dedicated marker (7.4), gate keys (9)."""
import glob
import hashlib
import json
import os
import platform
import re
import sys

from . import store
from .paths import packages_lock

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
UGUI_PACKAGE = "com.unity.ugui"
TMP_PACKAGE = "com.unity.textmeshpro"
TMP_SETTINGS_FILE = "TMP Settings.asset"
TMP_SETTINGS_DEFAULT = "Assets/TextMesh Pro/Resources/" + TMP_SETTINGS_FILE
TMP_ESSENTIALS_FILE = "TMP Essential Resources.unitypackage"
_VERSION_HEAD = re.compile(r"^\s*(\d+)\.")
INPUT_HANDLER = re.compile(r"^\s*activeInputHandler:\s*(\d+)\s*$", re.M)
ASSET_TREE_LIMIT = 20000          # files of the run's asset folder hashed at issue; more and no import is explained


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


def imported_by_unity(name):
    """Unity skips hidden assets: names starting with '.' or ending with '~' (Evaluation~, unity-skills~, Tests~)."""
    return not name.startswith(".") and not name.endswith("~")


def sha256_file(path):
    """sha256 of the file's bytes as they are (assets are binary as often as text: no line-ending normalisation)."""
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def asset_hashes(directory, limit=ASSET_TREE_LIMIT):
    """{path relative to `directory`, '/'-separated: sha256 of the file's bytes} for every file below it, .meta files
    included (the run's asset folder A, contract 2.8 N8/S6). Unlike package_tree nothing is skipped as hidden: Unity
    does not import a name starting with '.' or ending with '~', but a file the attempt finds there is still its own.
    None when the folder cannot be walked or holds more than `limit` files; a file that cannot be read is left out, so
    an import naming it is never explained."""
    directory = os.path.abspath(directory)
    if not os.path.isdir(directory):
        return None
    out = {}
    for base, dirs, files in os.walk(directory):
        dirs.sort()
        for name in sorted(files):
            if len(out) >= limit:
                return None
            path = os.path.join(base, name)
            try:
                out[os.path.relpath(path, directory).replace(os.sep, "/")] = sha256_file(path)
            except OSError:
                continue
    return out


def package_tree(root):
    """The plugin package as Unity imports it (contract 2.8 N7/S6): {hash, newestMtimeNs, paths}. hash is the tree rule
    over its importable files, newestMtimeNs their newest modification time, paths the package-relative paths of
    those files and folders ('' is the package folder itself). OSError when the tree cannot be read."""
    root = os.path.abspath(root)
    entries, paths, newest = [], {""}, 0
    for base, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if imported_by_unity(d))
        rel = os.path.relpath(base, root).replace(os.sep, "/")
        prefix = "" if rel == "." else rel + "/"
        paths.update(prefix + d for d in dirs)
        for name in sorted(files):
            if imported_by_unity(name):
                path = os.path.join(base, name)
                newest = max(newest, os.stat(path).st_mtime_ns)
                entries.append((prefix + name, path))
                paths.add(prefix + name)
    return {"hash": hash_files(entries), "newestMtimeNs": newest, "paths": paths}


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


# ---------------------------------------------------------------- TextMesh Pro essentials (contract 2.4, 3.1)


def _package_json(directory):
    try:
        with open(os.path.join(directory, "package.json"), "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def _mtime_ns(path):
    try:
        return os.stat(path).st_mtime_ns
    except OSError:
        return 0


def package_dirs(project, name, version=None):
    """Folders of package `name` in the project: the embedded ones under Packages/, then the Library/PackageCache/
    <name>@* copies, the one whose package.json version is `version` first and the newest of the rest after it."""
    root = glob.escape(project)
    embedded = [os.path.dirname(path) for path in sorted(glob.glob(os.path.join(root, "Packages", "*", "package.json")))
                if _package_json(os.path.dirname(path)).get("name") == name]
    cached = [d for d in glob.glob(os.path.join(root, "Library", "PackageCache", glob.escape(name) + "@*"))
              if _package_json(d).get("name") == name]
    cached.sort(key=lambda d: (_package_json(d).get("version") != version,
                               -_mtime_ns(os.path.join(d, "package.json"))))
    return embedded + cached


def package_major(project, name, entry):
    """Major version of an installed package. The lock entry carries it for registry and built-in packages; embedded,
    local and git packages have a path or URL there, so the package's own package.json is read; 0 when unknown."""
    version = entry.get("version") if isinstance(entry, dict) else None
    found = _VERSION_HEAD.match(version) if isinstance(version, str) else None
    for directory in [] if found else package_dirs(project, name):
        found = _VERSION_HEAD.match(str(_package_json(directory).get("version") or ""))
        if found:
            break
    return int(found.group(1)) if found else 0


def tmp_packages(project, installed):
    """Installed packages that carry TextMesh Pro: com.unity.textmeshpro, and com.unity.ugui from version 2 on (Unity 6
    ships TextMesh Pro inside it). The UI skills add TextMeshProUGUI wherever that assembly is loaded."""
    names = [TMP_PACKAGE] if TMP_PACKAGE in installed else []
    if UGUI_PACKAGE in installed and package_major(project, UGUI_PACKAGE, installed[UGUI_PACKAGE]) >= 2:
        names.append(UGUI_PACKAGE)
    return names


def has_text_mesh_pro(project):
    """True when the project carries TextMesh Pro (tmp_packages), i.e. when the UI skills give a label TextMeshProUGUI
    and not UnityEngine.UI.Text. Pack tasks read this through usk_eval.verifylib."""
    return bool(tmp_packages(project, packages_lock(project)))


def active_input_handler(project):
    """The project's Active Input Handling from ProjectSettings/ProjectSettings.asset: 0 Input Manager (Old), 1 Input
    System Package (New), 2 Both. A project whose file has no such key (or none) uses the Input Manager."""
    try:
        with open(os.path.join(project, "ProjectSettings", "ProjectSettings.asset"), "r", encoding="utf-8",
                  errors="replace") as handle:
            found = INPUT_HANDLER.search(handle.read())
    except OSError:
        return 0
    return int(found.group(1)) if found else 0


def tmp_settings_asset(project):
    """Project-relative path of a "TMP Settings.asset" that Resources.Load("TMP Settings") can reach, i.e. one lying
    directly in a Resources folder of Assets/ or of an embedded package; None when there is none. Importing the
    Essential Resources creates it at Assets/TextMesh Pro/Resources, which is looked at first."""
    if os.path.isfile(os.path.join(project, *TMP_SETTINGS_DEFAULT.split("/"))):
        return TMP_SETTINGS_DEFAULT
    for top in ("Assets", "Packages"):
        for base, dirs, files in os.walk(os.path.join(project, top)):
            dirs[:] = sorted(d for d in dirs if imported_by_unity(d))
            if os.path.basename(base).lower() == "resources" and TMP_SETTINGS_FILE in files:
                return os.path.relpath(os.path.join(base, TMP_SETTINGS_FILE), project).replace(os.sep, "/")
    return None


def tmp_essentials_package(project, installed):
    """Absolute path of "TMP Essential Resources.unitypackage" in the project's copy of the package that carries
    TextMesh Pro (Library/PackageCache, or an embedded package); None when the package is not resolved there."""
    for name in tmp_packages(project, installed):
        entry = installed.get(name)
        version = entry.get("version") if isinstance(entry, dict) else None
        for directory in package_dirs(project, name, version):
            candidate = os.path.join(directory, "Package Resources", TMP_ESSENTIALS_FILE)
            if os.path.isfile(candidate):
                return candidate
    return None


# ---------------------------------------------------------------- gate inputs (contract 9)


def gate_inputs_hash(repo_root, task_id, extra=()):
    """Per-task gateInputsHash over the runner (minus usage/ and report.py), the task's files and extra repo files.
    The gate key itself has one definition, eval/selftest3.py gate_key (it includes the pipeline)."""
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

# Producer:Betsy
