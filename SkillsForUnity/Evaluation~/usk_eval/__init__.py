"""UnitySkills evaluation runner: standard library only, Python 3.9+ (contract v3-contracts.md)."""
import importlib
import json
import os

RUNNER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # <pkg>/Evaluation~
PACKAGE_ROOT = os.path.dirname(RUNNER_DIR)                                 # <pkg>
PACKAGE_NAME = "com.besty.unity-skills"
RUNNER_SCRIPT = os.path.join(RUNNER_DIR, "run_eval.py")


def package_version(package_root=None):
    """runnerVersion: the `version` of <pkg>/package.json; never hardcoded (contract 9)."""
    path = os.path.join(package_root or PACKAGE_ROOT, "package.json")
    with open(path, "r", encoding="utf-8-sig") as handle:
        version = json.load(handle).get("version")
    if not isinstance(version, str) or not version:
        raise ValueError("package.json has no version: %s" % path)
    return version


def submodule(name):
    """usk_eval.<name> looked up through sys.modules, so tests can stand in for modules owned by other packages."""
    return importlib.import_module(__name__ + "." + name)

# Producer:Betsy
