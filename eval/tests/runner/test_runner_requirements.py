"""TextMesh Pro Essential Resources check (contract 2.4, 3.1): the environment helpers and the problem check, start and
next report for tasks that build uGUI. The projects are temp folders; no editor is involved."""
import json
import os
import shutil
import tempfile
import unittest

from .runner_support import Bench, make_project
from usk_eval import environment as E

UGUI, TMP = "com.unity.ugui", "com.unity.textmeshpro"
ESSENTIALS = "TMP Essential Resources.unitypackage"
DEFAULT_SETTINGS = "Assets/TextMesh Pro/Resources/TMP Settings.asset"


def write_lock(project, **packages):
    """Adds dependencies (name -> lock entry) to the project's packages-lock.json."""
    path = os.path.join(project, "Packages", "packages-lock.json")
    with open(path, "r", encoding="utf-8") as handle:
        lock = json.load(handle)
    lock["dependencies"].update(packages)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(lock, handle)


def registry(version):
    return {"version": version, "depth": 0, "source": "registry"}


def builtin(version):
    return {"version": version, "depth": 0, "source": "builtin"}


def write_package(directory, name, version, essentials=True):
    os.makedirs(os.path.join(directory, "Package Resources"), exist_ok=True)
    with open(os.path.join(directory, "package.json"), "w", encoding="utf-8") as handle:
        json.dump({"name": name, "version": version}, handle)
    if essentials:
        with open(os.path.join(directory, "Package Resources", ESSENTIALS), "wb") as handle:
            handle.write(b"unitypackage")
    return directory


def cache(project, name, version, folder=None, essentials=True):
    """A Library/PackageCache copy of a package; the folder suffix is a version or a fingerprint."""
    return write_package(os.path.join(project, "Library", "PackageCache", folder or "%s@%s" % (name, version)),
                         name, version, essentials)


