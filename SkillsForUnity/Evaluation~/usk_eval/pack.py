"""Task pack (contract 5.1-5.4, 9): manifest validation, public file hashes, task modules and task selection.

The default pack ships in <pkg>/Evaluation~/pack; --pack-dir swaps in a development directory laid out as
manifest.json, tasks/<ID>/ and private/<file>. Private verifiers themselves are fetched by private.py.
"""
import copy
import importlib.util
import json
import os
import re
import sys
import urllib.parse
from collections.abc import Mapping

from . import RUNNER_DIR, store, submodule
from .problems import ProblemError, problem

MANIFEST_NAME = "manifest.json"
PACK_DIR = os.path.join(RUNNER_DIR, "pack")
SCHEMA = 1
TRACKS = ("ops", "dev")
TIERS = ("basic", "standard", "challenge")
MODES = ("approval", "auto", "bypass")
PIPELINES = ("BuiltIn", "URP", "HDRP", "Custom")
DEFAULT_REQUIRES = {"pipeline": "URP", "mode": "bypass", "dedicated": False, "packages": []}
PUBLIC_REQUIRED = ("task.py", "prompt.zh-CN.md", "prompt.en.md")
REQUIRED_METHODS = ("build", "snapshot", "prompt_vars", "answer_schema")
TASK_MODULE_PREFIX = "usk_eval_task_"
MAX_FILE_BYTES = 4 * 1024 * 1024
LOOPBACK_HOSTS = ("127.0.0.1", "::1", "localhost")
MAX_DETAIL_ERRORS = 3

_HEX64 = re.compile(r"^[0-9a-f]{64}\Z")
_SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\Z")
_TASK_ID = re.compile(r"^[A-Za-z][A-Za-z0-9]{0,15}\Z")
_FILE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\Z")
_REF = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/+{}-]{0,127}\Z")
_FIELD = re.compile(r"\{([^{}]*)\}")


# ---------------------------------------------------------------- small helpers


def _invalid(detail):
    return ProblemError(problem("PACK_INVALID", detail=detail))


def _short(value):
    text = repr(value)
    return text if len(text) <= 40 else text[:37] + "..."


def _text(value):
    return isinstance(value, str) and bool(value.strip())


def _int(value, minimum=0):
    return type(value) is int and value >= minimum


def version_tuple(text):
    """(major, minor, patch) of a semantic version; pre-release and build suffixes are ignored."""
    match = _SEMVER.match(text) if isinstance(text, str) else None
    return tuple(int(part) for part in match.groups()) if match else (0, 0, 0)


def is_task_id(text):
    return isinstance(text, str) and bool(_TASK_ID.match(text))


def is_semver(text):
    return isinstance(text, str) and bool(_SEMVER.match(text))


def read_file(path, limit=MAX_FILE_BYTES):
    """Bytes of a local file; ValueError when it is larger than `limit`, OSError when it cannot be read."""
    with open(path, "rb") as handle:
        data = handle.read(limit + 1)
    if len(data) > limit:
        raise ValueError("file is larger than %d bytes" % limit)
    return data


def normalize_requires(requires):
    """A task's `requires` with the contract defaults filled in (the manifest and task.py may omit keys).

    `requires` may be any mapping (task_api's default is a read-only one). The two sequence-valued keys, packages and
    pipeline (a list of accepted pipelines), always come out as new lists, so a tuple in task.py compares equal to the
    JSON array in the manifest and the result never shares a list with its argument.
    """
    out = dict(DEFAULT_REQUIRES)
    if isinstance(requires, Mapping):
        out.update(requires)
    for key in ("packages", "pipeline"):
        if isinstance(out.get(key), (list, tuple)):
            out[key] = list(out[key])
    return out


def _stray_braces(text):
    rest = _FIELD.sub("", text)
    return "{" in rest or "}" in rest


