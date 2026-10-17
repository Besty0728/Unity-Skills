"""eval/tools/build_private_zip.py: the release asset for --private-zip is deterministic, complete and readable by the
runner (contract 5.9)."""
import hashlib
import io
import os
import shutil
import sys
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
TOOLS = os.path.join(REPO, "eval", "tools")
if TOOLS not in sys.path:
    sys.path.insert(0, TOOLS)

import build_private_zip  # noqa: E402

GOOD = os.path.join(HERE, "fixtures", "pack", "good")


class PrivateZipTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)

    def build(self, *extra, **kw):
        argv = ["--out", kw.get("out") or os.path.join(self.tmp, "private.zip"), "--pack-root", kw.get("pack", GOOD),
                "--private-root", kw.get("private", os.path.join(GOOD, "private"))] + list(extra)
        out = io.StringIO()
        with redirect_stdout(out):
            code = build_private_zip.main(argv)
        return code, out.getvalue()

    def test_zip_holds_exactly_the_manifest_files_and_is_reproducible(self):
        code, text = self.build()
        self.assertEqual(code, 0, text)
        path = os.path.join(self.tmp, "private.zip")
        with zipfile.ZipFile(path) as archive:
            self.assertEqual(archive.namelist(), ["F01.py", "F02.py"])
            self.assertEqual({i.date_time for i in archive.infolist()}, {build_private_zip.ZIP_TIME})
            for name in archive.namelist():
                with open(os.path.join(GOOD, "private", name), "rb") as handle:
                    self.assertEqual(archive.read(name), handle.read())
        with open(path, "rb") as handle:
            first = handle.read()
        self.assertIn(hashlib.sha256(first).hexdigest(), text)
        code, _text = self.build(out=os.path.join(self.tmp, "again.zip"))
        with open(os.path.join(self.tmp, "again.zip"), "rb") as handle:
            self.assertEqual(handle.read(), first)

    def test_a_file_that_differs_from_the_manifest_writes_no_zip(self):
        private = os.path.join(self.tmp, "private")
        shutil.copytree(os.path.join(GOOD, "private"), private)
        with open(os.path.join(private, "F02.py"), "a", encoding="utf-8") as handle:
            handle.write("# edited\n")
        os.remove(os.path.join(private, "F01.py"))
        code, text = self.build(private=private)
        self.assertEqual(code, 1, text)
        self.assertIn("error: F01.py: FileNotFoundError", text)
        self.assertIn("error: F02.py differs from the manifest", text)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "private.zip")))

    def test_an_unreadable_manifest_is_a_usage_error(self):
        code, text = self.build(pack=self.tmp)
        self.assertEqual(code, 2, text)
        self.assertIn("cannot read the manifest", text)


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
