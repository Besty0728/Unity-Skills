"""Project and package-root discovery (contract 1.4, 1.5, 2.1) and the state/IO directory layout (4.1)."""
import glob
import json
import os
import time

from . import PACKAGE_NAME, RUNNER_DIR
from .problems import ProblemError, notice, problem

REGISTRY_STALE_S = 120
EVAL_ASSET_ROOT = "Assets/__UnitySkillsEval"


def norm(path):
    return os.path.normcase(os.path.realpath(path))


def canonical(path):
    return os.path.realpath(os.path.abspath(os.path.expanduser(path)))


def is_project(directory):
    return bool(directory) and os.path.isdir(os.path.join(directory, "Assets")) and \
        os.path.isfile(os.path.join(directory, "ProjectSettings", "ProjectVersion.txt"))


def walk_up(start):
    try:
        current = os.path.abspath(start)
    except (OSError, ValueError):
        return None
    while True:
        if is_project(current):
            return current
        parent = os.path.dirname(current)
        if parent == current:
            return None
        current = parent


# ---------------------------------------------------------------- registry


def registry_path(environ):
    return environ.get("USK_EVAL_REGISTRY") or os.path.join(os.path.expanduser("~"), ".unity_skills", "registry.json")


def read_registry(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def registry_entries(registry):
    """Well-formed entries (dict with a positive int port), each a copy with `path` filled from its key."""
    out = []
    for key, info in registry.items():
        if not isinstance(info, dict):
            continue
        port = info.get("port")
        if not isinstance(port, int) or isinstance(port, bool) or port <= 0:
            continue
        entry = dict(info)
        entry["path"] = info.get("path") or key
        out.append(entry)
    return out


def entry_status(entry):
    status = entry.get("status")
    if isinstance(status, str) and status.lower() in ("reloading", "stopped"):
        return status.lower()
    return "running"


def heartbeat_fresh(entry, now):
    last = entry.get("last_active")
    return isinstance(last, (int, float)) and not isinstance(last, bool) and now - last <= REGISTRY_STALE_S


def live_entries(registry, now=None):
    now = time.time() if now is None else now
    return [e for e in registry_entries(registry) if entry_status(e) == "running" and heartbeat_fresh(e, now)]


def registry_entry(registry, project):
    wanted = norm(project)
    for entry in registry_entries(registry):
        try:
            if norm(entry["path"]) == wanted:
                return entry
        except (OSError, ValueError):
            continue
    return None


# ---------------------------------------------------------------- project


def project_from_runner(runner_dir=RUNNER_DIR):
    path = os.path.abspath(runner_dir)
    marker = os.sep + os.path.join("Library", "PackageCache") + os.sep
    index = path.find(marker)
    if index > 0:
        return path[:index]
    package_root = os.path.dirname(path)
    packages = os.path.dirname(package_root)
    if os.path.basename(packages) == "Packages":
        return os.path.dirname(packages)
    return None


def locate_project(arg, environ, cwd, runner_dir=RUNNER_DIR, now=None):
    """Contract 2.1: --project, USK_EVAL_PROJECT, runner location, walk_up(cwd), the single live registry entry."""
    for explicit in (arg, environ.get("USK_EVAL_PROJECT")):
        if explicit:
            if not is_project(explicit):
                raise ProblemError(problem("PROJECT_NOT_FOUND", detail="%s is not a Unity project" % explicit))
            return canonical(explicit)
    for candidate in (project_from_runner(runner_dir), walk_up(cwd)):
        if candidate and is_project(candidate):
            return canonical(candidate)
    seen = {}
    for entry in live_entries(read_registry(registry_path(environ)), now):
        if is_project(entry["path"]):
            seen.setdefault(norm(entry["path"]), entry["path"])
    if len(seen) == 1:
        return canonical(list(seen.values())[0])
    if len(seen) > 1:
        raise ProblemError(problem("PROJECT_AMBIGUOUS", detail=", ".join(sorted(seen.values()))))
    raise ProblemError(problem("PROJECT_NOT_FOUND"))


# ---------------------------------------------------------------- package root


def packages_lock(project):
    try:
        with open(os.path.join(project, "Packages", "packages-lock.json"), "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError):
        return {}
    deps = data.get("dependencies") if isinstance(data, dict) else None
    return deps if isinstance(deps, dict) else {}


def _package_name(directory):
    try:
        with open(os.path.join(directory, "package.json"), "r", encoding="utf-8-sig") as handle:
            return json.load(handle).get("name")
    except (OSError, ValueError, AttributeError):
        return None


def _embedded(project):
    for path in sorted(glob.glob(os.path.join(glob.escape(os.path.join(project, "Packages")), "*", "package.json"))):
        directory = os.path.dirname(path)
        if _package_name(directory) == PACKAGE_NAME:
            return directory
    return None


def _cached(project, lock_hash):
    pattern = os.path.join(glob.escape(os.path.join(project, "Library", "PackageCache")), PACKAGE_NAME + "@*")
    candidates = [d for d in sorted(glob.glob(pattern)) if _package_name(d) == PACKAGE_NAME]
    if len(candidates) <= 1:
        return (candidates[0] if candidates else None), []
    if lock_hash:
        matching = [d for d in candidates if lock_hash.startswith(d.rsplit("@", 1)[-1])]
        if len(matching) == 1:
            return matching[0], []
    newest = max(candidates, key=lambda d: os.path.getmtime(os.path.join(d, "package.json")))
    return newest, [notice("PACKAGE_ROOT_AMBIGUOUS", [newest])]


def resolve_package_root(project):
    """{root, source, notices} per contract 1.5; PACKAGE_NOT_FOUND when nothing matches."""
    entry = packages_lock(project).get(PACKAGE_NAME)
    entry = entry if isinstance(entry, dict) else None
    source = entry.get("source") if entry else None
    root, notices = None, []
    if source == "embedded":
        root = _embedded(project)
    elif source == "local":
        version = entry.get("version") or ""
        if version.startswith("file:"):
            target = version[len("file:"):]
            root = target if os.path.isabs(target) else os.path.join(project, "Packages", target)
    else:
        root, notices = _cached(project, entry.get("hash") if entry else None)
        if root is None and entry is None:
            root = _embedded(project)
            source = "embedded" if root else None
        elif root is not None and entry is None:
            source = "cache"
    if not root or _package_name(root) != PACKAGE_NAME:
        raise ProblemError(problem("PACKAGE_NOT_FOUND"))
    return {"root": canonical(root), "source": source or "unknown", "notices": notices}


# ---------------------------------------------------------------- state layout


class StatePaths(object):
    def __init__(self, project):
        self.project = project
        self.root = os.path.join(project, "Library", "UnitySkills", "eval")
        self.lock = os.path.join(self.root, "lock")
        self.current = os.path.join(self.root, "current.json")
        self.ledger = os.path.join(self.root, "ledger.jsonl")
        self.log = os.path.join(self.root, "runner.log")
        self.sessions = os.path.join(self.root, "sessions")
        self.panel = os.path.join(self.root, "panel")
        self.io_root = os.path.join(project, "Library", "UnitySkillsEvalIO")
        self.journal_dir = os.path.join(project, "Library", "UnitySkills")
        self.library = os.path.join(project, "Library")
        self.marker = os.path.join(project, "ProjectSettings", "UnitySkillsEvaluation.json")
        self.eval_assets = os.path.join(project, *EVAL_ASSET_ROOT.split("/"))

    def session_dir(self, sid):
        return os.path.join(self.sessions, sid)

    def session_file(self, sid):
        return os.path.join(self.sessions, sid, "session.json")

    def session_log(self, sid):
        return os.path.join(self.sessions, sid, "runner.log")

    def attempt_dir(self, sid, aid):
        return os.path.join(self.sessions, sid, "attempts", aid)

    def state_file(self, sid, aid):
        return os.path.join(self.attempt_dir(sid, aid), "state.json")

    def result_file(self, sid, aid):
        return os.path.join(self.attempt_dir(sid, aid), "result.json")

    def io_session(self, sid):
        return os.path.join(self.io_root, sid)

    def io_attempt(self, sid, aid):
        return os.path.join(self.io_root, sid, aid)

    def asset_dir(self, asset_path):
        return os.path.join(self.project, *asset_path.split("/"))

    def relative(self, path):
        return os.path.relpath(path, self.root).replace(os.sep, "/")

# Producer:Betsy
