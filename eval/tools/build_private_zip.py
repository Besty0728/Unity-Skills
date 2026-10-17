"""Builds the private verifiers zip of a release (the runner's --private-zip, contract 5.9). Writes nothing else.

The zip root holds exactly the private files the manifest names (<ID>.py), each checked against the manifest's sha256
and byte count; entries are sorted and stamped 1980-01-01, so the same files always give a byte-identical zip. After
writing, every task's verifier is fetched back through the runner's own zip reader.

    python3 -B eval/tools/build_private_zip.py --out dist/unity-skills-eval-private-v3.0.0.zip
                                               [--pack-root DIR] [--private-root DIR]

Exit 0 when the zip was written and read back, 1 when a private file is missing or differs from the manifest (no zip
is written), 2 when the command could not run.
"""
import argparse
import hashlib
import json
import os
import sys
import zipfile

sys.dont_write_bytecode = True
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RUNNER = os.path.join(REPO, "SkillsForUnity", "Evaluation~")
if RUNNER not in sys.path:
    sys.path.insert(0, RUNNER)

from usk_eval import pack, private          # noqa: E402
from usk_eval.problems import ProblemError  # noqa: E402

DEFAULT_PACK_ROOT = os.path.join(RUNNER, "pack")
DEFAULT_PRIVATE_ROOT = os.path.join(REPO, "eval", "pack", "private")
ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def private_entries(manifest):
    """{file: (sha256, bytes)} of every task's private verifier."""
    out = {}
    for _task_id, entry in sorted((manifest.get("tasks") or {}).items()):
        item = entry.get("private") or {}
        out[item["file"]] = (item["sha256"], item["bytes"])
    return out


def collect(manifest, private_root):
    """([(file, data)], errors) for the manifest's private files under private_root."""
    files, errors = [], []
    for name, (digest, size) in sorted(private_entries(manifest).items()):
        path = os.path.join(private_root, name)
        try:
            with open(path, "rb") as handle:
                data = handle.read()
        except OSError as exc:
            errors.append("%s: %s" % (name, type(exc).__name__))
            continue
        if hashlib.sha256(data).hexdigest() != digest or len(data) != size:
            errors.append("%s differs from the manifest (sha256 or bytes)" % name)
            continue
        files.append((name, data))
    return files, errors


def write_zip(files, out_path):
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    temp = out_path + ".tmp-%d" % os.getpid()
    try:
        with zipfile.ZipFile(temp, "w") as archive:
            for name, data in files:
                info = zipfile.ZipInfo(name, date_time=ZIP_TIME)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                archive.writestr(info, data)
        os.replace(temp, out_path)
    finally:
        if os.path.exists(temp):
            os.remove(temp)


def read_back(pack_root, out_path):
    """Errors from fetching every task's verifier out of the zip the way a session would."""
    opened = pack.open_pack(private_zip=out_path, base_dir=pack_root)
    errors = []
    for task_id in opened.task_ids():
        try:
            fetched = private.fetch(opened, task_id, private_zip=out_path)
        except private.PrivateUnavailable as exc:
            errors.append("%s: %s" % (task_id, ", ".join(a.get("error") or "?" for a in exc.attempts)))
            continue
        if not fetched.official:
            errors.append("%s: the zip copy is not official" % task_id)
        fetched.discard()
    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(prog="build_private_zip.py", description=__doc__.split("\n\n")[0])
    parser.add_argument("--out", required=True, help="zip file to write")
    parser.add_argument("--pack-root", default=DEFAULT_PACK_ROOT, help="folder holding manifest.json and tasks/")
    parser.add_argument("--private-root", default=DEFAULT_PRIVATE_ROOT, help="folder holding the private verifiers")
    args = parser.parse_args(argv)
    try:
        with open(os.path.join(args.pack_root, "manifest.json"), "r", encoding="utf-8-sig") as handle:
            manifest = json.load(handle)
        files, errors = collect(manifest, args.private_root)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print("build_private_zip: cannot read the manifest: %s: %s" % (type(exc).__name__, exc))
        return 2
    if errors:
        for error in errors:
            print("error: %s" % error)
        return 1
    try:
        write_zip(files, args.out)
        problems = read_back(args.pack_root, args.out)
    except (OSError, ProblemError) as exc:
        print("build_private_zip: %s" % exc)
        return 2
    if problems:
        for problem in problems:
            print("error: %s" % problem)
        return 1
    with open(args.out, "rb") as handle:
        digest = hashlib.sha256(handle.read()).hexdigest()
    print("%s: %d private files, sha256 %s" % (args.out, len(files), digest))
    return 0


if __name__ == "__main__":
    sys.exit(main())

# Producer:Betsy
