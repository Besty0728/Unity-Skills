using System;

namespace UnitySkills
{
    /// <summary>
    /// 2.7 中被 <see cref="SkillsSurfaceProfile"/> 取代的布尔 guide 开关的向后兼容视图。
    ///
    /// 包内刻意零调用者：这是 2.6.x 已发布的公共 API，用户自己的编辑器脚本可能仍在读它，删除只会
    /// 白白破坏其构建。重命名的公共成员按惯例保留 <see cref="ObsoleteAttribute"/> 转发器直到大版本
    /// （参考 <see cref="SkillsModeManager"/> 中的 AllowlistSkills 转发器）。本类与 <c>/health</c> 上
    /// 已弃用的 <c>guideMode</c> 别名表示同一概念，须同时退役，不可失步。
    /// </summary>
    [Obsolete("Use SkillsSurfaceProfile. v2.7 replaced the boolean guide switch with the three-way surfaceProfile; a bool cannot express noSceneAuthoring, so this shim only ever reports the guide profile.")]
    public static class SkillsGuideMode
    {
        /// <summary>转发到 <see cref="SkillsSurfaceProfile.OnChanged"/>。</summary>
        public static event Action OnChanged
        {
            add { SkillsSurfaceProfile.OnChanged += value; }
            remove { SkillsSurfaceProfile.OnChanged -= value; }
        }

        /// <summary>
        /// 当前档位为 <c>guide</c> 时为 true。赋 true 选中 guide 档；赋 false 只清除 guide，不动
        /// <c>noSceneAuthoring</c>——布尔无法表达该状态，静默降级为 <c>full</c> 会扩大用户特意收窄的暴露面。
        /// </summary>
        public static bool Enabled
        {
            get => SkillsSurfaceProfile.Current == SurfaceProfileKind.Guide;
            set
            {
                if (value)
                    SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
                else if (SkillsSurfaceProfile.Current == SurfaceProfileKind.Guide)
                    SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            }
        }
    }
}

// Producer:Betsy
