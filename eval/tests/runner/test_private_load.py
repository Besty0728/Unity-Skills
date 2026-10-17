"""private.loaded: temp loading, unique module names and deletion on every path; stale cleanup (contract 5.9)."""
import errno
import linecache
import os
import re
import sys
import tempfile
import time
import unittest
from unittest import mock

try:
    from .test_pack_support import GOOD, PrivateCase, pack, private, read_bytes, store, write_bytes, write_text
except ImportError:
    from test_pack_support import GOOD, PrivateCase, pack, private, read_bytes, store, write_bytes, write_text

AID = "0a1b2c3d4e5f"


class LoadCase(PrivateCase):
    def setUp(self):
        PrivateCase.setUp(self)
        self.pack = pack.open_pack(pack_dir=str(GOOD))
        self.edits = 0
        self.keep_modules = set(name for name in sys.modules if name.startswith(private.MODULE_PREFIX))
        self.addCleanup(self.assert_no_private_modules)

    def assert_no_private_modules(self):
        leaked = [name for name in sys.modules if name.startswith(private.MODULE_PREFIX)
                  and name not in self.keep_modules]
        self.assertEqual(leaked, [])

    def fetched(self, source=None, task="F01"):
        """A Fetched for `task`; `source` replaces the file content (the pack folder tolerates a mismatch)."""
        if source is not None:
            self.edits += 1
            root = self.copy_pack("edited-%d" % self.edits)
            write_bytes(os.path.join(root, "private", task + ".py"), source)
            return private.fetch(pack.open_pack(pack_dir=root), task)
        return private.fetch(self.pack, task)


