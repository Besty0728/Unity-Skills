using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Routes REST API requests to skill methods.
    /// </summary>
    public static class SkillRouter
    {
        private static volatile Dictionary<string, SkillInfo> _skills;
        private static volatile bool _initialized;
        private static string _cachedManifest;
        private static readonly object _initLock = new object();

        // Skills that trigger auto-workflow recording (modification operations)
        private static readonly HashSet<string> _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gameobject_create", "gameobject_delete", "gameobject_rename",
            "gameobject_set_transform", "gameobject_duplicate", "gameobject_set_parent",
            "gameobject_set_active", "gameobject_create_batch", "gameobject_delete_batch",
            "gameobject_rename_batch", "gameobject_set_transform_batch",
            "component_add", "component_remove", "component_set_property",
            "component_add_batch", "component_remove_batch", "component_set_property_batch",
            "material_create", "material_assign", "material_set_color", "material_set_texture",
            "material_set_emission", "material_set_float", "material_set_shader",
            "material_create_batch", "material_assign_batch", "material_set_colors_batch",
            "light_create", "light_set_properties", "light_set_enabled",
            "prefab_create", "prefab_instantiate", "prefab_apply", "prefab_unpack",
            "prefab_instantiate_batch",
            "ui_create_canvas", "ui_create_panel", "ui_create_button", "ui_create_text",
            "ui_create_image", "ui_create_inputfield", "ui_create_slider", "ui_create_toggle",
            "ui_create_batch", "ui_set_text", "ui_set_anchor", "ui_set_rect",
            "script_create", "script_delete", "script_create_batch",
            "script_replace", "script_rename", "script_move",
            "terrain_create", "terrain_set_height", "terrain_set_heights_batch", "terrain_paint_texture",
            "asset_import", "asset_delete", "asset_move", "asset_duplicate",
            "asset_set_labels",
            "scene_create", "scene_save",
            "camera_create", "camera_set_properties", "camera_set_culling_mask", "camera_set_orthographic",
            "physics_create_material", "physics_set_material", "physics_set_layer_collision", "physics_set_gravity",
            "timeline_create", "timeline_add_audio_track", "timeline_add_animation_track",
            "timeline_add_activation_track", "timeline_add_control_track", "timeline_add_signal_track",
            "timeline_remove_track", "timeline_add_clip", "timeline_set_duration", "timeline_set_binding",
            "texture_set_import_settings", "model_set_import_settings",
            "audio_set_import_settings", "sprite_set_import_settings",
            "navmesh_add_agent", "navmesh_set_agent", "navmesh_add_obstacle", "navmesh_set_obstacle",
            "navmesh_set_area_cost",
            "shader_create", "shader_delete", "shader_create_urp", "shader_set_global_keyword",
            "cleaner_delete_assets", "cleaner_delete_empty_folders", "cleaner_fix_missing_scripts",
            "scriptableobject_create", "scriptableobject_set", "scriptableobject_set_batch",
            "scriptableobject_delete", "scriptableobject_import_json",
            "event_add_listener", "event_remove_listener", "event_clear_listeners",
            "event_set_listener_state", "event_add_listener_batch", "event_copy_listeners",
            "smart_scene_layout", "smart_reference_bind", "smart_align_to_ground",
            "smart_distribute", "smart_snap_to_grid", "smart_randomize_transform", "smart_replace_objects",
            "console_set_pause_on_error", "console_set_collapse", "console_set_clear_on_play",
            "debug_set_defines",
            "project_add_tag", "project_set_quality_level",
            "optimize_set_static_flags", "optimize_audio_compression", "optimize_set_lod_group",
            "audio_add_source", "audio_set_source_properties", "audio_create_mixer",
            "model_set_animation_clips", "model_set_rig",
            "texture_set_type", "texture_set_platform_settings", "texture_set_sprite_settings",
            "light_add_probe_group", "light_add_reflection_probe",
            "animator_add_transition", "animator_add_state",
            "component_copy", "component_set_enabled",
            "prefab_create_variant"
        };

        // JSON 序列化设置，禁用 Unicode 转义确保中文正确显示
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.Default
        };

        private class SkillInfo
        {
            public string Name;
            public string Description;
            public MethodInfo Method;
            public ParameterInfo[] Parameters;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });

                foreach (var type in allTypes)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var attr = method.GetCustomAttribute<UnitySkillAttribute>();
                        if (attr != null)
                        {
                            var name = attr.Name ?? ToSnakeCase(method.Name);
                            skills[name] = new SkillInfo
                            {
                                Name = name,
                                Description = attr.Description ?? "",
                                Method = method,
                                Parameters = method.GetParameters()
                            };
                        }
                    }
                }

                _skills = skills; // Atomic assignment of fully-built dictionary
                _initialized = true;
                SkillsLogger.Log($"Discovered {_skills.Count} skills");
            }
        }

        public static string GetManifest()
        {
            Initialize();
            if (_cachedManifest != null) return _cachedManifest;

            var manifest = new
            {
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = _skills.Count,
                skills = _skills.Values.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    parameters = s.Parameters.Select(p => new
                    {
                        name = p.Name,
                        type = GetJsonType(p.ParameterType),
                        required = !p.HasDefaultValue,
                        defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                    })
                })
            };
            _cachedManifest = JsonConvert.SerializeObject(manifest, Formatting.Indented, _jsonSettings);
            return _cachedManifest;
        }

        public static string Execute(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Skill '{name}' not found",
                    availableSkills = _skills.Keys.Take(20).ToArray()
                }, _jsonSettings);
            }

            bool autoStartedWorkflow = false;
            try
            {
                var args = string.IsNullOrEmpty(json) ? new JObject() : JObject.Parse(json);
                var ps = skill.Parameters;
                var invoke = new object[ps.Length];

                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (args.TryGetValue(p.Name, StringComparison.OrdinalIgnoreCase, out var token))
                    {
                        invoke[i] = token.ToObject(p.ParameterType);
                    }
                    else if (p.HasDefaultValue)
                    {
                        invoke[i] = p.DefaultValue;
                    }
                    else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null)
                    {
                        invoke[i] = null;
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            status = "error",
                            error = $"Missing required parameter: {p.Name}"
                        }, _jsonSettings);
                    }
                }


                // Transactional Support: Start Undo Group
                UnityEditor.Undo.IncrementCurrentGroup();
                UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                int undoGroup = UnityEditor.Undo.GetCurrentGroup();

                // ========== AUTO WORKFLOW RECORDING ==========
                if (_workflowTrackedSkills.Contains(name) && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // Auto-snapshot target objects BEFORE skill execution for rollback support
                if (WorkflowManager.IsRecording)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // Verbose control
                bool verbose = true; // Default to true if not specified to maintain backward compatibility for direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    verbose = verboseToken.ToObject<bool>();
                }
                
                var result = skill.Method.Invoke(null, invoke);

                // ========== AUTO WORKFLOW END ==========
                if (autoStartedWorkflow)
                {
                    WorkflowManager.EndTask();
                    WorkflowManager.SaveHistory();
                }
                else if (WorkflowManager.IsRecording)
                {
                    WorkflowManager.SaveHistory();
                }
                // ========================================

                // Commit transaction
                UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                // ========== 统一错误响应检测 ==========
                if (result != null)
                {
                    // Use reflection instead of JObject.FromObject to avoid double serialization
                    var resultType = result.GetType();
                    var errorProp = resultType.GetProperty("error");
                    var successProp = resultType.GetProperty("success");

                    if (errorProp != null)
                    {
                        var errorVal = errorProp.GetValue(result);
                        if (errorVal != null)
                        {
                            bool hasSuccessProp = successProp != null;
                            bool successFalse = hasSuccessProp && successProp.GetValue(result) is bool b && !b;

                            // 模式A: { error = "..." } 或 模式B: { success = false, error = "..." }
                            if (!hasSuccessProp || successFalse)
                            {
                                return JsonConvert.SerializeObject(new
                                {
                                    status = "error",
                                    errorCode = "SKILL_ERROR",
                                    error = errorVal.ToString(),
                                    skill = name
                                }, _jsonSettings);
                            }
                        }
                    }
                }
                // =========================================

                if (!verbose && result != null)
                {
                    // "Summary Mode" Logic
                    // 1. Convert result to JToken to inspect it
                    var jsonResult = JToken.FromObject(result);
                    
                    // 2. Check if it's a large Array (> 10 items)
                    if (jsonResult is JArray arr && arr.Count > 10)
                    {
                        var truncatedItems = new JArray();
                        for(int i=0; i<5; i++) truncatedItems.Add(arr[i]);
                        
                        // Return a wrapper object instead of the list
                        // This keeps 'items' clean (same type) while providing meta info
                        var wrapper = new JObject
                        {
                            ["isTruncated"] = true,
                            ["totalCount"] = arr.Count,
                            ["showing"] = 5,
                            ["items"] = truncatedItems,
                            ["hint"] = "Result is truncated. To see all items, pass 'verbose=true' parameter."
                        };
                        
                        return JsonConvert.SerializeObject(new { status = "success", result = wrapper }, _jsonSettings);
                    }
                }
                
                // Full Mode (verbose=true OR small result) - Return original result as is
                return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
            }
            catch (TargetInvocationException ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                // Revert transaction
                UnityEditor.Undo.RevertAllInCurrentGroup();

                var inner = ex.InnerException ?? ex;
                return JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"[Transactional Revert] {inner.Message}"
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                // Revert transaction
                UnityEditor.Undo.RevertAllInCurrentGroup();
                
                return JsonConvert.SerializeObject(new { 
                    status = "error", 
                    error = $"[Transactional Revert] {ex.Message}" 
                }, _jsonSettings);
            }
        }

        public static void Refresh()
        {
            lock (_initLock)
            {
                _initialized = false;
                _skills = null;
                _cachedManifest = null;
            }
            Initialize();
        }

        private static string ToSnakeCase(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1_$2").ToLower();

        private static string GetJsonType(Type t)
        {
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            if (underlying == typeof(string)) return "string";
            if (underlying == typeof(int) || underlying == typeof(long)) return "integer";
            if (underlying == typeof(float) || underlying == typeof(double)) return "number";
            if (underlying == typeof(bool)) return "boolean";
            if (underlying.IsArray) return "array";
            return "object";
        }

        /// <summary>
        /// Auto-snapshot target objects from skill arguments for universal rollback support.
        /// Identifies common target parameters (name, instanceId, path, materialPath, etc.) and snapshots them.
        /// </summary>
        private static void TrySnapshotTargetsFromArgs(JObject args)
        {
            try
            {
                // Try to find target GameObject by common parameter names
                string targetName = null;
                int targetInstanceId = 0;
                string targetPath = null;

                if (args.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken))
                    targetName = nameToken.ToString();
                if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken))
                    targetInstanceId = idToken.ToObject<int>();
                if (args.TryGetValue("path", StringComparison.OrdinalIgnoreCase, out var pathToken))
                    targetPath = pathToken.ToString();

                // Snapshot GameObject if identifiable
                if (!string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
                {
                    var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath);
                    if (go != null)
                    {
                        WorkflowManager.SnapshotObject(go);
                        // Also snapshot Transform which is commonly modified
                        WorkflowManager.SnapshotObject(go.transform);
                        // Snapshot Renderer's material if present
                        var renderer = go.GetComponent<UnityEngine.Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null)
                            WorkflowManager.SnapshotObject(renderer.sharedMaterial);
                    }
                }

                // Snapshot Material asset if materialPath is provided
                if (args.TryGetValue("materialPath", StringComparison.OrdinalIgnoreCase, out var matPathToken))
                {
                    var matPath = matPathToken.ToString();
                    if (!string.IsNullOrEmpty(matPath))
                    {
                        var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(matPath);
                        if (mat != null)
                            WorkflowManager.SnapshotObject(mat);
                    }
                }

                // Snapshot asset if assetPath is provided
                if (args.TryGetValue("assetPath", StringComparison.OrdinalIgnoreCase, out var assetPathToken))
                {
                    var assetPath = assetPathToken.ToString();
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (asset != null)
                            WorkflowManager.SnapshotObject(asset);
                    }
                }

                // Handle child/parent operations
                if (args.TryGetValue("childName", StringComparison.OrdinalIgnoreCase, out var childNameToken))
                {
                    var (childGo, _) = GameObjectFinder.FindOrError(childNameToken.ToString(), 0, null);
                    if (childGo != null)
                        WorkflowManager.SnapshotObject(childGo.transform);
                }

                // Handle batch items - snapshot each target in the batch
                if (args.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var itemsToken))
                {
                    try
                    {
                        var items = itemsToken.ToObject<List<Dictionary<string, object>>>();
                        if (items != null)
                        {
                            foreach (var item in items.Take(50)) // Limit to avoid performance issues
                            {
                                string itemName = item.ContainsKey("name") ? item["name"]?.ToString() : null;
                                int itemId = item.ContainsKey("instanceId") ? Convert.ToInt32(item["instanceId"]) : 0;
                                string itemPath = item.ContainsKey("path") ? item["path"]?.ToString() : null;

                                if (!string.IsNullOrEmpty(itemName) || itemId != 0 || !string.IsNullOrEmpty(itemPath))
                                {
                                    var (itemGo, _) = GameObjectFinder.FindOrError(itemName, itemId, itemPath);
                                    if (itemGo != null)
                                    {
                                        WorkflowManager.SnapshotObject(itemGo);
                                        WorkflowManager.SnapshotObject(itemGo.transform);
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore batch parsing errors */ }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Workflow snapshot failed: {ex.Message}");
            }
        }
    }
}

