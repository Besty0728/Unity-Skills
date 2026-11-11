using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Module docs holding multi-skill workflow rules that a schema entry cannot express. Register a category only
    /// when its doc has such rules: each registration makes agents read one more file.
    /// </summary>
    internal static class SkillModuleDocs
    {
        private static readonly Dictionary<SkillCategory, string> _docs = new Dictionary<SkillCategory, string>
        {
            { SkillCategory.UI, "skills/ui/MODULE.md" },
            { SkillCategory.Prefab, "skills/prefab/MODULE.md" },
        };

        internal static string DocFor(SkillCategory category) =>
            _docs.TryGetValue(category, out var path) ? path : null;

        internal static IEnumerable<KeyValuePair<SkillCategory, string>> Registered => _docs;
    }
}

// Producer:Betsy
