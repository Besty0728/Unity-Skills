using NUnit.Framework;
using UnitySkills;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the pure superset-check that guards <see cref="UISkillsFontAssetBaker.Bake"/>
    /// against silently dropping glyphs. These tests only feed fake character strings
    /// into <see cref="UISkillsFontAssetBaker.FindCharactersDroppedByRebake"/>; they never
    /// load or touch the real FontAsset, so a Bake() regression here can never damage the
    /// shipped atlas.
    /// </summary>
    [TestFixture]
    public class UISkillsFontAssetBakerGuardTests
    {
        [Test]
        public void ExactSameSet_ReportsNothingDropped()
        {
            var dropped = UISkillsFontAssetBaker.FindCharactersDroppedByRebake("abc", "abc");

            Assert.That(dropped, Is.Empty);
        }

        [Test]
        public void NewSetIsSuperset_ReportsNothingDropped()
        {
            // New set adds characters on top of the existing ones -- still safe to rebake.
            var dropped = UISkillsFontAssetBaker.FindCharactersDroppedByRebake("abc", "abcdef");

            Assert.That(dropped, Is.Empty);
        }

        [Test]
        public void NewSetMissingOneCharacter_ReportsThatCharacter()
        {
            var dropped = UISkillsFontAssetBaker.FindCharactersDroppedByRebake("abc", "ab");

            Assert.That(dropped, Is.EqualTo(new[] { 'c' }));
        }

        [Test]
        public void NewSetMissingMultipleCharacters_ReportsAllOfThemSortedAndDeduplicated()
        {
            // "existing" repeats 'z' and 'x' to prove the result is deduplicated, and is
            // given out of order to prove the result is sorted rather than input-ordered.
            var dropped = UISkillsFontAssetBaker.FindCharactersDroppedByRebake("zxzxy", "y");

            Assert.That(dropped, Is.EqualTo(new[] { 'x', 'z' }));
        }

        [Test]
        public void EmptyExistingSet_NeverReportsAnythingDropped()
        {
            var dropped = UISkillsFontAssetBaker.FindCharactersDroppedByRebake("", "anything");

            Assert.That(dropped, Is.Empty);
        }

        [Test]
        public void NullArguments_AreTreatedAsEmptyAndDoNotThrow()
        {
            Assert.That(UISkillsFontAssetBaker.FindCharactersDroppedByRebake(null, "abc"), Is.Empty);
            Assert.That(UISkillsFontAssetBaker.FindCharactersDroppedByRebake("abc", null),
                Is.EqualTo(new[] { 'a', 'b', 'c' }));
            Assert.That(UISkillsFontAssetBaker.FindCharactersDroppedByRebake(null, null), Is.Empty);
        }
    }
}

// Producer:Betsy
