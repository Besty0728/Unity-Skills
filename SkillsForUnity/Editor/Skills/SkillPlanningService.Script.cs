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
        // ==================================================================================
        // Script semantic planner
        // ==================================================================================

        private static void AnalyzeScriptCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scriptName = GetStringArg(args, "scriptName", "name");
            var folder = GetStringArg(args, "folder") ?? "Assets/Scripts";
            var template = GetStringArg(args, "template");
            bool fromTemplate = string.IsNullOrEmpty(GetStringArg(args, "content"));

            if (string.IsNullOrWhiteSpace(scriptName))
                AddSemanticError(validation, "scriptName", "scriptName or name is required.");

            // Same template rules and default-folder switch as script_create itself.
            if (fromTemplate)
            {
                if (!ScriptSkills.TryResolveTemplate(template, GetStringArg(args, "namespaceName"), out _, out var templateErr))
                    AddSemanticError(validation, "template", ExtractError(templateErr));
                else if (ScriptSkills.IsEditorOnlyTemplate(template) && string.Equals(folder, "Assets/Scripts", StringComparison.OrdinalIgnoreCase))
                    folder = "Assets/Editor";
            }

            if (!string.IsNullOrWhiteSpace(scriptName))
            {
                // Validate the C# class name
                if (!System.Text.RegularExpressions.Regex.IsMatch(scriptName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    AddSemanticError(validation, "scriptName", $"'{scriptName}' is not a valid C# class name.");

                var predictedPath = Path.Combine(folder, scriptName + ".cs").Replace('\\', '/');
                // script_create refuses an existing file (it never overwrites), so dryRun must not promise an overwrite.
                if (File.Exists(predictedPath))
                    AddSemanticError(validation, "scriptName",
                        $"Script already exists at '{predictedPath}'; script_create never overwrites. Edit it with script_replace/script_append or choose another scriptName/folder.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = !string.IsNullOrWhiteSpace(scriptName)
                    ? Path.Combine(folder, scriptName + ".cs").Replace('\\', '/')
                    : "(unresolved)";

                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create C# Script",
                            ["target"] = predictedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Script",
                            ["path"] = predictedPath,
                            ["className"] = scriptName
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a C# script triggers compilation and Domain Reload. The REST server will be briefly unavailable.",
                    alwaysInclude: true, retryAfterSeconds: 10);
            }
        }
    }
}

// Producer:Betsy