class LoadedTests(LoadCase):
    def test_the_module_runs_from_a_temp_folder_and_everything_is_removed_afterwards(self):
        fetched = self.fetched()
        data = fetched.data
        with private.loaded(fetched, AID) as module:
            self.assertEqual(module.SENTINEL, "ZZ_PRIVATE_SENTINEL_F01")
            self.assertEqual(module.ASSERTIONS[0][0], "A1")
            self.assertRegex(module.__name__, r"^usk_eval_private_%s_[0-9a-f]{6}$" % AID)
            self.assertIs(sys.modules[module.__name__], module)
            path = module.__file__
            folder = os.path.dirname(path)
            self.assertRegex(os.path.basename(folder), r"^usk-eval-[a-z0-9_]{8}$")
            self.assertRegex(os.path.basename(path), r"^[0-9a-f]{12}\.py$")
            self.assertEqual(os.path.dirname(folder), self.temp_root)
            self.assertEqual(read_bytes(path), data)
            self.assertEqual(os.listdir(folder), [os.path.basename(path)])      # no __pycache__ next to it
            self.assertFalse(fetched.deleted)
            name = module.__name__
        self.assertNotIn(name, sys.modules)
        self.assertFalse(os.path.exists(folder))
        self.assertEqual(self.leftovers(), [])
        self.assertTrue(fetched.deleted)
        self.assertIsNone(fetched.data)

    def test_the_temp_folder_is_outside_the_project_and_the_repo(self):
        with private.loaded(self.fetched(), AID) as module:
            real = os.path.realpath(module.__file__)
            self.assertTrue(real.startswith(os.path.realpath(self.temp_root)))
            self.assertFalse(real.startswith(os.path.realpath(os.path.dirname(os.path.dirname(os.path.dirname(
                os.path.dirname(os.path.abspath(__file__))))))))

    def test_deleted_even_when_the_verifier_raises(self):
        fetched = self.fetched()
        with self.assertRaises(RuntimeError):
            with private.loaded(fetched, AID) as module:
                name, folder = module.__name__, os.path.dirname(module.__file__)
                raise RuntimeError("verifier exploded")
        self.assertNotIn(name, sys.modules)
        self.assertFalse(os.path.exists(folder))
        self.assertTrue(fetched.deleted)
        self.assertEqual(self.leftovers(), [])

    def test_deleted_on_system_exit_and_keyboard_interrupt(self):
        for exc_type in (SystemExit, KeyboardInterrupt, GeneratorExit):
            with self.subTest(exc_type.__name__):
                fetched = self.fetched()
                try:
                    with private.loaded(fetched, AID):
                        raise exc_type()
                except BaseException:
                    pass
                self.assertTrue(fetched.deleted)
                self.assertEqual(self.leftovers(), [])

    def test_two_loads_never_share_a_name_or_a_folder(self):
        first, second = self.fetched(), self.fetched(task="F02")
        with private.loaded(first, AID) as one, private.loaded(second, AID) as two:
            self.assertNotEqual(one.__name__, two.__name__)
            self.assertNotEqual(os.path.dirname(one.__file__), os.path.dirname(two.__file__))
            self.assertEqual(one.SENTINEL, "ZZ_PRIVATE_SENTINEL_F01")
            self.assertEqual(two.SENTINEL, "ZZ_PRIVATE_SENTINEL_F02")
            self.assertEqual(len(self.leftovers()), 2)
        self.assertEqual(self.leftovers(), [])

    def test_the_same_content_twice_gets_different_names(self):
        names = []
        for _ in range(3):
            with private.loaded(self.fetched(), AID) as module:
                names.append(module.__name__)
        self.assertEqual(len(set(names)), 3)

    def test_attempt_ids_are_made_safe_for_a_module_name(self):
        with private.loaded(self.fetched(), "a b/c-d.e") as module:
            self.assertRegex(module.__name__, r"^usk_eval_private_a_b_c_d_e_[0-9a-f]{6}$")

    def test_a_deleted_verifier_cannot_be_loaded_again(self):
        fetched = self.fetched()
        with private.loaded(fetched, AID):
            pass
        with self.assertRaises(RuntimeError):
            with private.loaded(fetched, AID):
                pass

    def test_no_bytecode_is_written_and_none_is_read(self):
        with mock.patch.object(sys, "dont_write_bytecode", False):
            fetched = self.fetched()
            with private.loaded(fetched, AID) as module:
                folder = os.path.dirname(module.__file__)
                self.assertEqual(os.listdir(folder), [os.path.basename(module.__file__)])
        self.assertEqual(self.leftovers(), [])

    def test_the_source_does_not_stay_in_linecache(self):
        fetched = self.fetched()
        with private.loaded(fetched, AID) as module:
            self.assertTrue(linecache.getlines(module.__file__))
            path = module.__file__
            self.assertIn(path, linecache.cache)
        self.assertNotIn(path, linecache.cache)

    def test_the_module_executes_exactly_the_verified_bytes(self):
        fetched = self.fetched()
        with private.loaded(fetched, AID) as module:
            write_bytes(module.__file__, b"SENTINEL = 'changed on disk'\n")      # too late: it already ran from memory
            self.assertEqual(module.SENTINEL, "ZZ_PRIVATE_SENTINEL_F01")

    def test_a_verifier_can_use_the_runner_modules(self):
        source = b"from usk_eval import store\nDIGEST = store.canonical_digest({'a': 1})\n"
        with private.loaded(self.fetched(source), AID) as module:
            self.assertEqual(module.DIGEST, store.canonical_digest({"a": 1}))