def write_asset(project, rel, text="%YAML 1.1\n"):
    path = os.path.join(project, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
    return path


class PackageHelperTests(unittest.TestCase):
    def setUp(self):
        self.tmp = os.path.realpath(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.project = make_project(self.tmp)

    def carriers(self, installed):
        return E.tmp_packages(self.project, installed)

    def test_packages_that_carry_text_mesh_pro(self):
        self.assertEqual(self.carriers({}), [])
        self.assertEqual(self.carriers({UGUI: builtin("1.0.0")}), [])
        self.assertEqual(self.carriers({UGUI: builtin("2.0.0")}), [UGUI])
        self.assertEqual(self.carriers({UGUI: builtin("2.5.0")}), [UGUI])
        self.assertEqual(self.carriers({UGUI: registry("2.0.0-pre.1")}), [UGUI])
        self.assertEqual(self.carriers({UGUI: builtin("1.0.0"), TMP: registry("3.0.7")}), [TMP])
        self.assertEqual(self.carriers({TMP: registry("3.2.0-pre.4"), UGUI: builtin("2.0.0")}), [TMP, UGUI])

    def test_has_text_mesh_pro_reads_the_lock_and_verifylib_shares_it(self):
        from usk_eval import verifylib
        self.assertIs(verifylib.has_text_mesh_pro, E.has_text_mesh_pro)
        self.assertIs(verifylib.active_input_handler, E.active_input_handler)
        self.assertFalse(E.has_text_mesh_pro(self.project))                       # only UnitySkills in the lock
        write_lock(self.project, **{UGUI: builtin("1.0.0")})
        self.assertFalse(E.has_text_mesh_pro(self.project))
        write_lock(self.project, **{TMP: registry("3.0.7")})
        self.assertTrue(E.has_text_mesh_pro(self.project))
        lock = os.path.join(self.project, "Packages", "packages-lock.json")
        os.remove(lock)
        self.assertFalse(E.has_text_mesh_pro(self.project))                       # no lock file at all
        self.assertEqual(verifylib.installed_packages(self.project), {})
        with open(lock, "w", encoding="utf-8") as handle:
            json.dump({"dependencies": {UGUI: builtin("2.0.0")}}, handle)
        self.assertTrue(E.has_text_mesh_pro(self.project))
        self.assertIn(UGUI, verifylib.installed_packages(self.project))

    def test_active_input_handling_is_read_from_the_settings_file(self):
        settings = os.path.join(self.project, "ProjectSettings", "ProjectSettings.asset")
        os.remove(settings)
        self.assertEqual(E.active_input_handler(self.project), 0)                 # no file: the Input Manager
        for text, handler in (("PlayerSettings:\n  productName: Game\n", 0),
                              ("PlayerSettings:\n  activeInputHandler: 0\n", 0),
                              ("PlayerSettings:\n  productName: Game\n  activeInputHandler: 2\n  other: 1\n", 2),
                              ("PlayerSettings:\r\n  activeInputHandler: 1\r\n", 1),
                              ("PlayerSettings:\n  note: activeInputHandler: 1\n", 0)):
            with open(settings, "w", encoding="utf-8", newline="") as handle:
                handle.write(text)
            self.assertEqual(E.active_input_handler(self.project), handler, text)

    def test_a_ugui_version_that_is_a_path_is_read_from_the_package_itself(self):
        installed = {UGUI: {"version": "file:../ugui", "depth": 0, "source": "local"}}
        self.assertEqual(self.carriers(installed), [])               # nothing to read the version from
        write_package(os.path.join(self.project, "Packages", "ugui-fork"), UGUI, "1.0.0")
        self.assertEqual(self.carriers(installed), [])
        write_package(os.path.join(self.project, "Packages", "ugui-fork"), UGUI, "2.1.0")
        self.assertEqual(self.carriers(installed), [UGUI])
        git = {UGUI: {"version": "https://example.com/ugui.git", "depth": 0, "source": "git", "hash": "abc"}}
        shutil.rmtree(os.path.join(self.project, "Packages", "ugui-fork"))
        self.assertEqual(self.carriers(git), [])
        cache(self.project, UGUI, "2.0.0", folder="%s@abc" % UGUI)
        self.assertEqual(self.carriers(git), [UGUI])
        self.assertEqual(self.carriers({UGUI: "2.0.0"}), [UGUI])        # a corrupt entry still finds the cache

    def test_settings_asset_is_found_where_resources_load_reaches_it(self):
        self.assertIsNone(E.tmp_settings_asset(self.project))
        for rel in ("Assets/Game/TMP Settings.asset",                         # not in a Resources folder
                    "Assets/Game/Resources/Fonts/TMP Settings.asset",         # Resources.Load("TMP Settings") misses it
                    "Assets/.hidden/Resources/TMP Settings.asset",            # Unity ignores hidden folders
                    "Assets/Tools~/Resources/TMP Settings.asset",
                    "Assets/Game/Resources/TMP Settings.asset.meta"):
            write_asset(self.project, rel)
        self.assertIsNone(E.tmp_settings_asset(self.project))
        write_asset(self.project, "Assets/Game/Resources/TMP Settings.asset")
        self.assertEqual(E.tmp_settings_asset(self.project), "Assets/Game/Resources/TMP Settings.asset")

    def test_default_location_embedded_packages_and_folder_case(self):
        write_asset(self.project, "Packages/com.acme.ui/Runtime/Resources/TMP Settings.asset")
        self.assertEqual(E.tmp_settings_asset(self.project),
                         "Packages/com.acme.ui/Runtime/Resources/TMP Settings.asset")
        write_asset(self.project, DEFAULT_SETTINGS)
        self.assertEqual(E.tmp_settings_asset(self.project), DEFAULT_SETTINGS)
        shutil.rmtree(os.path.join(self.project, "Packages", "com.acme.ui"))
        os.remove(os.path.join(self.project, *DEFAULT_SETTINGS.split("/")))
        write_asset(self.project, "Assets/Kit/resources/TMP Settings.asset")
        self.assertEqual(E.tmp_settings_asset(self.project), "Assets/Kit/resources/TMP Settings.asset")

    def test_essentials_package_lookup(self):
        installed = {UGUI: builtin("2.0.0")}
        self.assertIsNone(E.tmp_essentials_package(self.project, installed))
        bare = cache(self.project, UGUI, "2.0.0", folder=UGUI + "@8aa876c215ef", essentials=False)
        self.assertIsNone(E.tmp_essentials_package(self.project, installed))       # cached, but no .unitypackage
        shutil.rmtree(bare)
        found = cache(self.project, UGUI, "2.0.0", folder=UGUI + "@8aa876c215ef")
        self.assertEqual(E.tmp_essentials_package(self.project, installed),
                         os.path.join(found, "Package Resources", ESSENTIALS))
        self.assertIsNone(E.tmp_essentials_package(self.project, {UGUI: builtin("1.0.0")}))   # no TextMesh Pro there

    def test_the_cached_copy_of_the_locked_version_wins_then_the_newest(self):
        installed = {UGUI: builtin("2.0.0")}
        old = cache(self.project, UGUI, "2.0.0", folder=UGUI + "@aaaaaaaaaaaa")
        new = cache(self.project, UGUI, "2.5.0", folder=UGUI + "@bbbbbbbbbbbb")
        for directory, stamp in ((old, 1_700_000_000), (new, 1_800_000_000)):
            os.utime(os.path.join(directory, "package.json"), (stamp, stamp))
        wanted = os.path.join(old, "Package Resources", ESSENTIALS)
        self.assertEqual(E.tmp_essentials_package(self.project, installed), wanted)
        newest = os.path.join(new, "Package Resources", ESSENTIALS)
        self.assertEqual(E.tmp_essentials_package(self.project, {UGUI: builtin("2.2.0")}), newest)

    def test_the_text_mesh_pro_package_and_embedded_copies(self):
        installed = {UGUI: builtin("1.0.0"), TMP: registry("3.0.7")}
        found = cache(self.project, TMP, "3.0.7")
        self.assertEqual(E.tmp_essentials_package(self.project, installed),
                         os.path.join(found, "Package Resources", ESSENTIALS))
        embedded = write_package(os.path.join(self.project, "Packages", "tmp-fork"), TMP, "3.0.7")
        self.assertEqual(E.tmp_essentials_package(self.project, installed),
                         os.path.join(embedded, "Package Resources", ESSENTIALS))      # an embedded copy comes first


class Base(unittest.TestCase):
    def setUp(self):
        self.b = Bench(self)
        self.pack_tasks = self.b.stand.pack.manifest["tasks"]
        self.pack_tasks["S99"]["requires"]["packages"] = [UGUI]          # S99 builds uGUI, S98 does not
        self.project = self.b.project

    def codes(self, envelope):
        return [p["code"] for p in envelope["problems"]]

    def essentials(self, version="2.0.0"):
        directory = cache(self.project, UGUI, version, folder=UGUI + "@8aa876c215ef")
        return os.path.join(directory, "Package Resources", ESSENTIALS)


class MissingResourcesTests(Base):
    def test_check_names_both_fixes_and_the_cached_package(self):
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        package = self.essentials()
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["TMP_RESOURCES_MISSING"]))
        problem = env["problems"][0]
        self.assertEqual(problem["args"], ["S99", self.project, package])
        for part in ("Window > TextMeshPro > Import TMP Essential Resources",
                     "-batchmode -projectPath \"%s\"" % self.project, "-importPackage \"%s\"" % package, "-quit",
                     "editor closed"):
            self.assertIn(part, problem["fix"])
        self.assertIn("com.unity.ugui carries TextMesh Pro", problem["message"])
        self.assertFalse(env["data"]["ready"])
        self.assertIsNone(env["next"])

    def test_the_fix_describes_where_the_package_is_when_it_is_not_cached(self):
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["TMP_RESOURCES_MISSING"]))
        shown = "<com.unity.ugui folder>/Package Resources/TMP Essential Resources.unitypackage"
        self.assertEqual(env["problems"][0]["args"], ["S99", self.project, shown])
        self.assertIn("-importPackage \"%s\"" % shown, env["problems"][0]["fix"])

    def test_the_text_mesh_pro_package_of_unity_2022_is_covered(self):
        write_lock(self.project, **{UGUI: builtin("1.0.0"), TMP: registry("3.0.7")})
        directory = cache(self.project, TMP, "3.0.7")
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["TMP_RESOURCES_MISSING"]))
        self.assertEqual(env["problems"][0]["args"][2], os.path.join(directory, "Package Resources", ESSENTIALS))
        self.assertIn("com.unity.textmeshpro carries TextMesh Pro", env["problems"][0]["message"])

    def test_imported_resources_clear_the_problem(self):
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        self.essentials()
        write_asset(self.project, DEFAULT_SETTINGS)
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, env["problems"], env["data"]["ready"]), (0, [], True))
        os.remove(os.path.join(self.project, *DEFAULT_SETTINGS.split("/")))
        write_asset(self.project, "Assets/Elsewhere/Resources/TMP Settings.asset")          # moved by the user
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, env["problems"]), (0, []))

    def test_only_tasks_that_build_ugui_on_a_project_with_text_mesh_pro_are_checked(self):
        code, env = self.b.run("check", "--tasks", "S99")                     # no ugui in the lock at all
        self.assertEqual(self.codes(env), ["PACKAGE_MISSING"])
        write_lock(self.project, **{UGUI: builtin("1.0.0")})                  # Unity 2022 without TextMesh Pro
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, env["problems"]), (0, []))
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        code, env = self.b.run("check", "--tasks", "S98")                     # S98 has no uGUI requirement
        self.assertEqual((code, env["problems"]), (0, []))
        code, env = self.b.run("check", "--tasks", "S99,S98")                 # the problem names the uGUI task only
        self.assertEqual((code, self.codes(env), env["problems"][0]["args"][0]), (2, ["TMP_RESOURCES_MISSING"], "S99"))
        self.pack_tasks["S98"]["requires"]["packages"] = [UGUI]
        code, env = self.b.run("check", "--tasks", "S99,S98")
        self.assertEqual(env["problems"][0]["args"][0], "S99, S98")

    def test_the_problem_follows_the_package_check_in_the_listing(self):
        self.pack_tasks["S99"]["requires"]["packages"] = [UGUI, "com.unity.cinemachine"]
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        code, env = self.b.run("check", "--tasks", "S99")
        self.assertEqual((code, self.codes(env)), (2, ["PACKAGE_MISSING", "TMP_RESOURCES_MISSING"]))

    def test_start_is_blocked_and_next_checks_again(self):
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        self.essentials()
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S99")
        self.assertEqual((code, self.codes(env), env["data"]["created"]), (2, ["TMP_RESOURCES_MISSING"], False))
        self.assertFalse(os.path.exists(self.b.paths.current))
        settings = write_asset(self.project, DEFAULT_SETTINGS)
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S99")
        self.assertEqual(code, 0, env)
        sid = env["data"]["sessionId"]
        os.remove(settings)                                                   # gone between start and next
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, self.codes(env)), (2, ["TMP_RESOURCES_MISSING"]))
        self.assertEqual(self.b.session(sid)["tasks"]["S99"]["attempts"], [])
        write_asset(self.project, DEFAULT_SETTINGS)
        code, env = self.b.run("next", "--session", sid)
        self.assertEqual((code, env["data"]["taskId"]), (0, "S99"))

    def test_next_leaves_other_tasks_alone(self):
        write_lock(self.project, **{UGUI: builtin("2.0.0")})
        code, env = self.b.run("start", "--lang", "en", "--tasks", "S98")
        self.assertEqual(code, 0, env)
        code, env = self.b.run("next", "--session", env["data"]["sessionId"])
        self.assertEqual((code, env["data"]["taskId"]), (0, "S98"))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
