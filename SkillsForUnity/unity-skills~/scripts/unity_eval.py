"""UnitySkills evaluation entry for AI agents (contract 1.4, 1.5).

Finds the Unity project and its com.besty.unity-skills package, then runs the package's Evaluation~/run_eval.py with
the same arguments. It reads and writes no evaluation state and does not depend on the package version, so a copy
installed into an agent skill folder keeps working across package updates.
"""
import sys

sys.dont_write_bytecode = True

import glob  # noqa: E402
import io  # noqa: E402
import json  # noqa: E402
import os  # noqa: E402
import runpy  # noqa: E402
import time  # noqa: E402

PACKAGE_NAME = "com.besty.unity-skills"
REGISTRY_STALE_S = 120
EXIT_ENVIRONMENT = 2
EXIT_INTERNAL = 4

# code: (message, fix). The same texts as usk_eval/problems.py, which cannot be imported before the package is found.
PROBLEMS = {
    "PYTHON_TOO_OLD": ("Python 3.9 or newer is required",
                       "Install Python 3.9 or newer and run the command with it."),
    "PROJECT_NOT_FOUND": ("No Unity project was found",
                          "Run the command inside the Unity project folder, or pass --project <path to the project>."),
    "PROJECT_AMBIGUOUS": ("Several running Unity editors match and the current directory belongs to none of them",
                          "Several Unity editors are running; pass --project <path to the project>."),
    "PACKAGE_NOT_FOUND": ("The com.besty.unity-skills package root was not found",
                          "Install com.besty.unity-skills in this project and let Unity finish resolving packages."),
    "RUNNER_NOT_FOUND": ("The installed package has no Evaluation~/run_eval.py",
                         "The installed UnitySkills package has no evaluation runner; update UnitySkills to 3.0 or "
                         "newer."),
    "INTERNAL_ERROR": ("Unexpected runner error",
                       "Unexpected runner error; reinstall the UnitySkills package and report it."),
}


class Failure(Exception):
    def __init__(self, code, detail=None):
        Exception.__init__(self, code)
        self.code, self.detail = code, detail


def read_json(path):
    try:
        with io.open(path, "r", encoding="utf-8-sig") as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def canonical(path):
    return os.path.realpath(os.path.abspath(os.path.expanduser(path)))


def norm(path):
    return os.path.normcase(os.path.realpath(path))


def is_project(directory):
    return bool(directory) and os.path.isdir(os.path.join(directory, "Assets")) and \
        os.path.isfile(os.path.join(directory, "ProjectSettings", "ProjectVersion.txt"))


def walk_up(start):
    if not start:
        return None
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


def project_option(argv):
    """(present, value) of --project / --project=PATH anywhere in argv; value is None when it has no argument."""
    for index, token in enumerate(argv):
        if token == "--project":
            return True, (argv[index + 1] if index + 1 < len(argv) else None)
        if token.startswith("--project="):
            return True, token.split("=", 1)[1]
    return False, None


def live_registry_projects(environ, now):
    """{normalized path: path} of the registry's running entries whose heartbeat is fresh and whose path is a project."""
    registry = read_json(environ.get("USK_EVAL_REGISTRY") or
                         os.path.join(os.path.expanduser("~"), ".unity_skills", "registry.json"))
    found = {}
    for key, info in (registry.items() if isinstance(registry, dict) else ()):
        if not isinstance(info, dict):
            continue
        port, last, status = info.get("port"), info.get("last_active"), info.get("status")
        if not isinstance(port, int) or isinstance(port, bool) or port <= 0:
            continue
        if isinstance(status, str) and status.lower() in ("reloading", "stopped"):
            continue
        if not isinstance(last, (int, float)) or isinstance(last, bool) or now - last > REGISTRY_STALE_S:
            continue
        path = info.get("path") or key
        if isinstance(path, str) and is_project(path):
            found.setdefault(norm(path), path)
    return found


def locate_project(option_value, environ, cwd, script_dir, now=None):
    """--project, USK_EVAL_PROJECT, the project around cwd, around this script, the single live registry entry."""
    for explicit in (option_value, environ.get("USK_EVAL_PROJECT")):
        if explicit:
            if not is_project(explicit):
                raise Failure("PROJECT_NOT_FOUND", "%s is not a Unity project" % explicit)
            return canonical(explicit)
    for start in (cwd, script_dir):
        found = walk_up(start)
        if found:
            return canonical(found)
    live = live_registry_projects(environ, time.time() if now is None else now)
    if len(live) == 1:
        return canonical(list(live.values())[0])
    if live:
        raise Failure("PROJECT_AMBIGUOUS", ", ".join(sorted(live.values())))
    raise Failure("PROJECT_NOT_FOUND")


