using System;

namespace UnitySkills
{
    /// <summary>
    /// 技能模块分类，每个值对应一个 *Skills.cs 文件。
    /// </summary>
    public enum SkillCategory
    {
        Uncategorized = 0,
        GameObject,
        Component,
        Scene,
        Material,
        UI,
        UIToolkit,
        Asset,
        Editor,
        Script,
        Audio,
        Texture,
        Model,
        Timeline,
        Physics,
        Camera,
        Light,
        Shader,
        Terrain,
        NavMesh,
        Prefab,
        Animator,
        Package,
        Workflow,
        Perception,
        Smart,
        Validation,
        Optimization,
        Cleaner,
        Profiler,
        Debug,
        Console,
        Event,
        Test,
        ScriptableObject,
        ProBuilder,
        XR,
        Cinemachine,
        Project,
        AssetImport,
        Sample,
        Netcode,
        YooAsset,
        DOTween,
        PrimeTween,
        Graphics,
        Volume,
        URP,
        Decal,
        PostProcess,
        ShaderGraph,
        Behavior,
        HybridCLR,
        Addressables
    }

    /// <summary>
    /// CRUD + Execute + Analyze 操作类型，Flags 可组合。
    /// </summary>
    [Flags]
    public enum SkillOperation
    {
        Query   = 1,
        Create  = 2,
        Modify  = 4,
        Delete  = 8,
        Execute = 16,
        Analyze = 32
    }

    /// <summary>
    /// 标记一个静态方法为 Unity Skill，标记后会被自动发现并通过 REST API 暴露。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UnitySkillAttribute : Attribute
    {
        // === 基础字段 ===
        public string Name { get; set; }
        public string Description { get; set; }
        public bool TracksWorkflow { get; set; }

        /// <summary>
        /// 该技能自行管理工作流快照、应跳过 router 的通用执行前快照（<c>TrySnapshotTargetsFromArgs</c>）时为 true。
        /// 用于 asset_move/asset_delete/asset_duplicate/create_folder 这类自己拍专用快照的技能，避免通用
        /// 前置快照产生冗余备份。默认 false——普通技能仍自动拍前置快照。
        /// </summary>
        public bool SkipAutoPresnapshot { get; set; }

        // === 意图层元数据 ===

        /// <summary>模块分类，对应该技能所属的 *Skills.cs 文件。</summary>
        public SkillCategory Category { get; set; }

        /// <summary>该技能执行的 CRUD 操作类型。</summary>
        public SkillOperation Operation { get; set; }

        /// <summary>供 AI 检索与过滤的语义标签。</summary>
        public string[] Tags { get; set; }

        /// <summary>结果对象中产出的关键字段（如 "gameObject"、"instanceId"）。</summary>
        public string[] Outputs { get; set; }

        /// <summary>该技能需要的既有对象/资源（如 "gameObject"、"materialPath"）。</summary>
        public string[] RequiresInput { get; set; }

        /// <summary>无副作用（纯查询/只读）时为 true。</summary>
        public bool ReadOnly { get; set; }

        // === 风险与影响元数据 ===

        /// <summary>会修改场景层级（GameObject、Component、Transform）时为 true。</summary>
        public bool MutatesScene { get; set; }

        /// <summary>会创建、修改或删除磁盘资产时为 true。</summary>
        public bool MutatesAssets { get; set; }

        /// <summary>可能触发脚本编译或域重载时为 true。</summary>
        public bool MayTriggerReload { get; set; }

        /// <summary>可能进入或退出 Play Mode 时为 true。</summary>
        public bool MayEnterPlayMode { get; set; }

        /// <summary>无法提供有意义的 dry-run 预览（如异步作业、外部进程）时为 false。</summary>
        public bool SupportsDryRun { get; set; } = true;

        /// <summary>
        /// 该技能同步执行且可能阻塞编辑器主线程数秒以上（完整 NavMesh 烘焙、player 脚本编译、HybridCLR 预构建）时为 true。
        /// 其运行期间主线程上一切都不推进——包括 HTTP 请求队列——所以 agent 应把这次调用视为有意的停顿：
        /// 有异步作业路径时优先走异步，返回前不要期待任何响应，看似超时也不要重试。默认 false。
        /// </summary>
        public bool LongRunning { get; set; } = false;

        /// <summary>风险等级："low"（默认）、"medium" 或 "high"。</summary>
        public string RiskLevel { get; set; } = "low";

        /// <summary>该技能依赖的可选包（如 "com.unity.probuilder"）。</summary>
        public string[] RequiresPackages { get; set; }

        /// <summary>
        /// 权限风险档位。
        /// SemiAuto = 三档模式下均直接执行；FullAuto = Approval 模式下需用户授权。
        /// 默认 FullAuto，使未标注的 skill 在 Approval 模式下走授权流程（这是 Mode 字段的默认值，与出厂操作模式默认无关）。
        /// </summary>
        public SkillMode Mode { get; set; } = SkillMode.FullAuto;

        public UnitySkillAttribute() { }

        public UnitySkillAttribute(string name, string description = null)
        {
            Name = name;
            Description = description;
        }
    }
}

// Producer:Betsy
