using System.Collections.Generic;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Localization for UnitySkills.
    /// Persists language preference across Domain Reload.
    /// </summary>
    public static class Localization
    {
        public enum Language { English, Chinese }
        
        private const string PREF_LANGUAGE = "UnitySkills_Language";
        private static bool _initialized = false;
        private static Language _current = Language.English;
        
        public static Language Current
        {
            get
            {
                if (!_initialized)
                {
                    // Restore from EditorPrefs on first access
                    _current = (Language)EditorPrefs.GetInt(PREF_LANGUAGE, (int)Language.English);
                    _initialized = true;
                }
                return _current;
            }
            set
            {
                _current = value;
                // Persist to EditorPrefs
                EditorPrefs.SetInt(PREF_LANGUAGE, (int)value);
            }
        }

        public static string Get(string key)
        {
            if (Current == Language.Chinese && _chinese.TryGetValue(key, out var cn))
                return cn;
            if (_english.TryGetValue(key, out var en))
                return en;
            return key;
        }

        // UI Strings
        private static readonly Dictionary<string, string> _english = new Dictionary<string, string>
        {
            // Window
            {"window_title", "UnitySkills"},
            {"server_running", "● Server Running"},
            {"server_stopped", "● Server Stopped"},
            {"start_server", "Start Server"},
            {"stop_server", "Stop Server"},
            {"test_skill", "Test Skill"},
            {"skill_name", "Skill Name"},
            {"parameters_json", "Parameters (JSON)"},
            {"execute_skill", "Execute Skill"},
            {"result", "Result"},
            {"available_skills", "Available Skills"},
            {"refresh", "Refresh"},
            {"total_skills", "Total: {0} skills in {1} categories"},
            {"use", "Use"},
            {"language", "Language"},
            
            // Skill Configuration
            {"skill_config", "AI Skill Configuration"},
            {"claude_code", "Claude Code"},
            {"antigravity", "Antigravity"},
            {"gemini_cli", "Gemini CLI"},
            {"install_project", "Install to Project"},
            {"install_global", "Install Global"},
            {"installed", "✓ Installed"},
            {"not_installed", "Not installed"},
            {"install_success", "Skill installed successfully!"},
            {"install_failed", "Installation failed: {0}"},
            {"update", "Update"},
            {"update_success", "Skill updated successfully!"},
            {"update_failed", "Update failed: {0}"},
            {"uninstall", "Uninstall"},
            {"uninstall_success", "Skill uninstalled successfully!"},
            {"uninstall_failed", "Uninstall failed: {0}"},
            {"uninstall_confirm", "Are you sure you want to uninstall {0}?"},
            {"gemini_enable_hint", "\n\nNote: Enable experimental.skills in Gemini CLI /settings"},
            
            // Server stats
            {"server_stats", "Live Statistics"},
            {"queued_requests", "Queued Requests"},
            {"total_processed", "Total Processed"},
            {"architecture", "Architecture"},
            {"auto_restart", "Auto-restart after compile"},
            {"auto_restart_hint", "Server will automatically restart after Unity recompiles scripts"},
            {"timeout_unit", "min"},

            // Skill descriptions
            {"scene_create", "Create a new empty scene"},
            {"scene_load", "Load an existing scene"},
            {"scene_save", "Save the current scene"},
            {"scene_get_info", "Get current scene information"},
            {"scene_get_hierarchy", "Get scene hierarchy tree"},
            {"scene_screenshot", "Capture a screenshot of the scene view"},
            {"gameobject_create", "Create a new GameObject"},
            {"gameobject_delete", "Delete a GameObject by name or instance ID"},
            {"gameobject_find", "Find GameObjects by name, tag, or component"},
            {"gameobject_set_transform", "Set position, rotation, or scale of a GameObject"},
            {"gameobject_duplicate", "Duplicate a GameObject"},
            {"gameobject_set_parent", "Set the parent of a GameObject"},
            {"component_add", "Add a component to a GameObject"},
            {"component_remove", "Remove a component from a GameObject"},
            {"component_list", "List all components on a GameObject"},
            {"component_set_property", "Set a property on a component"},
            {"component_get_properties", "Get all properties of a component"},
            {"material_create", "Create a new material"},
            {"material_set_color", "Set a color property on a material or renderer"},
            {"material_set_texture", "Set a texture on a material"},
            {"material_assign", "Assign a material asset to a renderer"},
            {"material_set_float", "Set a float property on a material"},
            {"asset_import", "Import an asset from external path"},
            {"asset_delete", "Delete an asset"},
            {"asset_move", "Move or rename an asset"},
            {"asset_duplicate", "Duplicate an asset"},
            {"asset_find", "Find assets by name, type, or label"},
            {"asset_create_folder", "Create a new folder in Assets"},
            {"asset_refresh", "Refresh the Asset Database"},
            {"asset_get_info", "Get information about an asset"},
            {"editor_play", "Enter play mode"},
            {"editor_stop", "Exit play mode"},
            {"editor_pause", "Pause/unpause play mode"},
            {"editor_select", "Select a GameObject"},
            {"editor_get_selection", "Get currently selected objects"},
            {"editor_undo", "Undo the last action"},
            {"editor_redo", "Redo the last undone action"},
            {"editor_get_state", "Get current editor state"},
            {"editor_execute_menu", "Execute a Unity menu item"},
            {"editor_get_tags", "Get all available tags"},
            {"debug_get_logs", "Get console logs (filtered by type)"},

            // Cleaner Skills
            {"cleaner_find_unused_assets", "Find potentially unused assets of a specific type"},
            {"cleaner_find_duplicates", "Find duplicate files by content hash"},
            {"cleaner_find_missing_references", "Find components with missing script or asset references"},
            {"cleaner_delete_assets", "Delete specified assets safe with preview"},
            {"cleaner_get_asset_usage", "Find what objects reference a specific asset"},

            // Debug Enhance Skills
            {"debug_log", "Write a message to the Unity console"},
            {"editor_set_pause_on_error", "Enable or disable 'Error Pause' in Play mode"},
            
            // Perception Skills (NextGen)
            {"scene_summarize", "Get a structured summary of the current scene"},
            {"hierarchy_describe", "Get a text tree of the scene hierarchy"},
            {"script_analyze", "Analyze a MonoBehaviour script's public API"},
            {"scene_spatial_query", "Find objects within a radius of a point or near another object"},
            {"scene_materials", "Get an overview of all materials and shaders in the scene"},
            {"scene_context", "Generate a comprehensive scene snapshot for AI coding assistance"},
            {"scene_export_report", "Export complete scene structure and dependency report as markdown"},
            {"scene_dependency_analyze", "Analyze object dependency graph and impact of changes"},

            // Smart Skills
            {"smart_scene_query", "Find objects based on component property values (SQL-like)"},
            {"smart_scene_layout", "Organize selected objects into a layout"},
            {"smart_reference_bind", "Auto-fill a List/Array field with objects matching criteria"},

            // Terrain Skills
            {"terrain_create", "Create a new Terrain with TerrainData asset"},
            {"terrain_get_info", "Get terrain information including size, resolution, and layers"},
            {"terrain_get_height", "Get terrain height at world position"},
            {"terrain_set_height", "Set terrain height at normalized coordinates (0-1)"},
            {"terrain_set_heights_batch", "Set terrain heights in a rectangular region"},
            {"terrain_paint_texture", "Paint terrain texture layer"},

            // Workflow Skills
            {"bookmark_set", "Save current selection and scene view position as a bookmark"},
            {"bookmark_goto", "Restore selection and scene view from a bookmark"},
            {"bookmark_list", "List all saved bookmarks"},
            {"bookmark_delete", "Delete a bookmark"},
            {"history_undo", "Undo the last operation"},
            {"history_redo", "Redo the last undone operation"},
            {"history_get_current", "Get the name of the current undo group"},
            
            // UI Skills (Additional)
            {"ui_set_anchor", "Set anchor preset for a UI element"},
            {"ui_set_rect", "Set RectTransform size, position, and padding"},
            {"ui_layout_children", "Arrange child UI elements in a layout"},
            {"ui_align_selected", "Align selected UI elements"},
            {"ui_distribute_selected", "Distribute selected UI elements evenly"},
            
            // Validation Skills
            // Already partially present, ensuring completeness if needed

            {"editor_get_layers", "Get all available layers"},
            {"prefab_create", "Create a prefab from a GameObject"},
            {"prefab_instantiate", "Instantiate a prefab in the scene"},
            {"prefab_apply", "Apply changes from instance to prefab"},
            {"prefab_unpack", "Unpack a prefab instance"},
            {"script_create", "Create a new C# script"},
            {"script_read", "Read the contents of a script"},
            {"script_delete", "Delete a script file"},
            {"script_find_in_file", "Search for pattern in scripts"},
            {"script_append", "Append content to a script"},
            {"console_start_capture", "Start capturing console logs"},
            {"console_stop_capture", "Stop capturing console logs"},
            {"console_get_logs", "Get captured console logs"},
            {"console_clear", "Clear the Unity console"},
            {"console_log", "Write a message to the console"},
            {"scriptableobject_create", "Create a new ScriptableObject asset"},
            {"scriptableobject_get", "Get properties of a ScriptableObject"},
            {"scriptableobject_set", "Set a field/property on a ScriptableObject"},
            {"scriptableobject_list_types", "List available ScriptableObject types"},
            {"scriptableobject_duplicate", "Duplicate a ScriptableObject asset"},
            {"shader_create", "Create a new shader file"},
            {"shader_read", "Read shader source code"},
            {"shader_list", "List all shaders in project"},
            {"shader_get_properties", "Get properties of a shader"},
            {"shader_find", "Find shaders by name"},
            {"shader_delete", "Delete a shader file"},
            {"test_run", "Run Unity tests (returns job ID for polling)"},
            {"test_get_result", "Get the result of a test run"},
            {"test_list", "List available tests"},
            {"test_cancel", "Cancel a running test"},

            // Package Skills
            {"package_list", "List all installed packages"},
            {"package_check", "Check if a package is installed"},
            {"package_install", "Install a package"},
            {"package_remove", "Remove an installed package"},
            {"package_refresh", "Refresh the installed package list cache"},
            {"package_install_cinemachine", "Install Cinemachine (version 2 or 3)"},
            {"package_get_cinemachine_status", "Get Cinemachine installation status"},
        
            // Auto-generated: complete skill descriptions
            {"animator_add_parameter", "Add a parameter to an Animator Controller"},
            {"animator_assign_controller", "Assign an Animator Controller to a GameObject (supports name/instanceId/path)"},
            {"animator_create_controller", "Create a new Animator Controller"},
            {"animator_get_info", "Get Animator component information (supports name/instanceId/path)"},
            {"animator_get_parameters", "Get all parameters from an Animator Controller"},
            {"animator_list_states", "List all states in an Animator Controller layer"},
            {"animator_play", "Play an animation state on a GameObject (supports name/instanceId/path)"},
            {"animator_set_parameter", "Set a parameter value on a GameObject's Animator (supports name/instanceId/path)"},
            {"asset_delete_batch", "Delete multiple assets"},
            {"asset_import_batch", "Import multiple assets"},
            {"asset_move_batch", "Move multiple assets"},
            {"audio_get_settings", "Get audio import settings for an audio asset"},
            {"audio_set_settings", "Set audio import settings"},
            {"audio_set_settings_batch", "Set audio import settings for multiple audio files"},
            {"camera_align_view_to_object", "Align Scene View camera to look at an object."},
            {"camera_get_info", "Get Scene View camera position and rotation."},
            {"camera_look_at", "Focus Scene View camera on a point."},
            {"camera_set_transform", "Set Scene View camera position/rotation manually."},
            {"cinemachine_add_component", "Add a Cinemachine component (e.g., OrbitalFollow)."},
            {"cinemachine_add_extension", "Add a CinemachineExtension"},
            {"cinemachine_create_clear_shot", "Create a Cinemachine Clear Shot Camera."},
            {"cinemachine_create_mixing_camera", "Create a Cinemachine Mixing Camera."},
            {"cinemachine_create_state_driven_camera", "Create a Cinemachine State Driven Camera"},
            {"cinemachine_create_target_group", "Create a CinemachineTargetGroup"},
            {"cinemachine_create_vcam", "Create a new Virtual Camera"},
            {"cinemachine_get_brain_info", "Get info about the Active Camera and Blend."},
            {"cinemachine_impulse_generate", "Trigger an Impulse"},
            {"cinemachine_inspect_vcam", "Deeply inspect a VCam, returning fields and tooltips."},
            {"cinemachine_list_components", "List all available Cinemachine component names."},
            {"cinemachine_mixing_camera_set_weight", "Set weight of a child camera in a Mixing Camera"},
            {"cinemachine_remove_extension", "Remove a CinemachineExtension"},
            {"cinemachine_set_active", "Force activation of a VCam (SOLO) by setting highest priority."},
            {"cinemachine_set_component", "Switch VCam pipeline component (Body/Aim/Noise)"},
            {"cinemachine_set_lens", "Quickly configure Lens settings (FOV, Near, Far, OrthoSize)."},
            {"cinemachine_set_noise", "Configure Noise settings (Basic Multi Channel Perlin)."},
            {"cinemachine_set_spline", "Set Spline for VCam Body"},
            {"cinemachine_set_targets", "Set Follow and LookAt targets."},
            {"cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components."},
            {"cinemachine_state_driven_camera_add_instruction", "Add instruction to State Driven Camera"},
            {"cinemachine_target_group_add_member", "Add/Update member in TargetGroup"},
            {"cinemachine_target_group_remove_member", "Remove member from TargetGroup"},
            {"component_add_batch", "Add components to multiple GameObjects"},
            {"component_remove_batch", "Remove components from multiple GameObjects"},
            {"component_set_property_batch", "Set properties on multiple components (Efficient)"},
            {"create_cube", "Create a cube at the specified position"},
            {"create_sphere", "Create a sphere at the specified position"},
            {"debug_check_compilation", "Check if Unity is currently compiling scripts."},
            {"debug_force_recompile", "Force script recompilation."},
            {"debug_get_errors", "Get only active errors and exceptions from the console logs."},
            {"debug_get_system_info", "Get Editor and System capabilities."},
            {"delete_object", "Delete a GameObject by name"},
            {"editor_get_context", "Get full editor context - selected GameObjects, selected assets, active scene..."},
            {"event_add_listener", "Add a persistent listener to a UnityEvent (Editor time)"},
            {"event_get_listeners", "Get persistent listeners of a UnityEvent"},
            {"event_invoke", "Invoke a UnityEvent explicitly (Runtime only)"},
            {"event_remove_listener", "Remove a persistent listener by index"},
            {"find_objects_by_name", "Find all GameObjects containing a name (param: nameContains)"},
            {"gameobject_create_batch", "Create multiple GameObjects in one call (Efficient)"},
            {"gameobject_delete_batch", "Delete multiple GameObjects"},
            {"gameobject_duplicate_batch", "Duplicate multiple GameObjects in one call (Efficient)"},
            {"gameobject_get_info", "Get detailed info about a GameObject (supports name/instanceId/path)"},
            {"gameobject_rename", "Rename a GameObject (supports name/instanceId/path)"},
            {"gameobject_rename_batch", "Rename multiple GameObjects in one call (Efficient)"},
            {"gameobject_set_active", "Enable or disable a GameObject (supports name/instanceId/path)"},
            {"gameobject_set_active_batch", "Enable or disable multiple GameObjects"},
            {"gameobject_set_layer_batch", "Set layer for multiple GameObjects"},
            {"gameobject_set_parent_batch", "Set parent for multiple GameObjects"},
            {"gameobject_set_tag_batch", "Set tag for multiple GameObjects"},
            {"gameobject_set_transform_batch", "Set transform properties for multiple objects (Efficient)"},
            {"get_scene_info", "Get current scene information"},
            {"light_create", "Create a new light (Directional, Point, Spot, Area)"},
            {"light_find_all", "Find all lights in the scene"},
            {"light_get_info", "Get information about a light (supports name/instanceId/path)"},
            {"light_set_enabled", "Enable or disable a light (supports name/instanceId/path)"},
            {"light_set_enabled_batch", "Enable/disable multiple lights in one call (Efficient)"},
            {"light_set_properties", "Set light properties (supports name/instanceId/path)"},
            {"light_set_properties_batch", "Set properties for multiple lights in one call (Efficient)"},
            {"material_assign_batch", "Assign materials to multiple objects (Efficient)"},
            {"material_create_batch", "Create multiple materials (Efficient)"},
            {"material_duplicate", "Duplicate an existing material"},
            {"material_get_keywords", "Get all enabled shader keywords on a material"},
            {"material_get_properties", "Get all properties of a material (colors, floats, textures, keywords)"},
            {"material_set_colors_batch", "Set colors on multiple GameObjects in a single call"},
            {"material_set_emission", "Set emission color with HDR intensity and auto-enable emission"},
            {"material_set_emission_batch", "Set emission on multiple objects (Efficient)"},
            {"material_set_gi_flags", "Set global illumination flags (None, RealtimeEmissive, BakedEmissive, Emissiv..."},
            {"material_set_int", "Set an integer property on a material"},
            {"material_set_keyword", "Enable or disable a shader keyword (e.g., _EMISSION, _NORMALMAP, _METALLICGLO..."},
            {"material_set_render_queue", "Set material render queue (-1 for shader default, 2000=Geometry, 2450=AlphaTe..."},
            {"material_set_shader", "Change the shader of a material"},
            {"material_set_texture_offset", "Set texture offset (tiling position)"},
            {"material_set_texture_scale", "Set texture scale (tiling)"},
            {"material_set_vector", "Set a vector4 property on a material"},
            {"model_get_settings", "Get model import settings for a 3D model asset (FBX, OBJ, etc)"},
            {"model_set_settings", "Set model import settings"},
            {"model_set_settings_batch", "Set model import settings for multiple 3D models"},
            {"navmesh_bake", "Bake the NavMesh (Synchronous)"},
            {"navmesh_calculate_path", "Calculate a path between two points"},
            {"navmesh_clear", "Clear the NavMesh data"},
            {"optimize_mesh_compression", "Set mesh compression for 3D models"},
            {"optimize_textures", "Optimize texture settings (maxSize, compression)"},
            {"physics_check_overlap", "Check for colliders in a sphere"},
            {"physics_get_gravity", "Get global gravity setting"},
            {"physics_raycast", "Cast a ray and get hit info"},
            {"physics_set_gravity", "Set global gravity setting"},
            {"prefab_apply_overrides", "Apply all overrides from instance to source prefab asset"},
            {"prefab_get_overrides", "Get list of property overrides on a prefab instance"},
            {"prefab_instantiate_batch", "Instantiate multiple prefabs (Efficient)"},
            {"prefab_revert_overrides", "Revert all overrides on a prefab instance back to prefab values"},
            {"profiler_get_stats", "Get performance statistics (FPS, Memory, Batches)"},
            {"project_get_info", "Get project information including render pipeline, Unity version, and settings"},
            {"project_get_quality_settings", "Get current quality settings"},
            {"project_get_render_pipeline", "Get current render pipeline type and recommended shaders"},
            {"project_list_shaders", "List all available shaders in the project"},
            {"scene_get_loaded", "Get list of all currently loaded scenes"},
            {"scene_set_active", "Set the active scene (for multi-scene editing)"},
            {"scene_unload", "Unload a loaded scene (additive)"},
            {"script_create_batch", "Create multiple scripts (Efficient)"},
            {"set_object_position", "Set position of a GameObject"},
            {"set_object_rotation", "Set rotation of a GameObject (Euler angles)"},
            {"set_object_scale", "Set scale of a GameObject"},
            {"terrain_add_hill", "Add a smooth hill to the terrain at normalized position with radius and height"},
            {"terrain_flatten", "Flatten terrain to a specific height in a region"},
            {"terrain_generate_perlin", "Generate terrain using Perlin noise for natural-looking landscapes"},
            {"terrain_smooth", "Smooth terrain heights in a region to reduce sharp edges"},
            {"texture_get_settings", "Get texture import settings for an image asset"},
            {"texture_set_settings", "Set texture import settings"},
            {"texture_set_settings_batch", "Set texture import settings for multiple images"},
            {"timeline_add_animation_track", "Add an Animation track to a Timeline, optionally binding an object"},
            {"timeline_add_audio_track", "Add an Audio track to a Timeline"},
            {"timeline_create", "Create a new Timeline asset and Director instance"},
            {"ui_create_batch", "Create multiple UI elements (Efficient)"},
            {"ui_create_button", "Create a Button UI element"},
            {"ui_create_canvas", "Create a new Canvas"},
            {"ui_create_image", "Create an Image UI element"},
            {"ui_create_inputfield", "Create an InputField UI element"},
            {"ui_create_panel", "Create a Panel UI element"},
            {"ui_create_slider", "Create a Slider UI element"},
            {"ui_create_text", "Create a Text UI element"},
            {"ui_create_toggle", "Create a Toggle UI element"},
            {"ui_find_all", "Find all UI elements in the scene"},
            {"ui_set_text", "Set text content on a UI Text element (supports name/instanceId/path)"},
            {"validate_cleanup_empty_folders", "Find and optionally delete empty folders"},
            {"validate_find_missing_scripts", "Find all GameObjects with missing scripts"},
            {"validate_find_unused_assets", "Find potentially unused assets"},
            {"validate_fix_missing_scripts", "Remove missing script components from GameObjects"},
            {"validate_project_structure", "Get overview of project structure"},
            {"validate_scene", "Validate current scene for common issues"},
            {"validate_texture_sizes", "Find textures that may need optimization"},
            {"workflow_delete_task", "Delete a task from history (does not revert changes)"},
            {"workflow_list", "List persistent workflow history"},
            {"workflow_redo_task", "Redo a previously undone task (restore changes)"},
            {"workflow_revert_task", "Alias for workflow_undo_task (deprecated, use workflow_undo_task instead)"},
            {"workflow_session_end", "End the current session and save all tracked changes."},
            {"workflow_session_list", "List all recorded sessions (conversation-level history)"},
            {"workflow_session_start", "Start a new session (conversation-level)"},
            {"workflow_session_status", "Get the current session status"},
            {"workflow_session_undo", "Undo all changes made during a specific session (conversation-level undo)"},
            {"workflow_snapshot_created", "Record a newly created object for undo tracking"},
            {"workflow_snapshot_object", "Manually snapshot an object before modification"},
            {"workflow_task_end", "End the current persistent workflow task"},
            {"workflow_task_start", "Start a new persistent workflow task/session"},
            {"workflow_undo_task", "Undo changes from a specific task (restore to previous state)"},
            {"workflow_undone_list", "List all undone tasks that can be redone"},

            // Profiler Skills
            {"profiler_get_memory", "Get memory usage overview (total allocated, reserved, mono heap)"},
            {"profiler_get_runtime_memory", "Get top N objects by runtime memory usage in the scene"},
            {"profiler_get_texture_memory", "Get memory usage of all loaded textures"},
            {"profiler_get_mesh_memory", "Get memory usage of all loaded meshes"},
            {"profiler_get_material_memory", "Get memory usage of all loaded materials"},
            {"profiler_get_audio_memory", "Get memory usage of all loaded AudioClips"},
            {"profiler_get_object_count", "Count all loaded objects grouped by type"},
            {"profiler_get_rendering_stats", "Get rendering statistics (batches, triangles, vertices)"},
            {"profiler_get_asset_bundle_stats", "Get information about all loaded AssetBundles"},

            // Optimization Skills
            {"optimize_analyze_scene", "Analyze scene for performance bottlenecks"},
            {"optimize_find_large_assets", "Find assets exceeding a size threshold"},
            {"optimize_set_static_flags", "Set static flags on GameObjects"},
            {"optimize_get_static_flags", "Get static flags of a GameObject"},
            {"optimize_audio_compression", "Batch set audio compression settings"},
            {"optimize_find_duplicate_materials", "Find materials with identical properties"},
            {"optimize_analyze_overdraw", "Analyze transparent objects that may cause overdraw"},
            {"optimize_set_lod_group", "Add or configure LOD Group"},

            // Audio Skills (new)
            {"audio_find_clips", "Search for AudioClip assets in the project"},
            {"audio_get_clip_info", "Get detailed information about an AudioClip"},
            {"audio_add_source", "Add an AudioSource component to a GameObject"},
            {"audio_get_source_info", "Get AudioSource configuration"},
            {"audio_set_source_properties", "Set AudioSource properties"},
            {"audio_find_sources_in_scene", "Find all AudioSource components in the scene"},
            {"audio_create_mixer", "Create a new AudioMixer asset"},

            // Model Skills (new)
            {"model_find_assets", "Search for model assets in the project"},
            {"model_get_mesh_info", "Get detailed Mesh information (vertices, triangles)"},
            {"model_get_materials_info", "Get material mapping for a model asset"},
            {"model_get_animations_info", "Get animation clip information from a model"},
            {"model_set_animation_clips", "Configure animation clip splitting"},
            {"model_get_rig_info", "Get rig/skeleton binding information"},
            {"model_set_rig", "Set rig/skeleton binding type"},

            // Texture Skills (new)
            {"texture_find_assets", "Search for texture assets in the project"},
            {"texture_get_info", "Get detailed texture information (dimensions, format, memory)"},
            {"texture_set_type", "Set texture type"},
            {"texture_set_platform_settings", "Set platform-specific texture settings"},
            {"texture_get_platform_settings", "Get platform-specific texture settings"},
            {"texture_set_sprite_settings", "Configure Sprite-specific settings"},
            {"texture_find_by_size", "Find textures by dimension range"},

            // Light Skills (new)
            {"light_add_probe_group", "Add a Light Probe Group to a GameObject"},
            {"light_add_reflection_probe", "Create a Reflection Probe at a position"},
            {"light_get_lightmap_settings", "Get Lightmap baking settings"},

            // Package Skills (new)
            {"package_search", "Search for packages in the Unity Registry"},
            {"package_get_dependencies", "Get dependency list for an installed package"},
            {"package_get_versions", "Get all available versions for a package"},

            // Validation Skills (new)
            {"validate_missing_references", "Find null/missing object references on components"},
            {"validate_mesh_collider_convex", "Find non-convex MeshColliders"},
            {"validate_shader_errors", "Find shaders with compilation errors"},

            // Animator Skills (new)
            {"animator_add_state", "Add a state to an Animator Controller layer"},
            {"animator_add_transition", "Add a transition between two states"},

            // Component Skills (new)
            {"component_copy", "Copy a component from one GameObject to another"},
            {"component_set_enabled", "Enable or disable a Behaviour component"},

            // Perception Skills (new)
            {"scene_tag_layer_stats", "Get Tag/Layer usage stats and find potential issues"},
            {"scene_performance_hints", "Diagnose scene performance issues with actionable suggestions"},

            // Prefab Skills (new)
            {"prefab_create_variant", "Create a prefab variant from an existing prefab"},
            {"prefab_find_instances", "Find all instances of a prefab in the scene"},

            // Scene Skills (new)
            {"scene_find_objects", "Search GameObjects by name pattern, tag, or component type"},

            // Shader Skills (new)
            {"shader_check_errors", "Check shader for compilation errors"},
            {"shader_get_keywords", "Get shader keyword list"},
            {"shader_get_variant_count", "Get shader variant count for performance analysis"},
            {"shader_create_urp", "Create a URP shader from template"},
            {"shader_set_global_keyword", "Enable or disable a global shader keyword"},

            // AssetImport Skills
            {"asset_reimport", "Force reimport of an asset"},
            {"asset_reimport_batch", "Reimport multiple assets matching a pattern"},
            {"texture_get_import_settings", "Get texture import settings"},
            {"texture_set_import_settings", "Set texture import settings"},
            {"model_get_import_settings", "Get model import settings"},
            {"model_set_import_settings", "Set model import settings"},
            {"audio_get_import_settings", "Get audio import settings"},
            {"audio_set_import_settings", "Set audio import settings"},
            {"sprite_set_import_settings", "Set sprite import settings"},
            {"asset_get_labels", "Get asset labels"},
            {"asset_set_labels", "Set asset labels"},

            // Camera Skills (new)
            {"camera_create", "Create a new Camera"},
            {"camera_set_properties", "Set camera properties"},
            {"camera_get_properties", "Get camera properties"},
            {"camera_list", "List all cameras in the scene"},
            {"camera_screenshot", "Capture a screenshot from a camera"},
            {"camera_set_culling_mask", "Set camera culling mask"},
            {"camera_set_orthographic", "Set camera orthographic mode"},

            // Cleaner Skills (new)
            {"cleaner_find_empty_folders", "Find empty folders in the project"},
            {"cleaner_delete_empty_folders", "Delete empty folders"},
            {"cleaner_find_large_assets", "Find large assets in the project"},
            {"cleaner_fix_missing_scripts", "Remove missing script components"},
            {"cleaner_get_dependency_tree", "Get asset dependency tree"},

            // Console Skills (new)
            {"console_export", "Export console logs to file"},
            {"console_get_stats", "Get console log statistics"},
            {"console_set_pause_on_error", "Set pause on error"},
            {"console_set_collapse", "Set console collapse mode"},
            {"console_set_clear_on_play", "Set clear on play"},

            // Debug Skills (new)
            {"debug_get_memory_info", "Get detailed memory information"},
            {"debug_get_stack_trace", "Get stack trace of last error"},
            {"debug_get_assembly_info", "Get loaded assembly information"},
            {"debug_get_defines", "Get scripting define symbols"},
            {"debug_set_defines", "Set scripting define symbols"},

            // Event Skills (new)
            {"event_add_listener_batch", "Batch add persistent listeners"},
            {"event_clear_listeners", "Clear all persistent listeners"},
            {"event_copy_listeners", "Copy listeners between events"},
            {"event_get_listener_count", "Get listener count"},
            {"event_list_events", "List all UnityEvents on a component"},
            {"event_set_listener_state", "Set listener enabled state"},

            // NavMesh Skills (new)
            {"navmesh_add_agent", "Add NavMeshAgent component"},
            {"navmesh_set_agent", "Set NavMeshAgent properties"},
            {"navmesh_add_obstacle", "Add NavMeshObstacle component"},
            {"navmesh_set_obstacle", "Set NavMeshObstacle properties"},
            {"navmesh_set_area_cost", "Set NavMesh area cost"},
            {"navmesh_get_settings", "Get NavMesh settings"},
            {"navmesh_sample_position", "Sample nearest point on NavMesh"},

            // Physics Skills (new)
            {"physics_create_material", "Create a PhysicMaterial"},
            {"physics_set_material", "Assign PhysicMaterial to a collider"},
            {"physics_set_layer_collision", "Set layer collision matrix"},
            {"physics_get_layer_collision", "Get layer collision matrix"},
            {"physics_raycast_all", "Raycast and get all hits"},
            {"physics_spherecast", "Cast a sphere and get hit info"},
            {"physics_boxcast", "Cast a box and get hit info"},
            {"physics_overlap_box", "Check for colliders in a box"},

            // Project Skills (new)
            {"project_add_tag", "Add a new tag"},
            {"project_set_quality_level", "Set quality level"},
            {"project_get_tags", "Get all project tags"},
            {"project_get_layers", "Get all project layers"},
            {"project_get_packages", "Get installed packages"},
            {"project_get_build_settings", "Get build settings"},
            {"project_get_player_settings", "Get player settings"},

            // Script Skills (new)
            {"script_replace", "Replace content in a script"},
            {"script_rename", "Rename a script file"},
            {"script_move", "Move a script file"},
            {"script_list", "List scripts in the project"},
            {"script_get_info", "Get script information"},

            // ScriptableObject Skills (new)
            {"scriptableobject_delete", "Delete a ScriptableObject asset"},
            {"scriptableobject_find", "Find ScriptableObject assets"},
            {"scriptableobject_set_batch", "Batch set ScriptableObject fields"},
            {"scriptableobject_export_json", "Export ScriptableObject to JSON"},
            {"scriptableobject_import_json", "Import ScriptableObject from JSON"},

            // Smart Skills (new)
            {"smart_align_to_ground", "Align objects to ground surface"},
            {"smart_distribute", "Distribute objects evenly"},
            {"smart_snap_to_grid", "Snap objects to grid"},
            {"smart_randomize_transform", "Randomize object transforms"},
            {"smart_replace_objects", "Replace objects with prefab"},
            {"smart_scene_query_spatial", "Spatial query for nearby objects"},
            {"smart_select_by_component", "Select objects by component type"},

            // Test Skills (new)
            {"test_create_editmode", "Create an EditMode test"},
            {"test_create_playmode", "Create a PlayMode test"},
            {"test_get_last_result", "Get last test result"},
            {"test_get_summary", "Get test summary"},
            {"test_list_categories", "List test categories"},
            {"test_run_by_name", "Run a specific test by name"},

            // Timeline Skills (new)
            {"timeline_add_activation_track", "Add an Activation track"},
            {"timeline_add_control_track", "Add a Control track"},
            {"timeline_add_signal_track", "Add a Signal track"},
            {"timeline_add_clip", "Add a clip to a track"},
            {"timeline_remove_track", "Remove a track"},
            {"timeline_list_tracks", "List all tracks in a Timeline"},
            {"timeline_play", "Play/stop Timeline"},
            {"timeline_set_duration", "Set Timeline duration"},
            {"timeline_set_binding", "Set track binding object"},
        };

        private static readonly Dictionary<string, string> _chinese = new Dictionary<string, string>
        {
            // Window
            {"window_title", "UnitySkills"},
            {"server_running", "● 服务器运行中"},
            {"server_stopped", "● 服务器已停止"},
            {"start_server", "启动服务器"},
            {"stop_server", "停止服务器"},
            {"test_skill", "测试 Skill"},
            {"skill_name", "Skill 名称"},
            {"parameters_json", "参数 (JSON)"},
            {"execute_skill", "执行 Skill"},
            {"result", "结果"},
            {"available_skills", "可用 Skills"},
            {"refresh", "刷新"},
            {"total_skills", "共 {0} 个 Skills，{1} 个分类"},
            {"use", "使用"},
            {"language", "语言"},
            
            // Skill Configuration
            {"skill_config", "AI Skill 配置"},
            {"claude_code", "Claude Code"},
            {"antigravity", "Antigravity"},
            {"gemini_cli", "Gemini CLI"},
            {"install_project", "安装到项目"},
            {"install_global", "全局安装"},
            {"installed", "✓ 已安装"},
            {"not_installed", "未安装"},
            {"install_success", "Skill 安装成功！"},
            {"install_failed", "安装失败：{0}"},
            {"update", "更新"},
            {"update_success", "Skill 更新成功！"},
            {"update_failed", "更新失败：{0}"},
            {"uninstall", "卸载"},
            {"uninstall_success", "Skill 卸载成功！"},
            {"uninstall_failed", "卸载失败：{0}"},
            {"uninstall_confirm", "确定要卸载 {0} 吗？"},
            {"gemini_enable_hint", "\n\n注意：请在 Gemini CLI 的 /settings 中启用 experimental.skills"},
            
            // Server stats
            {"server_stats", "实时统计"},
            {"queued_requests", "队列中请求"},
            {"total_processed", "已处理总数"},
            {"architecture", "架构"},
            {"auto_restart", "编译后自动重启"},
            {"auto_restart_hint", "Unity 重新编译脚本后服务器将自动重启"},
            {"timeout_unit", "分钟"},

            // Skill descriptions
            {"scene_create", "创建新的空场景"},
            {"scene_load", "加载已有场景"},
            {"scene_save", "保存当前场景"},
            {"scene_get_info", "获取当前场景信息"},
            {"scene_get_hierarchy", "获取场景层级树"},
            {"scene_screenshot", "截取场景视图截图"},
            {"gameobject_create", "创建新的游戏对象"},
            {"gameobject_delete", "按名称或实例ID删除游戏对象"},
            {"gameobject_find", "按名称、标签或组件查找游戏对象"},
            {"gameobject_set_transform", "设置游戏对象的位置、旋转或缩放"},
            {"gameobject_duplicate", "复制游戏对象"},
            {"gameobject_set_parent", "设置游戏对象的父级"},
            {"component_add", "向游戏对象添加组件"},
            {"component_remove", "从游戏对象移除组件"},
            {"component_list", "列出游戏对象上的所有组件"},
            {"component_set_property", "设置组件属性"},
            {"component_get_properties", "获取组件的所有属性"},
            {"material_create", "创建新材质"},
            {"material_set_color", "设置材质或渲染器的颜色属性"},
            {"material_set_texture", "设置材质的贴图"},
            {"material_assign", "将材质资源分配给渲染器"},
            {"material_set_float", "设置材质的浮点属性"},
            {"asset_import", "从外部路径导入资源"},
            {"asset_delete", "删除资源"},
            {"asset_move", "移动或重命名资源"},
            {"asset_duplicate", "复制资源"},
            {"asset_find", "按名称、类型或标签查找资源"},
            {"asset_create_folder", "在 Assets 中创建新文件夹"},
            {"asset_refresh", "刷新资源数据库"},
            {"asset_get_info", "获取资源信息"},
            {"editor_play", "进入播放模式"},
            {"editor_stop", "退出播放模式"},
            {"editor_pause", "暂停/继续播放模式"},
            {"editor_select", "选中游戏对象"},
            {"editor_get_selection", "获取当前选中的对象"},
            {"editor_undo", "撤销上一步操作"},
            {"editor_redo", "重做上一步撤销的操作"},
            {"editor_get_state", "获取编辑器当前状态"},
            {"editor_execute_menu", "执行 Unity 菜单项"},
            {"editor_get_tags", "获取所有可用标签"},
            {"editor_get_layers", "获取所有可用图层"},
            {"prefab_create", "从游戏对象创建预制体"},
            {"prefab_instantiate", "在场景中实例化预制体"},
            {"prefab_apply", "将实例的更改应用到预制体"},
            {"prefab_unpack", "解包预制体实例"},
            {"script_create", "创建新的 C# 脚本"},
            {"script_read", "读取脚本内容"},
            {"script_delete", "删除脚本文件"},
            {"script_find_in_file", "在脚本中搜索模式"},
            {"script_append", "向脚本追加内容"},
            {"console_start_capture", "开始捕获控制台日志"},
            {"console_stop_capture", "停止捕获控制台日志"},
            {"console_get_logs", "获取捕获的控制台日志"},
            {"console_clear", "清空 Unity 控制台"},
            {"console_log", "向控制台写入消息"},
            {"scriptableobject_create", "创建新的 ScriptableObject 资源"},
            {"scriptableobject_get", "获取 ScriptableObject 的属性"},
            {"scriptableobject_set", "设置 ScriptableObject 的字段/属性"},
            {"scriptableobject_list_types", "列出可用的 ScriptableObject 类型"},
            {"scriptableobject_duplicate", "复制 ScriptableObject 资源"},
            {"shader_create", "创建新的 Shader 文件"},
            {"shader_read", "读取 Shader 源代码"},
            {"shader_list", "列出项目中的所有 Shader"},
            {"shader_get_properties", "获取 Shader 的属性"},
            {"shader_find", "按名称查找 Shader"},
            {"shader_delete", "删除 Shader 文件"},
            {"test_run", "运行 Unity 测试（返回任务ID用于轮询）"},
            {"test_get_result", "获取测试运行结果"},
            {"test_list", "列出可用测试"},
            {"test_cancel", "取消正在运行的测试"},

            // Package Skills
            {"package_list", "列出所有已安装的包"},
            {"package_check", "检查包是否已安装"},
            {"package_install", "安装包"},
            {"package_remove", "移除已安装的包"},
            {"package_refresh", "刷新已安装包列表缓存"},
            {"package_install_cinemachine", "安装 Cinemachine (版本 2 或 3)"},
            {"package_get_cinemachine_status", "获取 Cinemachine 安装状态"},

            // New Skills (Batch 1.2.0+)
            {"gameobject_rename", "重命名游戏对象"},
            {"gameobject_rename_batch", "批量重命名游戏对象"},
            
            // Model Skills
            {"model_get_settings", "获取3D模型(FBX/OBJ)的导入设置"},
            {"model_set_settings", "设置模型导入属性 (压缩/动画/材质等)"},
            {"model_set_settings_batch", "批量设置多个模型的导入属性"},
            
            // Texture Skills
            {"texture_get_settings", "获取贴图导入设置"},
            {"texture_set_settings", "设置贴图导入属性 (类型/压缩/Filter等)"},
            {"texture_set_settings_batch", "批量设置多个贴图的导入属性"},
            
            // Audio Skills
            {"audio_get_settings", "获取音频导入设置"},
            {"audio_set_settings", "设置音频导入属性 (加载方式/压缩/质量等)"},
            {"audio_set_settings_batch", "批量设置多个音频文件的导入属性"},
            
            // Animator Skills
            {"animator_create_controller", "创建新的 Animator Controller"},
            {"animator_add_parameter", "向 Animator Controller 添加参数"},
            {"animator_get_parameters", "获取 Animator Controller 的所有参数"},
            {"animator_set_parameter", "设置 Animator 参数值"},
            {"animator_play", "播放动画状态"},
            {"animator_get_info", "获取 Animator 组件信息"},
            {"animator_assign_controller", "将 Animator Controller 分配给游戏对象"},
            {"animator_list_states", "列出 Animator 层中的所有状态"},

            // Light Skills
            {"light_create", "创建新灯光"},
            {"light_set_properties", "设置灯光属性"},
            {"light_get_info", "获取灯光信息"},
            {"light_find_all", "查找场景中所有灯光"},
            {"light_set_enabled", "启用/禁用灯光"},
            {"light_set_enabled_batch", "批量启用/禁用灯光"},
            {"light_set_properties_batch", "批量设置灯光属性"},

            // Project Skills
            {"project_get_info", "获取项目信息 (渲染管线/版本等)"},
            {"project_get_render_pipeline", "获取当前渲染管线类型及推荐 Shader"},
            {"project_list_shaders", "列出项目中所有可用 Shader"},
            {"project_get_quality_settings", "获取当前质量设置"},

            // Validation Skills
            {"validate_scene", "验证当前场景常见问题"},
            {"validate_find_missing_scripts", "查找所有丢失脚本的游戏对象"},
            {"validate_cleanup_empty_folders", "查找并清理空文件夹"},
            {"validate_find_unused_assets", "查找可能未使用的资源 (实验性)"},
            {"validate_texture_sizes", "查找可能需要优化的贴图"},
            {"validate_project_structure", "获取项目结构概览"},
            {"validate_fix_missing_scripts", "一键移除游戏对象上丢失的脚本组件"},

            // UI Skills
            {"ui_create_canvas", "创建新画布(Canvas)"},
            {"ui_create_panel", "创建面板(Panel)"},
            {"ui_create_button", "创建按钮(Button)"},
            {"ui_create_text", "创建文本(Text)"},
            {"ui_create_image", "创建图像(Image)"},
            {"ui_create_batch", "批量创建UI元素"},
            {"ui_create_inputfield", "创建输入框(InputField)"},
            {"ui_create_slider", "创建滑动条(Slider)"},
            {"ui_create_toggle", "创建开关(Toggle)"},
            {"ui_set_text", "设置文本内容"},
            {"ui_find_all", "查找场景中所有UI元素"},
            
            // Prefab Skills (Batch)
            {"prefab_instantiate_batch", "批量实例化预制体"},
            
            // Event Skills
            {"event_get_listeners", "获取 UnityEvent 的持久化监听器列表"},
            {"event_add_listener", "添加持久化监听器 (支持 void/int/float/string/bool)"},
            {"event_remove_listener", "移除持久化监听器"},
            {"event_invoke", "立即触发事件 (仅运行时)"},
            
            // Physics Skills
            {"physics_raycast", "发射射线检测碰撞"},
            {"physics_check_overlap", "检测指定区域内的碰撞体"},
            {"physics_get_gravity", "获取全局重力设置"},
            {"physics_set_gravity", "设置全局重力"},
            
            // NavMesh Skills
            {"navmesh_bake", "烘焙寻路网格 (可能较慢)"},
            {"navmesh_clear", "清除寻路网格数据"},
            {"navmesh_calculate_path", "计算两点简的路径 (检测可达性)"},
            
            // Profiler Skills
            {"profiler_get_stats", "获取性能统计数据 (FPS/内存/DrawCalls)"},

            // Optimization Skills
            {"optimize_textures", "优化纹理设置 (压缩/最大尺寸)"},
            {"optimize_mesh_compression", "设置模型网格压缩级别"},
            
            // Debug Skills
            {"debug_get_errors", "获取控制台错误日志 (过滤)"},
            {"debug_check_compilation", "检查编译状态"},
            {"debug_force_recompile", "强制重编译脚本"},
            {"debug_get_system_info", "获取编辑器/系统信息"},
            
            // Camera Skills
            {"camera_align_view_to_object", "对齐 Scene 视图到物体"},
            {"camera_get_info", "获取 Scene 相机信息"},
            {"camera_set_transform", "设置 Scene 相机位置/旋转"},
            {"camera_look_at", "Scene 相机看向指定点"},
            
            // Timeline Skills
            {"timeline_create", "创建 Timeline 资产及实例"},
            {"timeline_add_audio_track", "添加音轨"},
            {"timeline_add_animation_track", "添加动画轨道(可选绑定对象)"},
            
            // Phase 4: Cinemachine & Logging
            {"cinemachine_create_vcam", "创建虚拟相机"},
            {"cinemachine_inspect_vcam", "内省虚拟相机 (获取组件与Tooltip)"},
            {"cinemachine_set_vcam_property", "通用设置虚拟相机属性 (支持反射)"},
            {"cinemachine_set_targets", "设置虚拟相机跟随/瞄准目标"},
            {"cinemachine_set_component", "切换虚拟相机组件 (Body/Aim/Noise)"},
            
            {"debug_get_logs", "获取控制台日志 (按类型筛选)"},

            // Cleaner Skills
            {"cleaner_find_unused_assets", "查找指定类型的潜在未使用资源"},
            {"cleaner_find_duplicates", "通过内容哈希查找重复文件"},
            {"cleaner_find_missing_references", "查找丢失脚本或资源引用的组件"},
            {"cleaner_delete_assets", "安全删除指定资源 (带预览)"},
            {"cleaner_get_asset_usage", "查找引用了特定资源的对象"},

            // Debug Enhance Skills
            {"debug_log", "向 Unity 控制台写入消息"},
            {"editor_set_pause_on_error", "启用/禁用播放模式下的'报错暂停'"},
            
            // Perception Skills (NextGen)
            {"scene_summarize", "获取当前场景的结构化摘要"},
            {"hierarchy_describe", "获取场景层级树的文本描述"},
            {"script_analyze", "分析 MonoBehaviour 脚本的公共 API"},
            {"scene_spatial_query", "查找指定坐标或对象附近半径内的物体"},
            {"scene_materials", "获取场景中所有材质和着色器的概览"},
            {"scene_context", "生成完整场景快照供 AI 编码辅助使用"},
            {"scene_export_report", "导出完整场景结构与依赖关系报告为 Markdown 文件"},
            {"scene_dependency_analyze", "分析对象依赖关系图与变更影响"},

            // Smart Skills
            {"smart_scene_query", "基于组件属性值查找对象 (类SQL查询)"},
            {"smart_scene_layout", "将选中对象按布局排列 (线性/网格/圆形/弧形)"},
            {"smart_reference_bind", "自动填充 List/Array 字段 (匹配标签或名称)"},

            // Terrain Skills
            {"terrain_create", "使用 TerrainData 创建新地形"},
            {"terrain_get_info", "获取地形信息 (尺寸/分辨率/图层)"},
            {"terrain_get_height", "获取世界坐标处的地形高度"},
            {"terrain_set_height", "设置归一化坐标 (0-1) 处的地形高度"},
            {"terrain_set_heights_batch", "批量设置矩形区域的地形高度"},
            {"terrain_paint_texture", "绘制地形贴图层"},

            // Workflow Skills
            {"bookmark_set", "保存当前选中项和场景视图位置为书签"},
            {"bookmark_goto", "从书签恢复选中项和场景视图"},
            {"bookmark_list", "列出所有已保存的书签"},
            {"bookmark_delete", "删除书签"},
            {"history_undo", "撤销上一次操作"},
            {"history_redo", "重做上一次撤销的操作"},
            {"history_get_current", "获取当前撤销组的名称"},
            
            // UI Skills (Additional)
            {"ui_set_anchor", "设置 UI 元素的锚点预设"},
            {"ui_set_rect", "设置 RectTransform 的尺寸、位置和边距"},
            {"ui_layout_children", "按布局排列子 UI 元素"},
            {"ui_align_selected", "对齐选中的 UI 元素"},
            {"ui_distribute_selected", "均匀分布选中的 UI 元素"},

            // Cinemachine Skills (Complete)
            {"cinemachine_add_component", "添加 Cinemachine 组件 (如 OrbitalFollow)"},
            {"cinemachine_set_lens", "快速配置镜头设置 (FOV/近裁面/远裁面/正交尺寸)"},
            {"cinemachine_list_components", "列出所有可用的 Cinemachine 组件名称"},
            {"cinemachine_impulse_generate", "触发震动脉冲"},
            {"cinemachine_get_brain_info", "获取活动相机和混合信息"},
            {"cinemachine_set_active", "强制激活虚拟相机 (设为最高优先级)"},
            {"cinemachine_set_noise", "配置噪声设置 (Basic Multi Channel Perlin)"},
            {"cinemachine_create_target_group", "创建 Cinemachine 目标组"},
            {"cinemachine_target_group_add_member", "向目标组添加/更新成员"},
            {"cinemachine_target_group_remove_member", "从目标组移除成员"},
            {"cinemachine_set_spline", "设置虚拟相机的样条轨道"},
            {"cinemachine_add_extension", "添加 Cinemachine 扩展"},
            {"cinemachine_remove_extension", "移除 Cinemachine 扩展"},
            {"cinemachine_create_mixing_camera", "创建 Cinemachine 混合相机"},
            {"cinemachine_mixing_camera_set_weight", "设置混合相机中子相机的权重"},
            {"cinemachine_create_clear_shot", "创建 Cinemachine Clear Shot 相机"},
            {"cinemachine_create_state_driven_camera", "创建 Cinemachine 状态驱动相机"},
            {"cinemachine_state_driven_camera_add_instruction", "向状态驱动相机添加指令"},

            // GameObject Batch Skills
            {"gameobject_create_batch", "批量创建游戏对象"},
            {"gameobject_delete_batch", "批量删除游戏对象"},
            {"gameobject_set_transform_batch", "批量设置变换属性"},
            {"gameobject_duplicate_batch", "批量复制游戏对象"},
            {"gameobject_set_active", "启用/禁用游戏对象"},
            {"gameobject_set_active_batch", "批量启用/禁用游戏对象"},
            {"gameobject_set_layer_batch", "批量设置图层"},
            {"gameobject_set_tag_batch", "批量设置标签"},
            {"gameobject_set_parent_batch", "批量设置父级"},
            {"gameobject_get_info", "获取游戏对象详细信息"},

            // Component Batch Skills
            {"component_add_batch", "批量添加组件"},
            {"component_remove_batch", "批量移除组件"},
            {"component_set_property_batch", "批量设置组件属性"},

            // Asset Batch Skills
            {"asset_import_batch", "批量导入资源"},
            {"asset_delete_batch", "批量删除资源"},
            {"asset_move_batch", "批量移动资源"},

            // Material Skills (Complete)
            {"material_create_batch", "批量创建材质"},
            {"material_assign_batch", "批量分配材质"},
            {"material_duplicate", "复制材质"},
            {"material_set_colors_batch", "批量设置颜色"},
            {"material_set_emission", "设置自发光颜色 (HDR强度/自动启用)"},
            {"material_set_emission_batch", "批量设置自发光"},
            {"material_set_int", "设置材质整数属性"},
            {"material_set_vector", "设置材质向量属性"},
            {"material_set_texture_offset", "设置贴图偏移"},
            {"material_set_texture_scale", "设置贴图缩放"},
            {"material_set_keyword", "启用/禁用 Shader 关键字"},
            {"material_set_render_queue", "设置材质渲染队列"},
            {"material_set_shader", "更改材质的 Shader"},
            {"material_set_gi_flags", "设置全局光照标志"},
            {"material_get_properties", "获取材质所有属性"},
            {"material_get_keywords", "获取材质启用的 Shader 关键字"},

            // Scene Skills (Complete)
            {"scene_get_loaded", "获取所有已加载场景列表"},
            {"scene_unload", "卸载场景 (Additive)"},
            {"scene_set_active", "设置活动场景 (多场景编辑)"},

            // Terrain Skills (Complete)
            {"terrain_add_hill", "在地形上添加平滑山丘"},
            {"terrain_generate_perlin", "使用 Perlin 噪声生成自然地形"},
            {"terrain_smooth", "平滑地形高度"},
            {"terrain_flatten", "将地形展平到指定高度"},

            // Prefab Skills (Complete)
            {"prefab_get_overrides", "获取预制体实例的属性覆盖列表"},
            {"prefab_revert_overrides", "还原预制体实例的所有覆盖"},
            {"prefab_apply_overrides", "将实例覆盖应用到源预制体"},

            // Workflow Skills (Complete)
            {"workflow_task_start", "开始新的持久化工作流任务"},
            {"workflow_task_end", "结束当前工作流任务"},
            {"workflow_snapshot_object", "手动快照对象 (修改前)"},
            {"workflow_list", "列出持久化工作流历史"},
            {"workflow_undo_task", "撤销指定任务的更改"},
            {"workflow_redo_task", "重做已撤销的任务"},
            {"workflow_undone_list", "列出可重做的已撤销任务"},
            {"workflow_revert_task", "workflow_undo_task 的别名 (已弃用)"},
            {"workflow_snapshot_created", "记录新创建的对象用于撤销追踪"},
            {"workflow_delete_task", "从历史中删除任务 (不还原更改)"},
            {"workflow_session_start", "开始新会话 (对话级)"},
            {"workflow_session_end", "结束当前会话并保存更改"},
            {"workflow_session_undo", "撤销整个会话的所有更改"},
            {"workflow_session_list", "列出所有会话历史"},
            {"workflow_session_status", "获取当前会话状态"},

            // Editor Skills (Complete)
            {"editor_get_context", "获取完整编辑器上下文 (选中对象/资源/场景/窗口)"},

            // Script Skills (Complete)
            {"script_create_batch", "批量创建脚本"},

            // Sample Skills
            {"create_cube", "在指定位置创建立方体"},
            {"create_sphere", "在指定位置创建球体"},
            {"delete_object", "按名称删除游戏对象"},
            {"get_scene_info", "获取当前场景信息"},
            {"set_object_position", "设置游戏对象位置"},
            {"set_object_rotation", "设置游戏对象旋转 (欧拉角)"},
            {"set_object_scale", "设置游戏对象缩放"},
            {"find_objects_by_name", "按名称查找所有游戏对象"},

            // Profiler Skills (new)
            {"profiler_get_memory", "获取内存使用概况 (总分配/保留/Mono堆)"},
            {"profiler_get_runtime_memory", "获取场景中内存占用最大的 N 个对象"},
            {"profiler_get_texture_memory", "获取所有已加载纹理的内存占用"},
            {"profiler_get_mesh_memory", "获取所有已加载网格的内存占用"},
            {"profiler_get_material_memory", "获取所有已加载材质的内存占用"},
            {"profiler_get_audio_memory", "获取所有已加载音频的内存占用"},
            {"profiler_get_object_count", "按类型统计已加载对象数量"},
            {"profiler_get_rendering_stats", "获取渲染统计 (批次/三角面/顶点)"},
            {"profiler_get_asset_bundle_stats", "获取已加载 AssetBundle 信息"},

            // Optimization Skills (new)
            {"optimize_analyze_scene", "分析场景性能瓶颈"},
            {"optimize_find_large_assets", "查找超过指定大小的资源"},
            {"optimize_set_static_flags", "设置游戏对象的 Static Flags"},
            {"optimize_get_static_flags", "获取游戏对象的 Static Flags"},
            {"optimize_audio_compression", "批量设置音频压缩"},
            {"optimize_find_duplicate_materials", "查找属性相同的重复材质"},
            {"optimize_analyze_overdraw", "分析可能导致 Overdraw 的透明物体"},
            {"optimize_set_lod_group", "添加或配置 LOD Group"},

            // Audio Skills (new)
            {"audio_find_clips", "搜索项目中的 AudioClip 资源"},
            {"audio_get_clip_info", "获取 AudioClip 详细信息"},
            {"audio_add_source", "添加 AudioSource 组件"},
            {"audio_get_source_info", "获取 AudioSource 配置"},
            {"audio_set_source_properties", "设置 AudioSource 属性"},
            {"audio_find_sources_in_scene", "查找场景中所有 AudioSource"},
            {"audio_create_mixer", "创建 AudioMixer 资源"},

            // Model Skills (new)
            {"model_find_assets", "搜索项目中的模型资源"},
            {"model_get_mesh_info", "获取网格详细信息 (顶点/三角面)"},
            {"model_get_materials_info", "获取模型的材质映射"},
            {"model_get_animations_info", "获取模型的动画剪辑信息"},
            {"model_set_animation_clips", "配置动画剪辑分割"},
            {"model_get_rig_info", "获取骨骼绑定信息"},
            {"model_set_rig", "设置骨骼绑定类型"},

            // Texture Skills (new)
            {"texture_find_assets", "搜索项目中的纹理资源"},
            {"texture_get_info", "获取纹理详细信息 (尺寸/格式/内存)"},
            {"texture_set_type", "设置纹理类型"},
            {"texture_set_platform_settings", "设置平台纹理压缩设置"},
            {"texture_get_platform_settings", "获取平台纹理设置"},
            {"texture_set_sprite_settings", "配置 Sprite 设置"},
            {"texture_find_by_size", "按尺寸范围查找纹理"},

            // Light Skills (new)
            {"light_add_probe_group", "添加光照探针组"},
            {"light_add_reflection_probe", "创建反射探针"},
            {"light_get_lightmap_settings", "获取光照贴图烘焙设置"},

            // Package Skills (new)
            {"package_search", "搜索 Unity Registry 中的包"},
            {"package_get_dependencies", "获取包的依赖关系"},
            {"package_get_versions", "获取包的所有可用版本"},

            // Validation Skills (new)
            {"validate_missing_references", "查找组件上的空引用/丢失引用"},
            {"validate_mesh_collider_convex", "查找非凸 MeshCollider"},
            {"validate_shader_errors", "查找有编译错误的 Shader"},

            // Animator Skills (new)
            {"animator_add_state", "添加动画状态到 Animator Controller 层"},
            {"animator_add_transition", "添加两个状态之间的过渡"},

            // Component Skills (new)
            {"component_copy", "复制组件到另一个游戏对象"},
            {"component_set_enabled", "启用/禁用 Behaviour 组件"},

            // Perception Skills (new)
            {"scene_tag_layer_stats", "获取 Tag/Layer 使用统计及潜在问题"},
            {"scene_performance_hints", "诊断场景性能问题并给出可操作建议"},

            // Prefab Skills (new)
            {"prefab_create_variant", "从现有预制体创建变体"},
            {"prefab_find_instances", "查找预制体在场景中的所有实例"},

            // Scene Skills (new)
            {"scene_find_objects", "按名称/标签/组件类型搜索游戏对象"},

            // Shader Skills (new)
            {"shader_check_errors", "检查 Shader 编译错误"},
            {"shader_get_keywords", "获取 Shader 关键字列表"},
            {"shader_get_variant_count", "获取 Shader 变体数量 (性能分析)"},
            {"shader_create_urp", "从模板创建 URP Shader"},
            {"shader_set_global_keyword", "启用/禁用全局 Shader 关键字"},

            // AssetImport Skills (new)
            {"texture_get_import_settings", "获取纹理导入设置"},
            {"audio_get_import_settings", "获取音频导入设置"},
            {"audio_set_import_settings", "设置音频导入设置"},
            {"sprite_set_import_settings", "设置精灵导入设置"},
            {"asset_get_labels", "获取资源标签"},
            {"asset_set_labels", "设置资源标签"},
            {"model_get_import_settings", "获取模型导入设置"},

            // Camera Skills (new)
            {"camera_create", "创建新相机"},
            {"camera_set_properties", "设置相机属性"},
            {"camera_get_properties", "获取相机属性"},
            {"camera_list", "列出场景中所有相机"},
            {"camera_screenshot", "从相机截图"},
            {"camera_set_culling_mask", "设置相机剔除遮罩"},
            {"camera_set_orthographic", "设置相机正交模式"},

            // Cleaner Skills (new)
            {"cleaner_find_empty_folders", "查找项目中的空文件夹"},
            {"cleaner_delete_empty_folders", "删除空文件夹"},
            {"cleaner_find_large_assets", "查找项目中的大资源"},
            {"cleaner_fix_missing_scripts", "移除丢失的脚本组件"},
            {"cleaner_get_dependency_tree", "获取资源依赖树"},

            // Console Skills (new)
            {"console_export", "导出控制台日志到文件"},
            {"console_get_stats", "获取控制台日志统计"},
            {"console_set_pause_on_error", "设置报错暂停"},
            {"console_set_collapse", "设置控制台折叠模式"},
            {"console_set_clear_on_play", "设置播放时清除"},

            // Debug Skills (new)
            {"debug_get_memory_info", "获取详细内存信息"},
            {"debug_get_stack_trace", "获取最后一条错误的堆栈跟踪"},
            {"debug_get_assembly_info", "获取已加载程序集信息"},
            {"debug_get_defines", "获取脚本宏定义符号"},
            {"debug_set_defines", "设置脚本宏定义符号"},

            // Event Skills (new)
            {"event_add_listener_batch", "批量添加持久化监听器"},
            {"event_clear_listeners", "清除所有持久化监听器"},
            {"event_copy_listeners", "复制事件监听器"},
            {"event_get_listener_count", "获取监听器数量"},
            {"event_list_events", "列出组件上的所有 UnityEvent"},
            {"event_set_listener_state", "设置监听器启用状态"},

            // NavMesh Skills (new)
            {"navmesh_add_agent", "添加 NavMeshAgent 组件"},
            {"navmesh_set_agent", "设置 NavMeshAgent 属性"},
            {"navmesh_add_obstacle", "添加 NavMeshObstacle 组件"},
            {"navmesh_set_obstacle", "设置 NavMeshObstacle 属性"},
            {"navmesh_set_area_cost", "设置 NavMesh 区域代价"},
            {"navmesh_get_settings", "获取 NavMesh 设置"},
            {"navmesh_sample_position", "采样 NavMesh 上最近的点"},

            // Physics Skills (new)
            {"physics_create_material", "创建物理材质"},
            {"physics_set_material", "分配物理材质到碰撞体"},
            {"physics_set_layer_collision", "设置层碰撞矩阵"},
            {"physics_get_layer_collision", "获取层碰撞矩阵"},
            {"physics_raycast_all", "射线检测所有命中"},
            {"physics_spherecast", "球形射线检测"},
            {"physics_boxcast", "盒形射线检测"},
            {"physics_overlap_box", "检测盒形区域内的碰撞体"},

            // Project Skills (new)
            {"project_add_tag", "添加新标签"},
            {"project_set_quality_level", "设置质量等级"},
            {"project_get_tags", "获取所有项目标签"},
            {"project_get_layers", "获取所有项目层"},
            {"project_get_packages", "获取已安装的包"},
            {"project_get_build_settings", "获取构建设置"},
            {"project_get_player_settings", "获取播放器设置"},

            // Script Skills (new)
            {"script_replace", "替换脚本中的内容"},
            {"script_rename", "重命名脚本文件"},
            {"script_move", "移动脚本文件"},
            {"script_list", "列出项目中的脚本"},
            {"script_get_info", "获取脚本信息"},

            // ScriptableObject Skills (new)
            {"scriptableobject_delete", "删除 ScriptableObject 资源"},
            {"scriptableobject_find", "查找 ScriptableObject 资源"},
            {"scriptableobject_set_batch", "批量设置 ScriptableObject 字段"},
            {"scriptableobject_export_json", "导出 ScriptableObject 为 JSON"},
            {"scriptableobject_import_json", "从 JSON 导入 ScriptableObject"},

            // Smart Skills (new)
            {"smart_align_to_ground", "将对象对齐到地面"},
            {"smart_distribute", "均匀分布对象"},
            {"smart_snap_to_grid", "将对象吸附到网格"},
            {"smart_randomize_transform", "随机化对象变换"},
            {"smart_replace_objects", "用预制体替换对象"},
            {"smart_scene_query_spatial", "空间查询附近对象"},
            {"smart_select_by_component", "按组件类型选择对象"},

            // Test Skills (new)
            {"test_create_editmode", "创建 EditMode 测试"},
            {"test_create_playmode", "创建 PlayMode 测试"},
            {"test_get_last_result", "获取上次测试结果"},
            {"test_get_summary", "获取测试摘要"},
            {"test_list_categories", "列出测试分类"},
            {"test_run_by_name", "按名称运行指定测试"},

            // Timeline Skills (new)
            {"timeline_add_activation_track", "添加激活轨道"},
            {"timeline_add_control_track", "添加控制轨道"},
            {"timeline_add_signal_track", "添加信号轨道"},
            {"timeline_add_clip", "添加剪辑到轨道"},
            {"timeline_remove_track", "移除轨道"},
            {"timeline_list_tracks", "列出 Timeline 中的所有轨道"},
            {"timeline_play", "播放/停止 Timeline"},
            {"timeline_set_duration", "设置 Timeline 时长"},
            {"timeline_set_binding", "设置轨道绑定对象"},
        };
    }
}
