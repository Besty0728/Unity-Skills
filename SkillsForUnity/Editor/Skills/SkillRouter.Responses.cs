using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        /// <summary>
        /// Every validation bucket in one fixed shape, shared by dryRun (v1 and v2) and the failed-execute report, so an
        /// agent reads the same block whichever of the two it received. An empty bucket is null, never omitted.
        /// </summary>
        private static object BuildValidationBlock(ParameterValidationResult validation) => new
        {
            missingParams = validation.MissingParams.Count > 0 ? validation.MissingParams.ToArray() : null,
            unknownParams = validation.UnknownParams.Count > 0 ? validation.UnknownParams.ToArray() : null,
            typeErrors = validation.TypeErrors.Count > 0 ? validation.TypeErrors.ToArray() : null,
            semanticErrors = validation.SemanticErrors.Count > 0 ? validation.SemanticErrors.ToArray() : null,
            missingPackages = validation.MissingPackages.Count > 0 ? validation.MissingPackages.ToArray() : null,
            warnings = validation.Warnings.Count > 0 ? validation.Warnings.ToArray() : null
        };

        /// <summary>
        /// The compact signature a failed execute carries: every effective parameter (the synthesized entityId included)
        /// with its type and required flag, plus the SkillParam note where one exists -- enough to rewrite the call
        /// without a separate schema or dryRun round trip.
        /// </summary>
        private static object[] BuildParameterReport(SkillInfo skill)
        {
            var report = new List<object>(skill.Parameters.Length + 1);
            for (int i = 0; i < skill.Parameters.Length; i++)
            {
                var p = skill.Parameters[i];
                var name = p.Name;
                var type = GetJsonType(p.ParameterType);
                var required = IsParameterRequired(skill, p);
                var description = GetParameterDescription(skill, i);
                report.Add(description == null
                    ? (object)new { name, type, required }
                    : new { name, type, required, description });
            }

            if (ShouldExposeSyntheticEntityId(skill))
                report.Add(new { name = EntityIdParameterName, type = "string", required = false });

            return report.ToArray();
        }

        private static string BuildMissingPackageResponse(SkillInfo skill, string name, ParameterValidationResult validation, List<string> resolutionNotes)
        {
            var missing = validation.MissingPackages.ToArray();
            var fixes = missing
                .Select(packageId => new SuggestedFix
                {
                    action = "install_package",
                    skill = "package_install",
                    args = new Dictionary<string, string> { ["packageId"] = packageId },
                    reason = $"Install {packageId}, wait for the domain reload to finish, then retry {name}."
                })
                .ToList();
            fixes.Add(new SuggestedFix
            {
                action = "retry",
                skill = "package_check",
                args = new Dictionary<string, string> { ["packageId"] = missing[0] },
                reason = "Confirm what is installed before installing, e.g. when the package may come from another source."
            });

            return SkillErrorResponse.Build(
                SkillErrorCode.MissingPackage,
                $"Skill '{name}' requires package(s) that are not installed: {string.Join(", ", missing)}",
                skill: name,
                details: new
                {
                    missingPackages = missing,
                    allowedParams = GetEffectiveParameterNames(skill),
                    validation = BuildValidationBlock(validation),
                    parameters = BuildParameterReport(skill)
                },
                suggestedFixes: fixes,
                relatedSkills: new List<string> { "package_install", "package_check" },
                retryStrategy: SkillErrorResponse.RetryInstallAndRetry,
                extra: ResolutionNotesExtra(resolutionNotes));
        }

        /// <summary>
        /// Appends the finder's pending notes to <paramref name="into"/>, skipping repeats: the planner, the workflow
        /// pre-snapshot and the skill body often resolve the same locator, and one note per resolution is enough.
        /// </summary>
        private static List<string> MergeResolutionNotes(List<string> into)
        {
            var drained = GameObjectFinder.DrainResolutionNotes();
            if (drained == null || drained.Count == 0)
                return into;

            into ??= new List<string>();
            foreach (var note in drained)
            {
                if (!string.IsNullOrWhiteSpace(note) && !into.Contains(note))
                    into.Add(note);
            }
            return into;
        }

        private static IDictionary<string, object> ResolutionNotesExtra(List<string> notes) =>
            notes == null || notes.Count == 0
                ? null
                : new Dictionary<string, object> { [ResolutionNotesKey] = notes.ToArray() };

        private static IDictionary<string, object> WithResolutionNotes(IDictionary<string, object> extra, List<string> notes)
        {
            if (notes == null || notes.Count == 0)
                return extra;

            var merged = extra != null ? new Dictionary<string, object>(extra) : new Dictionary<string, object>();
            merged[ResolutionNotesKey] = notes.ToArray();
            return merged;
        }

        /// <summary>
        /// Serializes a preview payload, appending top-level <c>resolutionNotes</c> only when there are any, and inserting
        /// <c>dryRunToken</c> right after <c>valid</c> only when one was issued -- the common path keeps the exact bytes a
        /// direct serialization produces.
        /// </summary>
        private static string SerializeWithResolutionNotes(object payload, List<string> notes, string dryRunToken = null)
        {
            bool hasNotes = notes != null && notes.Count > 0;
            if (!hasNotes && dryRunToken == null)
                return JsonConvert.SerializeObject(payload, _jsonSettings);

            var obj = JObject.FromObject(payload, JsonSerializer.Create(_jsonSettings));
            if (dryRunToken != null)
                obj.Property("valid")?.AddAfterSelf(new JProperty(DryRunPolicyService.TokenQueryKey, dryRunToken));
            if (hasNotes)
                obj[ResolutionNotesKey] = JArray.FromObject(notes);
            return JsonConvert.SerializeObject(obj, _jsonSettings);
        }

        /// <summary>
        /// A read-only preview of the verdict <see cref="ApplyModeGate"/> would give -- so a dry run can answer
        /// "is this call actually allowed to run," rather than making the agent hit the
        /// MODE_FORBIDDEN / MODE_RESTRICTED wall only once it reaches execute.
        ///
        /// Deliberately re-derives the conclusion from <see cref="SkillsModeManager.CurrentMode"/>, the allowlist, and
        /// <see cref="SkillsModeManager.IsForbiddenInSemi"/>, rather than calling
        /// <c>CheckAccess</c> directly: CheckAccess consumes this thread's one-shot grant token, and the gate wrapping it would also
        /// issue a grant request and write an audit entry -- none of which a preview should do. The order below matches CheckAccess exactly,
        /// minus the one-shot check -- a pending one-shot bypass belongs to the one execute call right after the grant, not to a preview,
        /// and reporting it here would be advertising a permission the next caller might not actually get.
        ///
        /// Should be read as a prediction, not a reservation: the mode or allowlist may change between this dry run and the execute call,
        /// so <c>allowed:true</c> is not a guarantee.
        ///
        /// The verdict is based on the skill's own metadata; for every skill except the "carried-write" entry points
        /// (batch_execute / batch_retry_failed, and the workflow undo/redo/revert family),
        /// that's the entire basis. Those entry points are rejected at execution time based on a classification of a payload this preview has no access to,
        /// so an additional note is attached here instead of a verdict -- see
        /// <see cref="SkillsSurfaceProfile.CarriedWritePreviewGate"/>.
        /// </summary>
        private static object BuildAuthorizationPreview(SkillInfo skill)
        {
            var verdict = BuildModeAuthorizationPreview(skill);

            // Already rejected at the skill layer: the SURFACE_EXCLUDED block has already said everything the payload needs to say;
            // saying it twice would read as two different walls.
            if (SkillsSurfaceProfile.IsExcluded(skill))
                return verdict;

            var payloadGate = SkillsSurfaceProfile.CarriedWritePreviewGate(skill.Name);
            if (payloadGate == null)
                return verdict;

            // Only appends, never replaces, so the original fields' names, values, and order are unchanged; the only addition is that note.
            var annotated = JObject.FromObject(verdict);
            foreach (var property in JObject.FromObject(payloadGate).Properties())
                annotated[property.Name] = property.Value;
            return annotated;
        }

        /// <summary>
        /// The metadata-only half of <see cref="BuildAuthorizationPreview"/>: first checks surface exclusion,
        /// then walks the mode/allowlist decision ladder in the same order as CheckAccess.
        /// </summary>
        private static object BuildModeAuthorizationPreview(SkillInfo skill)
        {
            var mode = SkillsModeManager.CurrentMode;
            var modeWire = SkillsModeManager.ModeToWire(mode);
            bool allowlisted = SkillsModeManager.IsInAllowlist(skill.Name);

            // First, consistent with the execute path: exclusion takes priority over Bypass and the allowlist,
            // so reporting allowed:true here for a skill that is "allowlisted but hidden"
            // would send the agent straight into a SURFACE_EXCLUDED it was just told it wouldn't hit.
            // The dry run itself is never blocked -- previewing an excluded skill is exactly how the agent learns "what the user needs to change."
            if (SkillsSurfaceProfile.IsExcluded(skill))
            {
                return new
                {
                    allowed = false,
                    blockedBy = SkillErrorCode.SurfaceExcluded.ToWireString(),
                    currentMode = modeWire,
                    allowlisted,
                    hint = BuildSurfaceExclusionHint(skill, forPreview: true),
                    surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                };
            }

            if (mode == SkillsOperatingMode.Bypass || allowlisted)
            {
                return new
                {
                    allowed = true,
                    blockedBy = (string)null,
                    currentMode = modeWire,
                    allowlisted,
                    hint = allowlisted
                        ? "Allowlisted — runs without approval in any mode."
                        : "Bypass mode — every skill runs without approval."
                };
            }

            if (SkillsModeManager.IsForbiddenInSemi(skill))
            {
                return new
                {
                    allowed = false,
                    blockedBy = SkillErrorCode.ModeForbidden.ToWireString(),
                    currentMode = modeWire,
                    allowlisted,
                    hint = "Classified as never-in-semi (delete / play mode / domain reload / high risk). Executing needs Bypass mode, or the user adding this skill to the allowlist."
                };
            }

            if (mode == SkillsOperatingMode.Auto || skill.Mode == SkillMode.SemiAuto)
            {
                return new
                {
                    allowed = true,
                    blockedBy = (string)null,
                    currentMode = modeWire,
                    allowlisted,
                    hint = "Executes directly under the current mode — no approval step."
                };
            }

            return new
            {
                allowed = false,
                blockedBy = SkillErrorCode.ModeRestricted.ToWireString(),
                currentMode = modeWire,
                allowlisted,
                hint = "FullAuto skill in Approval mode: the execute call will answer MODE_RESTRICTED with a grant token. Ask the user, then POST /permission/grant {skill, token} — that grant call runs the skill and returns its result."
            };
        }

        private static string SerializeSuccessResponse(object result, JToken sceneDiff = null, long? workflowEndMs = null, List<string> resolutionNotes = null)
        {
            var jsonResult = NormalizeSuccessResult(result);

            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                try
                {
                    if (jsonResult is JObject obj && !obj.ContainsKey("serverAvailability"))
                    {
                        var notice = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            "A skill execution may have triggered compilation or asset refresh.",
                            alwaysInclude: true);
                        if (notice != null)
                        {
                            obj["serverAvailability"] = JToken.FromObject(notice);
                            return BuildSuccessEnvelope(obj, sceneDiff, workflowEndMs, resolutionNotes);
                        }
                    }
                }
                catch { }
            }

            return BuildSuccessEnvelope(jsonResult, sceneDiff, workflowEndMs, resolutionNotes);
        }

        // Serializes the success envelope. sceneDiff (?diff=1), workflowEndMs (the auto-workflow EndTask persistence
        // time, in milliseconds) and resolutionNotes are only appended as top-level fields when present; when none exists, output is byte-for-byte identical to before diff was introduced.
        private static string BuildSuccessEnvelope(JToken result, JToken sceneDiff, long? workflowEndMs = null, List<string> resolutionNotes = null)
        {
            if (resolutionNotes != null && resolutionNotes.Count > 0)
            {
                var envelope = new JObject
                {
                    ["status"] = "success",
                    ["result"] = result,
                    [ResolutionNotesKey] = JArray.FromObject(resolutionNotes)
                };
                if (sceneDiff != null)
                    envelope["sceneDiff"] = sceneDiff;
                if (workflowEndMs != null)
                    envelope["workflowEndMs"] = workflowEndMs.Value;
                return JsonConvert.SerializeObject(envelope, _jsonSettings);
            }
            if (sceneDiff == null && workflowEndMs == null)
                return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
            if (workflowEndMs == null)
                return JsonConvert.SerializeObject(new { status = "success", result, sceneDiff }, _jsonSettings);
            if (sceneDiff == null)
                return JsonConvert.SerializeObject(new { status = "success", result, workflowEndMs = workflowEndMs.Value }, _jsonSettings);
            return JsonConvert.SerializeObject(new { status = "success", result, sceneDiff, workflowEndMs = workflowEndMs.Value }, _jsonSettings);
        }

        // Builds the sceneDiff payload for a successful ?diff=1 execution. A read-only skill just gets a note (nothing to diff);
        // everything else is delegated to SkillSceneDiff.Build. Fully isolated -- any failure degrades to {error:...}, and never disturbs the response envelope.
        private static JToken BuildSceneDiff(bool captureDiff, SkillInfo skill, SkillSceneDiff.DiffCapture diffCapture, object result)
        {
            if (!captureDiff)
                return null;
            try
            {
                if (skill.ReadOnly)
                    return new JObject { ["note"] = "read-only skill, no diff captured" };
                return SkillSceneDiff.Build(diffCapture, result);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[diff] build failed: {ex.Message}");
                return new JObject { ["error"] = $"diff failed: {ex.Message}" };
            }
        }

        private static JToken NormalizeSuccessResult(object result)
        {
            try
            {
                var token = result is JToken existingToken
                    ? existingToken.DeepClone()
                    : JToken.FromObject(result ?? new object(), JsonSerializer.Create(_jsonSettings));

                AddEntityIdsToResult(token);
                return token;
            }
            catch
            {
                return result is JToken fallbackToken
                    ? fallbackToken.DeepClone()
                    : JToken.FromObject(result ?? new object());
            }
        }

        private static void AddEntityIdsToResult(JToken token)
        {
            if (token == null)
                return;

            if (token is JObject obj)
            {
                TryAddEntityIdToResultObject(obj);
                foreach (var property in obj.Properties().ToArray())
                    AddEntityIdsToResult(property.Value);
                return;
            }

            if (token is JArray array)
            {
                foreach (var item in array)
                    AddEntityIdsToResult(item);
            }
        }

        private static void TryAddEntityIdToResultObject(JObject obj)
        {
            if (obj == null ||
                TryGetJsonValue(obj, EntityIdParameterName, out _) ||
                !TryGetJsonValue(obj, "instanceId", out var instanceIdToken))
            {
                return;
            }

            var unityObject = ResolveUnityObjectFromResultObject(obj, instanceIdToken);
            var entityId = UnityObjectIdUtility.GetEntityId(unityObject);
            if (!string.IsNullOrWhiteSpace(entityId))
                obj[EntityIdParameterName] = entityId;
        }

        private static UnityEngine.Object ResolveUnityObjectFromResultObject(JObject obj, JToken instanceIdToken)
        {
            if (TryReadInt(instanceIdToken, out var instanceId) && instanceId != 0)
            {
                var byInstanceId = UnityObjectIdUtility.ObjectIdToObject(instanceId);
                if (byInstanceId != null)
                    return byInstanceId;
            }

            foreach (var pathField in new[] { "assetPath", "materialPath", "profilePath", "prefabPath", "path" })
            {
                if (!TryGetJsonString(obj, pathField, out var candidatePath))
                    continue;

                var asset = TryResolveAssetPath(candidatePath);
                if (asset != null)
                    return asset;

                var sceneObject = TryResolveScenePath(candidatePath);
                if (sceneObject != null)
                    return sceneObject;
            }

            foreach (var nameField in new[] { "gameObject", "gameObjectName", "target", "targetName", "objectName", "cameraName", "vcamName", "sequencerName" })
            {
                if (!TryGetJsonString(obj, nameField, out var candidateName))
                    continue;

                var sceneObject = GameObjectFinder.Find(name: candidateName);
                if (sceneObject != null)
                    return sceneObject;
            }

            if (!LooksLikeAssetResult(obj) && TryGetJsonString(obj, "name", out var name))
                return GameObjectFinder.Find(name: name);

            return null;
        }

        private static UnityEngine.Object TryResolveAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalized);
        }

        private static GameObject TryResolveScenePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return GameObjectFinder.Find(path: normalized);
        }

        private static bool LooksLikeAssetResult(JObject obj)
        {
            return TryGetJsonValue(obj, "assetPath", out _) ||
                TryGetJsonValue(obj, "materialPath", out _) ||
                TryGetJsonValue(obj, "profilePath", out _) ||
                TryGetJsonValue(obj, "prefabPath", out _) ||
                TryGetJsonValue(obj, "shader", out _) ||
                TryGetJsonValue(obj, "texture", out _) ||
                TryGetJsonValue(obj, "renderPipeline", out _);
        }

        private static bool TryGetJsonString(JObject obj, string propertyName, out string value)
        {
            value = null;
            if (!TryGetJsonValue(obj, propertyName, out var token) ||
                token == null ||
                token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryGetJsonValue(JObject obj, string propertyName, out JToken value)
        {
            value = null;
            if (obj == null || string.IsNullOrEmpty(propertyName))
                return false;

            foreach (var property in obj.Properties())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
                return false;

            try
            {
                value = token.ToObject<int>();
                return true;
            }
            catch
            {
                return int.TryParse(token.ToString(), out value);
            }
        }
    }
}

// Producer:Betsy