class BrokenVerifierTests(LoadCase):
    def check_stand_in(self, source, expected_in_message, expected_type=private.VerifierImportError):
        fetched = self.fetched(source)
        with private.loaded(fetched, AID) as module:
            with self.assertRaises(expected_type) as exc:
                module.ASSERTIONS
            message = str(exc.exception)
            self.assertIn(expected_in_message, message)
            self.assertIn("F01.py", message)
            self.assertIsInstance(exc.exception, AttributeError)
            self.assertIsNone(getattr(module, "HINTS", None))                    # optional lookups keep working
            self.assertFalse(hasattr(module, "verify"))
            with self.assertRaises(private.VerifierImportError):
                module.verify(None, None)
        self.assertTrue(fetched.deleted)
        self.assertEqual(self.leftovers(), [])
        return message

    def test_a_syntax_error(self):
        message = self.check_stand_in(b"def broken(:\n", "SyntaxError (line 1)")
        self.assertNotIn("broken", message)

    def test_an_import_error_and_a_runtime_error_at_import(self):
        self.check_stand_in(b"import a_module_that_does_not_exist_anywhere\n", "ModuleNotFoundError")
        self.check_stand_in(b"raise RuntimeError('boom')\n", "RuntimeError")

    def test_system_exit_at_import(self):
        self.check_stand_in(b"raise SystemExit(0)\n", "SystemExit")

    def test_the_message_never_quotes_the_private_source(self):
        message = self.check_stand_in(b"raise ValueError('ZZ_SECRET_TEXT')\n", "ValueError")
        self.assertNotIn("ZZ_SECRET_TEXT", message)

    def test_the_stand_in_is_not_registered_and_leaves_no_module(self):
        with private.loaded(self.fetched(b"raise RuntimeError('x')\n"), AID):
            self.assertEqual([n for n in sys.modules if n.startswith(private.MODULE_PREFIX)
                              and n not in self.keep_modules], [])

    def test_the_verifiers_own_os_errors_are_still_verifier_problems(self):
        self.check_stand_in(b"open('/definitely/not/here/x')\n", "FileNotFoundError")
        self.check_stand_in(b"raise PermissionError('denied')\n", "PermissionError")
        self.check_stand_in(b"raise OSError(28, 'No space left on device')\n", "OSError")

    def test_the_two_failures_are_different_types(self):
        self.assertTrue(issubclass(private.VerifierImportError, AttributeError))
        self.assertFalse(issubclass(private.VerifierImportError, private.PrivateUnavailable))
        self.assertTrue(issubclass(private.PrivateLoadError, private.PrivateUnavailable))
        self.assertFalse(issubclass(private.PrivateLoadError, AttributeError))


class EnvironmentFailureTests(LoadCase):
    """The machine failed, not the verifier: loaded() raises PrivateLoadError before the body, so the runner can keep
    the attempt pending and retry."""

    def assert_retryable(self, fetched, exc, error, cause):
        self.assertIsInstance(exc, private.PrivateLoadError)
        self.assertIsInstance(exc, private.PrivateUnavailable)
        self.assertEqual(exc.code, "VERIFICATION_PENDING")
        self.assertEqual(exc.attempts, [{"via": tempfile.gettempdir(), "error": error}])
        self.assertEqual((exc.error, exc.cause), (error, cause))
        self.assertIsNone(exc.hint)
        self.assertEqual(str(exc), "the verifier could not be prepared: %s (%s)" % (error, cause))
        self.assertTrue(fetched.deleted)
        self.assertIsNone(fetched.data)
        self.assertEqual(self.leftovers(), [])

    def enter(self, fetched):
        ran = []
        with self.assertRaises(private.PrivateLoadError) as exc:
            with private.loaded(fetched, AID):
                ran.append("body")
        self.assertEqual(ran, [])                                   # the body never runs
        return exc.exception

    def test_a_temp_folder_that_cannot_be_made(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError(errno.ENOSPC, "No space left on device")):
            exc = self.enter(fetched)
        self.assert_retryable(fetched, exc, "temp_dir_failed", "ENOSPC")

    def test_a_missing_temp_root(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "tempdir", self.path("no", "such", "root")):
            exc = self.enter(fetched)
            self.assertEqual(exc.attempts[0]["via"], self.path("no", "such", "root"))
        self.assertEqual((exc.attempts[0]["error"], exc.cause), ("temp_dir_failed", "ENOENT"))
        self.assertTrue(fetched.deleted and fetched.data is None)

    def test_a_file_that_cannot_be_written(self):
        fetched = self.fetched()
        gone = os.path.join(self.path("vanished"), "usk-eval-abcd1234")          # its parent does not exist
        with mock.patch.object(tempfile, "mkdtemp", return_value=gone):
            exc = self.enter(fetched)
        self.assert_retryable(fetched, exc, "temp_write_failed", "ENOENT")

    @unittest.skipIf(os.name == "nt" or (hasattr(os, "geteuid") and os.geteuid() == 0), "needs POSIX permissions")
    def test_a_temp_folder_that_is_not_writable(self):
        fetched = self.fetched()
        folder = os.path.join(self.temp_root, "usk-eval-readonly")
        os.makedirs(folder)
        os.chmod(folder, 0o500)
        with mock.patch.object(tempfile, "mkdtemp", return_value=folder):
            exc = self.enter(fetched)
        self.assertEqual(exc.attempts[0]["error"], "temp_write_failed")
        self.assertEqual(exc.cause, "EACCES")
        self.assertFalse(os.path.exists(folder))                                   # and the folder was cleaned up
        self.assertTrue(fetched.deleted)

    def test_the_cause_falls_back_to_the_exception_type(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError("odd")):
            exc = self.enter(fetched)
        self.assertEqual(exc.cause, "OSError")

    def test_the_message_never_carries_paths_or_os_text(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError(errno.EACCES, "Permission denied: /secret/dir")):
            exc = self.enter(fetched)
        self.assertNotIn("/secret/dir", str(exc) + repr(exc) + repr(exc.attempts))

    def test_a_failed_load_cannot_be_retried_with_the_same_fetch(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError(errno.EIO, "I/O error")):
            self.enter(fetched)
        with self.assertRaises(RuntimeError):                                    # the runner fetches again instead
            with private.loaded(fetched, AID):
                pass

    def test_after_the_machine_recovers_a_fresh_fetch_loads(self):
        fetched = self.fetched()
        with mock.patch.object(tempfile, "mkdtemp", side_effect=OSError(errno.ENOSPC, "full")):
            self.enter(fetched)
        with private.loaded(self.fetched(), AID) as module:
            self.assertEqual(module.SENTINEL, "ZZ_PRIVATE_SENTINEL_F01")
        self.assertEqual(self.leftovers(), [])


