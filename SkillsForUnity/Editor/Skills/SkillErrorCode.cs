using System;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Stable, AI-parseable error codes for REST responses.
    /// Wire format is SCREAMING_SNAKE_CASE (see <see cref="SkillErrorCodeExtensions.ToWireString"/>).
    /// Add new values to the END to keep numeric ordering stable.
    /// </summary>
    public enum SkillErrorCode
    {
        Unknown = 0,
        SkillNotFound,
        MissingParam,
        UnknownParam,
        TypeMismatch,
        SemanticInvalid,
        InvalidJson,
        InvalidSkillName,
        TargetNotFound,
        MissingPackage,
        Compiling,
        ConfirmationRequired,
        InvalidToken,
        RateLimit,
        ServerStopped,
        SkillError,
        BodyTooLarge,
        QueueFull,
        Timeout,
        NotFound,
        Internal,
        ModeRestricted,
        ModeForbidden,
        GrantPendingApproval,
        InvalidMode,
    }

    internal static class SkillErrorCodeExtensions
    {
        public static string ToWireString(this SkillErrorCode code)
        {
            switch (code)
            {
                case SkillErrorCode.SkillNotFound:        return "SKILL_NOT_FOUND";
                case SkillErrorCode.MissingParam:         return "MISSING_PARAM";
                case SkillErrorCode.UnknownParam:         return "UNKNOWN_PARAM";
                case SkillErrorCode.TypeMismatch:         return "TYPE_MISMATCH";
                case SkillErrorCode.SemanticInvalid:      return "SEMANTIC_INVALID";
                case SkillErrorCode.InvalidJson:          return "INVALID_JSON";
                case SkillErrorCode.InvalidSkillName:     return "INVALID_SKILL_NAME";
                case SkillErrorCode.TargetNotFound:       return "TARGET_NOT_FOUND";
                case SkillErrorCode.MissingPackage:       return "MISSING_PACKAGE";
                case SkillErrorCode.Compiling:            return "COMPILING";
                case SkillErrorCode.ConfirmationRequired: return "CONFIRMATION_REQUIRED";
                case SkillErrorCode.InvalidToken:         return "INVALID_TOKEN";
                case SkillErrorCode.RateLimit:            return "RATE_LIMIT";
                case SkillErrorCode.ServerStopped:        return "SERVER_STOPPED";
                case SkillErrorCode.SkillError:           return "SKILL_ERROR";
                case SkillErrorCode.BodyTooLarge:         return "BODY_TOO_LARGE";
                case SkillErrorCode.QueueFull:            return "QUEUE_FULL";
                case SkillErrorCode.Timeout:              return "TIMEOUT";
                case SkillErrorCode.NotFound:             return "NOT_FOUND";
                case SkillErrorCode.Internal:             return "INTERNAL";
                case SkillErrorCode.ModeRestricted:       return "MODE_RESTRICTED";
                case SkillErrorCode.ModeForbidden:        return "MODE_FORBIDDEN";
                case SkillErrorCode.GrantPendingApproval: return "GRANT_PENDING_APPROVAL";
                case SkillErrorCode.InvalidMode:          return "INVALID_MODE";
                default:                                  return "UNKNOWN";
            }
        }

        private static Dictionary<string, SkillErrorCode> _byName;

        /// <summary>
        /// Reverse of <see cref="ToWireString"/>: accepts either the wire value ("TARGET_NOT_FOUND")
        /// or the enum name ("TargetNotFound"), case-insensitively. Used when a skill declares an
        /// errorCode on its own error object and the router honours it verbatim.
        /// </summary>
        public static bool TryParseWire(string value, out SkillErrorCode code)
        {
            code = SkillErrorCode.Unknown;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (_byName == null)
            {
                var map = new Dictionary<string, SkillErrorCode>(StringComparer.OrdinalIgnoreCase);
                foreach (SkillErrorCode candidate in Enum.GetValues(typeof(SkillErrorCode)))
                {
                    map[candidate.ToWireString()] = candidate;
                    map[candidate.ToString()] = candidate;
                }
                _byName = map;
            }

            return _byName.TryGetValue(value.Trim(), out code);
        }
    }
}

// Producer:Betsy
