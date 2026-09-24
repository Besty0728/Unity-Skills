using System;

namespace UnitySkills
{
    /// <summary>
    /// A short semantic note for a parameter whose meaning its name and type do not carry: coordinate space,
    /// units, encoding, allowed values, or precedence over another parameter. The router emits it as the
    /// parameter's <c>description</c> in schema, recommend and dryRun entries; parameters without the attribute
    /// carry no <c>description</c> key at all. Only meaningful on a parameter of a <c>[UnitySkill]</c> method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class SkillParamAttribute : Attribute
    {
        public SkillParamAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }
    }
}

// Producer:Betsy
