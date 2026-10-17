"""private.fetch from a zip, a private folder and a development pack folder; probes of each (contract 5.9)."""
import os
import shutil
import unittest
import zipfile

try:
    from .test_pack_support import (GOOD, PrivateCase, edit_json, make_zip, pack, private, problems, read_bytes, store,
                                    write_bytes, write_text)
except ImportError:
    from test_pack_support import (GOOD, PrivateCase, edit_json, make_zip, pack, private, problems, read_bytes, store,
                                   write_bytes, write_text)

SENTINEL = b"ZZ_PRIVATE_SENTINEL_F01"


class SourceCase(PrivateCase):
    def setUp(self):
        PrivateCase.setUp(self)
        self.pack_root = self.copy_pack()
        self.pack = pack.open_pack(base_dir=self.pack_root)               # verifiers by URL unless a source is given
        self.good = read_bytes(GOOD / "private" / "F01.py")
        self.entry = self.pack.private_entry("F01")

    def unavailable(self, **kwargs):
        with self.assertRaises(private.PrivateUnavailable) as exc:
            private.fetch(self.pack, "F01", **kwargs)
        return exc.exception.attempts

    def zip_with(self, entries, name="verifiers.zip", **kwargs):
        return make_zip(self.path(name), entries, **kwargs)


class ZipTests(SourceCase):
    def test_the_entry_at_the_zip_root_named_like_the_manifest_file(self):
        archive = self.zip_with({"F01.py": self.good, "F02.py": b"other", "README.txt": b"x"})
        fetched = private.fetch(self.pack, "F01", private_zip=archive)
        self.assertEqual((fetched.kind, fetched.file, fetched.via, fetched.official, fetched.attempts),
                         ("zip", "F01.py", archive, True, []))
        self.assertEqual((fetched.sha256, fetched.data), (self.entry["sha256"], self.good))
        self.assertEqual(self.leftovers(), [])

    def test_a_compressed_entry_is_fine(self):
        archive = self.zip_with({"F01.py": self.good}, compression=zipfile.ZIP_DEFLATED)
        self.assertEqual(private.fetch(self.pack, "F01", private_zip=archive).data, self.good)

    def test_a_wrong_hash_counts_as_not_available(self):
        archive = self.zip_with({"F01.py": self.good + b"# edited\n"})
        self.assertEqual(self.unavailable(private_zip=archive), [{"via": archive, "error": "hash_mismatch"}])

    def test_only_the_zip_root_counts(self):
        for name in ("sub/F01.py", "sub\\F01.py", "./F01.py", "f01.py", "F01.py/"):
            with self.subTest(name):
                archive = self.zip_with({name: self.good}, name="z-%d.zip" % abs(hash(name)))
                self.assertEqual(self.unavailable(private_zip=archive), [{"via": archive, "error": "entry_missing"}])

    def test_unusable_zips(self):
        text = self.path("not-a-zip.zip")
        write_text(text, "plain text")
        cases = ((text, "bad_zip"), (self.path("absent.zip"), "zip_not_found"), (self.root, "zip_not_found"))
        for path, expected in cases:
            with self.subTest(expected + os.path.basename(path)):
                self.assertEqual(self.unavailable(private_zip=path), [{"via": os.path.abspath(path), "error": expected}])

    def test_an_oversized_entry_is_refused_without_reading_it(self):
        archive = self.zip_with({"F01.py": b"\0" * (6 * 1024 * 1024)}, compression=zipfile.ZIP_DEFLATED)
        self.assertEqual(self.unavailable(private_zip=archive), [{"via": archive, "error": "too_large"}])

    def test_a_corrupt_entry(self):
        archive = self.zip_with({"F01.py": self.good})
        raw = bytearray(read_bytes(archive))
        raw[raw.find(SENTINEL)] ^= 0x01                                     # CRC no longer matches
        write_bytes(archive, bytes(raw))
        self.assertEqual(self.unavailable(private_zip=archive), [{"via": archive, "error": "bad_zip"}])

    def test_other_tasks_read_their_own_entry(self):
        archive = self.zip_with({"F01.py": b"x", "F02.py": read_bytes(GOOD / "private" / "F02.py")})
        self.assertEqual(private.fetch(self.pack, "F02", private_zip=archive).file, "F02.py")

    def test_paths_with_spaces_and_non_ascii_letters(self):
        archive = make_zip(self.path("dir \u00e9", "pack \u4e2d.zip"), {"F01.py": self.good})
        self.assertEqual(private.fetch(self.pack, "F01", private_zip=archive).via, archive)

    def test_a_pack_opened_with_a_zip_uses_it_by_default(self):
        archive = self.zip_with({"F01.py": self.good})
        opened = pack.open_pack(private_zip=archive, base_dir=self.pack_root)
        fetched = private.fetch(opened, "F01")
        self.assertEqual((fetched.kind, fetched.via), ("zip", archive))
        probed = private.probe(opened, "F01")
        self.assertEqual(probed, {"kind": "zip", "probedTask": "F01", "reachable": True, "via": archive, "attempts": []})

    def test_a_zip_override_beats_the_source_the_pack_was_opened_with(self):
        opened = pack.open_pack(pack_dir=self.pack_root)
        archive = self.zip_with({"F01.py": self.good})
        self.assertEqual(private.fetch(opened, "F01", private_zip=archive).kind, "zip")

    def test_probe_of_a_zip_without_the_entry(self):
        archive = self.zip_with({"other.py": b"x"})
        self.assertEqual(private.probe(self.pack, "F01", private_zip=archive),
                         {"kind": "zip", "probedTask": "F01", "reachable": False, "via": None,
                          "attempts": [{"via": archive, "error": "entry_missing"}]})


