using System;

namespace UnitySkills
{
    /// <summary>
    /// Skill module category. Each value maps to a *Skills.cs file.
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
        ShaderGraph
    }

    /// <summary>
    /// CRUD + Execute + Analyze operation types. Flags allow combinations.
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
    /// Marks a static method as a Unity Skill.
    /// Skills are automatically discovered and exposed via REST API.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UnitySkillAttribute : Attribute
    {
        // === Existing fields ===
        public string Name { get; set; }
        public string Description { get; set; }
        public bool TracksWorkflow { get; set; }

        /// <summary>
        /// True if this skill manages its own workflow snapshots and should skip the router's
        /// generic pre-execution snapshot (<c>TrySnapshotTargetsFromArgs</c>). Set this on skills
        /// like asset_move/asset_delete/asset_duplicate/create_folder that capture purpose-built
        /// snapshots themselves, so the generic pre-snapshot doesn't produce a redundant backup.
        /// Default false — normal skills still get the automatic pre-snapshot.
        /// </summary>
        public bool SkipAutoPresnapshot { get; set; }

        // === Intent-level metadata (v1.7) ===

        /// <summary>Module category, maps to the *Skills.cs file this skill belongs to.</summary>
        public SkillCategory Category { get; set; }

        /// <summary>CRUD operation type(s) this skill performs.</summary>
        public SkillOperation Operation { get; set; }

        /// <summary>Semantic tags for AI search and filtering.</summary>
        public string[] Tags { get; set; }

        /// <summary>Key fields produced in the result object (e.g. "gameObject", "instanceId").</summary>
        public string[] Outputs { get; set; }

        /// <summary>What existing objects/resources this skill needs (e.g. "gameObject", "materialPath").</summary>
        public string[] RequiresInput { get; set; }

        /// <summary>True if this skill has no side effects (pure query/read).</summary>
        public bool ReadOnly { get; set; }

        // === Risk & impact metadata ===

        /// <summary>True if this skill modifies the scene hierarchy (GameObjects, Components, transforms).</summary>
        public bool MutatesScene { get; set; }

        /// <summary>True if this skill creates, modifies, or deletes on-disk assets.</summary>
        public bool MutatesAssets { get; set; }

        /// <summary>True if this skill may trigger script compilation or Domain Reload.</summary>
        public bool MayTriggerReload { get; set; }

        /// <summary>True if this skill may enter or exit Play Mode.</summary>
        public bool MayEnterPlayMode { get; set; }

        /// <summary>False if this skill cannot provide a meaningful dry-run preview (e.g. async jobs, external processes).</summary>
        public bool SupportsDryRun { get; set; } = true;

        /// <summary>Risk level: "low" (default), "medium", or "high".</summary>
        public string RiskLevel { get; set; } = "low";

        /// <summary>Optional packages this skill requires (e.g. "com.unity.probuilder").</summary>
        public string[] RequiresPackages { get; set; }

        /// <summary>
        /// 权限风险档位（v1.9）。
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
