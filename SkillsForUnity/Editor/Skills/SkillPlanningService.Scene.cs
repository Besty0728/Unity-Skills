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
        // Scene semantic planner
        // ==================================================================================

        private static void AnalyzeSceneCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");
            AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    scenePath += ".unity";

                if (File.Exists(scenePath))
                    AddWarning(validation, $"Scene already exists at '{scenePath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Scene",
                            ["target"] = scenePath ?? "(unresolved)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = scenePath
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a new scene may briefly interrupt the connection.",
                    alwaysInclude: false, retryAfterSeconds: 3);
            }
        }

        private static void AnalyzeSceneSave(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
                AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var targetPath = !string.IsNullOrWhiteSpace(scenePath) ? scenePath : activeScene.path;

            if (string.IsNullOrWhiteSpace(targetPath))
                AddWarning(validation, "Scene has no path yet. A Save As dialog may appear or a default path will be used.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Save Scene",
                            ["target"] = targetPath ?? activeScene.name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = targetPath,
                            ["sceneName"] = activeScene.name,
                            ["isDirty"] = activeScene.isDirty
                        }
                    });
            }
        }

        private static void AnalyzeSceneLoad(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath) && !File.Exists(scenePath))
                AddSemanticError(validation, "scenePath", $"Scene file not found: {scenePath}");

            var additive = args?["additive"]?.Value<bool>() ?? false;
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (!additive && activeScene.isDirty)
                AddWarning(validation, $"Active scene '{activeScene.name}' has unsaved changes that will be lost.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = additive ? "Load Scene (Additive)" : "Load Scene",
                            ["target"] = scenePath ?? "(unresolved)",
                            ["additive"] = additive
                        }
                    },
                    modify: additive ? null : new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "ActiveScene",
                            ["from"] = activeScene.path,
                            ["to"] = scenePath
                        }
                    });

                if (!additive)
                {
                    plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                        "Loading a scene (non-additive) replaces the current scene and may briefly interrupt the connection.",
                        alwaysInclude: false, retryAfterSeconds: 3);
                }
            }
        }
    }
}

// Producer:Betsy
