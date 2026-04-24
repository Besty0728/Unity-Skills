using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using PkgInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnitySkills
{
    internal sealed class ShaderGraphDocument
    {
        public string AssetPath { get; set; }
        public JObject Root { get; set; }
        public Dictionary<string, JObject> ObjectsById { get; } = new Dictionary<string, JObject>(StringComparer.Ordinal);
        public List<JObject> OrderedObjects { get; } = new List<JObject>();
    }

    internal static class ShaderGraphReflectionHelper
    {
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const string PackageRoot = "Packages/com.unity.shadergraph";
        private const string TemplatesRoot = "Packages/com.unity.shadergraph/GraphTemplates";
        private const string BuiltinBlankTemplatePath = "builtin:blank";

        // TypeCache removed — delegated to SkillsCommon.FindTypeByName

        private static readonly Dictionary<string, string> PropertyTypeMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["float"] = "UnityEditor.ShaderGraph.Internal.Vector1ShaderProperty",
                ["vector1"] = "UnityEditor.ShaderGraph.Internal.Vector1ShaderProperty",
                ["vector2"] = "UnityEditor.ShaderGraph.Internal.Vector2ShaderProperty",
                ["vector3"] = "UnityEditor.ShaderGraph.Internal.Vector3ShaderProperty",
                ["vector4"] = "UnityEditor.ShaderGraph.Internal.Vector4ShaderProperty",
                ["color"] = "UnityEditor.ShaderGraph.Internal.ColorShaderProperty",
                ["boolean"] = "UnityEditor.ShaderGraph.Internal.BooleanShaderProperty",
                ["bool"] = "UnityEditor.ShaderGraph.Internal.BooleanShaderProperty",
                ["texture2d"] = "UnityEditor.ShaderGraph.Internal.Texture2DShaderProperty"
            };

        private static readonly Dictionary<int, string> KeywordTypeNames =
            new Dictionary<int, string>
            {
                [0] = "Boolean",
                [1] = "Enum"
            };

        private static readonly Dictionary<int, string> KeywordDefinitionNames =
            new Dictionary<int, string>
            {
                [0] = "ShaderFeature",
                [1] = "MultiCompile",
                [2] = "Predefined",
                [3] = "DynamicBranch"
            };

        private static readonly Dictionary<int, string> KeywordScopeNames =
            new Dictionary<int, string>
            {
                [0] = "Local",
                [1] = "Global"
            };

        public static object NoShaderGraph()
        {
            return new
            {
                error = "Shader Graph package (com.unity.shadergraph) is not installed. Install URP/HDRP with Shader Graph support via Package Manager."
            };
        }

        public static bool IsShaderGraphInstalled
        {
            get { return FindTypeInAssemblies("UnityEditor.ShaderGraph.GraphData") != null; }
        }

        public static bool HasPackageFolder
        {
            get
            {
                return TryGetPackageRoot(out _, out _);
            }
        }

        public static bool HasTemplateDirectory
        {
            get
            {
                return TryGetTemplatesDirectory(out _);
            }
        }

        public static Type FindTypeInAssemblies(string fullName) => SkillsCommon.FindTypeByName(fullName);

        public static IEnumerable<object> GetTemplateDescriptors(bool includeSubGraphs)
        {
            if (!TryGetPackageRoot(out var packageRoot, out _))
                return Enumerable.Empty<object>();

            if (!TryGetTemplatesDirectory(out var templatesDirectory))
            {
                return new[]
                {
                    new
                    {
                        name = "Blank Shader Graph",
                        path = BuiltinBlankTemplatePath,
                        group = "Builtin",
                        kind = "Graph",
                        source = "BuiltinFallback"
                    }
                };
            }

            return Directory.EnumerateFiles(templatesDirectory, "*.*", SearchOption.AllDirectories)
                .Where(fullPath =>
                    fullPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
                    (includeSubGraphs && fullPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(fullPath => fullPath, StringComparer.OrdinalIgnoreCase)
                .Select(fullPath =>
                {
                    var relative = Path.GetRelativePath(packageRoot, fullPath).Replace('\\', '/');
                    var logicalPath = $"{PackageRoot}/{relative}";
                    var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/');
                    var extension = Path.GetExtension(fullPath);
                    return new
                    {
                        name = Path.GetFileNameWithoutExtension(fullPath),
                        path = logicalPath,
                        group = string.IsNullOrWhiteSpace(directory) ? null : directory,
                        kind = string.Equals(extension, ".shadersubgraph", StringComparison.OrdinalIgnoreCase) ? "SubGraph" : "Graph",
                        source = "PackageTemplate"
                    };
                })
                .ToArray();
        }

        public static string ResolveTemplatePath(string templateNameOrPath, bool allowSubGraphs, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(templateNameOrPath))
                return null;

            if (string.Equals(templateNameOrPath, BuiltinBlankTemplatePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(templateNameOrPath, "Blank Shader Graph", StringComparison.OrdinalIgnoreCase))
                return BuiltinBlankTemplatePath;

            if (templateNameOrPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                templateNameOrPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveTemplateFilePath(templateNameOrPath, out var resolvedTemplateFilePath))
                {
                    error = $"Template not found: {templateNameOrPath}";
                    return null;
                }

                var isAllowed = templateNameOrPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
                                (allowSubGraphs && templateNameOrPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase));
                if (!isAllowed)
                {
                    error = $"Unsupported template extension: {templateNameOrPath}";
                    return null;
                }

                return resolvedTemplateFilePath;
            }

            var templates = GetTemplateDescriptors(allowSubGraphs)
                .Cast<object>()
                .Select(x => JObject.FromObject(x))
                .Where(x => string.Equals(x["name"]?.ToString(), templateNameOrPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (templates.Length == 0)
            {
                error = $"Template '{templateNameOrPath}' not found";
                return null;
            }

            if (templates.Length > 1)
            {
                error = $"Template '{templateNameOrPath}' is ambiguous. Use templatePath instead.";
                return null;
            }

            var logicalPath = templates[0]["path"]?.ToString();
            if (!TryResolveTemplateFilePath(logicalPath, out var resolvedPath))
            {
                error = $"Template not found: {logicalPath}";
                return null;
            }

            return resolvedPath;
        }

        public static bool TryCreateBlankGraph(string assetPath, string graphPath, out string error)
        {
            error = null;
            if (!IsShaderGraphInstalled)
            {
                error = "Shader Graph package is not installed";
                return false;
            }

            try
            {
                var graphType = FindTypeInAssemblies("UnityEditor.ShaderGraph.GraphData");
                var categoryDataType = FindTypeInAssemblies("UnityEditor.ShaderGraph.CategoryData");
                if (graphType == null || categoryDataType == null)
                {
                    error = "Required Shader Graph graph creation types were not found";
                    return false;
                }

                var graph = Activator.CreateInstance(graphType, true);
                InvokeMethod(graph, "AddContexts");
                InvokeMethod(graph, "InitializeOutputs", null, null);

                var defaultCategoryMethod = categoryDataType.GetMethod("DefaultCategory", StaticFlags);
                if (defaultCategoryMethod == null)
                {
                    error = "CategoryData.DefaultCategory was not found";
                    return false;
                }

                var category = defaultCategoryMethod.Invoke(null, new object[] { null });
                InvokeMethod(graph, "AddCategory", category);
                SetMemberValue(graph, "path", string.IsNullOrWhiteSpace(graphPath) ? "Shader Graphs" : graphPath);

                return TrySaveGraphData(assetPath, graph, out error);
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryCopyTemplate(string templatePath, string destinationPath, out string error)
        {
            error = null;

            try
            {
                var sourceFullPath = Path.GetFullPath(templatePath);
                if (!File.Exists(sourceFullPath))
                {
                    error = $"Template file missing at path: {templatePath}";
                    return false;
                }

                var text = File.ReadAllText(sourceFullPath, Encoding.UTF8);
                var destinationFullPath = Path.GetFullPath(destinationPath);
                var destinationDirectory = Path.GetDirectoryName(destinationFullPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory) && !Directory.Exists(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                File.WriteAllText(destinationFullPath, text, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryCreateBlankSubGraph(string assetPath, string outputTypeName, string graphPath, out string error)
        {
            error = null;
            if (!IsShaderGraphInstalled)
            {
                error = "Shader Graph package is not installed";
                return false;
            }

            try
            {
                var graphType = FindTypeInAssemblies("UnityEditor.ShaderGraph.GraphData");
                var subGraphOutputNodeType = FindTypeInAssemblies("UnityEditor.ShaderGraph.SubGraphOutputNode");
                var concreteSlotValueType = FindTypeInAssemblies("UnityEditor.ShaderGraph.ConcreteSlotValueType");
                if (graphType == null || subGraphOutputNodeType == null || concreteSlotValueType == null)
                {
                    error = "Required Shader Graph editor types were not found";
                    return false;
                }

                var graph = Activator.CreateInstance(graphType, true);
                SetMemberValue(graph, "isSubGraph", true);
                SetMemberValue(graph, "path", string.IsNullOrWhiteSpace(graphPath) ? "Sub Graphs" : graphPath);

                var outputNode = Activator.CreateInstance(subGraphOutputNodeType, true);
                InvokeMethod(graph, "AddNode", outputNode);
                SetMemberValue(graph, "outputNode", outputNode);

                if (!EnumTryParse(concreteSlotValueType, outputTypeName, out var slotTypeValue))
                {
                    error = $"Invalid outputType '{outputTypeName}'. Valid values: {string.Join(", ", Enum.GetNames(concreteSlotValueType))}";
                    return false;
                }

                InvokeMethod(outputNode, "AddSlot", slotTypeValue);
                return TrySaveGraphData(assetPath, graph, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryLoadGraphData(string assetPath, out object graph, out string error)
        {
            if (!TryReadAssetText(assetPath, out var text, out error))
            {
                graph = null;
                return false;
            }

            return TryLoadGraphData(assetPath, text, out graph, out error);
        }

        public static bool TryLoadGraphData(string assetPath, string text, out object graph, out string error)
        {
            graph = null;
            error = null;

            if (!IsShaderGraphInstalled)
            {
                error = "Shader Graph package is not installed";
                return false;
            }

            try
            {
                var graphType = FindTypeInAssemblies("UnityEditor.ShaderGraph.GraphData");
                var messageManagerType = FindTypeInAssemblies("UnityEditor.Graphing.Util.MessageManager");
                var multiJsonType = FindTypeInAssemblies("UnityEditor.ShaderGraph.Serialization.MultiJson");
                if (graphType == null || multiJsonType == null)
                {
                    error = "Required Shader Graph serialization types were not found";
                    return false;
                }

                graph = Activator.CreateInstance(graphType, true);
                if (messageManagerType != null)
                    SetMemberValue(graph, "messageManager", Activator.CreateInstance(messageManagerType, true));
                SetMemberValue(graph, "assetGuid", AssetDatabase.AssetPathToGUID(assetPath));

                var deserializeMethod = multiJsonType.GetMethods(StaticFlags)
                    .FirstOrDefault(method => string.Equals(method.Name, "Deserialize", StringComparison.Ordinal));
                if (deserializeMethod == null)
                {
                    error = "MultiJson.Deserialize was not found";
                    return false;
                }

                deserializeMethod.MakeGenericMethod(graphType).Invoke(null, new object[] { graph, text, null, false });
                InvokeMethod(graph, "OnEnable");
                InvokeMethod(graph, "ValidateGraph");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TrySaveGraphData(string assetPath, object graph, out string error)
        {
            error = null;

            try
            {
                InvokeMethod(graph, "ValidateGraph");

                var fileUtilitiesType = FindTypeInAssemblies("UnityEditor.ShaderGraph.FileUtilities");
                if (fileUtilitiesType == null)
                {
                    error = "Shader Graph FileUtilities type was not found";
                    return false;
                }

                var writeMethod = fileUtilitiesType.GetMethod("WriteShaderGraphToDisk", StaticFlags);
                if (writeMethod == null)
                {
                    error = "WriteShaderGraphToDisk was not found";
                    return false;
                }

                var result = writeMethod.Invoke(null, new[] { assetPath, graph });
                if (result == null)
                {
                    error = $"Failed to save Shader Graph asset: {assetPath}";
                    return false;
                }

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryAddProperty(
            string assetPath,
            string propertyType,
            string displayName,
            string referenceName,
            object value,
            bool exposed,
            bool hidden,
            out object propertyInfo,
            out string error)
        {
            propertyInfo = null;
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var property = CreatePropertyInstance(graph, propertyType, displayName, referenceName, value, exposed, hidden, out error);
            if (property == null)
                return false;

            InvokeMethod(graph, "AddGraphInput", property, -1);
            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            propertyInfo = TryReadGraphDocument(assetPath, out var document, out error)
                ? FindProperty(document, displayName, referenceName)
                : null;
            return true;
        }

        public static bool TryUpdateProperty(
            string assetPath,
            string propertyName,
            string referenceName,
            string newDisplayName,
            string newReferenceName,
            object value,
            bool? exposed,
            bool? hidden,
            out object propertyInfo,
            out string error)
        {
            propertyInfo = null;
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var property = FindGraphInput(graph, "properties", propertyName, referenceName);
            if (property == null)
            {
                error = $"Property not found: {propertyName ?? referenceName}";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(newDisplayName))
                InvokeMethod(property, "SetDisplayNameAndSanitizeForGraph", graph, newDisplayName);
            if (!string.IsNullOrWhiteSpace(newReferenceName))
                InvokeMethod(property, "SetReferenceNameAndSanitizeForGraph", graph, newReferenceName);
            if (value != null && !TryAssignPropertyValue(property, value, out error))
                return false;
            if (exposed.HasValue)
                SetMemberValue(property, "m_GeneratePropertyBlock", exposed.Value);
            if (hidden.HasValue)
                SetMemberValue(property, "hidden", hidden.Value);

            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            if (!TryReadGraphDocument(assetPath, out var document, out error))
                return false;

            propertyInfo = FindProperty(
                document,
                string.IsNullOrWhiteSpace(newDisplayName) ? propertyName : newDisplayName,
                string.IsNullOrWhiteSpace(newReferenceName) ? referenceName : newReferenceName);
            return true;
        }

        public static bool TryRemoveProperty(string assetPath, string propertyName, string referenceName, out string error)
        {
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var property = FindGraphInput(graph, "properties", propertyName, referenceName);
            if (property == null)
            {
                error = $"Property not found: {propertyName ?? referenceName}";
                return false;
            }

            InvokeMethod(graph, "RemoveGraphInput", property);
            return TrySaveGraphData(assetPath, graph, out error);
        }

        public static bool TryAddKeyword(
            string assetPath,
            string keywordType,
            string displayName,
            string referenceName,
            string definition,
            string scope,
            string entries,
            int value,
            out object keywordInfo,
            out string error)
        {
            keywordInfo = null;
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName) ? (string.IsNullOrWhiteSpace(keywordType) ? "Boolean" : keywordType) : displayName;
            var keyword = CreateKeywordInstance(graph, keywordType, displayName, referenceName, definition, scope, entries, value, out error);
            if (keyword == null)
                return false;

            InvokeMethod(graph, "AddGraphInput", keyword, -1);
            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            keywordInfo = TryReadGraphDocument(assetPath, out var document, out error)
                ? FindKeyword(document, effectiveDisplayName, referenceName)
                : null;
            return true;
        }

        public static bool TryUpdateKeyword(
            string assetPath,
            string displayName,
            string referenceName,
            string newDisplayName,
            string newReferenceName,
            string definition,
            string scope,
            string entries,
            int? value,
            out object keywordInfo,
            out string error)
        {
            keywordInfo = null;
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var keyword = FindGraphInput(graph, "keywords", displayName, referenceName);
            if (keyword == null)
            {
                error = $"Keyword not found: {displayName ?? referenceName}";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(newDisplayName))
                InvokeMethod(keyword, "SetDisplayNameAndSanitizeForGraph", graph, newDisplayName);
            if (!string.IsNullOrWhiteSpace(newReferenceName))
                InvokeMethod(keyword, "SetReferenceNameAndSanitizeForGraph", graph, newReferenceName);
            if (!string.IsNullOrWhiteSpace(definition) && !TrySetEnumMember(keyword, "keywordDefinition", definition, out error))
                return false;
            if (!string.IsNullOrWhiteSpace(scope) && !TrySetEnumMember(keyword, "keywordScope", scope, out error))
                return false;
            if (!string.IsNullOrWhiteSpace(entries) && !TryAssignKeywordEntries(keyword, entries, out error))
                return false;
            if (value.HasValue)
                SetMemberValue(keyword, "value", value.Value);

            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            if (!TryReadGraphDocument(assetPath, out var document, out error))
                return false;

            keywordInfo = FindKeyword(
                document,
                string.IsNullOrWhiteSpace(newDisplayName) ? displayName : newDisplayName,
                string.IsNullOrWhiteSpace(newReferenceName) ? referenceName : newReferenceName);
            return true;
        }

        public static bool TryRemoveKeyword(string assetPath, string displayName, string referenceName, out string error)
        {
            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var keyword = FindGraphInput(graph, "keywords", displayName, referenceName);
            if (keyword == null)
            {
                error = $"Keyword not found: {displayName ?? referenceName}";
                return false;
            }

            InvokeMethod(graph, "RemoveGraphInput", keyword);
            return TrySaveGraphData(assetPath, graph, out error);
        }

        private static bool TryReadAssetText(string assetPath, out string text, out string error)
        {
            text = null;
            error = null;

            try
            {
                var fullPath = Path.GetFullPath(assetPath);
                if (!File.Exists(fullPath))
                {
                    error = $"Shader Graph asset not found: {assetPath}";
                    return false;
                }

                text = File.ReadAllText(fullPath, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryReadGraphDocumentAndLoadGraphData(string assetPath, out ShaderGraphDocument document, out object graph, out string error)
        {
            document = null;
            graph = null;

            if (!TryReadAssetText(assetPath, out var text, out error))
                return false;

            if (!TryReadGraphDocument(assetPath, text, out document, out error))
                return false;

            TryLoadGraphData(assetPath, text, out graph, out _);
            return true;
        }

        public static bool TryReadGraphDocument(string assetPath, out ShaderGraphDocument document, out string error)
        {
            if (!TryReadAssetText(assetPath, out var text, out error))
            {
                document = null;
                return false;
            }

            return TryReadGraphDocument(assetPath, text, out document, out error);
        }

        public static bool TryReadGraphDocument(string assetPath, string text, out ShaderGraphDocument document, out string error)
        {
            document = null;
            error = null;

            try
            {
                var objects = ParseMultiJson(text);
                if (objects.Count == 0)
                {
                    error = $"No JSON objects found in Shader Graph asset: {assetPath}";
                    return false;
                }

                document = new ShaderGraphDocument
                {
                    AssetPath = assetPath,
                    Root = objects[0]
                };

                foreach (var obj in objects)
                {
                    document.OrderedObjects.Add(obj);
                    var objectId = obj["m_ObjectId"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(objectId))
                        document.ObjectsById[objectId] = obj;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static object DescribeGraphInfo(ShaderGraphDocument document)
        {
            var propertyIds = ExtractReferenceIds(document.Root["m_Properties"]);
            var keywordIds = ExtractReferenceIds(document.Root["m_Keywords"]);
            var nodeIds = ExtractReferenceIds(document.Root["m_Nodes"]);
            var edgeCount = document.Root["m_Edges"]?.Values<JToken>().Count() ?? 0;
            var targetIds = ExtractReferenceIds(document.Root["m_ActiveTargets"]);

            var targetTypes = targetIds
                .Select(id => document.ObjectsById.TryGetValue(id, out var target) ? ShortTypeName(target["m_Type"]?.ToString()) : null)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var nodeTypeCounts = nodeIds
                .Select(id => document.ObjectsById.TryGetValue(id, out var node) ? ShortTypeName(node["m_Type"]?.ToString()) : null)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .GroupBy(type => type, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    type = group.Key,
                    count = group.Count()
                })
                .ToArray();

            return new
            {
                success = true,
                assetPath = document.AssetPath,
                kind = document.AssetPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase) ? "SubGraph" : "Graph",
                graphType = ShortTypeName(document.Root["m_Type"]?.ToString()),
                graphPath = document.Root["m_Path"]?.ToString(),
                precision = document.Root["m_GraphPrecision"]?.ToObject<int?>(),
                previewMode = document.Root["m_PreviewMode"]?.ToObject<int?>(),
                propertyCount = propertyIds.Count,
                keywordCount = keywordIds.Count,
                nodeCount = nodeIds.Count,
                edgeCount,
                targetTypes,
                nodeTypeCounts
            };
        }

        public static object DescribeGraphStructure(ShaderGraphDocument document, int maxNodes, int maxEdges)
        {
            var nodes = ExtractReferenceIds(document.Root["m_Nodes"])
                .Select(id => document.ObjectsById.TryGetValue(id, out var node) ? DescribeNode(node) : null)
                .Where(node => node != null)
                .Take(Math.Max(1, maxNodes))
                .ToArray();

            var edges = (document.Root["m_Edges"] as JArray ?? new JArray())
                .Select(edge => DescribeEdge(document, edge as JObject))
                .Where(edge => edge != null)
                .Take(Math.Max(1, maxEdges))
                .ToArray();

            return new
            {
                success = true,
                assetPath = document.AssetPath,
                nodes,
                edges,
                properties = GetProperties(document),
                keywords = GetKeywords(document)
            };
        }

        public static object DescribeGraphStructure(ShaderGraphDocument document, object graph, int maxNodes, int maxEdges)
        {
            if (graph == null)
                return DescribeGraphStructure(document, maxNodes, maxEdges);

            var nodes = GetGraphNodes(graph)
                .Select(node => DescribeRuntimeNode(graph, node))
                .Where(node => node != null)
                .Take(Math.Max(1, maxNodes))
                .ToArray();

            var edges = GetGraphEdges(graph)
                .Select(DescribeRuntimeEdge)
                .Where(edge => edge != null)
                .Take(Math.Max(1, maxEdges))
                .ToArray();

            return new
            {
                success = true,
                assetPath = document.AssetPath,
                nodes,
                edges,
                properties = GetProperties(document),
                keywords = GetKeywords(document)
            };
        }

        public static object[] GetProperties(ShaderGraphDocument document)
        {
            return ExtractReferenceIds(document.Root["m_Properties"])
                .Select(id => document.ObjectsById.TryGetValue(id, out var property) ? DescribeProperty(property) : null)
                .Where(property => property != null)
                .ToArray();
        }

        public static object[] GetKeywords(ShaderGraphDocument document)
        {
            return ExtractReferenceIds(document.Root["m_Keywords"])
                .Select(id => document.ObjectsById.TryGetValue(id, out var keyword) ? DescribeKeyword(keyword) : null)
                .Where(keyword => keyword != null)
                .ToArray();
        }

        public static object FindProperty(ShaderGraphDocument document, string displayName, string referenceName)
        {
            return GetProperties(document)
                .Select(JObject.FromObject)
                .FirstOrDefault(item => MatchesNamedItem(item, displayName, referenceName));
        }

        public static object FindKeyword(ShaderGraphDocument document, string displayName, string referenceName)
        {
            return GetKeywords(document)
                .Select(JObject.FromObject)
                .FirstOrDefault(item => MatchesNamedItem(item, displayName, referenceName));
        }

        public static object[] GetSupportedNodes()
        {
            return ShaderGraphNodeRegistry.GetDescriptors()
                .Select(DescribeSupportedNode)
                .Where(item => item != null)
                .ToArray();
        }

        public static bool TryAddNode(
            string assetPath,
            string nodeType,
            float x,
            float y,
            object settings,
            out object nodeInfo,
            out string error)
        {
            nodeInfo = null;
            error = null;

            var descriptor = ShaderGraphNodeRegistry.Find(nodeType);
            if (descriptor == null)
            {
                error = $"Unsupported nodeType '{nodeType}'. Use shadergraph_list_supported_nodes to inspect supported values.";
                return false;
            }

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var runtimeType = FindTypeInAssemblies(descriptor.RuntimeTypeName);
            if (runtimeType == null)
            {
                error = $"Shader Graph node runtime type not found: {descriptor.RuntimeTypeName}";
                return false;
            }

            if (!ValidateNodeGraphScope(graph, descriptor, out error))
                return false;

            try
            {
                var node = Activator.CreateInstance(runtimeType, true);
                if (!TryApplyNodeSettings(graph, node, descriptor, settings, requireSettings: false, out error))
                    return false;

                SetNodePosition(node, x, y);
                InvokeMethod(graph, "AddNode", node);
                if (!TrySaveGraphData(assetPath, graph, out error))
                    return false;

                nodeInfo = DescribeRuntimeNode(graph, node);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryRemoveNode(string assetPath, string nodeId, out object removedInfo, out string error)
        {
            removedInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var node = FindGraphNode(graph, nodeId);
            if (node == null)
            {
                error = $"Node '{nodeId}' was not found";
                return false;
            }

            if (!TryGetBoolValue(GetMemberValue(node, "canDeleteNode"), true))
            {
                error = $"Node '{nodeId}' cannot be deleted";
                return false;
            }

            var removedEdgeCount = GetGraphEdges(graph)
                .Count(edge => EdgeTouchesNode(edge, nodeId));
            var typeName = node.GetType().Name;
            var name = GetMemberValue(node, "name")?.ToString();

            try
            {
                InvokeMethod(graph, "RemoveNode", node);
                if (!TrySaveGraphData(assetPath, graph, out error))
                    return false;

                removedInfo = new
                {
                    nodeId,
                    type = typeName,
                    name,
                    removedEdgeCount
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryMoveNode(string assetPath, string nodeId, float x, float y, out object nodeInfo, out string error)
        {
            nodeInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var node = FindGraphNode(graph, nodeId);
            if (node == null)
            {
                error = $"Node '{nodeId}' was not found";
                return false;
            }

            try
            {
                SetNodePosition(node, x, y);
                if (!TrySaveGraphData(assetPath, graph, out error))
                    return false;

                nodeInfo = DescribeRuntimeNode(graph, node);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryConnectNodes(
            string assetPath,
            string fromNodeId,
            int fromSlotId,
            string toNodeId,
            int toSlotId,
            out object edgeInfo,
            out string error)
        {
            edgeInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            if (!TryResolveNodeAndSlot(graph, fromNodeId, fromSlotId, requireInput: false, out var fromNode, out _, out error))
                return false;
            if (!TryResolveNodeAndSlot(graph, toNodeId, toSlotId, requireInput: true, out var toNode, out _, out error))
                return false;

            var existing = FindExactEdge(graph, fromNodeId, fromSlotId, toNodeId, toSlotId);
            if (existing != null)
            {
                edgeInfo = DescribeRuntimeEdge(existing);
                return true;
            }

            try
            {
                var fromSlotRef = InvokeMethod(fromNode, "GetSlotReference", fromSlotId);
                var toSlotRef = InvokeMethod(toNode, "GetSlotReference", toSlotId);
                var edge = InvokeMethod(graph, "Connect", fromSlotRef, toSlotRef);
                if (!TrySaveGraphData(assetPath, graph, out error))
                    return false;

                edgeInfo = DescribeRuntimeEdge(edge);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TryDisconnectNodes(
            string assetPath,
            string fromNodeId,
            int fromSlotId,
            string toNodeId,
            int toSlotId,
            out object edgeInfo,
            out string error)
        {
            edgeInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var edge = FindExactEdge(graph, fromNodeId, fromSlotId, toNodeId, toSlotId);
            if (edge == null)
            {
                error = $"Edge {fromNodeId}:{fromSlotId} -> {toNodeId}:{toSlotId} was not found";
                return false;
            }

            edgeInfo = DescribeRuntimeEdge(edge);

            try
            {
                InvokeMethod(graph, "RemoveEdge", edge);
                if (!TrySaveGraphData(assetPath, graph, out error))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        public static bool TrySetNodeDefaults(
            string assetPath,
            string nodeId,
            int slotId,
            object value,
            out object nodeInfo,
            out string error)
        {
            nodeInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            if (!TryResolveNodeAndSlot(graph, nodeId, slotId, requireInput: true, out var node, out var slot, out error))
                return false;

            if (IsSlotConnected(graph, nodeId, slotId))
            {
                error = $"Input slot {slotId} on node '{nodeId}' is connected. Disconnect it before setting a default value.";
                return false;
            }

            if (!TrySetSlotValue(slot, value, out error))
                return false;

            SyncNodeSettingFromInputSlot(node, slotId, slot);
            InvokeNodeUpdate(node);

            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            nodeInfo = DescribeRuntimeNode(graph, node);
            return true;
        }

        public static bool TrySetNodeSettings(
            string assetPath,
            string nodeId,
            object settings,
            out object nodeInfo,
            out string error)
        {
            nodeInfo = null;
            error = null;

            if (!TryLoadGraphData(assetPath, out var graph, out error))
                return false;

            var node = FindGraphNode(graph, nodeId);
            if (node == null)
            {
                error = $"Node '{nodeId}' was not found";
                return false;
            }

            var descriptor = ShaderGraphNodeRegistry.Find(node.GetType().Name);
            if (descriptor == null)
            {
                error = $"Node type '{node.GetType().Name}' is not in the supported editing subset";
                return false;
            }

            if (!TryApplyNodeSettings(graph, node, descriptor, settings, requireSettings: true, out error))
                return false;

            if (!TrySaveGraphData(assetPath, graph, out error))
                return false;

            nodeInfo = DescribeRuntimeNode(graph, node);
            return true;
        }

        private static object CreatePropertyInstance(
            object graph,
            string propertyType,
            string displayName,
            string referenceName,
            object value,
            bool exposed,
            bool hidden,
            out string error)
        {
            error = null;

            if (!PropertyTypeMap.TryGetValue(propertyType ?? string.Empty, out var propertyTypeName))
            {
                error = $"Unsupported propertyType '{propertyType}'. Supported values: {string.Join(", ", PropertyTypeMap.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}";
                return null;
            }

            var propertyRuntimeType = FindTypeInAssemblies(propertyTypeName);
            if (propertyRuntimeType == null)
            {
                error = $"Shader Graph property type not found: {propertyTypeName}";
                return null;
            }

            var property = Activator.CreateInstance(propertyRuntimeType, true);
            SetMemberValue(property, "displayName", displayName);
            SetMemberValue(property, "m_GeneratePropertyBlock", exposed);
            SetMemberValue(property, "hidden", hidden);

            if (!string.IsNullOrWhiteSpace(referenceName))
                SetMemberValue(property, "m_OverrideReferenceName", referenceName);

            if (value != null && !TryAssignPropertyValue(property, value, out error))
                return null;

            return property;
        }

        private static bool TryAssignPropertyValue(object property, object value, out string error)
        {
            error = null;
            var propertyTypeName = property.GetType().Name;

            try
            {
                switch (propertyTypeName)
                {
                    case "Vector1ShaderProperty":
                        SetMemberValue(property, "value", ConvertToFloat(value));
                        return true;
                    case "Vector2ShaderProperty":
                        SetMemberValue(property, "value", ConvertToVector2(value));
                        return true;
                    case "Vector3ShaderProperty":
                        SetMemberValue(property, "value", ConvertToVector3(value));
                        return true;
                    case "Vector4ShaderProperty":
                        SetMemberValue(property, "value", ConvertToVector4(value, 4));
                        return true;
                    case "ColorShaderProperty":
                        SetMemberValue(property, "value", ConvertToColor(value));
                        return true;
                    case "BooleanShaderProperty":
                        SetMemberValue(property, "value", ConvertToBool(value));
                        return true;
                    case "Texture2DShaderProperty":
                        return TryAssignTexturePropertyValue(property, value, out error);
                    default:
                        error = $"Unsupported property runtime type: {propertyTypeName}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryAssignTexturePropertyValue(object property, object value, out string error)
        {
            error = null;

            if (value == null)
                return true;

            var assetPath = value.ToString();
            if (string.IsNullOrWhiteSpace(assetPath))
                return true;

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (texture == null)
            {
                error = $"Texture2D not found: {assetPath}";
                return false;
            }

            var serializableTextureType = FindTypeInAssemblies("UnityEditor.ShaderGraph.Internal.SerializableTexture");
            if (serializableTextureType == null)
            {
                error = "SerializableTexture type was not found";
                return false;
            }

            var serializableTexture = Activator.CreateInstance(serializableTextureType, true);
            SetMemberValue(serializableTexture, "texture", texture);
            SetMemberValue(property, "value", serializableTexture);
            return true;
        }

        private static object CreateKeywordInstance(
            object graph,
            string keywordTypeName,
            string displayName,
            string referenceName,
            string definition,
            string scope,
            string entries,
            int value,
            out string error)
        {
            error = null;

            var shaderKeywordType = FindTypeInAssemblies("UnityEditor.ShaderGraph.ShaderKeyword");
            var keywordTypeEnum = FindTypeInAssemblies("UnityEditor.ShaderGraph.KeywordType");
            if (shaderKeywordType == null || keywordTypeEnum == null)
            {
                error = "ShaderKeyword runtime types were not found";
                return null;
            }

            if (!EnumTryParse(keywordTypeEnum, string.IsNullOrWhiteSpace(keywordTypeName) ? "Boolean" : keywordTypeName, out var keywordTypeValue))
            {
                error = $"Invalid keywordType '{keywordTypeName}'. Valid values: {string.Join(", ", Enum.GetNames(keywordTypeEnum))}";
                return null;
            }

            var keyword = Activator.CreateInstance(shaderKeywordType, new[] { keywordTypeValue });
            SetMemberValue(keyword, "displayName", string.IsNullOrWhiteSpace(displayName) ? keywordTypeValue.ToString() : displayName);

            if (!string.IsNullOrWhiteSpace(referenceName))
                SetMemberValue(keyword, "m_OverrideReferenceName", referenceName);
            if (!string.IsNullOrWhiteSpace(definition) && !TrySetEnumMember(keyword, "keywordDefinition", definition, out error))
                return null;
            if (!string.IsNullOrWhiteSpace(scope) && !TrySetEnumMember(keyword, "keywordScope", scope, out error))
                return null;

            if (!string.IsNullOrWhiteSpace(entries) && !TryAssignKeywordEntries(keyword, entries, out error))
                return null;

            SetMemberValue(keyword, "value", value);
            return keyword;
        }

        private static bool TryAssignKeywordEntries(object keyword, string entries, out string error)
        {
            error = null;

            var parsedEntries = ParseKeywordEntries(entries);
            if (parsedEntries.Count == 0)
            {
                error = "Keyword entries are empty";
                return false;
            }

            var keywordEntryType = FindTypeInAssemblies("UnityEditor.ShaderGraph.KeywordEntry");
            if (keywordEntryType == null)
            {
                error = "KeywordEntry type was not found";
                return false;
            }

            var listType = typeof(List<>).MakeGenericType(keywordEntryType);
            var list = Activator.CreateInstance(listType);
            var addMethod = listType.GetMethod("Add");
            var constructor = keywordEntryType.GetConstructor(InstanceFlags, null, new[] { typeof(int), typeof(string), typeof(string) }, null);
            if (constructor == null || addMethod == null)
            {
                error = "KeywordEntry constructor was not found";
                return false;
            }

            for (var i = 0; i < parsedEntries.Count; i++)
            {
                var entry = constructor.Invoke(new object[] { i + 1, parsedEntries[i].displayName, parsedEntries[i].referenceName });
                addMethod.Invoke(list, new[] { entry });
            }

            SetMemberValue(keyword, "entries", list);
            SetMemberValue(keyword, "keywordType", Enum.Parse(FindTypeInAssemblies("UnityEditor.ShaderGraph.KeywordType"), "Enum"));
            return true;
        }

        private static List<(string displayName, string referenceName)> ParseKeywordEntries(string entries)
        {
            var result = new List<(string displayName, string referenceName)>();
            if (string.IsNullOrWhiteSpace(entries))
                return result;

            try
            {
                if (entries.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var token = JToken.Parse(entries);
                    foreach (var item in token.Children())
                    {
                        if (item.Type == JTokenType.String)
                        {
                            var displayName = item.ToString();
                            result.Add((displayName, SanitizeKeywordReferenceName(displayName)));
                        }
                        else if (item.Type == JTokenType.Object)
                        {
                            var displayName = item["displayName"]?.ToString() ?? item["name"]?.ToString();
                            var referenceName = item["referenceName"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(displayName))
                                result.Add((displayName, string.IsNullOrWhiteSpace(referenceName) ? SanitizeKeywordReferenceName(displayName) : referenceName));
                        }
                    }
                }
                else
                {
                    foreach (var item in entries.Split(','))
                    {
                        var displayName = item.Trim();
                        if (!string.IsNullOrWhiteSpace(displayName))
                            result.Add((displayName, SanitizeKeywordReferenceName(displayName)));
                    }
                }
            }
            catch
            {
                return new List<(string displayName, string referenceName)>();
            }

            return result;
        }

        private static string SanitizeKeywordReferenceName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "VALUE";

            var chars = input.Trim().Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_').ToArray();
            return new string(chars);
        }

        private static object FindGraphInput(object graph, string memberName, string displayName, string referenceName)
        {
            var enumerable = GetMemberValue(graph, memberName) as IEnumerable;
            if (enumerable == null)
                return null;

            foreach (var item in enumerable)
            {
                var currentDisplayName = GetMemberValue(item, "displayName")?.ToString();
                var currentReferenceName = GetMemberValue(item, "referenceName")?.ToString();
                if ((!string.IsNullOrWhiteSpace(displayName) && string.Equals(currentDisplayName, displayName, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(referenceName) && string.Equals(currentReferenceName, referenceName, StringComparison.OrdinalIgnoreCase)))
                {
                    return item;
                }
            }

            return null;
        }

        private static List<JObject> ParseMultiJson(string text)
        {
            var result = new List<JObject>();
            var start = -1;
            var depth = 0;
            var inString = false;
            var escape = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (start < 0)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;
                    if (ch == '{')
                    {
                        start = i;
                        depth = 1;
                        inString = false;
                        escape = false;
                    }
                    continue;
                }

                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (ch == '\\')
                    {
                        escape = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                    continue;
                }

                if (ch != '}')
                    continue;

                depth--;
                if (depth != 0)
                    continue;

                var json = text.Substring(start, i - start + 1);
                result.Add(JObject.Parse(json));
                start = -1;
            }

            return result;
        }

        private static List<string> ExtractReferenceIds(JToken token)
        {
            if (!(token is JArray array))
                return new List<string>();

            return array
                .Select(item => item?["m_Id"]?.ToString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
        }

        private static object DescribeSupportedNode(ShaderGraphSupportedNodeDescriptor descriptor)
        {
            var runtimeType = FindTypeInAssemblies(descriptor.RuntimeTypeName);
            if (runtimeType == null)
                return null;

            var node = Activator.CreateInstance(runtimeType, true);
            if (string.Equals(descriptor.NodeType, "PropertyNode", StringComparison.Ordinal))
            {
                var property = CreatePropertyInstance(null, "float", "PreviewProperty", "_PreviewProperty", 1f, true, false, out var propertyError);
                if (property == null)
                    return null;

                SetMemberValue(node, "property", property);
            }

            return new
            {
                nodeType = descriptor.NodeType,
                aliases = descriptor.Aliases,
                runtimeType = descriptor.RuntimeTypeName,
                validatedVersions = descriptor.ValidatedVersions,
                supportsGraph = descriptor.SupportsGraph,
                supportsSubGraph = descriptor.SupportsSubGraph,
                requiresExistingProperty = descriptor.RequiresExistingProperty,
                notes = descriptor.Notes,
                settings = descriptor.Settings.Select(setting => new
                {
                    name = setting.Name,
                    valueType = setting.ValueType,
                    options = setting.Options,
                    notes = setting.Notes
                }).ToArray(),
                slots = DescribeNodeSlots(null, node, GetMemberValue(node, "objectId")?.ToString())
            };
        }

        private static object DescribeRuntimeNode(object graph, object node)
        {
            if (node == null)
                return null;

            var nodeId = GetMemberValue(node, "objectId")?.ToString();
            var slots = DescribeNodeSlots(graph, node, nodeId);
            var position = DescribeNodePosition(node);

            return new
            {
                nodeId,
                id = nodeId,
                type = node.GetType().Name,
                name = GetMemberValue(node, "name")?.ToString() ?? node.GetType().Name,
                position,
                slotCount = slots.Length,
                slots,
                settings = DescribeNodeSettings(node)
            };
        }

        private static object[] DescribeNodeSlots(object graph, object node, string nodeId)
        {
            var inputSlots = GetNodeSlots(node, true)
                .Select(slot => DescribeRuntimeSlot(graph, nodeId, slot))
                .Where(slot => slot != null);
            var outputSlots = GetNodeSlots(node, false)
                .Select(slot => DescribeRuntimeSlot(graph, nodeId, slot))
                .Where(slot => slot != null);

            return inputSlots
                .Concat(outputSlots)
                .OrderBy(slot => JObject.FromObject(slot)["slotId"]?.ToObject<int?>() ?? int.MaxValue)
                .ToArray();
        }

        private static object DescribeRuntimeSlot(object graph, string nodeId, object slot)
        {
            if (slot == null)
                return null;

            var slotId = Convert.ToInt32(GetMemberValue(slot, "id"), CultureInfo.InvariantCulture);
            var isInput = TryGetBoolValue(GetMemberValue(slot, "isInputSlot"));
            var isOutput = TryGetBoolValue(GetMemberValue(slot, "isOutputSlot"));
            var rawValue = isInput ? GetSlotValue(slot) : null;

            return new
            {
                slotId,
                displayName = GetMemberValue(slot, "displayName")?.ToString(),
                direction = isInput ? "input" : (isOutput ? "output" : "unknown"),
                valueType = GetSlotTypeName(slot, "valueType"),
                concreteValueType = GetSlotTypeName(slot, "concreteValueType"),
                isConnected = graph != null && !string.IsNullOrWhiteSpace(nodeId) && IsSlotConnected(graph, nodeId, slotId),
                defaultValue = isInput ? SerializeSlotValue(rawValue) : null
            };
        }

        private static object DescribeNodeSettings(object node)
        {
            if (node == null)
                return null;

            switch (node.GetType().Name)
            {
                case "PropertyNode":
                {
                    var property = GetMemberValue(node, "property");
                    return new
                    {
                        propertyReferenceName = property != null ? GetMemberValue(property, "referenceName")?.ToString() : null
                    };
                }
                case "BooleanNode":
                    return new
                    {
                        value = TryGetBoolValue(GetMemberValue(node, "m_Value"))
                    };
                case "ColorNode":
                {
                    var colorData = GetMemberValue(node, "m_Color");
                    var colorValue = colorData != null ? GetMemberValue(colorData, "color") : null;
                    var colorMode = colorData != null ? GetMemberValue(colorData, "mode")?.ToString() : null;
                    return new
                    {
                        value = RenderPipelineSkillsCommon.ToSerializableValue(colorValue),
                        mode = colorMode
                    };
                }
                case "Vector1Node":
                    return new
                    {
                        value = GetMemberValue(node, "m_Value")
                    };
                case "Vector2Node":
                case "Vector3Node":
                case "Vector4Node":
                    return new
                    {
                        value = RenderPipelineSkillsCommon.ToSerializableValue(GetMemberValue(node, "m_Value"))
                    };
                case "UVNode":
                    return new
                    {
                        channel = GetMemberValue(node, "uvChannel")?.ToString() ?? GetMemberValue(node, "m_OutputChannel")?.ToString()
                    };
                case "PositionNode":
                case "NormalVectorNode":
                case "ViewDirectionNode":
                    return new
                    {
                        space = GetMemberValue(node, "space")?.ToString() ?? GetMemberValue(node, "m_Space")?.ToString()
                    };
                default:
                    return new { };
            }
        }

        private static object DescribeNodePosition(object node)
        {
            var drawState = GetMemberValue(node, "drawState");
            var rectValue = drawState != null ? GetMemberValue(drawState, "position") : null;
            if (!(rectValue is Rect rect))
                return null;

            return new
            {
                x = rect.x,
                y = rect.y,
                width = rect.width,
                height = rect.height
            };
        }

        private static object DescribeRuntimeEdge(object edge)
        {
            if (edge == null)
                return null;

            var outputSlotRef = GetMemberValue(edge, "outputSlot");
            var inputSlotRef = GetMemberValue(edge, "inputSlot");
            var outputNode = outputSlotRef != null ? GetMemberValue(outputSlotRef, "node") : null;
            var inputNode = inputSlotRef != null ? GetMemberValue(inputSlotRef, "node") : null;
            var outputNodeId = outputNode != null ? GetMemberValue(outputNode, "objectId")?.ToString() : null;
            var inputNodeId = inputNode != null ? GetMemberValue(inputNode, "objectId")?.ToString() : null;

            return new
            {
                outputNodeId,
                outputNode = outputNode != null ? GetMemberValue(outputNode, "name")?.ToString() ?? outputNode.GetType().Name : null,
                outputSlotId = outputSlotRef != null ? Convert.ToInt32(GetMemberValue(outputSlotRef, "slotId"), CultureInfo.InvariantCulture) : (int?)null,
                inputNodeId,
                inputNode = inputNode != null ? GetMemberValue(inputNode, "name")?.ToString() ?? inputNode.GetType().Name : null,
                inputSlotId = inputSlotRef != null ? Convert.ToInt32(GetMemberValue(inputSlotRef, "slotId"), CultureInfo.InvariantCulture) : (int?)null
            };
        }

        private static IEnumerable<object> GetGraphNodes(object graph)
        {
            var nodeType = FindTypeInAssemblies("UnityEditor.ShaderGraph.AbstractMaterialNode");
            if (graph == null || nodeType == null)
                return Enumerable.Empty<object>();

            var method = graph.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, "GetNodes", StringComparison.Ordinal) &&
                    candidate.IsGenericMethodDefinition &&
                    candidate.GetParameters().Length == 0);
            if (method == null)
                return Enumerable.Empty<object>();

            var result = method.MakeGenericMethod(nodeType).Invoke(graph, Array.Empty<object>()) as IEnumerable;
            return result?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        private static IEnumerable<object> GetGraphEdges(object graph)
        {
            return (GetMemberValue(graph, "edges") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        private static object FindGraphNode(object graph, string nodeId)
        {
            return GetGraphNodes(graph)
                .FirstOrDefault(node => string.Equals(GetMemberValue(node, "objectId")?.ToString(), nodeId, StringComparison.Ordinal));
        }

        private static object[] GetNodeSlots(object node, bool inputs)
        {
            if (node == null)
                return Array.Empty<object>();

            var materialSlotType = FindTypeInAssemblies("UnityEditor.ShaderGraph.MaterialSlot");
            if (materialSlotType == null)
                return Array.Empty<object>();

            var listType = typeof(List<>).MakeGenericType(materialSlotType);
            var slotList = Activator.CreateInstance(listType);
            var method = node.GetType().GetMethods(InstanceFlags)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, inputs ? "GetInputSlots" : "GetOutputSlots", StringComparison.Ordinal) &&
                    candidate.IsGenericMethodDefinition &&
                    candidate.GetParameters().Length == 1);
            if (method == null)
                return Array.Empty<object>();

            method.MakeGenericMethod(materialSlotType).Invoke(node, new[] { slotList });
            return (slotList as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        }

        private static bool ValidateNodeGraphScope(object graph, ShaderGraphSupportedNodeDescriptor descriptor, out string error)
        {
            error = null;
            var isSubGraph = TryGetBoolValue(GetMemberValue(graph, "isSubGraph"));
            if (isSubGraph && !descriptor.SupportsSubGraph)
            {
                error = $"{descriptor.NodeType} is not enabled for SubGraph editing";
                return false;
            }

            if (!isSubGraph && !descriptor.SupportsGraph)
            {
                error = $"{descriptor.NodeType} is not enabled for Graph editing";
                return false;
            }

            return true;
        }

        private static bool TryResolveNodeAndSlot(object graph, string nodeId, int slotId, bool requireInput, out object node, out object slot, out string error)
        {
            node = FindGraphNode(graph, nodeId);
            slot = null;
            error = null;

            if (node == null)
            {
                error = $"Node '{nodeId}' was not found";
                return false;
            }

            slot = GetNodeSlots(node, inputs: requireInput)
                .FirstOrDefault(candidate => Convert.ToInt32(GetMemberValue(candidate, "id"), CultureInfo.InvariantCulture) == slotId);

            if (slot == null)
            {
                error = $"Slot {slotId} was not found on node '{nodeId}'";
                return false;
            }

            return true;
        }

        private static object FindExactEdge(object graph, string fromNodeId, int fromSlotId, string toNodeId, int toSlotId)
        {
            return GetGraphEdges(graph).FirstOrDefault(edge =>
            {
                var outputSlotRef = GetMemberValue(edge, "outputSlot");
                var inputSlotRef = GetMemberValue(edge, "inputSlot");
                var outputNode = outputSlotRef != null ? GetMemberValue(outputSlotRef, "node") : null;
                var inputNode = inputSlotRef != null ? GetMemberValue(inputSlotRef, "node") : null;
                var outputNodeId = outputNode != null ? GetMemberValue(outputNode, "objectId")?.ToString() : null;
                var inputNodeId = inputNode != null ? GetMemberValue(inputNode, "objectId")?.ToString() : null;
                var outputSlotId = outputSlotRef != null ? Convert.ToInt32(GetMemberValue(outputSlotRef, "slotId"), CultureInfo.InvariantCulture) : -1;
                var inputSlotId = inputSlotRef != null ? Convert.ToInt32(GetMemberValue(inputSlotRef, "slotId"), CultureInfo.InvariantCulture) : -1;

                return string.Equals(outputNodeId, fromNodeId, StringComparison.Ordinal) &&
                       outputSlotId == fromSlotId &&
                       string.Equals(inputNodeId, toNodeId, StringComparison.Ordinal) &&
                       inputSlotId == toSlotId;
            });
        }

        private static bool EdgeTouchesNode(object edge, string nodeId)
        {
            var outputSlotRef = GetMemberValue(edge, "outputSlot");
            var inputSlotRef = GetMemberValue(edge, "inputSlot");
            var outputNode = outputSlotRef != null ? GetMemberValue(outputSlotRef, "node") : null;
            var inputNode = inputSlotRef != null ? GetMemberValue(inputSlotRef, "node") : null;
            var outputNodeId = outputNode != null ? GetMemberValue(outputNode, "objectId")?.ToString() : null;
            var inputNodeId = inputNode != null ? GetMemberValue(inputNode, "objectId")?.ToString() : null;
            return string.Equals(outputNodeId, nodeId, StringComparison.Ordinal) ||
                   string.Equals(inputNodeId, nodeId, StringComparison.Ordinal);
        }

        private static bool IsSlotConnected(object graph, string nodeId, int slotId)
        {
            return GetGraphEdges(graph).Any(edge =>
            {
                var outputSlotRef = GetMemberValue(edge, "outputSlot");
                var inputSlotRef = GetMemberValue(edge, "inputSlot");
                return SlotReferenceMatches(outputSlotRef, nodeId, slotId) || SlotReferenceMatches(inputSlotRef, nodeId, slotId);
            });
        }

        private static bool SlotReferenceMatches(object slotReference, string nodeId, int slotId)
        {
            if (slotReference == null)
                return false;

            var node = GetMemberValue(slotReference, "node");
            var candidateNodeId = node != null ? GetMemberValue(node, "objectId")?.ToString() : null;
            var candidateSlotId = Convert.ToInt32(GetMemberValue(slotReference, "slotId"), CultureInfo.InvariantCulture);
            return string.Equals(candidateNodeId, nodeId, StringComparison.Ordinal) && candidateSlotId == slotId;
        }

        private static string GetSlotTypeName(object slot, string memberName)
        {
            var value = GetMemberValue(slot, memberName);
            return value?.ToString();
        }

        private static object GetSlotValue(object slot)
        {
            return GetMemberValue(slot, "value");
        }

        private static object SerializeSlotValue(object value)
        {
            if (value == null)
                return null;

            if (value is Matrix4x4 matrix)
            {
                return new
                {
                    m00 = matrix.m00, m01 = matrix.m01, m02 = matrix.m02, m03 = matrix.m03,
                    m10 = matrix.m10, m11 = matrix.m11, m12 = matrix.m12, m13 = matrix.m13,
                    m20 = matrix.m20, m21 = matrix.m21, m22 = matrix.m22, m23 = matrix.m23,
                    m30 = matrix.m30, m31 = matrix.m31, m32 = matrix.m32, m33 = matrix.m33
                };
            }

            return RenderPipelineSkillsCommon.ToSerializableValue(value);
        }

        private static void SetNodePosition(object node, float x, float y)
        {
            var drawState = GetMemberValue(node, "drawState");
            if (drawState == null)
                return;

            var rect = GetMemberValue(drawState, "position") is Rect currentRect
                ? currentRect
                : new Rect(0f, 0f, 0f, 0f);
            rect.position = new Vector2(x, y);
            SetMemberValue(drawState, "position", rect);
            SetMemberValue(node, "drawState", drawState);
        }

        private static bool TrySetSlotValue(object slot, object value, out string error)
        {
            error = null;

            var property = GetPropertyRecursive(slot.GetType(), "value");
            if (property == null || !property.CanWrite)
            {
                error = "This slot does not expose a writable default value";
                return false;
            }

            if (!TryConvertNodeValue(value, property.PropertyType, out var converted, out error))
                return false;

            property.SetValue(slot, converted, null);
            return true;
        }

        private static bool TryConvertNodeValue(object rawValue, Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            try
            {
                if (targetType == typeof(float))
                {
                    converted = ConvertToFloat(rawValue);
                    return true;
                }

                if (targetType == typeof(int))
                {
                    converted = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                    return true;
                }

                if (targetType == typeof(bool))
                {
                    converted = ConvertToBool(rawValue);
                    return true;
                }

                if (targetType == typeof(Vector2))
                {
                    converted = ConvertToVector2(rawValue);
                    return true;
                }

                if (targetType == typeof(Vector3))
                {
                    converted = ConvertToVector3(rawValue);
                    return true;
                }

                if (targetType == typeof(Vector4))
                {
                    if (rawValue is string stringValue && stringValue.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    {
                        var color = ConvertToColor(rawValue);
                        converted = new Vector4(color.r, color.g, color.b, color.a);
                        return true;
                    }

                    converted = ConvertToVector4(rawValue, 4);
                    return true;
                }

                if (targetType == typeof(Color))
                {
                    converted = ConvertToColor(rawValue);
                    return true;
                }

                error = $"Unsupported slot default value type: {targetType.Name}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void SyncNodeSettingFromInputSlot(object node, int slotId, object slot)
        {
            switch (node.GetType().Name)
            {
                case "Vector1Node" when slotId == 1:
                    SetMemberValue(node, "m_Value", Convert.ToSingle(GetSlotValue(slot), CultureInfo.InvariantCulture));
                    break;
                case "Vector2Node":
                {
                    var current = (Vector2)GetMemberValue(node, "m_Value");
                    var slotValue = Convert.ToSingle(GetSlotValue(slot), CultureInfo.InvariantCulture);
                    if (slotId == 1) current.x = slotValue;
                    if (slotId == 2) current.y = slotValue;
                    SetMemberValue(node, "m_Value", current);
                    break;
                }
                case "Vector3Node":
                {
                    var current = (Vector3)GetMemberValue(node, "m_Value");
                    var slotValue = Convert.ToSingle(GetSlotValue(slot), CultureInfo.InvariantCulture);
                    if (slotId == 1) current.x = slotValue;
                    if (slotId == 2) current.y = slotValue;
                    if (slotId == 3) current.z = slotValue;
                    SetMemberValue(node, "m_Value", current);
                    break;
                }
                case "Vector4Node":
                {
                    var current = (Vector4)GetMemberValue(node, "m_Value");
                    var slotValue = Convert.ToSingle(GetSlotValue(slot), CultureInfo.InvariantCulture);
                    if (slotId == 1) current.x = slotValue;
                    if (slotId == 2) current.y = slotValue;
                    if (slotId == 3) current.z = slotValue;
                    if (slotId == 4) current.w = slotValue;
                    SetMemberValue(node, "m_Value", current);
                    break;
                }
            }
        }

        private static bool TryApplyNodeSettings(object graph, object node, ShaderGraphSupportedNodeDescriptor descriptor, object settings, bool requireSettings, out string error)
        {
            error = null;
            if (!TryNormalizeSettings(settings, out var settingsObject, out error))
                return false;

            if ((settingsObject == null || !settingsObject.Properties().Any()))
            {
                if (requireSettings || descriptor.RequiresExistingProperty)
                {
                    error = "settings is required";
                    return false;
                }

                return true;
            }

            var allowed = new HashSet<string>(descriptor.Settings.Select(setting => setting.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var property in settingsObject.Properties())
            {
                if (!allowed.Contains(property.Name))
                {
                    error = $"Unsupported setting '{property.Name}' for nodeType '{descriptor.NodeType}'. Allowed values: {string.Join(", ", allowed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                    return false;
                }
            }

            switch (descriptor.NodeType)
            {
                case "PropertyNode":
                {
                    var referenceName = settingsObject["propertyReferenceName"]?.ToString();
                    if (string.IsNullOrWhiteSpace(referenceName))
                    {
                        error = "PropertyNode requires settings.propertyReferenceName";
                        return false;
                    }

                    var property = FindRuntimeShaderProperty(graph, referenceName);
                    if (property == null)
                    {
                        error = $"Shader Graph property '{referenceName}' was not found";
                        return false;
                    }

                    SetMemberValue(node, "property", property);
                    InvokeNodeUpdate(node);
                    return true;
                }
                case "BooleanNode":
                    SetMemberValue(node, "m_Value", ConvertToBool(settingsObject["value"]));
                    InvokeNodeUpdate(node);
                    return true;
                case "ColorNode":
                    return TrySetColorNodeValue(node, settingsObject["value"], out error);
                case "Vector1Node":
                    SetMemberValue(node, "m_Value", ConvertToFloat(settingsObject["value"]));
                    InvokeNodeUpdate(node);
                    return true;
                case "Vector2Node":
                    SetMemberValue(node, "m_Value", ConvertToVector2(settingsObject["value"]));
                    InvokeNodeUpdate(node);
                    return true;
                case "Vector3Node":
                    SetMemberValue(node, "m_Value", ConvertToVector3(settingsObject["value"]));
                    InvokeNodeUpdate(node);
                    return true;
                case "Vector4Node":
                    SetMemberValue(node, "m_Value", ConvertToVector4(settingsObject["value"], 4));
                    InvokeNodeUpdate(node);
                    return true;
                case "UVNode":
                    if (!TrySetEnumMember(node, "m_OutputChannel", settingsObject["channel"]?.ToString(), out error))
                        return false;
                    InvokeNodeUpdate(node);
                    return true;
                case "PositionNode":
                case "NormalVectorNode":
                case "ViewDirectionNode":
                    if (!TrySetEnumMember(node, "m_Space", settingsObject["space"]?.ToString(), out error))
                        return false;
                    InvokeNodeUpdate(node);
                    return true;
                default:
                    error = $"Node settings are not supported for '{descriptor.NodeType}'";
                    return false;
            }
        }

        private static bool TryNormalizeSettings(object settings, out JObject settingsObject, out string error)
        {
            error = null;
            settingsObject = null;

            if (settings == null)
                return true;

            if (settings is JObject jobject)
            {
                settingsObject = jobject;
                return true;
            }

            if (settings is JToken jtoken)
            {
                if (jtoken.Type != JTokenType.Object)
                {
                    error = "settings must be a JSON object";
                    return false;
                }

                settingsObject = (JObject)jtoken;
                return true;
            }

            if (settings is string json)
            {
                try
                {
                    settingsObject = JObject.Parse(json);
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Failed to parse settings JSON: {ex.Message}";
                    return false;
                }
            }

            settingsObject = JObject.FromObject(settings);
            return true;
        }

        private static object FindRuntimeShaderProperty(object graph, string referenceName)
        {
            var properties = GetMemberValue(graph, "properties") as IEnumerable;
            if (properties == null)
                return null;

            foreach (var property in properties)
            {
                if (string.Equals(GetMemberValue(property, "referenceName")?.ToString(), referenceName, StringComparison.OrdinalIgnoreCase))
                    return property;
            }

            return null;
        }

        private static bool TrySetColorNodeValue(object node, object rawValue, out string error)
        {
            error = null;

            try
            {
                var colorFieldType = GetFieldRecursive(node.GetType(), "m_Color")?.FieldType;
                if (colorFieldType == null)
                {
                    error = "ColorNode backing field was not found";
                    return false;
                }

                var colorStruct = Activator.CreateInstance(colorFieldType);
                SetMemberValue(colorStruct, "color", ConvertToColor(rawValue));

                var existingColor = GetMemberValue(node, "m_Color");
                var mode = existingColor != null ? GetMemberValue(existingColor, "mode") : null;
                if (mode == null)
                {
                    var colorModeType = FindTypeInAssemblies("UnityEditor.ShaderGraph.Internal.ColorMode");
                    mode = colorModeType != null ? Enum.Parse(colorModeType, "Default") : null;
                }

                if (mode != null)
                    SetMemberValue(colorStruct, "mode", mode);

                SetMemberValue(node, "m_Color", colorStruct);
                InvokeNodeUpdate(node);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        private static void InvokeNodeUpdate(object node)
        {
            if (node == null)
                return;

            try
            {
                InvokeMethod(node, "UpdateNodeAfterDeserialization");
            }
            catch
            {
                // Some nodes do not expose this path in the same way across versions.
            }
        }

        private static object DescribeNode(JObject node)
        {
            return new
            {
                id = node["m_ObjectId"]?.ToString(),
                type = ShortTypeName(node["m_Type"]?.ToString()),
                name = node["m_Name"]?.ToString(),
                slotCount = (node["m_Slots"] as JArray)?.Count ?? 0
            };
        }

        private static object DescribeEdge(ShaderGraphDocument document, JObject edge)
        {
            if (edge == null)
                return null;

            var outputNodeId = edge["m_OutputSlot"]?["m_Node"]?["m_Id"]?.ToString();
            var inputNodeId = edge["m_InputSlot"]?["m_Node"]?["m_Id"]?.ToString();

            document.ObjectsById.TryGetValue(outputNodeId ?? string.Empty, out var outputNode);
            document.ObjectsById.TryGetValue(inputNodeId ?? string.Empty, out var inputNode);

            return new
            {
                outputNodeId,
                outputNode = outputNode?["m_Name"]?.ToString() ?? ShortTypeName(outputNode?["m_Type"]?.ToString()),
                outputSlotId = edge["m_OutputSlot"]?["m_SlotId"]?.ToObject<int?>(),
                inputNodeId,
                inputNode = inputNode?["m_Name"]?.ToString() ?? ShortTypeName(inputNode?["m_Type"]?.ToString()),
                inputSlotId = edge["m_InputSlot"]?["m_SlotId"]?.ToObject<int?>()
            };
        }

        private static object DescribeProperty(JObject property)
        {
            return new
            {
                id = property["m_ObjectId"]?.ToString(),
                type = NormalizePropertyTypeName(ShortTypeName(property["m_Type"]?.ToString())),
                fullType = property["m_Type"]?.ToString(),
                displayName = property["m_Name"]?.ToString(),
                referenceName = FirstNonEmpty(property["m_OverrideReferenceName"]?.ToString(), property["m_DefaultReferenceName"]?.ToString()),
                exposed = property["m_GeneratePropertyBlock"]?.ToObject<bool?>(),
                hidden = property["m_Hidden"]?.ToObject<bool?>(),
                guid = property["m_Guid"]?["m_GuidSerialized"]?.ToString(),
                value = ConvertTokenValue(property["m_Value"])
            };
        }

        private static object DescribeKeyword(JObject keyword)
        {
            var entries = (keyword["m_Entries"] as JArray)?.Select(entry => new
            {
                id = entry["id"]?.ToObject<int?>(),
                displayName = entry["displayName"]?.ToString(),
                referenceName = entry["referenceName"]?.ToString()
            }).ToArray() ?? Array.Empty<object>();

            var keywordType = keyword["m_KeywordType"]?.ToObject<int?>();
            var definition = keyword["m_KeywordDefinition"]?.ToObject<int?>();
            var scope = keyword["m_KeywordScope"]?.ToObject<int?>();

            return new
            {
                id = keyword["m_ObjectId"]?.ToString(),
                displayName = keyword["m_Name"]?.ToString(),
                referenceName = FirstNonEmpty(keyword["m_OverrideReferenceName"]?.ToString(), keyword["m_DefaultReferenceName"]?.ToString()),
                keywordType = keywordType.HasValue && KeywordTypeNames.TryGetValue(keywordType.Value, out var resolvedKeywordType) ? resolvedKeywordType : keywordType?.ToString(),
                definition = definition.HasValue && KeywordDefinitionNames.TryGetValue(definition.Value, out var resolvedDefinition) ? resolvedDefinition : definition?.ToString(),
                scope = scope.HasValue && KeywordScopeNames.TryGetValue(scope.Value, out var resolvedScope) ? resolvedScope : scope?.ToString(),
                value = keyword["m_Value"]?.ToObject<int?>(),
                entries
            };
        }

        private static object ConvertTokenValue(JToken token)
        {
            if (token == null)
                return null;

            if (token.Type == JTokenType.Object)
                return token.ToObject<Dictionary<string, object>>();
            if (token.Type == JTokenType.Array)
                return token.ToObject<object[]>();
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer || token.Type == JTokenType.String || token.Type == JTokenType.Boolean)
                return ((JValue)token).Value;

            return token.ToString();
        }

        private static bool MatchesNamedItem(JObject item, string displayName, string referenceName)
        {
            return (!string.IsNullOrWhiteSpace(displayName) && string.Equals(item["displayName"]?.ToString(), displayName, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrWhiteSpace(referenceName) && string.Equals(item["referenceName"]?.ToString(), referenceName, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePropertyTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return typeName;

            if (typeName.EndsWith("ShaderProperty", StringComparison.Ordinal))
                return typeName.Substring(0, typeName.Length - "ShaderProperty".Length);
            return typeName;
        }

        private static string ShortTypeName(string fullTypeName)
        {
            if (string.IsNullOrWhiteSpace(fullTypeName))
                return fullTypeName;

            var lastDot = fullTypeName.LastIndexOf('.');
            return lastDot >= 0 ? fullTypeName.Substring(lastDot + 1) : fullTypeName;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) ? left : right;
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            var property = GetPropertyRecursive(type, memberName);
            if (property != null)
                return property.GetValue(instance, null);

            var field = GetFieldRecursive(type, memberName);
            return field?.GetValue(instance);
        }

        private static void SetMemberValue(object instance, string memberName, object value)
        {
            var type = instance.GetType();
            var property = GetPropertyRecursive(type, memberName);
            if (property != null)
            {
                property.SetValue(instance, ChangeType(value, property.PropertyType), null);
                return;
            }

            var field = GetFieldRecursive(type, memberName);
            if (field != null)
            {
                field.SetValue(instance, ChangeType(value, field.FieldType));
                return;
            }

            throw new MissingMemberException(type.FullName, memberName);
        }

        private static object InvokeMethod(object instance, string methodName, params object[] arguments)
        {
            var type = instance.GetType();
            var methods = GetMethodsRecursive(type, methodName).ToArray();
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != arguments.Length)
                    continue;

                try
                {
                    var invokeArguments = new object[arguments.Length];
                    for (var i = 0; i < parameters.Length; i++)
                        invokeArguments[i] = ChangeType(arguments[i], parameters[i].ParameterType);
                    return method.Invoke(instance, invokeArguments);
                }
                catch
                {
                    // Try the next overload.
                }
            }

            throw new MissingMethodException(type.FullName, methodName);
        }

        private static bool TrySetEnumMember(object instance, string memberName, string enumName, out string error)
        {
            error = null;
            var type = instance.GetType();
            var property = GetPropertyRecursive(type, memberName);
            var targetType = property != null ? property.PropertyType : GetFieldRecursive(type, memberName)?.FieldType;
            if (targetType == null || !targetType.IsEnum)
            {
                error = $"Enum member '{memberName}' was not found";
                return false;
            }

            if (!EnumTryParse(targetType, enumName, out var parsed))
            {
                error = $"Invalid {memberName} '{enumName}'. Valid values: {string.Join(", ", Enum.GetNames(targetType))}";
                return false;
            }

            SetMemberValue(instance, memberName, parsed);
            return true;
        }

        private static bool EnumTryParse(Type enumType, string enumName, out object parsed)
        {
            parsed = null;
            if (enumType == null || !enumType.IsEnum || string.IsNullOrWhiteSpace(enumName))
                return false;

            foreach (var name in Enum.GetNames(enumType))
            {
                if (string.Equals(name, enumName, StringComparison.OrdinalIgnoreCase))
                {
                    parsed = Enum.Parse(enumType, name);
                    return true;
                }
            }

            return false;
        }

        private static object ChangeType(object value, Type targetType)
        {
            if (targetType == null)
                return value;

            if (value == null)
                return null;

            if (targetType.IsInstanceOfType(value))
                return value;

            if (targetType.IsEnum)
            {
                if (value is string enumName)
                    return Enum.Parse(targetType, enumName, true);
                return Enum.ToObject(targetType, value);
            }

            if (targetType == typeof(string))
                return value.ToString();

            if (targetType == typeof(int))
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);

            return value;
        }

        private static bool AssetPathExists(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return false;

            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                return true;

            return File.Exists(Path.GetFullPath(assetPath));
        }

        private static bool TryResolveTemplateFilePath(string templatePath, out string resolvedPath)
        {
            resolvedPath = null;
            if (string.IsNullOrWhiteSpace(templatePath))
                return false;

            if (string.Equals(templatePath, BuiltinBlankTemplatePath, StringComparison.OrdinalIgnoreCase))
            {
                resolvedPath = BuiltinBlankTemplatePath;
                return true;
            }

            if (Path.IsPathRooted(templatePath))
            {
                resolvedPath = Path.GetFullPath(templatePath);
                return File.Exists(resolvedPath);
            }

            if (templatePath.StartsWith(PackageRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetPackageRoot(out var packageRoot, out _))
                    return false;

                var relative = templatePath.Substring(PackageRoot.Length).TrimStart('/', '\\');
                resolvedPath = Path.GetFullPath(Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(resolvedPath);
            }

            resolvedPath = Path.GetFullPath(templatePath);
            return File.Exists(resolvedPath);
        }

        private static bool TryGetTemplatesDirectory(out string templatesDirectory)
        {
            templatesDirectory = null;
            if (!TryGetPackageRoot(out var packageRoot, out _))
                return false;

            templatesDirectory = Path.Combine(packageRoot, "GraphTemplates");
            return Directory.Exists(templatesDirectory);
        }

        private static bool TryGetPackageRoot(out string packageRoot, out string packageVersion)
        {
            packageRoot = null;
            packageVersion = null;

            var graphDataType = FindTypeInAssemblies("UnityEditor.ShaderGraph.GraphData");
            if (graphDataType == null)
                return false;

            try
            {
                var packageInfo = PkgInfo.FindForAssembly(graphDataType.Assembly);
                if (packageInfo != null && !string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                {
                    packageRoot = Path.GetFullPath(packageInfo.resolvedPath);
                    packageVersion = packageInfo.version;
                    return Directory.Exists(packageRoot);
                }
            }
            catch
            {
                // Fall through to heuristic path resolution.
            }

            try
            {
                var projectRoot = Path.GetDirectoryName(Application.dataPath);
                if (!string.IsNullOrWhiteSpace(projectRoot))
                {
                    var packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
                    if (Directory.Exists(packageCacheRoot))
                    {
                        var candidates = Directory.GetDirectories(packageCacheRoot, "com.unity.shadergraph@*")
                            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        if (candidates.Length > 0)
                        {
                            packageRoot = Path.GetFullPath(candidates[0]);
                            var directoryName = Path.GetFileName(packageRoot);
                            var atIndex = directoryName.IndexOf('@');
                            if (atIndex >= 0 && atIndex < directoryName.Length - 1)
                                packageVersion = directoryName.Substring(atIndex + 1);
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore filesystem probing failures.
            }

            return false;
        }

        private static PropertyInfo GetPropertyRecursive(Type type, string memberName)
        {
            while (type != null)
            {
                var property = type.GetProperty(memberName, InstanceFlags);
                if (property != null)
                    return property;
                type = type.BaseType;
            }

            return null;
        }

        private static FieldInfo GetFieldRecursive(Type type, string memberName)
        {
            while (type != null)
            {
                var field = type.GetField(memberName, InstanceFlags);
                if (field != null)
                    return field;
                type = type.BaseType;
            }

            return null;
        }

        private static IEnumerable<MethodInfo> GetMethodsRecursive(Type type, string methodName)
        {
            while (type != null)
            {
                foreach (var method in type.GetMethods(InstanceFlags).Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)))
                    yield return method;
                type = type.BaseType;
            }
        }

        private static float ConvertToFloat(object value)
        {
            if (value is JToken token)
                value = ConvertTokenValue(token);
            return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        }

        private static bool ConvertToBool(object value)
        {
            if (value is JToken token)
                value = ConvertTokenValue(token);

            if (value is bool boolValue)
                return boolValue;
            if (value is string stringValue)
            {
                if (bool.TryParse(stringValue, out var parsedBool))
                    return parsedBool;
                if (int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    return parsedInt != 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
        }

        private static bool TryGetBoolValue(object value, bool fallback = false)
        {
            if (value == null)
                return fallback;

            try
            {
                return ConvertToBool(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static Vector2 ConvertToVector2(object value)
        {
            var vector = (Vector4)ConvertToVector4(value, 2);
            return new Vector2(vector.x, vector.y);
        }

        private static Vector3 ConvertToVector3(object value)
        {
            var vector = (Vector4)ConvertToVector4(value, 3);
            return new Vector3(vector.x, vector.y, vector.z);
        }

        private static object ConvertToVector4(object value, int dimensions)
        {
            if (value is JToken token)
                value = token;

            var components = new float[4];
            if (value is JObject obj)
            {
                components[0] = obj["x"]?.ToObject<float?>() ?? obj["r"]?.ToObject<float?>() ?? 0f;
                components[1] = obj["y"]?.ToObject<float?>() ?? obj["g"]?.ToObject<float?>() ?? 0f;
                components[2] = obj["z"]?.ToObject<float?>() ?? obj["b"]?.ToObject<float?>() ?? 0f;
                components[3] = obj["w"]?.ToObject<float?>() ?? obj["a"]?.ToObject<float?>() ?? 0f;
            }
            else if (value is JArray array)
            {
                for (var i = 0; i < Math.Min(array.Count, 4); i++)
                    components[i] = array[i].ToObject<float>();
            }
            else if (value is string stringValue)
            {
                var parts = stringValue.Split(',')
                    .Select(part => part.Trim())
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .ToArray();
                for (var i = 0; i < Math.Min(parts.Length, 4); i++)
                    components[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            }
            else
            {
                components[0] = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (dimensions == 2)
                return new Vector4(components[0], components[1], 0f, 0f);
            if (dimensions == 3)
                return new Vector4(components[0], components[1], components[2], 0f);
            return new Vector4(components[0], components[1], components[2], components[3]);
        }

        private static Color ConvertToColor(object value)
        {
            if (value is JToken token)
                value = token;

            if (value is string html && ColorUtility.TryParseHtmlString(html, out var parsedColor))
                return parsedColor;

            if (value is JObject obj)
            {
                return new Color(
                    obj["r"]?.ToObject<float?>() ?? obj["x"]?.ToObject<float?>() ?? 0f,
                    obj["g"]?.ToObject<float?>() ?? obj["y"]?.ToObject<float?>() ?? 0f,
                    obj["b"]?.ToObject<float?>() ?? obj["z"]?.ToObject<float?>() ?? 0f,
                    obj["a"]?.ToObject<float?>() ?? obj["w"]?.ToObject<float?>() ?? 1f);
            }

            if (value is JArray array)
            {
                var components = array.Select(item => item.ToObject<float>()).ToArray();
                return new Color(
                    components.Length > 0 ? components[0] : 0f,
                    components.Length > 1 ? components[1] : 0f,
                    components.Length > 2 ? components[2] : 0f,
                    components.Length > 3 ? components[3] : 1f);
            }

            throw new InvalidOperationException("Color value must be an HTML string, object, or array");
        }
    }
}