class PrivateDirTests(SourceCase):
    def folder(self, **edits):
        target = self.path("verifiers dir \u00e9")
        shutil.copytree(os.path.join(self.pack_root, "private"), target)
        for name, data in edits.items():
            write_bytes(os.path.join(target, name), data)
        return target

    def test_a_matching_file_is_official(self):
        folder = self.folder()
        fetched = private.fetch(self.pack, "F01", private_dir=folder)
        self.assertEqual((fetched.kind, fetched.via, fetched.official, fetched.attempts),
                         ("private-dir", os.path.join(folder, "F01.py"), True, []))
        self.assertEqual((fetched.sha256, fetched.data), (self.entry["sha256"], self.good))

    def test_a_differing_file_is_used_but_not_official(self):
        edited = self.good + b"# edited\n"
        folder = self.folder(**{"F01.py": edited})
        fetched = private.fetch(self.pack, "F01", private_dir=folder)
        self.assertFalse(fetched.official)
        self.assertEqual(fetched.sha256, store.sha256_bytes(edited))
        self.assertNotEqual(fetched.sha256, self.entry["sha256"])
        self.assertEqual(fetched.data, edited)
        self.assertEqual(fetched.attempts, [{"via": os.path.join(folder, "F01.py"), "error": "hash_mismatch_allowed"}])

    def test_a_missing_or_unusable_file(self):
        folder = self.folder()
        os.remove(os.path.join(folder, "F01.py"))
        self.assertEqual(self.unavailable(private_dir=folder), [{"via": os.path.join(folder, "F01.py"),
                                                                  "error": "not_found"}])
        os.makedirs(os.path.join(folder, "F01.py"))
        self.assertEqual(self.unavailable(private_dir=folder)[0]["error"], "not_found")
        write_bytes(os.path.join(self.path("big"), "F01.py"), b"x" * (private.MAX_FILE_BYTES + 1))
        self.assertEqual(self.unavailable(private_dir=self.path("big"))[0]["error"], "too_large")

    def test_a_pack_opened_with_a_private_dir_uses_it_by_default(self):
        folder = self.folder()
        opened = pack.open_pack(private_dir=folder, base_dir=self.pack_root)
        fetched = private.fetch(opened, "F01")
        self.assertEqual((fetched.kind, fetched.official), ("private-dir", True))
        self.assertEqual(private.probe(opened, "F01")["via"], os.path.join(folder, "F01.py"))

    def test_a_relative_path_is_resolved(self):
        folder = self.folder()
        try:
            relative = os.path.relpath(folder)
        except ValueError:
            self.skipTest("no relative path on this drive")
        fetched = private.fetch(self.pack, "F01", private_dir=relative)
        self.assertEqual(os.path.realpath(fetched.via), os.path.realpath(os.path.join(folder, "F01.py")))
        self.assertTrue(os.path.isabs(fetched.via))

    def test_probe_counts_a_differing_file_as_reachable(self):
        folder = self.folder(**{"F01.py": b"x = 1\n"})
        probed = private.probe(self.pack, "F01", private_dir=folder)
        self.assertTrue(probed["reachable"])
        self.assertEqual((probed["kind"], probed["via"]), ("private-dir", os.path.join(folder, "F01.py")))

    def test_probe_of_a_missing_file(self):
        folder = self.folder()
        os.remove(os.path.join(folder, "F02.py"))
        probed = private.probe(self.pack, "F02", private_dir=folder)
        self.assertEqual((probed["reachable"], probed["via"], probed["kind"]), (False, None, "private-dir"))


