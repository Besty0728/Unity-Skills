"""Checks a task pack against its manifest and the last released manifest (contract 5.1, 9). Read-only.

    python3 -B eval/tools/check_eval_pack.py [--pack-dir DIR | --pack-root DIR --private-root DIR] [--manifest FILE]
                                             [--baseline FILE | --baseline-ref REF | --no-baseline]
                                             [--require-baseline] [--package-json FILE] [--release]

Exit 0 when there is no error, 1 when there is, 2 when the check could not run.
Errors: a manifest the runner would reject; a public or private file that differs from its manifest hash; a public
file or task folder the manifest does not list; task.py attributes that disagree with the manifest; a task whose
files, seeds, minutes or requires changed since the baseline while its caseVersion did not rise (or fell); a
packVersion that did not move far enough. Warnings: files in the private folder the manifest does not use; a plugin
range that excludes the package version (an error with --release). Without --baseline the newest git tag v* that holds
a manifest is the baseline; --require-baseline turns "no baseline found" (a shallow clone without tags, the first
release) into an error, which /release should use.
"""
import argparse
import json
import os
import subprocess
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
MANIFEST_GIT_PATH = "SkillsForUnity/Evaluation~/pack/manifest.json"
GIT_TIMEOUT_S = 30


class Report(object):
    def __init__(self):
        self.errors, self.warnings, self.notes = [], [], []

    def error(self, text):
        self.errors.append(text)

    def warn(self, text):
        self.warnings.append(text)

    def note(self, text):
        self.notes.append(text)


# ---------------------------------------------------------------- the pack on disk


def _check_public(pack_root, manifest, report):
    try:
        modified = pack.scan_public(pack_root, manifest)
    except ProblemError as exc:
        report.error(exc.problems[0]["message"])
        return
    listed = set("%s/%s" % (task_id, name) for task_id, entry in manifest["tasks"].items() for name in entry["public"])
    for label in modified:
        if label in listed:
            report.error("public file %s differs from the manifest; run build_pack_manifest.py" % label)
        else:
            report.error("public file %s is not listed in the manifest" % label)


def _check_task_folders(pack_root, manifest, report):
    """A folder under tasks/ that the manifest does not list would ship in the package without a hash."""
    try:
        names = sorted(os.listdir(os.path.join(pack_root, "tasks")))
    except OSError:
        return
    for name in names:
        if name not in manifest["tasks"] and not pack.ignored_name(name):
            report.error("tasks/%s is not listed in the manifest" % name)


def _check_private(private_root, manifest, report):
    used = set()
    for task_id, entry in manifest["tasks"].items():
        private = entry["private"]
        used.add(private["file"])
        path = os.path.join(private_root, private["file"])
        try:
            raw = pack.read_file(path)
        except FileNotFoundError:
            report.error("task %s: private verifier %s not found in %s" % (task_id, private["file"], private_root))
            continue
        except (OSError, ValueError) as exc:
            report.error("task %s: private verifier %s cannot be read (%s)" % (task_id, private["file"],
                                                                              type(exc).__name__))
            continue
        if store.sha256_bytes(raw) != private["sha256"]:
            report.error("task %s: private verifier %s differs from the manifest; run build_pack_manifest.py" % (
                task_id, private["file"]))
        elif len(raw) != private["bytes"]:
            report.error("task %s: private verifier %s is %d bytes, the manifest says %d" % (
                task_id, private["file"], len(raw), private["bytes"]))
    try:
        names = sorted(os.listdir(private_root))
    except OSError:
        return
    for name in names:
        path = os.path.join(private_root, name)
        if name.endswith(".py") and name != "__init__.py" and name not in used and os.path.isfile(path):
            report.warn("private file %s is not used by the manifest" % name)


def _check_tasks(pack_root, manifest, report):
    for task_id, entry in manifest["tasks"].items():
        try:
            task = pack.load_task_file(task_id, os.path.join(pack_root, "tasks", task_id, "task.py"))
        except ProblemError as exc:
            report.error(exc.problems[0]["message"])
            continue
        for mismatch in pack.task_mismatches(task, task_id, entry):
            report.error("task %s: %s" % (task_id, mismatch))


def _check_plugin(manifest, package_version, release, report):
    if not package_version:
        report.note("package version unknown; plugin range not checked")
        return
    if pack.plugin_range_problem(manifest, package_version):
        plugin = manifest.get("plugin") or {}
        text = "plugin range %s..%s does not admit package version %s" % (plugin.get("min") or "any",
                                                                          plugin.get("max") or "latest",
                                                                          package_version)
        (report.error if release else report.warn)(text)


