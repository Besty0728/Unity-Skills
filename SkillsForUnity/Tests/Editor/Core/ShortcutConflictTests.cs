using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor.ShortcutManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Pure-logic unit tests for shortcut conflict comparison (<see cref="ShortcutConflictUtil"/>).
    ///
    /// Covers the three judgment categories the task calls for: identical combination conflicts / different
    /// modifiers don't conflict / empty bindings don't conflict, plus a differing keyCode, a null sequence, and
    /// a direct test of CombinationsEqual. All of it only depends on the KeyCombination struct, with no need
    /// for a real ShortcutManager, so it runs reliably under EditMode.
    /// </summary>
    [TestFixture]
    public class ShortcutConflictTests
    {
        private static KeyCombination Combo(KeyCode k, ShortcutModifiers m) => new KeyCombination(k, m);

        [TestCase(ShortcutActions.OpenMainPanelId, "OpenMainPanel")]
        [TestCase(ShortcutActions.OpenAuditLogId, "OpenAuditLog")]
        public void PanelShortcut_IsRegisteredWithoutDefaultBinding(string expectedId, string methodName)
        {
            var method = typeof(ShortcutActions).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            var attribute = method?.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(ShortcutAttribute))
                .SingleOrDefault();

            Assert.That(method, Is.Not.Null);
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.ConstructorArguments[0].Value, Is.EqualTo(expectedId));
            Assert.That(attribute.ConstructorArguments
                .Where(argument => argument.ArgumentType == typeof(KeyCode))
                .All(argument => (KeyCode)argument.Value == KeyCode.None), Is.True);
        }

        [Test]
        public void SameCombination_Conflicts()
        {
            var a = new[] { Combo(KeyCode.M, ShortcutModifiers.Alt) };
            var b = new[] { Combo(KeyCode.M, ShortcutModifiers.Alt) };
            Assert.IsTrue(ShortcutConflictUtil.SequencesConflict(a, b));
        }

        [Test]
        public void DifferentModifiers_DoNotConflict()
        {
            var a = new[] { Combo(KeyCode.M, ShortcutModifiers.Alt) };
            var b = new[] { Combo(KeyCode.M, ShortcutModifiers.Shift) };
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(a, b));
        }

        [Test]
        public void DifferentKeyCode_DoNotConflict()
        {
            var a = new[] { Combo(KeyCode.M, ShortcutModifiers.Action) };
            var b = new[] { Combo(KeyCode.N, ShortcutModifiers.Action) };
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(a, b));
        }

        [Test]
        public void EmptyBinding_NeverConflicts()
        {
            var empty = new KeyCombination[0];
            var some  = new[] { Combo(KeyCode.M, ShortcutModifiers.Alt) };
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(empty, some));
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(some, empty));
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(empty, empty));
        }

        [Test]
        public void NullSequence_NeverConflicts()
        {
            var some = new[] { Combo(KeyCode.M, ShortcutModifiers.Alt) };
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(null, some));
            Assert.IsFalse(ShortcutConflictUtil.SequencesConflict(some, null));
        }

        [Test]
        public void CombinationsEqual_MatchesKeyCodeAndModifiers()
        {
            Assert.IsTrue(ShortcutConflictUtil.CombinationsEqual(
                Combo(KeyCode.K, ShortcutModifiers.Action | ShortcutModifiers.Shift),
                Combo(KeyCode.K, ShortcutModifiers.Action | ShortcutModifiers.Shift)));

            Assert.IsFalse(ShortcutConflictUtil.CombinationsEqual(
                Combo(KeyCode.K, ShortcutModifiers.Action),
                Combo(KeyCode.K, ShortcutModifiers.Action | ShortcutModifiers.Shift)));
        }
    }
}

// Producer:Betsy