def template_error(template, allow_loopback_http=False):
    """Why a private URL template is unusable (contract 5.1), or None.

    Only {ref} and {file} may appear, {file} is required, and the URL must be https (http only on loopback hosts when
    allow_loopback_http is set, the USK_EVAL_PRIVATE_URLS test hook).
    """
    if not isinstance(template, str) or not template.strip():
        return "expected a non-empty string"
    for name in _FIELD.findall(template):
        if name not in ("ref", "file"):
            return "unknown placeholder {%s}" % name
    if _stray_braces(template):
        return "unbalanced braces"
    if "{file}" not in template:
        return "missing {file}"
    try:
        parts = urllib.parse.urlsplit(template)
        int(parts.port or 0)                # raises ValueError for a malformed port
    except ValueError:
        return "not a valid URL"
    host = (parts.hostname or "").lower()
    if not host or "{" in parts.netloc or "@" in parts.netloc:
        return "expected a plain host without placeholders or credentials"
    if parts.scheme == "https":
        return None
    if parts.scheme == "http" and allow_loopback_http and host in LOOPBACK_HOSTS:
        return None
    return "expected an https URL"


# ---------------------------------------------------------------- manifest validation (contract 5.1)


def _plugin_errors(plugin, bad):
    if not isinstance(plugin, dict):
        bad("plugin", "expected an object {min, max}")
        return
    for key in ("min", "max"):
        value = plugin.get(key)
        if value is not None and not is_semver(value):
            bad("plugin.%s" % key, "expected a semantic version or null")
    low, high = plugin.get("min"), plugin.get("max")
    if is_semver(low) and is_semver(high) and version_tuple(low) > version_tuple(high):
        bad("plugin", "min is greater than max")


def _tracks_errors(tracks, bad):
    """Validates tracks and returns {taskId: (track, tier)}."""
    listed = {}
    if not isinstance(tracks, dict):
        bad("tracks", "expected an object")
        return listed
    for track, tiers in tracks.items():
        if track not in TRACKS:
            bad("tracks.%s" % track, "unknown track (%s)" % "|".join(TRACKS))
        elif not isinstance(tiers, dict):
            bad("tracks.%s" % track, "expected an object of tiers")
        else:
            for tier, ids in tiers.items():
                where = "tracks.%s.%s" % (track, tier)
                if tier not in TIERS:
                    bad(where, "unknown tier (%s)" % "|".join(TIERS))
                elif not isinstance(ids, list):
                    bad(where, "expected a list of task ids")
                else:
                    for task_id in ids:
                        if not (isinstance(task_id, str) and _TASK_ID.match(task_id)):
                            bad(where, "invalid task id %s" % _short(task_id))
                        elif task_id in listed:
                            bad(where, "task %s is listed more than once" % task_id)
                        else:
                            listed[task_id] = (track, tier)
    return listed


def _pipeline_errors(where, value, bad):
    """requires.pipeline: a pipeline name, null (any pipeline) or a non-empty list of distinct names, in task order."""
    names = "|".join(PIPELINES)
    if value is None or (isinstance(value, str) and value in PIPELINES):
        return
    if not isinstance(value, list):
        bad(where, "expected one of %s, a list of them or null, got %s" % (names, _short(value)))
        return
    if not value:
        bad(where, "expected at least one of %s" % names)
    seen = set()
    for index, item in enumerate(value):
        if not (isinstance(item, str) and item in PIPELINES):
            bad("%s[%d]" % (where, index), "expected one of %s, got %s" % (names, _short(item)))
        elif item in seen:
            bad("%s[%d]" % (where, index), "duplicate pipeline %s" % item)
        else:
            seen.add(item)


def _requires_errors(where, requires, bad):
    if requires is None:
        return
    if not isinstance(requires, dict):
        bad(where, "expected an object")
        return
    _pipeline_errors(where + ".pipeline", requires.get("pipeline", DEFAULT_REQUIRES["pipeline"]), bad)
    if requires.get("mode", DEFAULT_REQUIRES["mode"]) not in MODES:
        bad(where + ".mode", "expected one of %s" % "|".join(MODES))
    if type(requires.get("dedicated", False)) is not bool:
        bad(where + ".dedicated", "expected a boolean")
    packages = requires.get("packages", [])
    if not isinstance(packages, list) or not all(_text(name) for name in packages):
        bad(where + ".packages", "expected a list of package names")