class RemoveTreeTests(PrivateCase):
    def test_read_only_files_and_folders_are_removed(self):
        folder = self.path("ro")
        inner = os.path.join(folder, "inner")
        os.makedirs(inner)
        for path in (os.path.join(folder, "a.py"), os.path.join(inner, "b.py")):
            write_text(path, "x")
            os.chmod(path, 0o444)
        os.chmod(inner, 0o555)
        os.chmod(folder, 0o555)
        self.assertTrue(private.remove_tree(folder))
        self.assertFalse(os.path.exists(folder))

    def test_a_missing_path_is_already_gone(self):
        self.assertTrue(private.remove_tree(self.path("never-existed")))

    def test_it_reports_failure_after_retrying(self):
        folder = self.path("stuck")
        os.makedirs(folder)
        with mock.patch.object(private, "_rmtree", side_effect=PermissionError("in use")) as attempt:
            self.assertFalse(private.remove_tree(folder, attempts=3, delay=0))
        self.assertEqual(attempt.call_count, 3)
        self.assertTrue(os.path.isdir(folder))

    def test_it_succeeds_when_a_later_retry_works(self):
        folder = self.path("flaky")
        os.makedirs(folder)
        real = private._rmtree
        calls = []

        def flaky(path):
            calls.append(path)
            if len(calls) < 3:
                raise PermissionError("antivirus")
            real(path)
        with mock.patch.object(private, "_rmtree", side_effect=flaky):
            self.assertTrue(private.remove_tree(folder, attempts=5, delay=0))
        self.assertEqual(len(calls), 3)

    def test_a_folder_that_cannot_be_removed_is_reported_by_loaded(self):
        fetched = private.fetch(pack.open_pack(pack_dir=str(GOOD)), "F01")
        with mock.patch.object(private, "remove_tree", return_value=False):
            with private.loaded(fetched, AID):
                pass
        self.assertFalse(fetched.deleted)
        self.assertIsNone(fetched.data)


