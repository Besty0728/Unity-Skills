using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitySkills
{
    internal sealed class ShaderGraphNodeSettingDescriptor
    {
        public string Name { get; set; }
        public string ValueType { get; set; }
        public string[] Options { get; set; } = Array.Empty<string>();
        public string Notes { get; set; }
    }

    internal sealed class ShaderGraphSupportedNodeDescriptor
    {
        public string NodeType { get; set; }
        public string RuntimeTypeName { get; set; }
        public string[] Aliases { get; set; } = Array.Empty<string>();
        public bool SupportsGraph { get; set; } = true;
        public bool SupportsSubGraph { get; set; } = true;
        public bool RequiresExistingProperty { get; set; }
        public string Notes { get; set; }
        public string[] ValidatedVersions { get; set; } =
            { "Unity 2022.3 / ShaderGraph 14", "Unity 6 / Graphics current" };
        public ShaderGraphNodeSettingDescriptor[] Settings { get; set; } = Array.Empty<ShaderGraphNodeSettingDescriptor>();
    }

    internal static class ShaderGraphNodeRegistry
    {
        private static readonly IReadOnlyList<ShaderGraphSupportedNodeDescriptor> Descriptors =
            new[]
            {
                Create(
                    "PropertyNode",
                    "UnityEditor.ShaderGraph.PropertyNode",
                    requiresExistingProperty: true,
                    notes: "Requires an existing blackboard property reference.",
                    settings: Setting("propertyReferenceName", "string", notes: "Must match an existing Shader Graph property reference name.")),
                Create("BooleanNode", "UnityEditor.ShaderGraph.BooleanNode",
                    settings: Setting("value", "bool")),
                Create("ColorNode", "UnityEditor.ShaderGraph.ColorNode",
                    settings: Setting("value", "color")),
                Create("Vector1Node", "UnityEditor.ShaderGraph.Vector1Node",
                    aliases: new[] { "Float" },
                    settings: Setting("value", "float")),
                Create("Vector2Node", "UnityEditor.ShaderGraph.Vector2Node",
                    settings: Setting("value", "vector2")),
                Create("Vector3Node", "UnityEditor.ShaderGraph.Vector3Node",
                    settings: Setting("value", "vector3")),
                Create("Vector4Node", "UnityEditor.ShaderGraph.Vector4Node",
                    settings: Setting("value", "vector4")),
                Create("SampleTexture2DNode", "UnityEditor.ShaderGraph.SampleTexture2DNode"),
                Create("SamplerStateNode", "UnityEditor.ShaderGraph.SamplerStateNode"),
                Create("UVNode", "UnityEditor.ShaderGraph.UVNode",
                    settings: Setting("channel", "enum", options: new[] { "UV0", "UV1", "UV2", "UV3" })),
                Create("TilingAndOffsetNode", "UnityEditor.ShaderGraph.TilingAndOffsetNode"),
                Create("SplitNode", "UnityEditor.ShaderGraph.SplitNode"),
                Create("CombineNode", "UnityEditor.ShaderGraph.CombineNode"),
                Create("AppendVectorNode", "UnityEditor.ShaderGraph.AppendVectorNode",
                    validatedVersions: new[] { "Unity 6 / Graphics current" },
                    notes: "Unity 6 only in current validation set. The installed 2022.3 ShaderGraph 14 package does not expose this node type."),
                Create("AddNode", "UnityEditor.ShaderGraph.AddNode"),
                Create("SubtractNode", "UnityEditor.ShaderGraph.SubtractNode"),
                Create("MultiplyNode", "UnityEditor.ShaderGraph.MultiplyNode"),
                Create("DivideNode", "UnityEditor.ShaderGraph.DivideNode"),
                Create("LerpNode", "UnityEditor.ShaderGraph.LerpNode"),
                Create("OneMinusNode", "UnityEditor.ShaderGraph.OneMinusNode"),
                Create("SaturateNode", "UnityEditor.ShaderGraph.SaturateNode"),
                Create("ClampNode", "UnityEditor.ShaderGraph.ClampNode"),
                Create("RemapNode", "UnityEditor.ShaderGraph.RemapNode"),
                Create("BranchNode", "UnityEditor.ShaderGraph.BranchNode"),
                Create("NormalUnpackNode", "UnityEditor.ShaderGraph.NormalUnpackNode"),
                Create("NormalStrengthNode", "UnityEditor.ShaderGraph.NormalStrengthNode"),
                Create("PositionNode", "UnityEditor.ShaderGraph.PositionNode",
                    settings: Setting("space", "enum", options: new[] { "Object", "View", "World", "Tangent", "AbsoluteWorld" })),
                Create("NormalVectorNode", "UnityEditor.ShaderGraph.NormalVectorNode",
                    settings: Setting("space", "enum", options: new[] { "Object", "View", "World", "Tangent" })),
                Create("ViewDirectionNode", "UnityEditor.ShaderGraph.ViewDirectionNode",
                    settings: Setting("space", "enum", options: new[] { "Object", "View", "World", "Tangent" }))
            };

        public static IReadOnlyList<ShaderGraphSupportedNodeDescriptor> GetDescriptors()
        {
            return Descriptors;
        }

        public static ShaderGraphSupportedNodeDescriptor Find(string nodeType)
        {
            if (string.IsNullOrWhiteSpace(nodeType))
                return null;

            return Descriptors.FirstOrDefault(descriptor =>
                string.Equals(descriptor.NodeType, nodeType, StringComparison.OrdinalIgnoreCase) ||
                descriptor.Aliases.Any(alias => string.Equals(alias, nodeType, StringComparison.OrdinalIgnoreCase)));
        }

        private static ShaderGraphSupportedNodeDescriptor Create(
            string nodeType,
            string runtimeTypeName,
            bool requiresExistingProperty = false,
            string notes = null,
            string[] aliases = null,
            string[] validatedVersions = null,
            params ShaderGraphNodeSettingDescriptor[] settings)
        {
            return new ShaderGraphSupportedNodeDescriptor
            {
                NodeType = nodeType,
                RuntimeTypeName = runtimeTypeName,
                RequiresExistingProperty = requiresExistingProperty,
                Notes = notes,
                Aliases = aliases ?? Array.Empty<string>(),
                ValidatedVersions = validatedVersions ?? new[] { "Unity 2022.3 / ShaderGraph 14", "Unity 6 / Graphics current" },
                Settings = settings ?? Array.Empty<ShaderGraphNodeSettingDescriptor>()
            };
        }

        private static ShaderGraphNodeSettingDescriptor Setting(
            string name,
            string valueType,
            string[] options = null,
            string notes = null)
        {
            return new ShaderGraphNodeSettingDescriptor
            {
                Name = name,
                ValueType = valueType,
                Options = options ?? Array.Empty<string>(),
                Notes = notes
            };
        }
    }
}

// Producer:Betsy