def _task_errors(task_id, entry, listed, bad):
    where = "tasks.%s" % task_id
    if not (isinstance(task_id, str) and _TASK_ID.match(task_id)):
        bad(where, "invalid task id")
        return
    if not isinstance(entry, dict):
        bad(where, "expected an object")
        return
    track, tier = entry.get("track"), entry.get("tier")
    if track not in TRACKS:
        bad(where + ".track", "expected one of %s" % "|".join(TRACKS))
    if tier not in TIERS:
        bad(where + ".tier", "expected one of %s" % "|".join(TIERS))
    if task_id not in listed:
        bad(where, "not listed in tracks")
    elif (track, tier) != listed[task_id]:
        bad(where, "track/tier %s/%s differ from its tracks position %s/%s" % ((track, tier) + listed[task_id]))
    if not _int(entry.get("caseVersion"), 1):
        bad(where + ".caseVersion", "expected an integer >= 1")
    if not _int(entry.get("minutes"), 1):
        bad(where + ".minutes", "expected an integer >= 1")
    seeds = entry.get("seeds")
    if not isinstance(seeds, list) or not seeds or not all(type(seed) is int for seed in seeds):
        bad(where + ".seeds", "expected a non-empty list of integers")
    _requires_errors(where + ".requires", entry.get("requires"), bad)
    public = entry.get("public")
    if not isinstance(public, dict):
        bad(where + ".public", "expected an object of file name to sha256")
    else:
        for name in PUBLIC_REQUIRED:
            if name not in public:
                bad(where + ".public", "missing %s" % name)
        for name, digest in public.items():
            if not (isinstance(name, str) and _FILE_NAME.match(name)):
                bad(where + ".public", "invalid file name %s" % _short(name))
            elif not (isinstance(digest, str) and _HEX64.match(digest)):
                bad(where + ".public." + name, "expected 64 lowercase hex digits")
    private = entry.get("private")
    if not isinstance(private, dict):
        bad(where + ".private", "expected {file, sha256, bytes}")
        return
    name = private.get("file")
    if not (isinstance(name, str) and _FILE_NAME.match(name) and name.endswith(".py")):
        bad(where + ".private.file", "expected a plain .py file name")
    if not (isinstance(private.get("sha256"), str) and _HEX64.match(private["sha256"])):
        bad(where + ".private.sha256", "expected 64 lowercase hex digits")
    if not _int(private.get("bytes"), 1):
        bad(where + ".private.bytes", "expected an integer >= 1")


def _private_errors(private, require_urls, bad):
    if private is None:
        if require_urls:
            bad("private", "urlTemplates and refs are required to fetch verifiers by URL")
        return
    if not isinstance(private, dict):
        bad("private", "expected an object {urlTemplates, refs}")
        return
    templates, refs = private.get("urlTemplates", []), private.get("refs", [])
    if not isinstance(templates, list):
        bad("private.urlTemplates", "expected a list")
        templates = []
    if not isinstance(refs, list):
        bad("private.refs", "expected a list")
        refs = []
    if require_urls and not (templates and refs):
        bad("private", "urlTemplates and refs must not be empty to fetch verifiers by URL")
    for template in templates:
        reason = template_error(template)
        if reason:
            bad("private.urlTemplates", "%s: %s" % (_short(template), reason))
    for ref in refs:
        if not (isinstance(ref, str) and _REF.match(ref) and ".." not in ref) or _stray_braces(ref) or \
                any(name != "plugin" for name in _FIELD.findall(ref)):
            bad("private.refs", "invalid ref %s (only {plugin} may be a placeholder)" % _short(ref))


def manifest_errors(manifest, require_urls=False):
    """Every structural problem of a manifest (contract 5.1) as 'path: message'; [] when it is valid.

    Unknown keys are ignored (schema 1 only ever adds optional fields). require_urls demands the private URL list.
    """
    if not isinstance(manifest, dict):
        return ["manifest: expected a JSON object"]
    errors = []

    def bad(where, message):
        errors.append("%s: %s" % (where, message))

    if type(manifest.get("schema")) is not int or manifest["schema"] != SCHEMA:
        bad("schema", "expected the integer %d" % SCHEMA)
    if not _text(manifest.get("packId")):
        bad("packId", "expected a non-empty string")
    if not is_semver(manifest.get("packVersion")):
        bad("packVersion", "expected a semantic version")
    _plugin_errors(manifest.get("plugin"), bad)
    listed = _tracks_errors(manifest.get("tracks"), bad)
    tasks = manifest.get("tasks")
    if not isinstance(tasks, dict):
        bad("tasks", "expected an object")
    else:
        for task_id, entry in tasks.items():
            _task_errors(task_id, entry, listed, bad)
        for task_id in listed:
            if task_id not in tasks:
                bad("tracks", "task %s has no entry in tasks" % task_id)
    _private_errors(manifest.get("private"), require_urls, bad)
    return errors


