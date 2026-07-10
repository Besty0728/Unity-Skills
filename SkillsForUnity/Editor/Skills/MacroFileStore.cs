using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    /// <summary>Parsed macro file (schemaVersion 1) as loaded by the Macro Recorder panel.</summary>
    internal sealed class MacroFile
    {
        public int SchemaVersion;
        public string RecordedAtUtc;
        public string UnityVersion;
        public string SceneName;
        public string Note;
        public JObject Params;       // $param defaults, user-editable; {} when absent
        public JArray Steps;
        public List<string> Warnings = new List<string>();
        public bool Replayable;
    }

    /// <summary>
    /// Macro file (.json) build / parse / save / load for the Macro Recorder panel.
    ///
    /// Format: {schemaVersion:1, meta:{recordedAtUtc, unityVersion, sceneName, note},
    /// params:{}, steps:[...], warnings:[...], replayable}. steps is the macro_export product
    /// (directly POSTable to /skills/batch as {"steps":...,"params":...}); params starts empty
    /// and is meant for hand-added {"$param":...} slots.
    ///
    /// Build/Parse are pure (EditMode-testable); Save/Load wrap them with the recorder session
    /// and file IO. Parse validates strictly: unknown schemaVersion or a non-array steps is a
    /// structured error, not a best-effort load.
    /// </summary>
    internal static class MacroFileStore
    {
        public const int CurrentSchemaVersion = 1;

        // ===== Pure layer =====

        public static JObject BuildJson(JArray steps, IEnumerable<string> warnings, bool replayable,
            string note, string recordedAtUtc, string unityVersion, string sceneName, JObject parameters = null)
        {
            return new JObject
            {
                ["schemaVersion"] = CurrentSchemaVersion,
                ["meta"] = new JObject
                {
                    ["recordedAtUtc"] = recordedAtUtc,
                    ["unityVersion"] = unityVersion,
                    ["sceneName"] = sceneName,
                    ["note"] = note
                },
                ["params"] = parameters ?? new JObject(),
                ["steps"] = steps ?? new JArray(),
                ["warnings"] = warnings != null ? JArray.FromObject(warnings) : new JArray(),
                ["replayable"] = replayable
            };
        }

        public static bool TryParse(string json, out MacroFile file, out string error)
        {
            file = null;

            JObject root;
            try
            {
                // DateParseHandling.None: keep ISO-looking strings (recordedAtUtc, and any step
                // value that happens to look like a date) as strings instead of letting
                // Newtonsoft coerce them to DateTime and reformat on the way back out.
                using (var reader = new JsonTextReader(new StringReader(json ?? "")))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JToken.ReadFrom(reader) as JObject;
                }
            }
            catch (JsonException ex)
            {
                error = $"Not valid JSON: {ex.Message}";
                return false;
            }
            if (root == null)
            {
                error = "The macro file root must be a JSON object.";
                return false;
            }

            var versionToken = root["schemaVersion"];
            if (versionToken == null || versionToken.Type != JTokenType.Integer)
            {
                error = "Missing or non-integer 'schemaVersion'.";
                return false;
            }
            int version = versionToken.Value<int>();
            if (version != CurrentSchemaVersion)
            {
                error = $"Unsupported schemaVersion {version} (this build reads version {CurrentSchemaVersion}).";
                return false;
            }

            if (!(root["steps"] is JArray steps))
            {
                error = "'steps' must be a JSON array.";
                return false;
            }
            for (int i = 0; i < steps.Count; i++)
            {
                if (!(steps[i] is JObject step)
                    || !(step["skill"] is JToken skillToken)
                    || skillToken.Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(skillToken.Value<string>()))
                {
                    error = $"steps[{i}] must be an object with a non-empty string 'skill' field.";
                    return false;
                }
            }

            var paramsToken = root["params"];
            if (paramsToken != null && paramsToken.Type != JTokenType.Null && !(paramsToken is JObject))
            {
                error = "'params' must be a JSON object.";
                return false;
            }

            var meta = root["meta"] as JObject;
            var parsed = new MacroFile
            {
                SchemaVersion = version,
                RecordedAtUtc = meta?["recordedAtUtc"]?.ToString(),
                UnityVersion = meta?["unityVersion"]?.ToString(),
                SceneName = meta?["sceneName"]?.ToString(),
                Note = meta?["note"]?.Type == JTokenType.Null ? null : meta?["note"]?.ToString(),
                Params = paramsToken as JObject ?? new JObject(),
                Steps = steps,
                Replayable = root["replayable"]?.Type == JTokenType.Boolean && root["replayable"].Value<bool>()
            };
            if (root["warnings"] is JArray warningsArr)
            {
                foreach (var w in warningsArr)
                    parsed.Warnings.Add(w?.ToString() ?? "");
            }

            file = parsed;
            error = null;
            return true;
        }

        // ===== Session / file IO layer =====

        /// <summary>Serializes the recorder's stopped session (macro_export product) to path.</summary>
        public static bool TrySaveCurrentSession(string path, out string error)
        {
            if (!MacroRecorderService.TryGetStoppedSessionExport(out var steps, out var warnings,
                    out var replayable, out error))
                return false;

            var json = BuildJson(steps, warnings, replayable,
                MacroRecorderService.SessionNote,
                MacroRecorderService.SessionStartedUtc,
                Application.unityVersion,
                SceneManager.GetActiveScene().name);
            try
            {
                File.WriteAllText(path, json.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            error = null;
            return true;
        }

        public static bool TryLoadFile(string path, out MacroFile file, out string error)
        {
            file = null;
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return TryParse(json, out file, out error);
        }
    }
}