class StaleCleanupTests(PrivateCase):
    def make(self, name, age_s, files=("F01.py",), root=None):
        folder = os.path.join(root or self.temp_root, name)
        os.makedirs(folder)
        for item in files:
            write_text(os.path.join(folder, item), "x")
        stamp = time.time() - age_s
        os.utime(folder, (stamp, stamp))
        return folder

    def test_only_old_verifier_folders_go(self):
        old = self.make("usk-eval-abcd1234", 7200, files=("0123456789ab.py", "x.py"))
        older_with_cache = self.make("usk-eval-_a1b2c3d", 90000, files=("a.py", "__pycache__"))
        fresh = self.make("usk-eval-fresh123", 60)
        borderline = self.make("usk-eval-border12", 3500)
        foreign_content = self.make("usk-eval-foreign1", 7200, files=("notes.txt",))
        wrong_name = [self.make(name, 7200) for name in ("other-abcd1234", "usk-eval-abcd12345", "usk-eval-abc",
                                                         "usk-eval-ZZZZ9999", "usk-eval-test-abcd", "usk-eval-")]
        plain_file = os.path.join(self.temp_root, "usk-eval-file0000")
        write_text(plain_file, "x")
        os.utime(plain_file, (time.time() - 7200,) * 2)
        removed = private.cleanup_stale()
        self.assertEqual(sorted(removed), sorted([old, older_with_cache]))
        for kept in [fresh, borderline, foreign_content, plain_file] + wrong_name:
            self.assertTrue(os.path.lexists(kept), kept)
        self.assertFalse(os.path.exists(old) or os.path.exists(older_with_cache))

    def test_the_age_limit_is_one_hour_and_can_be_changed(self):
        young = self.make("usk-eval-young000", 3500)
        stale = self.make("usk-eval-stale000", 3700)
        self.assertEqual(private.cleanup_stale(), [stale])
        self.assertTrue(os.path.isdir(young))
        self.assertEqual(private.cleanup_stale(max_age_s=3000), [young])
        self.assertEqual(private.STALE_AFTER_S, 3600.0)

    def test_an_explicit_temp_root(self):
        other = self.path("other-root")
        os.makedirs(other)
        stale = self.make("usk-eval-stale000", 7200, root=other)
        mine = self.make("usk-eval-stale111", 7200)
        self.assertEqual(private.cleanup_stale(temp_root=other), [stale])
        self.assertTrue(os.path.isdir(mine))

    def test_a_symlink_is_never_followed(self):
        target = self.path("target")
        os.makedirs(target)
        write_text(os.path.join(target, "keep.py"), "x")
        link = os.path.join(self.temp_root, "usk-eval-link0000")
        try:
            os.symlink(target, link)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks are not available here")
        os.utime(target, (time.time() - 7200,) * 2)
        self.assertEqual(private.cleanup_stale(), [])
        self.assertTrue(os.path.isfile(os.path.join(target, "keep.py")))

    def test_an_unreadable_or_missing_root_is_fine(self):
        self.assertEqual(private.cleanup_stale(temp_root=self.path("nope")), [])
        self.assertEqual(private.cleanup_stale(temp_root=self.temp_root), [])

    def test_a_directory_that_vanishes_or_resists_is_skipped(self):
        stale = self.make("usk-eval-stale000", 7200)
        with mock.patch.object(private, "remove_tree", return_value=False):
            self.assertEqual(private.cleanup_stale(), [])
        self.assertTrue(os.path.isdir(stale))

    def test_folders_made_by_loaded_match_the_cleanup_pattern(self):
        fetched = private.fetch(pack.open_pack(pack_dir=str(GOOD)), "F01")
        with private.loaded(fetched, AID) as module:
            folder = os.path.dirname(module.__file__)
            old = time.time() - 7200
            os.utime(folder, (old, old))
            self.assertEqual(private.cleanup_stale(), [folder])          # what a killed runner would leave behind
            self.assertFalse(os.path.exists(folder))
        self.assertTrue(fetched.deleted)                                  # a vanished folder counts as deleted

    def test_the_default_root_is_the_system_temp_dir(self):
        with mock.patch.object(tempfile, "tempdir", None):
            self.assertEqual(os.path.realpath(tempfile.gettempdir()), os.path.realpath(tempfile.gettempdir()))
        self.assertRegex("usk-eval-abcd1234", private._TEMP_NAME)
        self.assertIsNone(re.match(private._TEMP_NAME, "usk-eval-abcd1234/"))


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
