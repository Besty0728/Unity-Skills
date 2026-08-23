using System;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// REST 响应用的稳定错误码，供 AI 解析。
    /// 线上格式为 SCREAMING_SNAKE_CASE（见 <see cref="SkillErrorCodeExtensions.ToWireString"/>）。
    /// 新增值一律追加到末尾，以保持数值顺序稳定。
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
        SurfaceExcluded,
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
                case SkillErrorCode.SurfaceExcluded:      return "SURFACE_EXCLUDED";
                default:                                  return "UNKNOWN";
            }
        }

        private static Dictionary<string, SkillErrorCode> _byName;

        /// <summary>
        /// <see cref="ToWireString"/> 的逆操作：线上值（"TARGET_NOT_FOUND"）与枚举名
        /// （"TargetNotFound"）都接受，大小写不敏感。用于 skill 在自己的错误对象上声明
        /// errorCode、由 router 原样沿用的场景。
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
