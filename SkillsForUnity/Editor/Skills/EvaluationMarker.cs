using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// The dedicated-evaluation-project marker, ProjectSettings/UnitySkillsEvaluation.json (contract 7.4). Only the
    /// panel writes it, and only after the user confirmed; the runner reads it to allow the standard and challenge
    /// tiers, and reports any other project as user-project.
    /// </summary>
    internal static class EvaluationMarker
    {
        internal const string FileName = "UnitySkillsEvaluation.json";
        internal const string Kind = "UnitySkillsEvaluationProject";

        internal enum MarkerState { Missing, Valid, Invalid }

        internal static string PathFor(string projectRoot) => Path.Combine(projectRoot, "ProjectSettings", FileName);

        /// <summary>Same rule as the runner: parsable, schema 1 and the expected kind.</summary>
        internal static MarkerState Read(string projectRoot)
        {
            var raw = EvaluationState.Read(PathFor(projectRoot));
            switch (raw.Status)
            {
                case EvaluationState.ReadStatus.Missing:
                    return MarkerState.Missing;
                case EvaluationState.ReadStatus.Ok:
                    return raw.Value.Value<long>("schema") == 1 && raw.Value.Value<string>("kind") == Kind
                        ? MarkerState.Valid
                        : MarkerState.Invalid;
                default:
                    return MarkerState.Invalid;
            }
        }

        internal static void Write(string projectRoot, DateTime nowUtc)
        {
            EvaluationState.WriteTextAtomic(PathFor(projectRoot), Serialize(nowUtc, SkillsLogger.Version));
        }

        internal static string Serialize(DateTime createdAtUtc, string version)
        {
            var marker = new JObject
            {
                ["schema"] = 1,
                ["kind"] = Kind,
                ["createdAt"] = EvaluationState.FormatIso(createdAtUtc),
                ["createdWith"] = version,
            };
            return marker.ToString(Newtonsoft.Json.Formatting.None) + "\n";
        }
    }
}

// Producer:Betsy
