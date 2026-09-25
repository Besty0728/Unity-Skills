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
        private static void AnalyzeMaterialCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var savePath = GetStringArg(args, "savePath");
            bool savePathSafe = true;
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object saveErr)
            {
                AddSemanticError(validation, "savePath", ExtractError(saveErr));
                savePathSafe = false;
            }

            var resolvedShaderName = ResolveShaderName(GetStringArg(args, "shaderName"), validation);
            string resolvedPath = null;
            if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
            {
                // The executor's own resolver, so the plan names the file material_create will write.
                if (savePathSafe && !MaterialSkills.TryResolveMaterialSavePath(savePath, name, out resolvedPath, out var resolveErr))
                    AddSemanticError(validation, "savePath", ExtractError(resolveErr));
            }
            else if (string.IsNullOrEmpty(savePath))
                AddWarning(validation, "Material will be created in memory only because savePath is omitted.");

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
                            ["action"] = "Create Material",
                            ["target"] = name,
                            ["shader"] = resolvedShaderName,
                            ["savePath"] = resolvedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["shader"] = resolvedShaderName,
                            ["path"] = resolvedPath,
                            ["renderPipeline"] = ProjectSkills.DetectRenderPipeline().ToString(),
                            ["persistent"] = !string.IsNullOrEmpty(resolvedPath)
                        }
                    });
            }
        }

        private static void AnalyzeMaterialCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialCreate(iv, null);

                var name = GetStringArg(item, "name");
                var savePath = GetStringArg(item, "savePath");
                var resolvedShader = ResolveShaderName(GetStringArg(item, "shaderName"), iv);

                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                ctx.AddItemPlan(i,
                    name,
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    string resolvedPath = null;
                    if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
                        MaterialSkills.TryResolveMaterialSavePath(savePath, name, out resolvedPath, out _);
                    creates.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["shader"] = resolvedShader,
                        ["path"] = resolvedPath
                    });
                }
            }
            ctx.EmitPlan("Create Materials (batch)", create: creates);
        }

        private static void AnalyzeMaterialAssign(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var materialPath = GetStringArg(args, "materialPath");
            AddErrorFromValidation(validation, Validate.Required(materialPath, "materialPath"), "materialPath");

            var target = ResolveTarget(args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }

            Renderer renderer = null;
            if (target.LiveObject != null)
            {
                renderer = target.LiveObject.GetComponent<Renderer>();
                if (renderer == null)
                {
                    AddSemanticError(validation, "renderer", "No Renderer component found");
                    return;
                }
            }
            else
            {
                WarnIfPending(validation, "target", target);
            }

            if (string.IsNullOrEmpty(materialPath))
                return;

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                AddSemanticError(validation, "materialPath", $"Material not found: {materialPath}");
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var assigned = new Dictionary<string, object>
                {
                    ["target"] = target.Path,
                    ["material"] = materialPath
                };
                if (renderer != null)
                    assigned["rendererType"] = renderer.GetType().Name;
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Assign Material",
                            ["target"] = target.Path,
                            ["materialPath"] = materialPath
                        }
                    },
                    modify: new List<object> { assigned });
            }
        }

        private static void AnalyzeMaterialAssignBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialAssign(iv, null);
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
                        ["material"] = GetStringArg(item, "materialPath")
                    });
            }
            ctx.EmitPlan("Assign Materials (batch)", modify: modifies);
        }
    }
}

// Producer:Betsy
