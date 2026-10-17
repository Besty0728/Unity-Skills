"""usk_eval.verifylib.uyaml: the Unity YAML / .meta reader (ported unchanged from v1; its tests plus edge cases)."""
import os
import tempfile
import unittest

try:
    from . import test_verifylib_support as support  # noqa: F401  (puts the packaged runner on sys.path)
except ImportError:
    import test_verifylib_support as support  # noqa: F401

from usk_eval.verifylib import uyaml as Y

TEXT = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  m_Name: Glow
  m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}
  m_ValidKeywords:
  - _EMISSION
  m_SavedProperties:
    serializedVersion: 3
    m_Colors:
    - _BaseColor: {r: 0.8, g: 0.1, b: 0.1, a: 1}
    - _EmissionColor: {r: 2, g: 1, b: 0, a: 1}
--- !u!1001 &55
PrefabInstance:
  m_Modification:
    m_Modifications:
    - target: {fileID: 1, guid: abc, type: 3}
      propertyPath: m_Mass
      value: 50
      objectReference: {fileID: 0}
    m_RemovedComponents: []
  m_SourcePrefab: {fileID: 100100000, guid: abc, type: 3}
"""


class UYamlTests(unittest.TestCase):
    def test_wrapped_flow_mapping(self):
        text = ("--- !u!114 &11400000\nMonoBehaviour:\n"
                "  projectilePrefab: {fileID: 3707123456789012345, guid: abcdef,\n    type: 3}\n  fireRate: 2.5\n")
        doc = Y.loads(text)[0]
        self.assertEqual(doc.get("projectilePrefab", "guid"), "abcdef")
        self.assertEqual(doc.get("projectilePrefab", "type"), "3")
        self.assertEqual(doc.get("fireRate"), "2.5")

    def test_material_and_prefab_instance(self):
        docs = Y.loads(TEXT)
        self.assertEqual([d.type_name for d in docs], ["Material", "PrefabInstance"])
        mat = docs[0]
        self.assertEqual(mat.get("m_Shader", "guid"), "933532a4fcc9baf4fa0491de14d08ed7")
        self.assertEqual(mat.get("m_ValidKeywords"), ["_EMISSION"])
        self.assertEqual(Y.material_colors(mat)["_EmissionColor"], (2.0, 1.0, 0.0, 1.0))
        mods = docs[1].get("m_Modification", "m_Modifications")
        self.assertEqual(mods[0]["propertyPath"], "m_Mass")
        self.assertEqual(mods[0]["value"], "50")
        self.assertEqual(mods[0]["target"]["guid"], "abc")
        self.assertEqual(docs[1].get("m_Modification", "m_RemovedComponents"), [])
        self.assertEqual(docs[1].get("m_SourcePrefab", "guid"), "abc")

    def test_document_headers(self):
        docs = Y.loads(TEXT)
        self.assertEqual([(d.class_id, d.file_id, d.stripped) for d in docs], [(21, 2100000, False), (1001, 55, False)])
        self.assertEqual(repr(docs[0]), "Doc(21 &2100000 Material)")
        stripped = Y.loads("--- !u!4 &9 stripped\nTransform:\n  m_Father: {fileID: 0}\n")[0]
        self.assertTrue(stripped.stripped)

    def test_lookup_helpers(self):
        docs = Y.loads(TEXT)
        self.assertEqual([d.file_id for d in Y.by_type(docs, "Material")], [2100000])
        self.assertEqual(Y.by_type(docs, "Nothing"), [])
        self.assertIs(Y.index(docs)[55], docs[1])

    def test_doc_get_walks_dicts_and_lists_and_defaults(self):
        doc = Y.loads(TEXT)[0]
        self.assertEqual(doc.get("m_ValidKeywords", 0), "_EMISSION")
        self.assertEqual(doc.get("m_ValidKeywords", -1), "_EMISSION")
        self.assertIsNone(doc.get("m_ValidKeywords", 5))
        self.assertEqual(doc.get("m_Missing", "x", default="d"), "d")
        self.assertEqual(doc.get("m_Name"), "Glow")

    def test_scalars(self):
        self.assertEqual(Y.scalar("{fileID: 0}"), {"fileID": "0"})
        self.assertEqual(Y.scalar("[]"), [])
        self.assertEqual(Y.scalar("{}"), {})
        self.assertEqual(Y.scalar("'quoted'"), "quoted")
        self.assertEqual(Y.scalar(' "double" '), "double")
        self.assertEqual(Y.scalar("[a, b]"), ["a", "b"])
        self.assertEqual(Y.scalar("plain text"), "plain text")

    def test_empty_document_and_empty_values(self):
        docs = Y.loads("--- !u!1 &1\n")
        self.assertEqual((docs[0].type_name, docs[0].body), (None, {}))
        doc = Y.loads("--- !u!114 &2\nMonoBehaviour:\n  m_Name:\n  m_Script: {fileID: 0}\n")[0]
        self.assertIsNone(doc.get("m_Name"))

    def test_load_reads_utf8_files_with_crlf(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "Glow.mat")
            with open(path, "wb") as handle:
                handle.write(TEXT.replace("\n", "\r\n").encode("utf-8"))
            docs = Y.load(path)
            self.assertEqual(docs[0].get("m_Name"), "Glow")
            self.assertEqual(docs[0].get("m_Shader", "guid"), "933532a4fcc9baf4fa0491de14d08ed7")

    def test_meta_guid(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "Glow.mat")
            with open(path + ".meta", "w", encoding="utf-8") as handle:
                handle.write("fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nNativeFormatImporter:\n")
            self.assertEqual(Y.meta_guid(path), "0123456789abcdef0123456789abcdef")
            with open(path + ".meta", "w", encoding="utf-8") as handle:
                handle.write("fileFormatVersion: 2\n")
            self.assertIsNone(Y.meta_guid(path))
            with self.assertRaises(IOError):
                Y.meta_guid(os.path.join(tmp, "Missing.mat"))

    def test_material_colors_of_a_document_without_colors(self):
        self.assertEqual(Y.material_colors(Y.loads(TEXT)[1]), {})


if __name__ == "__main__":
    unittest.main()

# Producer:Betsy
