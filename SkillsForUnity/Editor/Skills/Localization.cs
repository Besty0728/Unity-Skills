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
            if (_current == Language.Chinese && _chinese.TryGetValue(key, out var cn))
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

            // Asset Import Skills
            {"asset_reimport", "Force reimport of an asset"},
            {"asset_reimport_batch", "Reimport multiple assets matching a pattern"},
            {"texture_set_import_settings", "Set texture import settings (maxSize, compression, readable)"},
            {"model_set_import_settings", "Set model (FBX) import settings"},

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

            // Asset Import Skills
            {"asset_reimport", "强制重新导入资源"},
            {"asset_reimport_batch", "批量重新导入匹配模式的资源"},
            {"texture_set_import_settings", "设置贴图导入设置 (最大尺寸/压缩/可读性)"},
            {"model_set_import_settings", "设置模型(FBX)导入设置"},

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
        };
    }
}
