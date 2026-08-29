using System;

namespace UnitySkills
{
    /// <summary>
    /// Skill module categories; each value corresponds to one *Skills.cs file.
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
        Addressables,
        QFramework
    }

    /// <summary>
    /// CRUD + Execute + Analyze operation types; combinable via Flags.
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
    /// Marks a static method as a Unity Skill; once marked, it is auto-discovered and exposed via the REST API.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class UnitySkillAttribute : Attribute
    {
        // === Basic fields ===
        public string Name { get; set; }
        public string Description { get; set; }
        public bool TracksWorkflow { get; set; }

        /// <summary>
        /// True when this skill manages its own workflow snapshots and the router's generic
        /// pre-execution snapshot (<c>TrySnapshotTargetsFromArgs</c>) should be skipped. Used by
        /// skills like asset_move/asset_delete/asset_duplicate/create_folder that take their own
        /// dedicated snapshot, to avoid a redundant backup from the generic pre-snapshot. Defaults
        /// to false — ordinary skills still get an automatic pre-snapshot.
        /// </summary>
        public bool SkipAutoPresnapshot { get; set; }

        // === Intent-layer metadata ===

        /// <summary>Module category, corresponding to the *Skills.cs file this skill belongs to.</summary>
        public SkillCategory Category { get; set; }

        /// <summary>The CRUD operation type this skill performs.</summary>
        public SkillOperation Operation { get; set; }

        /// <summary>Semantic tags for AI retrieval and filtering.</summary>
        public string[] Tags { get; set; }

        /// <summary>The key fields produced in the result object (e.g. "gameObject", "instanceId").</summary>
        public string[] Outputs { get; set; }

        /// <summary>The existing objects/resources this skill requires (e.g. "gameObject", "materialPath").</summary>
        public string[] RequiresInput { get; set; }

        /// <summary>True when the skill has no side effects (pure query/read-only).</summary>
        public bool ReadOnly { get; set; }

        // === Risk and impact metadata ===

        /// <summary>True when it modifies the scene hierarchy (GameObject, Component, Transform).</summary>
        public bool MutatesScene { get; set; }

        /// <summary>True when it creates, modifies, or deletes on-disk assets.</summary>
        public bool MutatesAssets { get; set; }

        /// <summary>True when it may trigger script compilation or a domain reload.</summary>
        public bool MayTriggerReload { get; set; }

        /// <summary>True when it may enter or exit Play Mode.</summary>
        public bool MayEnterPlayMode { get; set; }

        /// <summary>False when a meaningful dry-run preview can't be provided (e.g. an async job, an external process).</summary>
        public bool SupportsDryRun { get; set; } = true;

        /// <summary>
        /// True when this skill executes synchronously and may block the editor's main thread
        /// for several seconds or more (a full NavMesh bake, player script compilation, HybridCLR
        /// pre-build). While it runs, nothing on the main thread advances — including the HTTP
        /// request queue — so an agent should treat this call as a deliberate pause: prefer the
        /// async job path when one exists, expect no response before it returns, and don't retry
        /// just because it looks like a timeout. Defaults to false.
        /// </summary>
        public bool LongRunning { get; set; } = false;

        /// <summary>Risk level: "low" (default), "medium", or "high".</summary>
        public string RiskLevel { get; set; } = "low";

        /// <summary>The optional packages this skill depends on (e.g. "com.unity.probuilder").</summary>
        public string[] RequiresPackages { get; set; }

        /// <summary>
        /// The permission risk tier.
        /// SemiAuto = executes directly under all three operating modes; FullAuto = requires user
        /// authorization under Approval mode.
        /// Defaults to FullAuto, so an unannotated skill goes through the authorization flow
        /// under Approval mode (this is the default of the Mode field, unrelated to the
        /// factory-default operating mode).
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
