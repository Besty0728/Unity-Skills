"""Computes the hashes of a task pack and writes its manifest.json deterministically (contract 5.1, 9).

    python3 -B eval/tools/build_pack_manifest.py [--pack-dir DIR | --pack-root DIR --private-root DIR]
                                                 [--manifest FILE] [--check] [--seed ID=N[,N...]] ...

The task attributes (track, tier, case_version, minutes, requires) come from each tasks/<ID>/task.py; the existing
manifest keeps what only it knows: pack id and version, plugin range, track order, seeds and the private URL list. A
skeleton manifest with only those parts is a valid starting point, or pass --pack-id / --pack-version for a new pack.
--check rewrites nothing and fails when the file on disk is not what this tool would write.
"""
import argparse
import json
import os
import sys

sys.dont_write_bytecode = True
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RUNNER = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
if RUNNER not in sys.path:
    sys.path.insert(0, RUNNER)

from usk_eval import pack, store           # noqa: E402
from usk_eval.problems import ProblemError  # noqa: E402

DEFAULT_PACK_ROOT = os.path.join(RUNNER, "pack")
DEFAULT_PRIVATE_ROOT = os.path.join(REPO, "eval", "pack", "private")
PACKAGE_JSON = os.path.join(REPO, "SkillsForUnity", "package.json")
DEFAULT_PRIVATE = {
    "urlTemplates": [
        "https://raw.githubusercontent.com/Besty0728/Unity-Skills/{ref}/eval/pack/private/{file}",
        "https://cdn.jsdelivr.net/gh/Besty0728/Unity-Skills@{ref}/eval/pack/private/{file}",
    ],
    "refs": ["v{plugin}", "main", "beta"],
}
SEED_BASE = 7001
SEED_STEP = 101
KNOWN_TOP = ("schema", "packId", "packVersion", "plugin", "tracks", "tasks", "private")
KNOWN_TASK = ("track", "tier", "caseVersion", "minutes", "seeds", "requires", "public", "private")
REQUIRES_ORDER = ("pipeline", "mode", "dedicated", "packages")


class BuildError(Exception):
    def __init__(self, errors):
        Exception.__init__(self, "; ".join(errors))
        self.errors = list(errors)


def _sha256_file(path):
    return store.sha256_bytes(pack.read_file(path))


def _ordered_requires(requires):
    normalized = pack.normalize_requires(requires)
    out = {key: normalized[key] for key in REQUIRES_ORDER if key in normalized}
    out.update({key: normalized[key] for key in sorted(normalized) if key not in out})
    return out


def _public_map(directory):
    names = []
    for name in sorted(os.listdir(directory)):
        if pack.ignored_name(name):
            continue
        if not os.path.isfile(os.path.join(directory, name)):
            raise BuildError(["%s: unexpected folder in a task folder" % os.path.join(directory, name)])
        names.append(name)
    names = [n for n in pack.PUBLIC_REQUIRED if n in names] + [n for n in names if n not in pack.PUBLIC_REQUIRED]
    return {name: _sha256_file(os.path.join(directory, name)) for name in names}


def _first_free_seed(used):
    seed = SEED_BASE
    while seed in used:
        seed += SEED_STEP
    return seed


def _load_tasks(pack_root):
    tasks_root = os.path.join(pack_root, "tasks")
    if not os.path.isdir(tasks_root):
        raise BuildError(["%s: no tasks folder" % pack_root])
    tasks, errors = {}, []
    for name in sorted(os.listdir(tasks_root)):
        path = os.path.join(tasks_root, name, "task.py")
        if pack.ignored_name(name) or not os.path.isfile(path):
            continue
        if not pack.is_task_id(name):
            errors.append("tasks/%s: not a valid task id" % name)
            continue
        try:
            task = pack.load_task_file(name, path)
        except ProblemError as exc:
            errors.append(exc.problems[0]["message"])
            continue
        if task.id != name:
            errors.append("tasks/%s: task.py declares id %r" % (name, task.id))
            continue
        tasks[name] = task
    if errors:
        raise BuildError(errors)
    return tasks


def ignored_folders(pack_root):
    """Folders under tasks/ without a task.py: they are not part of the manifest."""
    tasks_root = os.path.join(pack_root, "tasks")
    try:
        names = sorted(os.listdir(tasks_root))
    except OSError:
        return []
    return [name for name in names if not pack.ignored_name(name)
            and not os.path.isfile(os.path.join(tasks_root, name, "task.py"))]


def _tracks(previous, tasks):
    """Previous order for tasks that keep their track and tier; new or moved tasks are appended, sorted by id."""
    tracks = {track: {tier: [] for tier in pack.TIERS} for track in pack.TRACKS}
    placed = set()
    old = previous.get("tracks") if isinstance(previous.get("tracks"), dict) else {}
    for track in pack.TRACKS:
        for tier in pack.TIERS:
            ids = (old.get(track) or {}).get(tier) if isinstance(old.get(track), dict) else None
            for task_id in ids if isinstance(ids, list) else []:
                task = tasks.get(task_id)
                if task is not None and task_id not in placed and (task.track, task.tier) == (track, tier):
                    tracks[track][tier].append(task_id)
                    placed.add(task_id)
    for task_id in sorted(tasks):
        task = tasks[task_id]
        if task_id not in placed and task.track in tracks and task.tier in tracks[task.track]:
            tracks[task.track][task.tier].append(task_id)
            placed.add(task_id)
    return tracks