def package_name(directory):
    data = read_json(os.path.join(directory, "package.json"))
    return data.get("name") if isinstance(data, dict) else None


def embedded_root(project):
    pattern = os.path.join(glob.escape(os.path.join(project, "Packages")), "*", "package.json")
    for path in sorted(glob.glob(pattern)):
        if package_name(os.path.dirname(path)) == PACKAGE_NAME:
            return os.path.dirname(path)
    return None


def cached_root(project, lock_hash):
    """The PackageCache copy. Its suffix is the lock hash prefix on Unity 2022 but a package.json fingerprint on
    Unity 6000, so several copies are told apart by that prefix first and by the newest package.json second."""
    pattern = os.path.join(glob.escape(os.path.join(project, "Library", "PackageCache")), PACKAGE_NAME + "@*")
    candidates = [d for d in sorted(glob.glob(pattern)) if package_name(d) == PACKAGE_NAME]
    if len(candidates) <= 1:
        return candidates[0] if candidates else None
    if isinstance(lock_hash, str) and lock_hash:
        matching = [d for d in candidates if d.rsplit("@", 1)[-1] and lock_hash.startswith(d.rsplit("@", 1)[-1])]
        if len(matching) == 1:
            return matching[0]
    return max(candidates, key=lambda d: os.path.getmtime(os.path.join(d, "package.json")))


def resolve_package_root(project):
    """Package root per the lock entry: embedded -> Packages/*, local -> its file: path, anything else -> the cache."""
    lock = read_json(os.path.join(project, "Packages", "packages-lock.json"))
    dependencies = lock.get("dependencies") if isinstance(lock, dict) else None
    entry = dependencies.get(PACKAGE_NAME) if isinstance(dependencies, dict) else None
    entry = entry if isinstance(entry, dict) else None
    source = entry.get("source") if entry else None
    root = None
    if source == "embedded":
        root = embedded_root(project)
    elif source == "local":
        version = entry.get("version")
        if isinstance(version, str) and version.startswith("file:"):
            target = version[len("file:"):]
            root = target if os.path.isabs(target) else os.path.join(project, "Packages", target)
    else:
        root = cached_root(project, entry.get("hash") if entry else None)
        if root is None and entry is None:
            root = embedded_root(project)
    if not root or package_name(root) != PACKAGE_NAME:
        raise Failure("PACKAGE_NOT_FOUND")
    return canonical(root)


def with_project(argv, project):
    """argv with --project <project> right after the command; appended when argv starts with an option."""
    if argv and not argv[0].startswith("-"):
        return argv[:1] + ["--project", project] + argv[1:]
    return argv + ["--project", project]


def report(command, code, detail=None, exit_code=EXIT_ENVIRONMENT):
    message, fix = PROBLEMS[code]
    problem = {"code": code, "message": message + (": %s" % detail if detail else ""), "fix": fix}
    envelope = {"ok": False, "command": command, "data": None, "problems": [problem], "next": None}
    sys.stdout.write(json.dumps(envelope, ensure_ascii=True, separators=(",", ":")) + "\n")
    return exit_code


def current_directory():
    try:
        return os.getcwd()
    except OSError:
        return None


def main(argv=None):
    argv = list(sys.argv[1:] if argv is None else argv)
    command = argv[0] if argv else None
    try:
        if sys.version_info < (3, 9):
            raise Failure("PYTHON_TOO_OLD")
        entry = os.path.abspath(__file__)
        present, value = project_option(argv)
        project = locate_project(value, os.environ, current_directory(), os.path.dirname(entry))
        runner = os.path.join(resolve_package_root(project), "Evaluation~", "run_eval.py")
        if not os.path.isfile(runner):
            raise Failure("RUNNER_NOT_FOUND")
        os.environ["USK_EVAL_ENTRY"] = entry
        sys.argv = [runner] + (argv if present else with_project(argv, project))
        runpy.run_path(runner, run_name="__main__")      # SystemExit carries the runner's exit code through
        return 0
    except Failure as failure:
        return report(command, failure.code, failure.detail)
    except Exception as exc:                              # a locating or loading bug must still answer with an envelope
        return report(command, "INTERNAL_ERROR", type(exc).__name__, EXIT_INTERNAL)


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
