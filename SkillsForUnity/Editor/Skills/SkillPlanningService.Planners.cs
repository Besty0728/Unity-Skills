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
        private static void ApplySemanticPlanner(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation,
            IDictionary<string, object> plan)
        {
            switch (skill.Name)
            {
                case "camera_set_properties":
                    AnalyzeEnumSetterParameter<CameraClearFlags>(validation, "clearFlags");
                    break;
                case "light_set_properties":
                    AnalyzeEnumSetterParameter<LightShadows>(validation, "shadows");
                    break;
                case "light_create":
                    AnalyzeRequiredEnumParameter<LightType>(validation, "lightType");
                    AnalyzeRequiredEnumParameter<LightShadows>(validation, "shadows");
                    break;
                case "light_set_enabled_batch":
                    AnalyzeLightSetEnabledBatch(validation);
                    break;
                case "gameobject_create":
                    AnalyzeGameObjectCreate(validation, plan);
                    break;
                case "gameobject_create_batch":
                    AnalyzeGameObjectCreateBatch(validation, plan);
                    break;
                case "gameobject_rename":
                    AnalyzeGameObjectRename(validation, plan);
                    break;
                case "gameobject_rename_batch":
                    AnalyzeGameObjectRenameBatch(validation, plan);
                    break;
                case "gameobject_delete":
                    AnalyzeGameObjectDelete(validation, plan);
                    break;
                case "gameobject_delete_batch":
                    AnalyzeGameObjectDeleteBatch(validation, plan);
                    break;
                case "gameobject_set_parent":
                    AnalyzeGameObjectSetParent(validation, plan);
                    break;
                case "gameobject_set_parent_batch":
                    AnalyzeGameObjectSetParentBatch(validation, plan);
                    break;
                case "component_add":
                    AnalyzeComponentAdd(validation, plan);
                    break;
                case "component_add_batch":
                    AnalyzeComponentAddBatch(validation, plan);
                    break;
                case "component_remove":
                    AnalyzeComponentRemove(validation, plan);
                    break;
                case "component_remove_batch":
                    AnalyzeComponentRemoveBatch(validation, plan);
                    break;
                case "component_set_property":
                    AnalyzeComponentSetProperty(validation, plan);
                    break;
                case "component_set_property_batch":
                    AnalyzeComponentSetPropertyBatch(validation, plan);
                    break;
                case "material_create":
                    AnalyzeMaterialCreate(validation, plan);
                    break;
                case "material_create_batch":
                    AnalyzeMaterialCreateBatch(validation, plan);
                    break;
                case "material_assign":
                    AnalyzeMaterialAssign(validation, plan);
                    break;
                case "material_assign_batch":
                    AnalyzeMaterialAssignBatch(validation, plan);
                    break;
                case "asset_import":
                    AnalyzeAssetImport(validation, plan);
                    break;
                case "asset_import_batch":
                    AnalyzeAssetImportBatch(validation, plan);
                    break;
                case "asset_delete":
                    AnalyzeAssetDelete(validation, plan);
                    break;
                case "asset_delete_batch":
                    AnalyzeAssetDeleteBatch(validation, plan);
                    break;
                case "asset_move":
                    AnalyzeAssetMove(validation, plan);
                    break;
                case "asset_move_batch":
                    AnalyzeAssetMoveBatch(validation, plan);
                    break;
                case "scene_create":
                    AnalyzeSceneCreate(validation, plan);
                    break;
                case "scene_save":
                    AnalyzeSceneSave(validation, plan);
                    break;
                case "scene_load":
                    AnalyzeSceneLoad(validation, plan);
                    break;
                case "prefab_create":
                    AnalyzePrefabCreate(validation, plan);
                    break;
                case "prefab_apply":
                    AnalyzePrefabApply(validation, plan);
                    break;
                case "script_create":
                    AnalyzeScriptCreate(validation, plan);
                    break;
                case "timeline_add_audio_track":
                case "timeline_add_animation_track":
                case "timeline_add_activation_track":
                case "timeline_add_control_track":
                case "timeline_add_signal_track":
                case "timeline_remove_track":
                case "timeline_list_tracks":
                case "timeline_add_clip":
                case "timeline_set_duration":
                case "timeline_play":
                case "timeline_set_binding":
                    AnalyzeTimelineSceneLocatorSkill(skill.Name, validation);
                    break;
                case "smart_scene_layout":
                case "smart_align_to_ground":
                case "smart_snap_to_grid":
                case "smart_randomize_transform":
                case "smart_replace_objects":
                    AnalyzeRequiresEditorSelection(validation, minimumCount: 1, requireRectTransform: false);
                    break;
                case "smart_distribute":
                    AnalyzeRequiresEditorSelection(validation, minimumCount: 3, requireRectTransform: false);
                    break;
                case "ui_align_selected":
                    AnalyzeRequiresEditorSelection(validation, minimumCount: 2, requireRectTransform: true);
                    break;
                case "ui_distribute_selected":
                    AnalyzeRequiresEditorSelection(validation, minimumCount: 3, requireRectTransform: true);
                    break;
            }

            // Must go last, not first: the per-skill analyzers above name the parameter that genuinely can't be resolved,
            // and this generic check actively yields when one of them already covers the same target (see TokenAlreadyReported).
            // If placed first, an empty request body would get both verdicts at once, since nothing would yet be reported to yield to.
            ApplyRequiredInputGroups(skill, validation);
        }

        /// <summary>
        /// light_set_enabled_batch refuses an item without <c>enabled</c> (MISSING_PARAM) rather than guessing a default,
        /// so dryRun lists each such item as a missing parameter, <c>items[i].enabled</c>. Validation only, like the enum
        /// analyzers above: a correct body gets exactly the response it got before, and malformed items are left to the
        /// executor's own parse error.
        /// </summary>
        private static void AnalyzeLightSetEnabledBatch(SkillRouter.ParameterValidationResult validation)
        {
            var itemsJson = GetStringArg(validation?.Args, "items");
            if (string.IsNullOrWhiteSpace(itemsJson))
                return;

            JArray items;
            try { items = JArray.Parse(itemsJson); }
            catch { return; }

            for (int i = 0; i < items.Count; i++)
            {
                var missing = $"items[{i}].enabled";
                // A dryRun runs the planners twice on one validation (ValidateParameters, then BuildPlanData).
                if (items[i] is JObject item &&
                    (!item.TryGetValue("enabled", StringComparison.OrdinalIgnoreCase, out var enabled) || enabled.Type == JTokenType.Null) &&
                    !validation.MissingParams.Contains(missing))
                {
                    validation.MissingParams.Add(missing);
                }
            }
        }

        private static void AnalyzeTimelineSceneLocatorSkill(string skillName, SkillRouter.ParameterValidationResult validation)
        {
            var args = validation?.Args;
            if (args == null)
                return;

            var path = GetStringArg(args, "path");
            if (!string.IsNullOrWhiteSpace(path) && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AddSemanticError(
                    validation,
                    "path",
                    $"The path parameter of {skillName} is a scene hierarchy path, not an Assets resource path. Use the name, instanceId or hierarchy path of the Timeline GameObject in the scene instead.");
            }

            var name = GetStringArg(args, "name");
            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AddSemanticError(
                    validation,
                    "name",
                    $"{skillName} targets a PlayableDirector GameObject in the scene; it does not accept an Assets resource path as name.");
            }
        }
    }
}

// Producer:Betsy