def _private_file(old, task_id):
    name = (old.get("private") or {}).get("file") if isinstance(old.get("private"), dict) else None
    return name if isinstance(name, str) and name else task_id + ".py"


def build_manifest(pack_root, private_root, previous=None, options=None):
    """The manifest dict for the pack on disk (keys in a fixed order); BuildError when it cannot be built or is invalid.

    options: packId, packVersion, pluginMin, pluginMax (a version, or None for no limit when the key is present),
    seeds {ID: [int, ...]}.
    """
    previous = previous if isinstance(previous, dict) else {}
    options = options or {}
    tasks = _load_tasks(pack_root)
    tracks = _tracks(previous, tasks)
    old_tasks = previous.get("tasks") if isinstance(previous.get("tasks"), dict) else {}
    ordered = [task_id for track in pack.TRACKS for tier in pack.TIERS for task_id in tracks[track][tier]]
    used = set()
    for task_id in ordered:
        old_seeds = (old_tasks.get(task_id) or {}).get("seeds") if isinstance(old_tasks.get(task_id), dict) else None
        used.update(seed for seed in old_seeds or [] if type(seed) is int)
    errors, entries = [], {}
    for task_id in ordered:
        task = tasks[task_id]
        old = old_tasks.get(task_id) if isinstance(old_tasks.get(task_id), dict) else {}
        seeds = (options.get("seeds") or {}).get(task_id) or old.get("seeds")
        if not isinstance(seeds, list) or not seeds:
            seeds = [_first_free_seed(used)]
            used.add(seeds[0])
        private_file = _private_file(old, task_id)
        private_path = os.path.join(private_root, private_file)
        try:
            raw = pack.read_file(private_path)
        except (OSError, ValueError):
            errors.append("%s: private verifier %s is missing or unreadable in %s" % (task_id, private_file,
                                                                                    private_root))
            continue
        try:
            public = _public_map(os.path.join(pack_root, "tasks", task_id))
        except BuildError as exc:
            errors.extend(exc.errors)
            continue
        entry = {"track": task.track, "tier": task.tier, "caseVersion": task.case_version, "minutes": task.minutes,
                 "seeds": seeds, "requires": _ordered_requires(task.requires), "public": public,
                 "private": {"file": private_file, "sha256": store.sha256_bytes(raw), "bytes": len(raw)}}
        entry.update({key: value for key, value in old.items() if key not in KNOWN_TASK})
        entries[task_id] = entry
    if errors:
        raise BuildError(errors)
    old_plugin = previous.get("plugin") if isinstance(previous.get("plugin"), dict) else {}
    plugin_min = options["pluginMin"] if "pluginMin" in options else old_plugin.get("min")
    plugin_max = options["pluginMax"] if "pluginMax" in options else old_plugin.get("max")
    if plugin_min is None and "pluginMin" not in options and not old_plugin:
        plugin_min = _package_version()
    manifest = {"schema": pack.SCHEMA,
                "packId": options.get("packId") or previous.get("packId"),
                "packVersion": options.get("packVersion") or previous.get("packVersion"),
                "plugin": {"min": plugin_min, "max": plugin_max},
                "tracks": tracks, "tasks": entries,
                "private": previous.get("private") if isinstance(previous.get("private"), dict)
                else json.loads(json.dumps(DEFAULT_PRIVATE))}
    manifest.update({key: value for key, value in previous.items() if key not in KNOWN_TOP})
    found = pack.manifest_errors(manifest, require_urls=True)
    if found:
        raise BuildError(found)
    return manifest


def _package_version():
    try:
        with open(PACKAGE_JSON, "r", encoding="utf-8-sig") as handle:
            return json.load(handle).get("version")
    except (OSError, ValueError):
        return None


def render(manifest):
    return store.dumps(manifest) + "\n"


def changes(previous, manifest):
    """Human-readable differences between the previous manifest (may be {}) and the new one."""
    out = []
    old_tasks = (previous or {}).get("tasks") if isinstance((previous or {}).get("tasks"), dict) else {}
    for task_id, entry in manifest["tasks"].items():
        old = old_tasks.get(task_id)
        if not isinstance(old, dict):
            out.append("%s: added" % task_id)
            continue
        changed = []
        for name, digest in entry["public"].items():
            if (old.get("public") or {}).get(name) != digest:
                changed.append(name)
        if (old.get("private") or {}).get("sha256") != entry["private"]["sha256"]:
            changed.append("private %s" % entry["private"]["file"])
        changed += [key for key in ("track", "tier", "caseVersion", "minutes", "seeds", "requires")
                    if old.get(key) != entry[key]]
        if changed:
            out.append("%s: %s changed" % (task_id, ", ".join(changed)))
    out += ["%s: removed" % task_id for task_id in old_tasks if task_id not in manifest["tasks"]]
    return out