def parse_manifest(raw, label=MANIFEST_NAME, require_urls=False):
    """Validated manifest dict from raw bytes; PACK_INVALID / SCHEMA_UNSUPPORTED (a newer manifest) otherwise."""
    try:
        manifest = json.loads(raw.decode("utf-8-sig"))
    except (ValueError, RecursionError):
        raise _invalid("%s is not valid JSON" % label)
    schema = manifest.get("schema") if isinstance(manifest, dict) else None
    if type(schema) is int and schema > SCHEMA:
        raise ProblemError(problem("SCHEMA_UNSUPPORTED", detail="%s has schema %d" % (label, schema)))
    errors = manifest_errors(manifest, require_urls)
    if errors:
        more = len(errors) - MAX_DETAIL_ERRORS
        raise _invalid("%s: %s%s" % (label, "; ".join(errors[:MAX_DETAIL_ERRORS]), " (+%d more)" % more if more > 0
                                     else ""))
    return manifest


def read_manifest(path, require_urls=False):
    """(manifest, sha256 of the raw file bytes) for manifest.json at `path`."""
    try:
        raw = read_file(path)
    except FileNotFoundError:
        raise _invalid("%s not found" % path)
    except (OSError, ValueError) as exc:
        raise _invalid("%s cannot be read: %s" % (path, type(exc).__name__))
    return parse_manifest(raw, require_urls=require_urls), store.sha256_bytes(raw)


# ---------------------------------------------------------------- plugin range, selection


def plugin_in_range(manifest, version):
    plugin = manifest.get("plugin") or {}
    current = version_tuple(version)
    low, high = plugin.get("min"), plugin.get("max")
    return not (low and current < version_tuple(low)) and not (high and current > version_tuple(high))


def plugin_range_problem(manifest, version):
    """The PACK_PLUGIN_RANGE problem when `version` is outside plugin.min..plugin.max, else None."""
    if plugin_in_range(manifest, version):
        return None
    plugin = manifest.get("plugin") or {}
    return problem("PACK_PLUGIN_RANGE", [plugin.get("min") or "any", plugin.get("max") or "latest"])


def tier_tasks(manifest, track, tier):
    """The default, ordered task list of a track and tier."""
    return list((((manifest.get("tracks") or {}).get(track)) or {}).get(tier) or [])


def all_task_ids(manifest):
    """Every task id in default play order: tracks ops then dev, tiers basic to challenge."""
    return [task_id for track in TRACKS for tier in TIERS for task_id in tier_tasks(manifest, track, tier)]


def select_tasks(manifest, track, tier, tasks=None):
    """Ordered task ids for `--tasks` (CSV string or list; None or empty = the whole tier); TASKS_INVALID on unknown
    or duplicate ids, an empty selection or an empty tier."""
    pool = tier_tasks(manifest, track, tier)
    if tasks:
        items = tasks.split(",") if isinstance(tasks, str) else tasks
        wanted = [str(item).strip() for item in items if str(item).strip()]
        unknown = [task for task in wanted if task not in pool]
        if unknown:
            raise ProblemError(problem("TASKS_INVALID", ["not in %s/%s: %s" % (track, tier, ", ".join(unknown))]))
        if len(set(wanted)) != len(wanted):
            raise ProblemError(problem("TASKS_INVALID", ["duplicate task ids"]))
        if not wanted:
            raise ProblemError(problem("TASKS_INVALID", ["empty selection"]))
        return wanted
    if not pool:
        raise ProblemError(problem("TASKS_INVALID", ["%s/%s has no tasks" % (track, tier)]))
    return pool


# ---------------------------------------------------------------- module loading


def load_source_module(name, path, data=None):
    """Executes a Python file as module `name` (registered in sys.modules) with no bytecode cache read or written.

    `data` are the already verified bytes; the file is only read when none are given. The module comes from
    importlib.util.spec_from_file_location, so __file__ and __spec__ behave as for a normal import.
    """
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None:
        raise ImportError("not a Python source file: %s" % os.path.basename(path))
    if data is None:
        with open(path, "rb") as handle:
            data = handle.read()
    module = importlib.util.module_from_spec(spec)
    previous = sys.modules.get(name)
    sys.modules[name] = module
    try:
        exec(compile(data, path, "exec", dont_inherit=True), module.__dict__)
    except BaseException:
        if previous is not None:
            sys.modules[name] = previous
        else:
            sys.modules.pop(name, None)
        raise
    return module


