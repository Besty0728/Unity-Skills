using System;

namespace UnitySkills
{
    /// <summary>
    /// Backward-compatible view of the boolean guide switch that <see cref="SkillsSurfaceProfile"/> replaced in 2.7.
    ///
    /// Deliberately zero callers within the package: this is a public API already shipped in 2.6.x,
    /// and a user's own editor scripts may still read it, so removing it would only break their build for nothing. Renamed public members conventionally keep an <see cref="ObsoleteAttribute"/> forwarder
    /// until the next major version (see the AllowlistSkills forwarder in <see cref="SkillsModeManager"/>). This class represents the same concept as the deprecated <c>guideMode</c> alias on <c>/health</c>,
    /// so the two must be retired together and never drift out of sync.
    /// </summary>
    [Obsolete("Use SkillsSurfaceProfile. v2.7 replaced the boolean guide switch with the three-way surfaceProfile; a bool cannot express noSceneAuthoring, so this shim only ever reports the guide profile.")]
    public static class SkillsGuideMode
    {
        /// <summary>Forwards to <see cref="SkillsSurfaceProfile.OnChanged"/>.</summary>
        public static event Action OnChanged
        {
            add { SkillsSurfaceProfile.OnChanged += value; }
            remove { SkillsSurfaceProfile.OnChanged -= value; }
        }

        /// <summary>
        /// True when the current tier is <c>guide</c>. Setting true selects the guide tier; setting false only clears guide without touching <c>noSceneAuthoring</c> -- a bool cannot express that state, and
        /// silently downgrading to <c>full</c> would widen an exposure surface the user deliberately narrowed.
        /// </summary>
        public static bool Enabled
        {
            get => SkillsSurfaceProfile.Current == SurfaceProfileKind.Guide;
            set
            {
                if (value)
                    SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
                else if (SkillsSurfaceProfile.Current == SurfaceProfileKind.Guide)
                    SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            }
        }
    }
}

// Producer:Betsy
