using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        // Skills whose real requirement is "at least one GameObject must already be selected in the Hierarchy",
        // read from UnityEditor.Selection at request time rather than from any JSON body parameter. No group
        // mechanism can express this ("selection" names no parameter any of these skills accepts, and
        // _requiredInputGroups candidates must themselves be real accepted parameter names per
        // RequiredInputGroups_NameOnlyRealParameters) - so it is enforced here directly instead, and
        // "selection"/"selectedGameObjects" stay in SkillMetadataGuardTests's allowlist with this same justification.
        private static void AnalyzeRequiresEditorSelection(SkillRouter.ParameterValidationResult validation, int minimumCount, bool requireRectTransform)
        {
            var selected = UnityEditor.Selection.gameObjects ?? Array.Empty<GameObject>();
            if (requireRectTransform)
                selected = selected.Where(go => go.GetComponent<RectTransform>() != null).ToArray();

            if (selected.Length < minimumCount)
            {
                var message = minimumCount <= 1
                    ? "No GameObjects selected in the Hierarchy. Select one or more objects first."
                    : $"Select at least {minimumCount} {(requireRectTransform ? "UI elements (with a RectTransform)" : "GameObjects")} in the Hierarchy first.";
                AddSemanticError(validation, "selection", message);
            }
        }

        /// <summary>
        /// Enforces the half of RequiresInput that says "you must specify a target" - nothing enforced it before:
        /// a group-level check that confirms at least one parameter able to satisfy a semantic token carries a usable value.
        ///
        /// <para>Only adds the "whole group is missing" verdict. Per-parameter requiredness is still MissingParams's job
        /// and is left as-is - when the two overlap (the token's key is itself a required CLR parameter), this stays
        /// silent instead of reporting the same empty request body twice in two different wordings.</para>
        ///
        /// <para>The candidate list is intersected with what this skill actually accepts. Without this step, for the 35
        /// skills that declare RequiresInput "gameObject" but only accept <c>items</c> (all the <c>*_batch</c> ones)
        /// or use differently-named locators (component_copy's sourceName/targetName), this check could never be
        /// satisfied - turning a metadata inconsistency into a skill nobody could ever call. This is also why the
        /// token vocabulary stops here: cinemachine's <c>vcam</c> token, intersected, leaves only {instanceId, path},
        /// which would reject the perfectly reasonable <c>{vcamName: "CM vcam1"}</c>.</para>
        /// </summary>
        private static void ApplyRequiredInputGroups(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation)
        {
            // BuildPlan allows validation to be null, and unlike the per-skill analyzers below, this check runs for
            // every skill - so this is the one place that must explicitly handle that case.
            if (skill == null || validation?.Args == null)
                return;

            var tokens = skill.RequiresInput;
            if (tokens == null || tokens.Length == 0)
                return;

            foreach (var token in tokens)
            {
                if (token == null || !_requiredInputGroups.TryGetValue(token, out var candidates))
                    continue;

                var accepted = candidates.Where(candidate => SkillAcceptsParameter(skill, candidate)).ToArray();
                if (accepted.Length == 0)
                    continue;

                if (accepted.Any(candidate => validation.MissingParams.Any(missing =>
                        string.Equals(missing, candidate, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                if (accepted.Any(candidate => HasUsableArgument(validation.Args, candidate)))
                    continue;

                if (TokenAlreadyReported(validation, token, accepted))
                    continue;

                AddSemanticError(validation, RequiredInputGroupField(token),
                    $"Provide one of: {string.Join(", ", accepted)}.");
            }
        }

        /// <summary>
        /// Determines whether some analyzer has already spoken about this target - if so, the generic "Provide one
        /// of: ..." is just a second wording of the same complaint. An empty gameobject_delete request body used to
        /// get both this <c>field: "target"</c> and AnalyzeGameObjectDelete's <c>field: "gameObject"</c>, leaving the
        /// agent to guess whether it has one problem or two.
        ///
        /// <para>Matching checks not just accepted parameters but also the token's own parts: the object-locator
        /// analyzer reports under "gameObject" (the token name, not any skill parameter); the two-sided token "gameObject|path" needs either half recognized.</para>
        /// </summary>
        private static bool TokenAlreadyReported(
            SkillRouter.ParameterValidationResult validation, string token, string[] accepted)
        {
            if (validation.SemanticErrors.Count == 0)
                return false;

            var tokenParts = token.Split('|');

            foreach (var entry in validation.SemanticErrors)
            {
                if (!SkillResultHelper.TryGetMemberValue(entry, "field", out var fieldValue))
                    continue;

                var field = fieldValue?.ToString();
                if (string.IsNullOrEmpty(field))
                    continue;

                if (tokenParts.Any(part => string.Equals(field, part, StringComparison.OrdinalIgnoreCase)) ||
                    accepted.Any(candidate => string.Equals(field, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Which <c>field</c> name the whole-group-missing error is reported under. The two object-locator
        /// tokens use "target", because no single parameter should be blamed - the caller supplied none of them.
        /// </summary>
        private static string RequiredInputGroupField(string token)
        {
            if (token.StartsWith("gameObject", StringComparison.OrdinalIgnoreCase))
                return "target";
            return token;
        }

        /// <summary>
        /// Determines whether this skill accepts this key: its declared parameters, plus the synthesized entityId
        /// locator the router injects for skills that accept instanceId. AllowedParameterSet is exactly the router's
        /// answer to this question, so reading it keeps the two sides consistent.
        /// </summary>
        private static bool SkillAcceptsParameter(SkillRouter.SkillInfo skill, string parameterName)
        {
            if (skill.AllowedParameterSet != null)
                return skill.AllowedParameterSet.Contains(parameterName);

            return skill.ParameterNames != null &&
                skill.ParameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Determines whether the request body gives a value the locator layer would actually act on. Consistent with
        /// <see cref="ReadObjectLocator"/>: a blank string and instanceId 0 both mean "not supplied" - counting them as
        /// provided here would let the <c>{"name": ""}</c> or <c>{"instanceId": 0}</c> agents routinely send verbatim slip past this check.
        ///
        /// <para>A quoted number in a numeric key is judged as a number, not as a non-empty string:
        /// <see cref="GetIntArg"/> converts <c>{"instanceId": "0"}</c> to 0, so the locator layer then finds no
        /// target - counting it as provided is exactly what would let a request body that can never resolve pass
        /// dryRun and only fail at execution time. String locators keep string semantics -
        /// <c>{"name": "0"}</c> refers to a GameObject named "0", and the executor accepts it.</para>
        /// </summary>
        private static bool HasUsableArgument(JObject args, string key)
        {
            if (!args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) ||
                token == null ||
                token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                try { return token.ToObject<double>() != 0d; }
                catch { return false; }
            }

            if (token.Type == JTokenType.String && IsNumericLocatorKey(key))
            {
                // Applies the same conversion as GetIntArg, hence the same verdict: any input that ends up as 0
                // (including text it can't parse at all) leaves the locator layer with no target.
                try { return token.ToObject<int>() != 0; }
                catch { return false; }
            }

            return !string.IsNullOrWhiteSpace(token.ToString());
        }

        /// <summary>
        /// Keys the locator layer reads via <see cref="GetIntArg"/>. Every instanceId-shaped skill parameter in this
        /// package is declared as <c>int</c> (instanceId, parentInstanceId, sourceInstanceId, vcamInstanceId, ...),
        /// so recognizing it by suffix is enough, with no list to keep in sync with twenty-odd call sites.
        /// </summary>
        private static bool IsNumericLocatorKey(string key) =>
            key != null && key.EndsWith("instanceId", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A validation-only planner, for cases where the only risk worth planning for is a setter on an enum
        /// parameter. Rejects invalid values at the dryRun layer - exactly where the agent looks before submitting - and does nothing else.
        ///
        /// <para>It supplements rather than replaces the skill's own rejection: the skill body is responsible for
        /// stopping the invalid value from being written, while this is responsible for stopping the agent from being
        /// told "this call is ready to go". Reusing <see cref="SkillParamUtil.TryParseEnumParam{TEnum}"/> means the
        /// same case-insensitive parsing that rejects integer literals, and the same "Invalid value 'X' for parameter
        /// 'Y'. Valid values: ..." wording as actual execution time.</para>
        ///
        /// <para>Deliberately does not call <see cref="MarkSemantic"/>: that would attach steps/changes to every
        /// dryRun of these skills, changing the response an already-correct request body would get. The only goal
        /// here is to stop saying valid:true for an incorrect request body.</para>
        /// </summary>
        private static void AnalyzeEnumSetterParameter<TEnum>(
            SkillRouter.ParameterValidationResult validation,
            string parameterName) where TEnum : struct
        {
            var raw = GetStringArg(validation?.Args, parameterName);
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (!SkillParamUtil.TryParseEnumParam<TEnum>(raw, parameterName, out _, out var error))
                AddSemanticError(validation, parameterName, ExtractError(error));
        }

        /// <summary>
        /// A validation-only planner, for create-style enum parameters a skill reads via
        /// <see cref="SkillParamUtil.TryParseRequiredEnum{TEnum}"/> - ones whose declared default is already a member name (e.g. "Point", "soft").
        ///
        /// <para>Differs from <see cref="AnalyzeEnumSetterParameter{TEnum}"/> in exactly one place, and must: empty
        /// values. Omitting this key is valid, since it binds to the CLR default and that default is valid. Explicitly
        /// passing an empty string or null is not valid, because ValidateParameters binds the given value without
        /// substituting the default, so the skill's TryParseRequiredEnum rejects it - while the setter analyzer's rule
        /// that "empty means leave it alone, let it through" would answer valid:true for the one body that can never run.</para>
        /// </summary>
        private static void AnalyzeRequiredEnumParameter<TEnum>(
            SkillRouter.ParameterValidationResult validation,
            string parameterName) where TEnum : struct
        {
            var args = validation?.Args;
            if (args == null ||
                !args.TryGetValue(parameterName, StringComparison.OrdinalIgnoreCase, out var token))
            {
                return;
            }

            var raw = token == null || token.Type == JTokenType.Null ? null : token.ToString();
            if (!SkillParamUtil.TryParseRequiredEnum<TEnum>(raw, parameterName, out _, out var error))
                AddSemanticError(validation, parameterName, ExtractError(error));
        }
    }
}

// Producer:Betsy
