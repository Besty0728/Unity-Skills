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
        private static void AnalyzeComponentAdd(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var target = ResolveTarget(args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }
            WarnIfPending(validation, "target", target);

            // Already reported above, or listed in MissingParams.
            if (string.IsNullOrEmpty(componentType))
                return;

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            var go = target.LiveObject;
            bool alreadyExists = go != null && go.GetComponent(type) != null && !AllowsMultiple(type);
            if (alreadyExists)
                AddWarning(validation, $"Component {type.Name} already exists on {go.name}; execution will be a no-op warning.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Add Component",
                            ["target"] = target.Path,
                            ["componentType"] = type.FullName
                        }
                    },
                    create: alreadyExists
                        ? new List<object>()
                        : new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["target"] = target.Path,
                                ["component"] = type.Name,
                                ["fullTypeName"] = type.FullName
                            }
                        });
            }
        }

        private static void AnalyzeComponentAddBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var warnings = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var target = ResolveTarget(item);
                if (target.Error != null)
                    errors.Add(ExtractError(target.Error));

                Type type = null;
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    var go = target.LiveObject;
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else if (go != null && go.GetComponent(type) != null && !AllowsMultiple(type))
                        warnings.Add($"Component {type.Name} already exists on {go.name}");
                }

                ctx.ReportItemErrors(i, errors);
                foreach (var warning in warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");
                if (errors.Count == 0)
                    WarnIfPending(validation, "target", target, i);

                ctx.AddItemPlan(i,
                    target.Error == null ? target.Path : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0 && type != null && warnings.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["target"] = target.Path,
                        ["component"] = type.Name,
                        ["fullTypeName"] = type.FullName
                    });
                }
            }
            ctx.EmitPlan("Add Components (batch)", create: creates);
        }

        private static void AnalyzeComponentRemove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var target = ResolveTarget(args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }

            if (string.IsNullOrEmpty(componentType))
                return;

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            int componentIndex = GetIntArg(args, "componentIndex");
            var go = target.LiveObject;
            if (go != null)
            {
                var components = go.GetComponents(type);
                if (components.Length == 0)
                {
                    AddSemanticError(validation, "component", $"Component not found on {go.name}: {componentType}");
                    return;
                }

                if (componentIndex < 0 || componentIndex >= components.Length)
                {
                    AddSemanticError(validation, "componentIndex", $"Component index {componentIndex} out of range. Found {components.Length} components of type {componentType}");
                    return;
                }

                var requiredBy = ComponentSkills.FindBlockingDependents(go, new[] { components[componentIndex] });
                if (requiredBy.Length > 0)
                {
                    AddSemanticError(validation, "component", $"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}");
                    return;
                }
            }
            else
            {
                WarnIfPending(validation, "target", target);
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Remove Component",
                            ["target"] = target.Path,
                            ["componentType"] = type.FullName,
                            ["componentIndex"] = componentIndex
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = target.Path,
                            ["component"] = type.Name,
                            ["componentIndex"] = componentIndex
                        }
                    });
            }
        }

        private static void AnalyzeComponentRemoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            // What earlier items remove, so each item is judged against the object as execution will find it.
            var removedSoFar = new HashSet<Component>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var target = ResolveTarget(item);
                if (target.Error != null)
                    errors.Add(ExtractError(target.Error));

                Type type = null;
                Component[] toRemove = null;
                var componentIndex = GetOptionalIntArg(item, "componentIndex");
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    var go = target.LiveObject;
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else if (go != null)
                    {
                        var components = go.GetComponents(type).Where(c => !removedSoFar.Contains(c)).ToArray();
                        if (components.Length == 0)
                            errors.Add($"Component not found: {componentType}");
                        else if (componentIndex.HasValue && (componentIndex.Value < 0 || componentIndex.Value >= components.Length))
                            errors.Add($"Component index {componentIndex.Value} out of range. Found {components.Length} components of type {componentType}");
                        else
                        {
                            toRemove = componentIndex.HasValue ? new[] { components[componentIndex.Value] } : components;
                            var requiredBy = ComponentSkills.FindBlockingDependents(go, toRemove, removedSoFar);
                            if (requiredBy.Length > 0)
                                errors.Add($"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}");
                        }
                    }
                }

                ctx.ReportItemErrors(i, errors);
                if (errors.Count == 0)
                    WarnIfPending(validation, "target", target, i);

                ctx.AddItemPlan(i,
                    target.Error == null ? target.Path : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0 && type != null)
                {
                    var removed = new Dictionary<string, object>
                    {
                        ["target"] = target.Path,
                        ["component"] = type.Name
                    };
                    // A pending target has no components to count yet.
                    if (toRemove != null)
                    {
                        removed["count"] = toRemove.Length;
                        removedSoFar.UnionWith(toRemove);
                    }
                    if (componentIndex.HasValue)
                        removed["componentIndex"] = componentIndex.Value;
                    deletes.Add(removed);
                }
            }
            ctx.EmitPlan("Remove Components (batch)", delete: deletes);
        }

        private static void AnalyzeComponentSetProperty(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            var propertyName = GetStringArg(args, "propertyName");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");
            AddErrorFromValidation(validation, Validate.Required(propertyName, "propertyName"), "propertyName");

            var target = ResolveTarget(args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }

            if (string.IsNullOrEmpty(componentType))
                return;

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            // Only a live object can be asked whether it carries the component; everything below is checked on the type.
            var go = target.LiveObject;
            if (go != null)
            {
                if (go.GetComponent(type) == null)
                {
                    AddSemanticError(validation, "component", $"Component not found: {componentType}");
                    return;
                }
            }
            else
            {
                WarnIfPending(validation, "target", target);
            }

            if (string.IsNullOrEmpty(propertyName))
                return;

            var (prop, field) = FindMember(type, propertyName);
            if (prop == null && field == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            var targetType = prop?.PropertyType ?? field?.FieldType;
            if (targetType == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            if (prop != null && !prop.CanWrite)
            {
                AddSemanticError(validation, "propertyName", $"Property {propertyName} is read-only");
                return;
            }

            TryValidateComponentAssignment(args, propertyName, targetType, validation);

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Set Component Property",
                            ["target"] = target.Path,
                            ["componentType"] = type.FullName,
                            ["propertyName"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = target.Path,
                            ["component"] = type.Name,
                            ["property"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    });
            }
        }

        private static void AnalyzeComponentSetPropertyBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeComponentSetProperty(iv, null);
                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                var target = ResolveTarget(item);
                ctx.AddItemPlan(i,
                    target.Error == null ? target.Path : InferPrimaryTarget(item),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0 && target.Error == null)
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = target.Path,
                        ["component"] = GetStringArg(item, "componentType"),
                        ["property"] = GetStringArg(item, "propertyName")
                    });
            }
            ctx.EmitPlan("Set Component Properties (batch)", modify: modifies);
        }
    }
}

// Producer:Betsy