def check_pack(pack_root, private_root, manifest_path, package_version=None, release=False):
    """(Report, manifest or None): everything that can be checked without a baseline."""
    report = Report()
    try:
        raw = pack.read_file(manifest_path)
        data = json.loads(raw.decode("utf-8-sig"))
    except (OSError, ValueError) as exc:
        report.error("manifest %s cannot be read (%s)" % (manifest_path, type(exc).__name__))
        return report, None
    for error in pack.manifest_errors(data, require_urls=True):
        report.error("manifest: %s" % error)
    if report.errors:
        return report, None
    _check_public(pack_root, data, report)
    _check_task_folders(pack_root, data, report)
    _check_private(private_root, data, report)
    _check_tasks(pack_root, data, report)
    _check_plugin(data, package_version, release, report)
    return report, data


# ---------------------------------------------------------------- baseline


def _task_diff(old, entry):
    """What differs between a task in the baseline and now; any difference needs a higher caseVersion (contract 9)."""
    diff = []
    old_public = old.get("public") if isinstance(old.get("public"), dict) else {}
    for name in sorted(set(old_public) | set(entry["public"])):
        if old_public.get(name) != entry["public"].get(name):
            diff.append(name)
    old_private = old.get("private") if isinstance(old.get("private"), dict) else {}
    if old_private.get("sha256") != entry["private"]["sha256"]:
        diff.append("private verifier")
    for key in ("track", "tier", "minutes", "seeds"):
        if old.get(key) != entry.get(key):
            diff.append(key)
    if pack.normalize_requires(old.get("requires")) != pack.normalize_requires(entry.get("requires")):
        diff.append("requires")
    return diff


def _metadata(manifest):
    """The parts of a manifest that are not task content: everything except packVersion, the task hashes and the
    fields _task_diff already covers (those need a caseVersion, not just a packVersion)."""
    covered = ("track", "tier", "caseVersion", "minutes", "seeds", "requires", "public")
    tasks = {}
    for task_id, entry in (manifest.get("tasks") or {}).items():
        if isinstance(entry, dict):
            private = entry.get("private") if isinstance(entry.get("private"), dict) else {}
            tasks[task_id] = dict({key: value for key, value in entry.items() if key not in covered + ("private",)},
                                  private={key: value for key, value in private.items() if key != "sha256"})
    return store.canonical_digest(dict({key: value for key, value in manifest.items()
                                        if key not in ("packVersion", "tasks")}, tasks=tasks))


def compare_with_baseline(manifest, baseline, report):
    """The caseVersion and packVersion rules of contract 5.1 and 9 against the last released manifest."""
    if not isinstance(baseline, dict) or not isinstance(baseline.get("tasks"), dict):
        report.note("the baseline holds no tasks; comparison skipped")
        return
    if baseline.get("packId") != manifest["packId"]:
        report.note("the baseline is pack %r, not %r; comparison skipped" % (baseline.get("packId"),
                                                                              manifest["packId"]))
        return
    old_tasks, new_tasks = baseline["tasks"], manifest["tasks"]
    raised = False
    for task_id, entry in new_tasks.items():
        old = old_tasks.get(task_id)
        if not isinstance(old, dict) or type(old.get("caseVersion")) is not int:
            continue
        before, after, diff = old["caseVersion"], entry["caseVersion"], _task_diff(old, entry)
        if after < before:
            report.error("task %s: caseVersion fell from %d to %d" % (task_id, before, after))
        elif after == before and diff:
            report.error("task %s: %s changed but caseVersion is still %d" % (task_id, ", ".join(diff), after))
        elif after > before:
            raised = True
            if not diff:
                report.warn("task %s: caseVersion raised from %d to %d without a change" % (task_id, before, after))
    added = sorted(set(new_tasks) - set(old_tasks))
    removed = sorted(set(old_tasks) - set(new_tasks))
    if added:
        report.note("tasks added since the baseline: %s" % ", ".join(added))
    if removed:
        report.note("tasks removed since the baseline: %s" % ", ".join(removed))
    if not pack.is_semver(baseline.get("packVersion")):
        report.note("the baseline has no valid packVersion; packVersion rule skipped")
        return
    old_version, new_version = pack.version_tuple(baseline["packVersion"]), pack.version_tuple(manifest["packVersion"])
    if added or removed or raised:
        if new_version[:2] <= old_version[:2]:
            report.error("packVersion %s must raise at least the minor version of %s: tasks or caseVersions changed" % (
                manifest["packVersion"], baseline["packVersion"]))
    elif new_version <= old_version and _metadata(manifest) != _metadata(baseline):
        report.error("packVersion %s must be above %s: the manifest changed" % (manifest["packVersion"],
                                                                                baseline["packVersion"]))


def _git(repo, args, run):
    try:
        done = run(["git", "-C", repo] + list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                   timeout=GIT_TIMEOUT_S)
    except (OSError, subprocess.SubprocessError):
        return None
    return done.stdout if done.returncode == 0 else None


