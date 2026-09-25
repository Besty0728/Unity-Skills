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
        private static void AnalyzeAssetImport(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath))
            {
                bool isDir = Directory.Exists(sourcePath);
                if (!File.Exists(sourcePath) && !isDir)
                    AddSemanticError(validation, "sourcePath", $"Source not found: {sourcePath}");
                else if (isDir)
                    AddSemanticError(validation, "sourcePath", $"Source path must be a file, not a directory: {sourcePath}");
            }
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));

            if (plan != null)
            {
                MarkSemantic(plan);
                var dir = string.IsNullOrEmpty(destinationPath) ? null : Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Import Asset",
                            ["target"] = destinationPath,
                            ["sourcePath"] = sourcePath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["sourcePath"] = sourcePath,
                            ["destinationPath"] = destinationPath,
                            ["createsDirectory"] = !string.IsNullOrEmpty(dir) && !Directory.Exists(dir)
                        }
                    });
            }
        }

        private static void AnalyzeAssetImportBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetImport(iv, null);
                ctx.ReportDelegatedErrors(i, iv);

                ctx.AddItemPlan(i,
                    GetStringArg(item, "destinationPath") ?? GetStringArg(item, "sourcePath"),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["sourcePath"] = GetStringArg(item, "sourcePath"),
                        ["destinationPath"] = GetStringArg(item, "destinationPath")
                    });
                }
            }
            ctx.EmitPlan("Import Assets (batch)", create: creates);
        }

        private static void AnalyzeAssetDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var assetPath = GetStringArg(validation.Args, "assetPath");
            AddErrorFromValidation(validation, Validate.Required(assetPath, "assetPath"), "assetPath");
            if (!string.IsNullOrEmpty(assetPath) && Validate.SafePath(assetPath, "assetPath", isDelete: true) is object pathErr)
                AddSemanticError(validation, "assetPath", ExtractError(pathErr));
            if (!string.IsNullOrEmpty(assetPath) && !SkillsCommon.PathExists(assetPath))
                AddSemanticError(validation, "assetPath", $"Asset not found: {assetPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete Asset",
                            ["target"] = assetPath
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = assetPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var path = GetStringArg(item, "path");
                var errors = new List<string>();
                if (Validate.Required(path, "path") is object pathRequired)
                    errors.Add(ExtractError(pathRequired));
                else
                {
                    if (Validate.SafePath(path, "path", isDelete: true) is object pathErr)
                        errors.Add(ExtractError(pathErr));
                    if (!SkillsCommon.PathExists(path))
                        errors.Add($"Asset not found: {path}");
                }

                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    path,
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0)
                    deletes.Add(new Dictionary<string, object> { ["target"] = path });
            }
            ctx.EmitPlan("Delete Assets (batch)", delete: deletes);
        }

        private static void AnalyzeAssetMove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath) && Validate.SafePath(sourcePath, "sourcePath") is object srcErr)
                AddSemanticError(validation, "sourcePath", ExtractError(srcErr));
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));
            if (!string.IsNullOrEmpty(sourcePath) && !SkillsCommon.PathExists(sourcePath))
                AddSemanticError(validation, "sourcePath", $"Asset not found: {sourcePath}");
            if (!string.IsNullOrEmpty(destinationPath) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath) != null)
                AddWarning(validation, $"Destination already exists and may cause move failure: {destinationPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Move Asset",
                            ["target"] = sourcePath,
                            ["destinationPath"] = destinationPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["from"] = sourcePath,
                            ["to"] = destinationPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetMoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var iv = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetMove(iv, null);
                ctx.ReportDelegatedErrors(i, iv);
                foreach (var warning in iv.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                ctx.AddItemPlan(i,
                    GetStringArg(item, "sourcePath"),
                    iv.SemanticErrors.Count == 0,
                    iv.SemanticErrors.Select(ExtractSemanticMessage).ToArray());

                if (iv.SemanticErrors.Count == 0)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["from"] = GetStringArg(item, "sourcePath"),
                        ["to"] = GetStringArg(item, "destinationPath")
                    });
                }
            }
            ctx.EmitPlan("Move Assets (batch)", modify: modifies);
        }
    }
}

// Producer:Betsy
