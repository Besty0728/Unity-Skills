using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor.ShortcutManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 快捷键冲突比对纯逻辑单测（<see cref="ShortcutConflictUtil"/>）。
    ///
    /// 覆盖任务要求的三类判定：相同组合冲突 / 不同修饰键不冲突 / 空绑定不冲突，
    /// 外加 keyCode 不同、null 序列、CombinationsEqual 直测。全部只依赖 KeyCombination
    /// 结构体，无需真实 ShortcutManager，可稳定在 EditMode 下跑。
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