def describe_error(exc):
    message = " ".join(str(exc).split())[:200]
    text = "%s: %s" % (type(exc).__name__, message) if message else type(exc).__name__
    line = getattr(exc, "lineno", None)
    return "%s (line %s)" % (text, line) if isinstance(exc, SyntaxError) and line else text


def load_task_file(task_id, path):
    """The TASK object of pack/tasks/<ID>/task.py; PACK_INVALID when the module is not a complete PublicTask."""
    base = submodule("task_api").PublicTask
    try:
        module = load_source_module(TASK_MODULE_PREFIX + task_id, path)
    except (Exception, SystemExit) as exc:
        raise _invalid("task %s: %s" % (task_id, describe_error(exc)))
    task = getattr(module, "TASK", None)
    if not isinstance(task, base):
        raise _invalid("task %s: task.py defines no TASK instance of PublicTask" % task_id)
    for method in REQUIRED_METHODS:
        if getattr(type(task), method, None) is getattr(base, method, None):
            raise _invalid("task %s: %s() is not implemented" % (task_id, method))
    return task


def task_mismatches(task, task_id, entry):
    """How a loaded task's class attributes differ from its manifest entry (contract 5.1); [] when they agree."""
    out = []
    for name, actual, expected in (("id", task.id, task_id), ("track", task.track, entry.get("track")),
                                   ("tier", task.tier, entry.get("tier")),
                                   ("case_version", task.case_version, entry.get("caseVersion")),
                                   ("minutes", task.minutes, entry.get("minutes"))):
        if actual != expected or type(actual) is not type(expected):
            out.append("%s is %s but the manifest says %s" % (name, _short(actual), _short(expected)))
    actual, expected = normalize_requires(task.requires), normalize_requires(entry.get("requires"))
    for key in sorted(set(actual) | set(expected)):
        if actual.get(key) != expected.get(key):
            out.append("requires.%s is %s but the manifest says %s" % (key, _short(actual.get(key)),
                                                                        _short(expected.get(key))))
    return out


# ---------------------------------------------------------------- public file integrity


def ignored_name(name):
    """Names that never count as pack content: dot files, bytecode caches, Windows folder metadata."""
    return name.startswith(".") or name == "__pycache__" or name.endswith((".pyc", ".pyo")) or \
        name.lower() in ("thumbs.db", "desktop.ini")


def scan_public(root, manifest):
    """Labels ('<ID>/<file>') of public files that differ from the manifest or are not listed in it.

    A listed file that is missing is a damaged pack (PACK_INVALID).
    """
    modified = []
    for task_id, entry in manifest["tasks"].items():
        directory = os.path.join(root, "tasks", task_id)
        listed = entry["public"]
        for name, expected in listed.items():
            try:
                digest = store.sha256_bytes(read_file(os.path.join(directory, name)))
            except FileNotFoundError:
                raise _invalid("tasks/%s/%s is missing" % (task_id, name))
            except (OSError, ValueError) as exc:
                raise _invalid("tasks/%s/%s cannot be read: %s" % (task_id, name, type(exc).__name__))
            if digest != expected:
                modified.append("%s/%s" % (task_id, name))
        for name in sorted(os.listdir(directory)):
            if name not in listed and not ignored_name(name):
                modified.append("%s/%s" % (task_id, name))
    return sorted(modified)


def scan_private_dir(directory, manifest):
    """Labels ('private/<file>') of private files in `directory` whose hash differs from the manifest."""
    modified = []
    for entry in manifest["tasks"].values():
        private = entry["private"]
        try:
            digest = store.sha256_bytes(read_file(os.path.join(directory, private["file"])))
        except (OSError, ValueError):
            continue                       # absent or unreadable: reported when that task's verifier is fetched
        if digest != private["sha256"]:
            modified.append("private/%s" % private["file"])
    return sorted(modified)


# ---------------------------------------------------------------- Pack


