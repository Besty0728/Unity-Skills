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
                default:                                  return "UNKNOWN";
            }
        }
    }
}