def _read_previous(path):
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, "rb") as handle:
            data = json.loads(handle.read().decode("utf-8-sig"))
    except (OSError, ValueError):
        raise BuildError(["%s exists but is not valid JSON" % path])
    if not isinstance(data, dict):
        raise BuildError(["%s must hold a JSON object" % path])
    return data


def _seed_option(values):
    seeds = {}
    for value in values or []:
        task_id, _sep, text = value.partition("=")
        try:
            seeds[task_id] = [int(item) for item in text.split(",") if item.strip()]
        except ValueError:
            seeds[task_id] = []
        if not pack.is_task_id(task_id) or not seeds[task_id]:
            raise BuildError(["--seed %s: expected ID=N[,N...]" % value])
    return seeds


def _version_option(flag, text):
    if text is None:
        return None
    if text.lower() in ("none", "null", ""):
        return None
    if not pack.is_semver(text):
        raise BuildError(["%s %s: expected a semantic version or none" % (flag, text)])
    return text


def _parser():
    parser = argparse.ArgumentParser(description="Compute the hashes of a task pack and write manifest.json.")
    parser.add_argument("--pack-dir", help="development pack folder: manifest.json, tasks/, private/")
    parser.add_argument("--pack-root", help="folder holding manifest.json and tasks/ (default: the package pack)")
    parser.add_argument("--private-root", help="folder holding the private verifiers (default: eval/pack/private)")
    parser.add_argument("--manifest", help="manifest file (default: <pack root>/manifest.json)")
    parser.add_argument("--check", action="store_true", help="fail when the manifest is not up to date; write nothing")
    parser.add_argument("--pack-id")
    parser.add_argument("--pack-version")
    parser.add_argument("--plugin-min", help="version, or none")
    parser.add_argument("--plugin-max", help="version, or none")
    parser.add_argument("--seed", action="append", metavar="ID=N[,N...]", help="seeds for a task (default: kept)")
    return parser


def main(argv=None, out=None):
    out = out or sys.stdout
    args = _parser().parse_args(argv)
    if args.pack_dir and (args.pack_root or args.private_root):
        print("build_pack_manifest: --pack-dir excludes --pack-root and --private-root", file=out)
        return 2
    pack_root = os.path.abspath(args.pack_dir or args.pack_root or DEFAULT_PACK_ROOT)
    private_root = os.path.abspath(os.path.join(pack_root, "private") if args.pack_dir
                                   else args.private_root or DEFAULT_PRIVATE_ROOT)
    manifest_path = os.path.abspath(args.manifest or os.path.join(pack_root, pack.MANIFEST_NAME))
    try:
        options = {"seeds": _seed_option(args.seed)}
        if args.pack_id:
            options["packId"] = args.pack_id
        if args.pack_version:
            if not pack.is_semver(args.pack_version):
                raise BuildError(["--pack-version %s: expected a semantic version" % args.pack_version])
            options["packVersion"] = args.pack_version
        if args.plugin_min is not None:
            options["pluginMin"] = _version_option("--plugin-min", args.plugin_min)
        if args.plugin_max is not None:
            options["pluginMax"] = _version_option("--plugin-max", args.plugin_max)
        previous = _read_previous(manifest_path)
        manifest = build_manifest(pack_root, private_root, previous, options)
    except BuildError as exc:
        for error in exc.errors:
            print("error: %s" % error, file=out)
        print("build_pack_manifest: FAILED", file=out)
        return 1
    text = render(manifest)
    try:
        with open(manifest_path, "rb") as handle:
            current = handle.read()
    except OSError:
        current = None
    summary = changes(previous, manifest)
    if args.check:
        if current == text.encode("utf-8"):
            print("build_pack_manifest: %s is up to date (%d tasks)" % (manifest_path, len(manifest["tasks"])),
                  file=out)
            return 0
        for line in summary:
            print("stale: %s" % line, file=out)
        print("build_pack_manifest: %s is out of date; run the tool without --check" % manifest_path, file=out)
        return 1
    if current != text.encode("utf-8"):
        try:
            store.atomic_write_bytes(manifest_path, text.encode("utf-8"))
        except store.StoreError as exc:
            print("error: cannot write %s: %s" % (manifest_path, exc.detail), file=out)
            return 2
    for line in summary:
        print(line, file=out)
    for name in ignored_folders(pack_root):
        print("warning: tasks/%s has no task.py and is not in the manifest" % name, file=out)
    print("build_pack_manifest: %s %s (%d tasks)" % ("wrote" if current != text.encode("utf-8") else "unchanged",
                                                     manifest_path, len(manifest["tasks"])), file=out)
    return 0


if __name__ == "__main__":
    for _stream in (sys.stdout, sys.stderr):          # paths with letters the console cannot encode must not crash
        try:
            _stream.reconfigure(errors="backslashreplace")
        except (AttributeError, ValueError):
            pass
    sys.exit(main())

# Producer:Betsy
