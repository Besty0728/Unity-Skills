using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Regression coverage for issue #60. Resizing an EditorWindow repaints the custom
    /// token-track repeatedly; the maximum-level painter must therefore have a bounded,
    /// resize-specific column budget instead of redrawing the full fixed 48 columns every time
    /// (the six rows stay, so the frozen waveform keeps its shape during a resize).
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
    }
}

// Producer:Betsy
