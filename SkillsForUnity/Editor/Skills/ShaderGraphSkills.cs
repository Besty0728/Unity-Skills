using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Shader Graph 资产的创建、检视与受约束编辑技能。
    /// </summary>
    public static class ShaderGraphSkills
    {
        [UnitySkill("shadergraph_list_templates", "List Shader Graph templates shipped by the installed Shader Graph package",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "template", "list", "graph", "subgraph" },
            Outputs = new[] { "count", "templates" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.shadergraph" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphListTemplates(bool includeSubGraphs = false, string filter = null)
        {
            if (!ShaderGraphReflectionHelper.HasPackageFolder)
                return ShaderGraphReflectionHelper.NoShaderGraph();

            var templates = ShaderGraphReflectionHelper.GetTemplateDescriptors(includeSubGraphs)
                .Select(Newtonsoft.Json.Linq.JObject.FromObject)
                .Where(item =>
                    string.IsNullOrWhiteSpace(filter) ||
                    item["name"]?.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item["group"]?.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            return new
            {
                success = true,
                count = templates.Length,
                templates
            };
        }

        [UnitySkill("shadergraph_create_graph", "Create a Shader Graph asset from a package template",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Create,
            Tags = new[] { "shadergraph", "create", "graph", "template", "asset" },
            Outputs = new[] { "path", "templatePath", "graph" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphCreateGraph(string savePath, string templateName = "Unlit Simple", string templatePath = null)
        {
            if (!ShaderGraphReflectionHelper.HasPackageFolder)
                return ShaderGraphReflectionHelper.NoShaderGraph();
            if (Validate.Required(savePath, "savePath") is object err) return err;

            var fileName = Path.GetFileNameWithoutExtension(savePath);
            var resolvedPath = RenderPipelineSkillsCommon.ResolveAssetSavePath(savePath, string.IsNullOrWhiteSpace(fileName) ? "New Shader Graph" : fileName, ".shadergraph");
            if (Validate.SafePath(resolvedPath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(resolvedPath))
                return new { error = $"Asset already exists: {resolvedPath}" };

            var useBlankGraphFallback = false;
            string resolvedTemplatePath = null;
            string warning = null;

            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                resolvedTemplatePath = ShaderGraphReflectionHelper.ResolveTemplatePath(templatePath, false, out var directTemplateError);
                if (!string.IsNullOrWhiteSpace(directTemplateError))
                    return new { error = directTemplateError };
                useBlankGraphFallback = string.Equals(resolvedTemplatePath, "builtin:blank", StringComparison.OrdinalIgnoreCase);
            }
            else if (ShaderGraphReflectionHelper.HasTemplateDirectory)
            {
                resolvedTemplatePath = ShaderGraphReflectionHelper.ResolveTemplatePath(templateName, false, out var namedTemplateError);
                if (!string.IsNullOrWhiteSpace(namedTemplateError))
                    return new { error = namedTemplateError };
                useBlankGraphFallback = string.Equals(resolvedTemplatePath, "builtin:blank", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                useBlankGraphFallback = true;
                resolvedTemplatePath = "builtin:blank";
                if (!string.IsNullOrWhiteSpace(templateName) &&
                    !string.Equals(templateName, "Blank Shader Graph", StringComparison.OrdinalIgnoreCase))
                {
                    warning = $"Installed Shader Graph package does not expose GraphTemplates. Created a blank graph instead of '{templateName}'.";
                }
            }

            RenderPipelineSkillsCommon.EnsureAssetFolderExists(resolvedPath);
            if (useBlankGraphFallback)
            {
                if (!ShaderGraphReflectionHelper.TryCreateBlankGraph(resolvedPath, "Shader Graphs", out var createError))
                    return new { error = createError };
            }
            else
            {
                if (!ShaderGraphReflectionHelper.TryCopyTemplate(resolvedTemplatePath, resolvedPath, out var copyError))
                    return new { error = copyError };
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(resolvedPath);
            if (asset != null)
                WorkflowManager.SnapshotCreatedAsset(asset);

            if (!ShaderGraphReflectionHelper.TryReadGraphDocument(resolvedPath, out var document, out var readError))
                return new { success = true, path = resolvedPath, templatePath = resolvedTemplatePath, creationMode = useBlankGraphFallback ? "BlankFallback" : "TemplateCopy", warning = warning ?? readError };

            return new
            {
                success = true,
                path = resolvedPath,
                templatePath = resolvedTemplatePath,
                creationMode = useBlankGraphFallback ? "BlankFallback" : "TemplateCopy",
                warning,
                graph = ShaderGraphReflectionHelper.DescribeGraphInfo(document)
            };
        }

        [UnitySkill("shadergraph_create_subgraph", "Create a blank Shader Sub Graph asset with a configured output slot",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Create,
            Tags = new[] { "shadergraph", "create", "subgraph", "asset" },
            Outputs = new[] { "path", "graph" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphCreateSubGraph(string savePath, string outputType = "Vector4", string graphPath = "Sub Graphs")
        {
            if (!ShaderGraphReflectionHelper.IsShaderGraphInstalled)
                return ShaderGraphReflectionHelper.NoShaderGraph();
            if (Validate.Required(savePath, "savePath") is object err) return err;

            var fileName = Path.GetFileNameWithoutExtension(savePath);
            var resolvedPath = RenderPipelineSkillsCommon.ResolveAssetSavePath(savePath, string.IsNullOrWhiteSpace(fileName) ? "New Shader Sub Graph" : fileName, ".shadersubgraph");
            if (Validate.SafePath(resolvedPath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(resolvedPath))
                return new { error = $"Asset already exists: {resolvedPath}" };

            RenderPipelineSkillsCommon.EnsureAssetFolderExists(resolvedPath);
            if (!ShaderGraphReflectionHelper.TryCreateBlankSubGraph(resolvedPath, outputType, graphPath, out var createError))
                return new { error = createError };

            var asset = AssetDatabase.LoadMainAssetAtPath(resolvedPath);
            if (asset != null)
                WorkflowManager.SnapshotCreatedAsset(asset);

            if (!ShaderGraphReflectionHelper.TryReadGraphDocument(resolvedPath, out var document, out var readError))
                return new { success = true, path = resolvedPath, warning = readError };

            return new
            {
                success = true,
                path = resolvedPath,
                graph = ShaderGraphReflectionHelper.DescribeGraphInfo(document)
            };
        }

        [UnitySkill("shadergraph_list_assets", "List Shader Graph and Sub Graph assets in the project",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "list", "assets", "graph", "subgraph" },
            Outputs = new[] { "count", "assets" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphListAssets(string filter = null, bool includeSubGraphs = true, int limit = 100)
        {
            var searchFilter = string.IsNullOrWhiteSpace(filter) ? string.Empty : filter;
            var assets = AssetDatabase.FindAssets(searchFilter, new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                    !string.IsNullOrEmpty(path) &&
                    (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
                     (includeSubGraphs && path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase))))
                .Where(path => string.IsNullOrWhiteSpace(filter) || path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .Select(path => new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    kind = path.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase) ? "SubGraph" : "Graph"
                })
                .ToArray();

            return new
            {
                success = true,
                count = assets.Length,
                assets
            };
        }

        [UnitySkill("shadergraph_get_info", "Get a high-level summary of a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "inspect", "info", "graph", "subgraph" },
            Outputs = new[] { "assetPath", "kind", "propertyCount", "keywordCount", "nodeCount", "edgeCount" },
            ReadOnly = true,
            RequiresInput = new[] { "assetPath" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphGetInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            if (!ShaderGraphReflectionHelper.TryReadGraphDocument(assetPath, out var document, out var error))
                return new { error };

            return ShaderGraphReflectionHelper.DescribeGraphInfo(document);
        }

        [UnitySkill("shadergraph_get_structure", "Inspect nodes, edges, properties, and keywords inside a Shader Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "structure", "nodes", "edges", "inspect" },
            Outputs = new[] { "nodes", "edges", "properties", "keywords" },
            ReadOnly = true,
            RequiresInput = new[] { "assetPath" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphGetStructure(string assetPath, int maxNodes = 200, int maxEdges = 200)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            if (!ShaderGraphReflectionHelper.TryReadGraphDocumentAndLoadGraphData(assetPath, out var document, out var graph, out var error))
                return new { error };

            if (graph != null)
                return ShaderGraphReflectionHelper.DescribeGraphStructure(document, graph, maxNodes, maxEdges);

            return ShaderGraphReflectionHelper.DescribeGraphStructure(document, maxNodes, maxEdges);
        }

        [UnitySkill("shadergraph_list_supported_nodes", "List the constrained Shader Graph node subset that can be safely edited",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "node", "supported", "list", "editing" },
            Outputs = new[] { "count", "nodes" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.shadergraph" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphListSupportedNodes()
        {
            if (!ShaderGraphReflectionHelper.IsShaderGraphInstalled)
                return ShaderGraphReflectionHelper.NoShaderGraph();

            var nodes = ShaderGraphReflectionHelper.GetSupportedNodes();
            return new
            {
                success = true,
                count = nodes.Length,
                nodes
            };
        }

        [UnitySkill("shadergraph_add_node", "Add a supported node to a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Create,
            Tags = new[] { "shadergraph", "node", "add", "graph", "subgraph" },
            Outputs = new[] { "assetPath", "node" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "nodeType" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphAddNode(string assetPath, string nodeType, float x = 0f, float y = 0f, object settings = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(nodeType, "nodeType") is object typeErr) return typeErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryAddNode(assetPath, nodeType, x, y, settings, out var nodeInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                node = nodeInfo
            };
        }

        [UnitySkill("shadergraph_remove_node", "Remove a node and its related edges from a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Delete,
            Tags = new[] { "shadergraph", "node", "remove", "delete" },
            Outputs = new[] { "assetPath", "node" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "nodeId" },
            RequiresPackages = new[] { "com.unity.shadergraph" },
            RiskLevel = "medium")]
        public static object ShaderGraphRemoveNode(string assetPath, string nodeId)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(nodeId, "nodeId") is object nodeErr) return nodeErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryRemoveNode(assetPath, nodeId, out var removedInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                node = removedInfo
            };
        }

        [UnitySkill("shadergraph_move_node", "Move a node inside a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "node", "move", "position" },
            Outputs = new[] { "assetPath", "node" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "nodeId" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphMoveNode(string assetPath, string nodeId, float x, float y)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(nodeId, "nodeId") is object nodeErr) return nodeErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryMoveNode(assetPath, nodeId, x, y, out var nodeInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                node = nodeInfo
            };
        }

        [UnitySkill("shadergraph_connect_nodes", "Connect an output slot to an input slot in a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "node", "connect", "edge" },
            Outputs = new[] { "assetPath", "edge" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "fromNodeId", "fromSlotId", "toNodeId", "toSlotId" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphConnectNodes(string assetPath, string fromNodeId, int fromSlotId, string toNodeId, int toSlotId)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(fromNodeId, "fromNodeId") is object fromErr) return fromErr;
            if (Validate.Required(toNodeId, "toNodeId") is object toErr) return toErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryConnectNodes(assetPath, fromNodeId, fromSlotId, toNodeId, toSlotId, out var edgeInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                edge = edgeInfo
            };
        }

        [UnitySkill("shadergraph_disconnect_nodes", "Disconnect a specific edge in a Shader Graph or Sub Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "node", "disconnect", "edge" },
            Outputs = new[] { "assetPath", "edge" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "fromNodeId", "fromSlotId", "toNodeId", "toSlotId" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphDisconnectNodes(string assetPath, string fromNodeId, int fromSlotId, string toNodeId, int toSlotId)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(fromNodeId, "fromNodeId") is object fromErr) return fromErr;
            if (Validate.Required(toNodeId, "toNodeId") is object toErr) return toErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryDisconnectNodes(assetPath, fromNodeId, fromSlotId, toNodeId, toSlotId, out var edgeInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                edge = edgeInfo
            };
        }

        [UnitySkill("shadergraph_set_node_defaults", "Set the default value of an unconnected input slot on a supported node",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "node", "default", "slot", "value" },
            Outputs = new[] { "assetPath", "node" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "nodeId", "slotId", "value" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphSetNodeDefaults(string assetPath, string nodeId, int slotId, object value)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(nodeId, "nodeId") is object nodeErr) return nodeErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TrySetNodeDefaults(assetPath, nodeId, slotId, value, out var nodeInfo, out var error))
                return SetNodeDefaultsError(error);

            return new
            {
                success = true,
                assetPath,
                node = nodeInfo
            };
        }

        [UnitySkill("shadergraph_set_node_settings", "Set whitelisted settings on a supported Shader Graph node",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "node", "settings", "edit" },
            Outputs = new[] { "assetPath", "node" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath", "nodeId", "settings" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphSetNodeSettings(string assetPath, string nodeId, object settings)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(nodeId, "nodeId") is object nodeErr) return nodeErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TrySetNodeSettings(assetPath, nodeId, settings, out var nodeInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                node = nodeInfo
            };
        }

        [UnitySkill("shadergraph_list_properties", "List exposed graph properties defined in a Shader Graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "property", "list", "inspect" },
            Outputs = new[] { "count", "properties" },
            ReadOnly = true,
            RequiresInput = new[] { "assetPath" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphListProperties(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            if (!ShaderGraphReflectionHelper.TryReadGraphDocument(assetPath, out var document, out var error))
                return new { error };

            var properties = ShaderGraphReflectionHelper.GetProperties(document);
            return new
            {
                success = true,
                assetPath,
                count = properties.Length,
                properties
            };
        }

        [UnitySkill("shadergraph_add_property", "Add a constrained Shader Graph blackboard property to an existing graph",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Create,
            Tags = new[] { "shadergraph", "property", "add", "blackboard" },
            Outputs = new[] { "assetPath", "property" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphAddProperty(
            string assetPath,
            string propertyType,
            string displayName,
            string referenceName = null,
            object value = null,
            bool exposed = true,
            bool hidden = false)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.Required(propertyType, "propertyType") is object typeErr) return typeErr;
            if (Validate.Required(displayName, "displayName") is object nameErr) return nameErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryAddProperty(assetPath, propertyType, displayName, referenceName, value, exposed, hidden, out var propertyInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                property = propertyInfo
            };
        }

        [UnitySkill("shadergraph_update_property", "Update a constrained Shader Graph blackboard property",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "property", "update", "blackboard" },
            Outputs = new[] { "assetPath", "property" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphUpdateProperty(
            string assetPath,
            string propertyName = null,
            string referenceName = null,
            string newDisplayName = null,
            string newReferenceName = null,
            object value = null,
            bool? exposed = null,
            bool? hidden = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (string.IsNullOrWhiteSpace(propertyName) && string.IsNullOrWhiteSpace(referenceName))
                return new { error = "propertyName or referenceName is required" };

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryUpdateProperty(assetPath, propertyName, referenceName, newDisplayName, newReferenceName, value, exposed, hidden, out var propertyInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                property = propertyInfo
            };
        }

        [UnitySkill("shadergraph_remove_property", "Remove a Shader Graph blackboard property from an existing graph",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Delete,
            Tags = new[] { "shadergraph", "property", "remove", "blackboard" },
            Outputs = new[] { "assetPath", "propertyName", "referenceName" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" },
            RiskLevel = "medium")]
        public static object ShaderGraphRemoveProperty(string assetPath, string propertyName = null, string referenceName = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (string.IsNullOrWhiteSpace(propertyName) && string.IsNullOrWhiteSpace(referenceName))
                return new { error = "propertyName or referenceName is required" };

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryRemoveProperty(assetPath, propertyName, referenceName, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                propertyName,
                referenceName
            };
        }

        [UnitySkill("shadergraph_list_keywords", "List Shader Graph keywords defined in a graph asset",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Query,
            Tags = new[] { "shadergraph", "keyword", "list", "inspect" },
            Outputs = new[] { "count", "keywords" },
            ReadOnly = true,
            RequiresInput = new[] { "assetPath" },
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGraphListKeywords(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            if (!ShaderGraphReflectionHelper.TryReadGraphDocument(assetPath, out var document, out var error))
                return new { error };

            var keywords = ShaderGraphReflectionHelper.GetKeywords(document);
            return new
            {
                success = true,
                assetPath,
                count = keywords.Length,
                keywords
            };
        }

        [UnitySkill("shadergraph_add_keyword", "Add a Shader Graph keyword to an existing graph",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Create,
            Tags = new[] { "shadergraph", "keyword", "add", "blackboard" },
            Outputs = new[] { "assetPath", "keyword" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphAddKeyword(
            string assetPath,
            string keywordType = "Boolean",
            string displayName = null,
            string referenceName = null,
            string definition = "ShaderFeature",
            string scope = "Local",
            string entries = null,
            int value = 0)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryAddKeyword(assetPath, keywordType, displayName, referenceName, definition, scope, entries, value, out var keywordInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                keyword = keywordInfo
            };
        }

        [UnitySkill("shadergraph_update_keyword", "Update a Shader Graph keyword on an existing graph",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Modify,
            Tags = new[] { "shadergraph", "keyword", "update", "blackboard" },
            Outputs = new[] { "assetPath", "keyword" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" })]
        public static object ShaderGraphUpdateKeyword(
            string assetPath,
            string displayName = null,
            string referenceName = null,
            string newDisplayName = null,
            string newReferenceName = null,
            string definition = null,
            string scope = null,
            string entries = null,
            int? value = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(referenceName))
                return new { error = "displayName or referenceName is required" };

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryUpdateKeyword(assetPath, displayName, referenceName, newDisplayName, newReferenceName, definition, scope, entries, value, out var keywordInfo, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                keyword = keywordInfo
            };
        }

        [UnitySkill("shadergraph_remove_keyword", "Remove a Shader Graph keyword from an existing graph",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Delete,
            Tags = new[] { "shadergraph", "keyword", "remove", "blackboard" },
            Outputs = new[] { "assetPath", "displayName", "referenceName" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" },
            RequiresPackages = new[] { "com.unity.shadergraph" },
            RiskLevel = "medium")]
        public static object ShaderGraphRemoveKeyword(string assetPath, string displayName = null, string referenceName = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object assetErr) return assetErr;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(referenceName))
                return new { error = "displayName or referenceName is required" };

            SnapshotGraphAsset(assetPath);
            if (!ShaderGraphReflectionHelper.TryRemoveKeyword(assetPath, displayName, referenceName, out var error))
                return GraphError(error);

            return new
            {
                success = true,
                assetPath,
                displayName,
                referenceName
            };
        }

        [UnitySkill("shadergraph_reimport", "Force reimport of a Shader Graph or Sub Graph asset after external edits",
            Category = SkillCategory.ShaderGraph, Operation = SkillOperation.Execute,
            Tags = new[] { "shadergraph", "reimport", "refresh", "asset" },
            Outputs = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "assetPath" })]
        public static object ShaderGraphReimport(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (!File.Exists(Path.GetFullPath(assetPath)))
                return new { error = $"Asset not found: {assetPath}" };

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            return new
            {
                success = true,
                assetPath
            };
        }

        private static void SnapshotGraphAsset(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset != null)
                WorkflowManager.SnapshotObject(asset);
        }

        /// <summary>
        /// 把 <c>ShaderGraphReflectionHelper</c> 的失败包装成 router 的第一层错误契约
        /// （<c>SkillResultHelper.TryGetErrorContext</c>）。此处所有 node/slot/edge/property/keyword
        /// 失败讲的都是图自身的内部状态——本资产内不存在的 nodeId、slotId、propertyName 或 keyword，
        /// 与场景 GameObject 或材质暴露的 shader 属性无关。若交给 router 的消息模式分类器
        /// （<see cref="SkillErrorClassifier"/>）处理，"Node 'x' was not found" 和
        /// "Keyword not found: x" 会被读成泛化的 not-found，配上 relatedSkills =
        /// gameobject_find/scene_get_hierarchy；"Property not found: x" 会被读成 PropertyNotOnTarget，
        /// 配上 material_get_properties——两者都把调用方引去场景/材质系统里翻找一个只存在于本图资产
        /// 内部的东西。在此显式声明 relatedSkills/suggestedFixes 会无条件覆盖那份猜测
        /// （<c>errorContext.RelatedSkills ?? classified.RelatedSkills</c>）；errorCode 仍交由分类器
        /// 从消息文本推断，因为对这些消息它推得本就正确
        /// （TARGET_NOT_FOUND / SEMANTIC_INVALID / MISSING_PACKAGE）。
        /// </summary>
        private static object GraphError(string error) => new
        {
            error,
            relatedSkills = new[] { "shadergraph_get_structure", "shadergraph_get_info" },
            suggestedFixes = new[]
            {
                new
                {
                    action = "find_target",
                    skill = "shadergraph_get_structure",
                    reason = "List the graph's actual nodes, slots, edges, properties, and keywords, then retry with an id/name from that structure."
                }
            }
        };

        /// <summary>
        /// <c>shadergraph_set_node_defaults</c> 把 <paramref name="value"/> 交给
        /// <c>ShaderGraphReflectionHelper.TrySetNodeDefaults</c>，后者按 slot 的 CLR 类型用
        /// <c>Convert.ToSingle</c> 等做转换。形状不匹配时——给只要裸数字的 Vector1 slot 传了对象
        /// （<c>{"x":3.14}</c>），或反过来给 DynamicVector slot 传了裸数字——转换内部抛
        /// <c>InvalidCastException</c>，helper 捕获后原样透出为 <c>ex.Message</c>：
        /// "Specified cast is not valid."，既不说哪个参数、哪个 slot，也不说期望什么形状。
        /// 该文本还匹配不上 <see cref="SkillErrorClassifier"/> 的任何规则，于是落到
        /// SKILL_ERROR + abort——等于告诉调用方放弃，而不是换个形状重试。
        /// 这里按子串识别，因为确切措辞是运行时消息而非契约；helper 的其他错误仍走
        /// <see cref="GraphError"/>。
        /// </summary>
        private static object SetNodeDefaultsError(string error)
        {
            if (!string.IsNullOrEmpty(error) && error.IndexOf("cast is not valid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new
                {
                    error = "The 'value' shape does not match this slot's default value type. A Vector1 (float) slot needs a bare number, e.g. 3.14. A Vector2/Vector3/Vector4/Color slot needs an object with matching component keys (x/y/z/w or r/g/b/a), a JSON array, or a comma-separated string. A boolean slot needs true/false.",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "value",
                    relatedSkills = new[] { "shadergraph_get_structure" },
                    suggestedFixes = new[]
                    {
                        new
                        {
                            action = "fix_param",
                            skill = "shadergraph_get_structure",
                            reason = "Inspect the target slot's concreteValueType/slot kind, then retry with a value shaped for that type."
                        }
                    }
                };
            }

            return GraphError(error);
        }
    }
}

// Producer:Betsy