class Pack(object):
    """An opened pack.

    source: package | pack-dir | zip (where the public tasks and private files come from, contract 4.4);
    private_kind: url | zip | pack-dir | private-dir; official: no modified file and not a pack-dir.
    """

    def __init__(self, root, manifest, manifest_sha256, source, private_kind, private_path, official, modified):
        self.root = root
        self.manifest = manifest
        self.manifest_sha256 = manifest_sha256
        self.source = source
        self.private_kind = private_kind
        self.private_path = private_path
        self.official = official
        self.modified = list(modified)
        self._tasks = {}

    @property
    def id(self):
        return self.manifest["packId"]

    @property
    def version(self):
        return self.manifest["packVersion"]

    @property
    def plugin_range(self):
        plugin = self.manifest.get("plugin") or {}
        return plugin.get("min"), plugin.get("max")

    def task_ids(self):
        return all_task_ids(self.manifest)

    def _entry(self, task_id):
        entry = (self.manifest.get("tasks") or {}).get(task_id) if isinstance(task_id, str) else None
        if entry is None:
            raise ProblemError(problem("TASKS_INVALID", ["unknown task id %s" % _short(task_id)]))
        return entry

    def meta(self, task_id):
        """The task's manifest entry plus its id, with `requires` defaults filled in."""
        meta = copy.deepcopy(self._entry(task_id))
        meta["id"] = task_id
        meta["requires"] = normalize_requires(meta.get("requires"))
        return meta

    def private_entry(self, task_id):
        return dict(self._entry(task_id)["private"])

    def task_dir(self, task_id):
        self._entry(task_id)
        return os.path.join(self.root, "tasks", task_id)

    def prompt_path(self, task_id, lang):
        return os.path.join(self.task_dir(task_id), "prompt.%s.md" % lang)

    def select(self, track, tier, tasks=None):
        return select_tasks(self.manifest, track, tier, tasks)

    def plugin_range_problem(self, version):
        return plugin_range_problem(self.manifest, version)

    def load_task(self, task_id):
        """The task's TASK object, checked against its manifest entry (PACK_INVALID on any mismatch)."""
        task = self._tasks.get(task_id)
        if task is None:
            entry = self._entry(task_id)
            task = load_task_file(task_id, os.path.join(self.task_dir(task_id), "task.py"))
            errors = task_mismatches(task, task_id, entry)
            if errors:
                raise _invalid("task %s: %s" % (task_id, "; ".join(errors)))
            self._tasks[task_id] = task
        return task


def _directory(flag, path):
    if not os.path.isdir(path):
        raise ProblemError(problem("BAD_ARGUMENTS", ["%s: not a directory: %s" % (flag, path)]))
    return os.path.abspath(path)


def open_pack(pack_dir=None, private_zip=None, private_dir=None, base_dir=None):
    """The pack for this run (contract 2.4, 5.9). At most one of the three sources may be given; base_dir stands in
    for <pkg>/Evaluation~/pack (tests).

    default            package pack, verifiers by URL           source package,  private_kind url
    private_zip        package pack, verifiers from a zip       source zip,      private_kind zip
    private_dir        package pack, verifiers from a folder    source package,  private_kind private-dir
    pack_dir           everything from the folder (dev)         source pack-dir, private_kind pack-dir, never official
    """
    given = [flag for flag, value in (("--pack-dir", pack_dir), ("--private-zip", private_zip),
                                      ("--private-dir", private_dir)) if value]
    if len(given) > 1:
        raise ProblemError(problem("BAD_ARGUMENTS", ["%s are mutually exclusive" % " and ".join(given)]))
    root, source, kind, path = base_dir or PACK_DIR, "package", "url", None
    if pack_dir:
        root = path = _directory("--pack-dir", pack_dir)
        source, kind = "pack-dir", "pack-dir"
    elif private_zip:
        source, kind, path = "zip", "zip", os.path.abspath(private_zip)
    elif private_dir:
        kind, path = "private-dir", _directory("--private-dir", private_dir)
    manifest, digest = read_manifest(os.path.join(root, MANIFEST_NAME), require_urls=(kind == "url"))
    modified = scan_public(root, manifest)
    if kind == "private-dir":
        modified = sorted(modified + scan_private_dir(path, manifest))
    return Pack(root, manifest, digest, source, kind, path, source != "pack-dir" and not modified, modified)

# Producer:Betsy
