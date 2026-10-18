using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The write primitives behind batch_state.json and the console history: a rewrite goes through a temp file and one
    /// rename, so an interrupted write leaves the previous document whole; an append after a torn tail starts a new line.
    /// </summary>
    [TestFixture]
    public class LibraryStateFileTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UnitySkillsStateFile_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "state.json");
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        [Test]
        public void Write_InterruptedMidDocument_LeavesThePreviousDocumentWhole()
        {
            LibraryStateFile.WriteAllText(_path, "{\"generation\":1}");

            Assert.Throws<InvalidOperationException>(() => LibraryStateFile.Write(_path, stream =>
            {
                var head = Encoding.UTF8.GetBytes("{\"generation\":2,\"jobs\":[");
                stream.Write(head, 0, head.Length);
                throw new InvalidOperationException("interrupted mid-write");
            }));

            Assert.AreEqual("{\"generation\":1}", File.ReadAllText(_path));
            Assert.IsFalse(File.Exists(_path + LibraryStateFile.TempSuffix), "A failed write removes its temp file.");
        }

        [Test]
        public void Write_ALeftoverTempFromAKill_DoesNotDisturbTheNextWrite()
        {
            LibraryStateFile.WriteAllText(_path, "{\"generation\":1}");
            File.WriteAllText(_path + LibraryStateFile.TempSuffix, "{\"generation\":2,\"jo");

            LibraryStateFile.WriteAllText(_path, "{\"generation\":3}");

            Assert.AreEqual("{\"generation\":3}", File.ReadAllText(_path));
            Assert.IsFalse(File.Exists(_path + LibraryStateFile.TempSuffix));
        }

        [Test]
        public void Write_WithBackup_KeepsTheReplacedGeneration()
        {
            string backup = _path + ".bak";
            LibraryStateFile.WriteAllText(_path, "one", backup);
            Assert.IsFalse(File.Exists(backup), "The first write has no generation to keep.");

            LibraryStateFile.WriteAllText(_path, "two", backup);
            LibraryStateFile.WriteAllText(_path, "three", backup);

            Assert.AreEqual("three", File.ReadAllText(_path));
            Assert.AreEqual("two", File.ReadAllText(backup));
        }

        [Test]
        public void Write_KeepsAReaderOfThePreviousDocumentConsistent()
        {
            LibraryStateFile.WriteAllText(_path, "previous-document");
            using (var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                LibraryStateFile.WriteAllText(_path, "next-document");
                using (var text = new StreamReader(reader))
                    Assert.That(text.ReadToEnd(), Is.EqualTo("previous-document").Or.EqualTo("next-document"),
                        "A reader sees one whole generation, never a prefix of the next one.");
            }
            Assert.AreEqual("next-document", File.ReadAllText(_path));
        }

        [Test]
        public void AppendLines_AfterATornTail_StartsANewLine()
        {
            File.WriteAllText(_path, "{\"a\":1}\n{\"torn");

            LibraryStateFile.AppendLines(_path, Encoding.UTF8.GetBytes("{\"b\":2}\n"));
            LibraryStateFile.AppendLines(_path, Encoding.UTF8.GetBytes("{\"c\":3}\n"));

            CollectionAssert.AreEqual(new[] { "{\"a\":1}", "{\"torn", "{\"b\":2}", "{\"c\":3}" }, File.ReadAllLines(_path));
        }
    }
}

// Producer:Betsy