def baseline_from_git(repo, ref=None, run=subprocess.run):
    """(manifest dict, label) from `ref`, or from the newest v* tag that holds a manifest; (None, None) otherwise."""
    if ref:
        refs = [ref]
    else:
        listing = _git(repo, ["tag", "--list", "v*", "--sort=-v:refname"], run)
        refs = listing.decode("utf-8", "replace").split() if listing else []
    for name in refs:
        raw = _git(repo, ["show", "%s:%s" % (name, MANIFEST_GIT_PATH)], run)
        if raw is None:
            continue
        try:
            data = json.loads(raw.decode("utf-8-sig"))
        except ValueError:
            continue
        if isinstance(data, dict):
            return data, "git %s" % name
    return None, None


def _load_baseline_file(path):
    try:
        data = json.loads(pack.read_file(path).decode("utf-8-sig"))
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


# ---------------------------------------------------------------- command line


def _package_version(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            version = json.load(handle).get("version")
    except (OSError, ValueError):
        return None
    return version if pack.is_semver(version) else None


def _parser():
    parser = argparse.ArgumentParser(description="Check a task pack against its manifest and the last release.")
    parser.add_argument("--pack-dir", help="development pack folder: manifest.json, tasks/, private/")
    parser.add_argument("--pack-root", help="folder holding manifest.json and tasks/ (default: the package pack)")
    parser.add_argument("--private-root", help="folder holding the private verifiers (default: eval/pack/private)")
    parser.add_argument("--manifest", help="manifest file (default: <pack root>/manifest.json)")
    parser.add_argument("--package-json", help="package.json whose version the plugin range must admit")
    parser.add_argument("--release", action="store_true", help="a plugin range that excludes the package is an error")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--baseline", help="released manifest file to compare with")
    group.add_argument("--baseline-ref", help="git ref whose manifest is the baseline")
    group.add_argument("--no-baseline", action="store_true", help="skip the comparison with a released manifest")
    parser.add_argument("--require-baseline", action="store_true", help="fail when no baseline manifest is found")
    return parser


def main(argv=None, out=None, run=subprocess.run):
    out = out or sys.stdout
    args = _parser().parse_args(argv)
    if args.pack_dir and (args.pack_root or args.private_root):
        print("check_eval_pack: --pack-dir excludes --pack-root and --private-root", file=out)
        return 2
    if args.no_baseline and args.require_baseline:
        print("check_eval_pack: --require-baseline contradicts --no-baseline", file=out)
        return 2
    pack_root = os.path.abspath(args.pack_dir or args.pack_root or DEFAULT_PACK_ROOT)
    private_root = os.path.abspath(os.path.join(pack_root, "private") if args.pack_dir
                                   else args.private_root or DEFAULT_PRIVATE_ROOT)
    manifest_path = os.path.abspath(args.manifest or os.path.join(pack_root, pack.MANIFEST_NAME))
    if not os.path.isfile(manifest_path):
        print("check_eval_pack: no manifest at %s" % manifest_path, file=out)
        return 2
    report, manifest = check_pack(pack_root, private_root, manifest_path,
                                  _package_version(args.package_json or PACKAGE_JSON), args.release)
    if manifest is not None:
        baseline, label = None, None
        if args.no_baseline:
            report.note("baseline comparison disabled")
        elif args.baseline:
            baseline, label = _load_baseline_file(args.baseline), args.baseline
            if baseline is None:
                print("check_eval_pack: cannot read the baseline %s" % args.baseline, file=out)
                return 2
        else:
            baseline, label = baseline_from_git(REPO, args.baseline_ref, run)
            if baseline is None and args.baseline_ref:
                print("check_eval_pack: no manifest at git ref %s" % args.baseline_ref, file=out)
                return 2
            if baseline is None and args.require_baseline:
                report.error("no released manifest found in git tags; pass --baseline or --baseline-ref")
            elif baseline is None:
                report.note("no released manifest found in git tags; baseline comparison skipped")
        if baseline is not None:
            report.note("baseline: %s (pack %s)" % (label, baseline.get("packVersion")))
            compare_with_baseline(manifest, baseline, report)
    for kind, lines in (("error", report.errors), ("warning", report.warnings), ("note", report.notes)):
        for line in lines:
            print("%s: %s" % (kind, line), file=out)
    status = "FAILED" if report.errors else "OK"
    print("check_eval_pack: %s (%d errors, %d warnings%s)" % (
        status, len(report.errors), len(report.warnings),
        ", %d tasks" % len(manifest["tasks"]) if manifest is not None else ""), file=out)
    return 1 if report.errors else 0


if __name__ == "__main__":
    for _stream in (sys.stdout, sys.stderr):          # paths with letters the console cannot encode must not crash
        try:
            _stream.reconfigure(errors="backslashreplace")
        except (AttributeError, ValueError):
            pass
    sys.exit(main())

# Producer:Betsy
