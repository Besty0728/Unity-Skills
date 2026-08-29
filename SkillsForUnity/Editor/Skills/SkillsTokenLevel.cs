using System;

namespace UnitySkills
{
    /// <summary>
    /// Derived token-saving level. This is a view over the three persisted settings, not another
    /// preference: changing either setting immediately changes the reported level.
    /// </summary>
    public enum TokenLevel
    {
        Minimal,
        Standard,
        Full,
        Maximum,
        Custom,
    }

    /// <summary>
    /// A snapshot of the settings represented by a token level. The struct is deliberately free of
    /// UI or localization dependencies so editor panels and tests can consume it directly.
    /// </summary>
    public readonly struct TokenLevelSettings : IEquatable<TokenLevelSettings>
    {
        public readonly SurfaceProfileKind SurfaceProfile;
        public readonly bool SummaryAutoTruncate;
        public readonly int SummaryPageSize;

        public TokenLevelSettings(
            SurfaceProfileKind surfaceProfile,
            bool summaryAutoTruncate,
            int summaryPageSize)
        {
            SurfaceProfile = surfaceProfile;
            SummaryAutoTruncate = summaryAutoTruncate;
            SummaryPageSize = summaryPageSize > 0
                ? summaryPageSize
                : SkillRouter.DefaultSummaryPageSize;
        }

        public bool Equals(TokenLevelSettings other) =>
            SurfaceProfile == other.SurfaceProfile &&
            SummaryAutoTruncate == other.SummaryAutoTruncate &&
            SummaryPageSize == other.SummaryPageSize;

        public override bool Equals(object obj) =>
            obj is TokenLevelSettings other && Equals(other);

        public override int GetHashCode() =>
            ((int)SurfaceProfile * 397) ^
            (SummaryAutoTruncate ? 1 : 0) ^
            SummaryPageSize;

        public static bool operator ==(TokenLevelSettings left, TokenLevelSettings right) => left.Equals(right);
        public static bool operator !=(TokenLevelSettings left, TokenLevelSettings right) => !left.Equals(right);
    }

    /// <summary>
    /// Derives and applies the four standard token presets. The level is computed from
    /// (SurfaceProfile, SummaryAutoTruncate, SummaryPageSize); no level value is persisted.
    /// </summary>
    public static class SkillsTokenLevel
    {
        public const int MinimalPageSize = 5;
        public const int StandardPageSize = 10;
        public const int FullPageSize = 20;
        public const int DefaultSummaryPageSize = 10;

        /// <summary>Raised after a source setting changes through either backing service.</summary>
        public static event Action OnChanged;

        static SkillsTokenLevel()
        {
            SkillsSurfaceProfile.OnChanged += RaiseChanged;
            SkillRouter.SummarySettingsChanged += RaiseChanged;
        }

        /// <summary>The level derived from the currently persisted settings.</summary>
        public static TokenLevel Current => Resolve(
            SkillsSurfaceProfile.Current,
            SkillRouter.SummaryAutoTruncate,
            SkillRouter.SummaryPageSize);

        /// <summary>Current source settings as one immutable snapshot.</summary>
        public static TokenLevelSettings CurrentSettings => new TokenLevelSettings(
            SkillsSurfaceProfile.Current,
            SkillRouter.SummaryAutoTruncate,
            SkillRouter.SummaryPageSize);

        /// <summary>Convenience aliases for panels that bind the individual source fields.</summary>
        public static bool SummaryAutoTruncate
        {
            get => SkillRouter.SummaryAutoTruncate;
            set => SkillRouter.SummaryAutoTruncate = value;
        }

        public static int SummaryPageSize
        {
            get => SkillRouter.SummaryPageSize;
            set => SkillRouter.SummaryPageSize = value;
        }

        public static SurfaceProfileKind SurfaceProfile
        {
            get => SkillsSurfaceProfile.Current;
            set => SkillsSurfaceProfile.Current = value;
        }

        /// <summary>
        /// Resolves a level from an arbitrary setting tuple. Maximum deliberately ignores page
        /// size because pagination is inactive while truncation is off.
        /// </summary>
        public static TokenLevel Resolve(
            SurfaceProfileKind surfaceProfile,
            bool summaryAutoTruncate,
            int summaryPageSize)
        {
            if (summaryPageSize <= 0)
                summaryPageSize = DefaultSummaryPageSize;

            if (!summaryAutoTruncate && surfaceProfile == SurfaceProfileKind.Full)
                return TokenLevel.Maximum;

            if (summaryAutoTruncate)
            {
                if (surfaceProfile == SurfaceProfileKind.NoSceneAuthoring && summaryPageSize == MinimalPageSize)
                    return TokenLevel.Minimal;
                if (surfaceProfile == SurfaceProfileKind.Guide && summaryPageSize == StandardPageSize)
                    return TokenLevel.Standard;
                if (surfaceProfile == SurfaceProfileKind.Full && summaryPageSize == FullPageSize)
                    return TokenLevel.Full;
            }
            return TokenLevel.Custom;
        }

        /// <summary>Gets the canonical source tuple for a standard level.</summary>
        public static bool TryGetPreset(TokenLevel level, out TokenLevelSettings settings)
        {
            switch (level)
            {
                case TokenLevel.Minimal:
                    settings = new TokenLevelSettings(SurfaceProfileKind.NoSceneAuthoring, true, MinimalPageSize);
                    return true;
                case TokenLevel.Standard:
                    settings = new TokenLevelSettings(SurfaceProfileKind.Guide, true, StandardPageSize);
                    return true;
                case TokenLevel.Full:
                    settings = new TokenLevelSettings(SurfaceProfileKind.Full, true, FullPageSize);
                    return true;
                case TokenLevel.Maximum:
                    settings = new TokenLevelSettings(SurfaceProfileKind.Full, false, DefaultSummaryPageSize);
                    return true;
                default:
                    settings = default(TokenLevelSettings);
                    return false;
            }
        }

        /// <summary>
        /// Applies one of the four canonical tuples. Custom is a read-only derived state and is a
        /// no-op here; callers can edit the individual aliases to intentionally create it.
        /// </summary>
        public static bool TryApplyPreset(TokenLevel level)
        {
            if (!TryGetPreset(level, out var settings)) return false;

            // Assign all three source values, including page size for Maximum, so selecting a
            // preset always leaves a deterministic tuple for the next level calculation.
            SkillsSurfaceProfile.Current = settings.SurfaceProfile;
            SkillRouter.SummaryAutoTruncate = settings.SummaryAutoTruncate;
            SkillRouter.SummaryPageSize = settings.SummaryPageSize;
            return true;
        }

        /// <summary>Applies an arbitrary tuple, useful for the advanced/custom controls.</summary>
        public static void ApplySettings(TokenLevelSettings settings)
        {
            SkillsSurfaceProfile.Current = settings.SurfaceProfile;
            SkillRouter.SummaryAutoTruncate = settings.SummaryAutoTruncate;
            SkillRouter.SummaryPageSize = settings.SummaryPageSize;
        }

        private static void RaiseChanged()
        {
            var handlers = OnChanged;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)?.Invoke(); }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning(
                        $"SkillsTokenLevel OnChanged handler '{handler.Method?.DeclaringType?.Name}.{handler.Method?.Name}' threw: {ex.Message}");
                }
            }
        }
    }
}

// Producer:Betsy
