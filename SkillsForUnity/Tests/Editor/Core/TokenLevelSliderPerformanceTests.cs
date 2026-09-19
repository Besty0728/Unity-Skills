using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Regression coverage for issue #60. Resizing an EditorWindow invalidates the custom
    /// token-track mesh repeatedly; the maximum-level painter must therefore have a bounded,
    /// resize-specific geometry budget instead of rebuilding the old fixed 48x6 grid.
    /// </summary>
    [TestFixture]
    public class TokenLevelSliderPerformanceTests
    {
        [Test]
        public void MaximumTrackHorizontalSlices_ScaleWithWidthAndStayBounded()
        {
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackHorizontalSliceCount(0f), Is.EqualTo(0));
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackHorizontalSliceCount(1f), Is.EqualTo(4));
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackHorizontalSliceCount(120f), Is.EqualTo(10));
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackHorizontalSliceCount(10_000f), Is.EqualTo(24));
        }

        [Test]
        public void MaximumTrackVerticalSlices_UseSingleRowWhileResizing()
        {
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackVerticalSliceCount(true), Is.EqualTo(1));
            Assert.That(TokenLevelSliderWidget.GetMaximumTrackVerticalSliceCount(false), Is.EqualTo(6));
        }
    }
}

// Producer:Betsy