class PackDirTests(SourceCase):
    def test_the_file_comes_from_the_private_folder_of_the_pack_and_is_never_official(self):
        opened = pack.open_pack(pack_dir=self.pack_root)
        fetched = private.fetch(opened, "F01")
        self.assertEqual((fetched.kind, fetched.via, fetched.official, fetched.attempts),
                         ("pack-dir", os.path.join(self.pack_root, "private", "F01.py"), False, []))
        self.assertEqual((fetched.sha256, fetched.data), (self.entry["sha256"], self.good))

    def test_a_differing_file_is_tolerated(self):
        edited = b"ASSERTIONS = []\n"
        write_bytes(os.path.join(self.pack_root, "private", "F01.py"), edited)
        opened = pack.open_pack(pack_dir=self.pack_root)
        fetched = private.fetch(opened, "F01")
        self.assertEqual((fetched.official, fetched.sha256, fetched.data), (False, store.sha256_bytes(edited), edited))
        self.assertEqual(fetched.attempts[0]["error"], "hash_mismatch_allowed")

    def test_a_missing_file(self):
        os.remove(os.path.join(self.pack_root, "private", "F02.py"))
        opened = pack.open_pack(pack_dir=self.pack_root)
        with self.assertRaises(private.PrivateUnavailable) as exc:
            private.fetch(opened, "F02")
        self.assertEqual(exc.exception.attempts[0]["error"], "not_found")

    def test_a_private_dir_override_replaces_the_pack_folder(self):
        opened = pack.open_pack(pack_dir=self.pack_root)
        other = self.path("other")
        os.makedirs(other)
        write_bytes(os.path.join(other, "F01.py"), self.good)
        fetched = private.fetch(opened, "F01", private_dir=other)
        self.assertEqual((fetched.kind, fetched.official), ("private-dir", True))

    def test_probe_of_a_pack_dir(self):
        opened = pack.open_pack(pack_dir=self.pack_root)
        self.assertEqual(private.probe(opened, "F01"),
                         {"kind": "pack-dir", "probedTask": "F01", "reachable": True,
                          "via": os.path.join(self.pack_root, "private", "F01.py"), "attempts": []})


class GuardTests(SourceCase):
    def test_an_unknown_source_kind_is_a_programming_error(self):
        self.pack.private_kind = "carrier-pigeon"
        with self.assertRaises(ValueError):
            private.fetch(self.pack, "F01")

    def test_sources_without_a_path(self):
        for kind in ("zip", "private-dir", "pack-dir"):
            with self.subTest(kind):
                self.pack.private_kind, self.pack.private_path = kind, None
                self.assertEqual(self.unavailable(), [{"via": None, "error": "no_source_path"}])

    def test_nothing_is_written_by_any_source(self):
        archive = self.zip_with({"F01.py": self.good})
        private.fetch(self.pack, "F01", private_zip=archive)
        private.fetch(self.pack, "F01", private_dir=os.path.join(self.pack_root, "private"))
        private.fetch(pack.open_pack(pack_dir=self.pack_root), "F01")
        self.assertEqual(self.leftovers(), [])

    def test_kinds_and_codes_match_the_contract_vocabulary(self):
        kinds = {private.fetch(self.pack, "F01", private_zip=self.zip_with({"F01.py": self.good})).kind,
                 private.fetch(self.pack, "F01", private_dir=os.path.join(self.pack_root, "private")).kind,
                 private.fetch(pack.open_pack(pack_dir=self.pack_root), "F01").kind}
        self.assertEqual(kinds, {"zip", "private-dir", "pack-dir"})
        self.assertIn(private.PrivateUnavailable.code, problems.BLOCKING)

    def zip_with(self, entries, name="verifiers.zip", **kwargs):
        return make_zip(self.path(name), entries, **kwargs)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
