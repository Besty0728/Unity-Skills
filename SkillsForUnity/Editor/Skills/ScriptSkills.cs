using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Script management skills: create, read, modify.
    /// </summary>
    public static class ScriptSkills
    {
        private const int DefaultDiagnosticLimit = 20;

        [UnitySkill("script_create", "Create a C# script: content writes the complete source verbatim (template and namespaceName are then ignored); without it a template is filled (template: MonoBehaviour by default, ScriptableObject, Editor, EditorWindow; namespaceName wraps it). scriptName is the file name, and a MonoBehaviour/ScriptableObject class must carry the same name (content that declares none only warns). GET the returned waitUrl (/jobs/<jobId>?wait=90) to get the compile result in one call. Before generating gameplay scripts, actively consider coupling, performance, and maintainability.", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Create,
            Tags = new[] { "script", "csharp", "create", "template" },
            Outputs = new[] { "path", "className", "namespaceName", "jobId", "waitUrl" },
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object ScriptCreate(
            [SkillParam("File name without .cs and without path separators, written as <folder>/<scriptName>.cs; a template's class gets the same name.")]
            string scriptName = null,
            [SkillParam("Alias of scriptName, used only when scriptName is omitted.")]
            string name = null,
            [SkillParam("Must start with Assets/ or Packages/; created if missing. Editor/EditorWindow templates switch the default Assets/Scripts to Assets/Editor.")]
            string folder = "Assets/Scripts",
            [SkillParam("MonoBehaviour (default), ScriptableObject, Editor or EditorWindow, case/space-insensitive; another bare name is rejected; text containing code is a literal template ({CLASS}/{NAMESPACE} substituted). Ignored when content is given.")]
            string template = null,
            [SkillParam("Wraps the template class in this namespace; ignored when content is given.")]
            string namespaceName = null,
            bool checkCompile = true,
            int diagnosticLimit = DefaultDiagnosticLimit,
            [SkillParam("Complete C# source, written verbatim (template and namespaceName are ignored). A MonoBehaviour/ScriptableObject must declare a class named scriptName.")]
            string content = null)
        {
            scriptName = scriptName ?? name;
            if (string.IsNullOrEmpty(scriptName))
                return new { error = "scriptName is required" };
            if (HasPathSeparators(scriptName))
                return new { error = "scriptName must not contain path separators" };

            bool fromTemplate = string.IsNullOrEmpty(content);
            // Rejected before any folder is created or file written: an unknown bare template name could never compile.
            if (fromTemplate && !TryResolveTemplate(template, namespaceName, out _, out var templateErr))
                return templateErr;

            if (fromTemplate && IsEditorOnlyTemplate(template) &&
                string.Equals(folder, "Assets/Scripts", System.StringComparison.OrdinalIgnoreCase))
            {
                folder = "Assets/Editor";
            }

            if (!string.IsNullOrEmpty(folder) && Validate.SafePath(folder, "folder") is object folderErr) return folderErr;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, scriptName + ".cs");
            if (File.Exists(path))
                return new { error = $"Script already exists: {path}" };

            var warnings = new List<string>();
            WriteNewScriptFile(path, scriptName, template, namespaceName, content, warnings);
            AssetDatabase.ImportAsset(path);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);

            var result = CreateScriptMutationResult(path, "script_create", checkCompile, diagnosticLimit);
            result["className"] = scriptName;
            result["namespaceName"] = fromTemplate ? namespaceName : DeclaredNamespace(content);
            if (warnings.Count > 0)
                result["warnings"] = warnings;
            result["designReminder"] = "Before filling in gameplay logic, actively consider coupling, performance, and maintainability. Prefer clear responsibilities, explicit dependencies, avoid unnecessary Update-driven logic, and only introduce heavier patterns such as UniTask or global event systems when clearly justified.";
            return result;
        }

        /// <summary>
        /// Writes a new script file (UTF-8, no BOM) and returns the source written: content verbatim when given,
        /// otherwise the filled template. Warnings about verbatim content are added to warnings.
        /// </summary>
        internal static string WriteNewScriptFile(string path, string scriptName, string template, string namespaceName,
            string content, List<string> warnings)
        {
            string source;
            if (string.IsNullOrEmpty(content))
            {
                if (!TryResolveTemplate(template, namespaceName, out var templateSource, out var templateErr))
                    throw new System.ArgumentException(SkillResultHelper.TryGetError(templateErr, out var message) ? message : "Unknown template");
                source = templateSource
                    .Replace("{CLASS}", scriptName)
                    .Replace("{NAMESPACE}", string.IsNullOrEmpty(namespaceName) ? "DefaultNamespace" : namespaceName);
            }
            else
            {
                source = content;
                warnings?.AddRange(CheckVerbatimContent(content, scriptName, template, namespaceName));
            }

            File.WriteAllText(path, source, SkillsCommon.Utf8NoBom);
            return source;
        }

        /// <summary>
        /// Warnings for verbatim script content, which is written as-is even when they apply: template/namespaceName
        /// passed alongside it are ignored, and a MonoBehaviour or ScriptableObject only binds to its asset when the
        /// class name equals the file name, so content declaring no type named scriptName is flagged.
        /// </summary>
        internal static List<string> CheckVerbatimContent(string content, string scriptName, string template, string namespaceName)
        {
            var warnings = new List<string>();
            if (!string.IsNullOrEmpty(template) || !string.IsNullOrEmpty(namespaceName))
                warnings.Add("content was written verbatim, so template and namespaceName were ignored.");
            if (!DeclaresTypeNamed(content, scriptName))
                warnings.Add($"content declares no type named '{scriptName}'. Unity attaches a MonoBehaviour or ScriptableObject only when its class name equals the file name ({scriptName}.cs); rename the class or the file (script_rename) if this one is meant to be one.");
            return warnings;
        }

        internal static bool DeclaresTypeNamed(string source, string typeName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(typeName))
                return false;
            return Regex.IsMatch(source, @"\b(?:class|struct|interface|enum|record)\s+" + Regex.Escape(typeName) + @"\b",
                RegexOptions.None, System.TimeSpan.FromSeconds(1));
        }

        private static string DeclaredNamespace(string source)
        {
            var match = Regex.Match(source ?? string.Empty, @"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline, System.TimeSpan.FromSeconds(1));
            return match.Success ? match.Groups[1].Value : null;
        }

        [UnitySkill("script_create_batch", "Create multiple scripts efficiently. Before batch-generating gameplay scripts, actively consider coupling, performance, and maintainability for each class role. items: JSON array of {scriptName, folder, template, namespace, content} (content = verbatim source, as in script_create)", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Create,
            Tags = new[] { "script", "batch", "create", "bulk" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            MayTriggerReload = true, MutatesAssets = true,
            RiskLevel = "high")]
        public static object ScriptCreateBatch(
            [SkillParam("JSON array of {scriptName|name, folder?, template?, namespaceName|namespace?, content?}, each as in script_create.")]
            string items)
        {
            return BatchExecutor.Execute<BatchScriptItem>(items, item =>
            {
                var result = ScriptCreate(
                    item.scriptName ?? item.name,
                    null,
                    item.folder ?? "Assets/Scripts",
                    item.template,
                    item.namespaceName ?? item.@namespace,
                    content: item.content);
                if (!SkillResultHelper.TryGetError(result, out _))
                    return result;

                // The item fails with the single call's structured error (errorCode, validValues, ...) kept intact.
                var failed = new JObject { ["target"] = item.scriptName ?? item.name, ["success"] = false };
                foreach (var property in JObject.FromObject(result, JsonSerializer.Create(SkillsCommon.JsonSettings)).Properties())
                    failed[property.Name] = property.Value;
                return failed;
            }, item => item.scriptName ?? item.name);
        }

        private class BatchScriptItem
        {
            public string scriptName { get; set; }
            public string name { get; set; }
            public string folder { get; set; }
            public string template { get; set; }
            public string namespaceName { get; set; }
            public string @namespace { get; set; }
            public string content { get; set; }
        }

        [UnitySkill("script_read", "Read the contents of a script",
            Category = SkillCategory.Script, Operation = SkillOperation.Query,
            Tags = new[] { "script", "read", "content", "source" },
            Outputs = new[] { "path", "lines", "content" },
            RequiresInput = new[] { "scriptPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptRead(string scriptPath)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            var content = File.ReadAllText(scriptPath, System.Text.Encoding.UTF8);
            return new { path = NormalizePath(scriptPath), lines = content.Split('\n').Length, content };
        }

        [UnitySkill("script_delete", "Delete a script file", TracksWorkflow = true, SkipAutoPresnapshot = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Delete,
            Tags = new[] { "script", "delete", "remove", "file" },
            Outputs = new[] { "deleted", "jobId", "waitUrl" },
            RequiresInput = new[] { "scriptPath" },
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object ScriptDelete(string scriptPath)
        {
            if (Validate.SafePath(scriptPath, "scriptPath", isDelete: true) is object pathErr) return pathErr;
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            if (!WorkflowManager.DeleteAssetToTrash(scriptPath))
                return new { error = $"Failed to delete script: {scriptPath}" };
            var job = AsyncJobService.StartScriptMutationJob("script_delete", NormalizePath(scriptPath), checkCompile: false, diagnosticLimit: DefaultDiagnosticLimit, supportsDiagnostics: false);
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["status"] = "accepted",
                ["deleted"] = NormalizePath(scriptPath),
                ["jobId"] = job.jobId,
                ["waitUrl"] = AsyncJobService.BuildWaitUrl(job.jobId)
            };
            ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                result,
                $"Script asset deleted: {NormalizePath(scriptPath)}. Unity may briefly reload the script domain.",
                alwaysInclude: true);
            return result;
        }

        [UnitySkill("script_find_in_file", "Search for pattern in scripts",
            Category = SkillCategory.Script, Operation = SkillOperation.Query,
            Tags = new[] { "script", "search", "pattern", "grep" },
            Outputs = new[] { "pattern", "matchCount", "matches" },
            // This parameter has no CLR default value, but IsParameterRequired treats a no-default
            // reference-type parameter as optional, so schema reports required:false while Validate.Required rejects both missing and empty string -- the two must agree here.
            RequiresInput = new[] { "pattern" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptFindInFile(
            [SkillParam("Case-sensitive substring matched per line; with isRegex a .NET regex.")]
            string pattern,
            string folder = "Assets", bool isRegex = false, int limit = 50)
        {
            if (!string.IsNullOrEmpty(folder) && Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
            if (Validate.Required(pattern, "pattern") is object err) return err;

            if (!Directory.Exists(folder))
                return new { error = $"Directory not found: {folder}" };

            var results = new List<object>();
            var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (results.Count >= limit) break;

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    bool match = isRegex
                        ? Regex.IsMatch(lines[i], pattern, RegexOptions.None, System.TimeSpan.FromSeconds(1))
                        : lines[i].Contains(pattern);

                    if (!match) continue;

                    results.Add(new
                    {
                        file = NormalizePath(file),
                        line = i + 1,
                        content = lines[i].Trim()
                    });

                    if (results.Count >= limit) break;
                }
            }

            return new { pattern, matchCount = results.Count, matches = results };
        }

        [UnitySkill("script_append", "Append content to a script", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Modify,
            Tags = new[] { "script", "append", "insert", "code" },
            Outputs = new[] { "path", "jobId", "waitUrl", "insertedAtLine", "insertionScope" },
            RequiresInput = new[] { "scriptPath" },
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object ScriptAppend(string scriptPath, string content,
            [SkillParam("0-based line index to insert before; the line count appends at the end. -1 or out of range: before the last line that is only '}', except that in a namespaced file a member (not a type) goes before its last class's closing brace. insertedAtLine/insertionScope report where it went.")]
            int atLine = -1,
            bool checkCompile = true, int diagnosticLimit = DefaultDiagnosticLimit)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var lines = File.ReadAllLines(scriptPath).ToList();
            var placement = PlanAppend(lines, content, atLine);
            lines.Insert(placement.Index, content);

            File.WriteAllLines(scriptPath, lines, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(scriptPath);
            var result = CreateScriptMutationResult(scriptPath, "script_append", checkCompile, diagnosticLimit);
            result["insertedAtLine"] = placement.Index;
            result["insertionScope"] = placement.Scope;
            if (placement.Warning != null)
                result["warnings"] = new List<string> { placement.Warning };
            return result;
        }

        internal readonly struct AppendPlacement
        {
            public readonly int Index;
            public readonly string Scope;
            public readonly string Warning;

            public AppendPlacement(int index, string scope, string warning = null)
            {
                Index = index;
                Scope = scope;
                Warning = warning;
            }
        }

        /// <summary>
        /// Where script_append inserts <paramref name="content"/>. An in-range atLine is taken verbatim and atLine ==
        /// line count appends; otherwise the default is the last line that is only '}', as before, except when that
        /// brace closes a namespace and the content is a member: a member is never valid directly in a namespace,
        /// so it goes before the closing brace of the namespace's last class instead. Any doubt about the brace
        /// structure keeps the old placement ("legacy").
        /// </summary>
        internal static AppendPlacement PlanAppend(IReadOnlyList<string> lines, string content, int atLine)
        {
            if (atLine >= 0 && atLine < lines.Count)
                return new AppendPlacement(atLine, "atLine");
            if (atLine == lines.Count)
                return new AppendPlacement(lines.Count, "endOfFile");

            int lastBrace = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if ((lines[i] ?? string.Empty).Trim() == "}")
                {
                    lastBrace = i;
                    break;
                }
            }
            if (lastBrace <= 0)
                return new AppendPlacement(lines.Count, "endOfFile");

            var blocks = ScanBraceBlocks(lines);
            var closing = blocks?.FirstOrDefault(b => b.CloseLine == lastBrace);
            if (closing == null)
                return new AppendPlacement(lastBrace, "legacy", "Could not read the brace structure; inserted before the last '}' line as before.");

            bool namespaceLevelContent = IsNamespaceLevelDeclaration(content);
            if (closing.Namespace != null)
            {
                if (namespaceLevelContent)
                    return new AppendPlacement(lastBrace, "namespace:" + closing.Namespace);

                var type = LastTypeBody(closing, blocks, lines);
                if (type != null)
                    return new AppendPlacement(type.CloseLine, "type:" + type.TypeName,
                        $"Inserted into '{type.TypeName}' instead of namespace '{closing.Namespace}': a member cannot be declared directly in a namespace. Pass atLine to choose the line.");

                return new AppendPlacement(lastBrace, "namespace:" + closing.Namespace,
                    $"No class body was found in namespace '{closing.Namespace}', so the content went directly into the namespace, where only types can be declared. Pass atLine to choose the line.");
            }

            if (closing.TypeName != null)
                return new AppendPlacement(lastBrace, "type:" + closing.TypeName,
                    namespaceLevelContent ? $"content declares a type; it was nested inside '{closing.TypeName}'. Use atLine to place it elsewhere." : null);

            return new AppendPlacement(lastBrace, "block");
        }

        private static readonly Regex TypeHeaderRegex = new Regex(
            @"^(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|new|ref|file)\s+)*(?<kind>record\s+struct|record\s+class|record|class|struct|interface|enum|delegate)\b\s*(?<name>@?[A-Za-z_]\w*)?",
            RegexOptions.Compiled);

        private static readonly Regex NamespaceHeaderRegex = new Regex(@"^namespace\s+(?<name>[\w.@]+)\s*$", RegexOptions.Compiled);

        private static readonly Regex UsingDirectiveRegex = new Regex(@"^(?:global\s+)?using\s+(?:static\s+)?[\w.@]+\s*(?:=\s*[^;(]+)?;", RegexOptions.Compiled);

        /// <summary>
        /// True when the first significant line of <paramref name="content"/> (after blank, comment, attribute-only
        /// and preprocessor lines) declares a type, a namespace or a using directive: things a namespace may contain.
        /// </summary>
        internal static bool IsNamespaceLevelDeclaration(string content)
        {
            foreach (var raw in (content ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith("*") ||
                    line.StartsWith("#") || (line.StartsWith("[") && line.EndsWith("]")))
                    continue;
                return TypeHeaderRegex.IsMatch(line) || line.StartsWith("namespace ") || UsingDirectiveRegex.IsMatch(line);
            }
            return false;
        }

        private sealed class BraceBlock
        {
            public int OpenLine;
            public int CloseLine;
            public int CloseColumn;
            public BraceBlock Parent;
            public string Namespace;
            public string TypeName;
            // class / struct / record: a body that can hold methods and fields.
            public bool HoldsMembers;
        }

        // The namespace's last class/struct/record whose closing brace starts its own line, searching nested namespaces
        // when the namespace itself declares none.
        private static BraceBlock LastTypeBody(BraceBlock ns, List<BraceBlock> blocks, IReadOnlyList<string> lines)
        {
            var children = blocks.Where(b => b.Parent == ns).OrderBy(b => b.CloseLine).ToList();
            var type = children.LastOrDefault(b => b.HoldsMembers && b.OpenLine < b.CloseLine &&
                                                   lines[b.CloseLine].Length - lines[b.CloseLine].TrimStart().Length == b.CloseColumn);
            if (type != null)
                return type;
            var nested = children.LastOrDefault(b => b.Namespace != null);
            return nested != null ? LastTypeBody(nested, blocks, lines) : null;
        }

        /// <summary>
        /// Every brace-delimited block of a C# source, skipping comments, string/char literals (verbatim and interpolated,
        /// holes included) and preprocessor lines. Null when the structure does not balance.
        /// </summary>
        private static List<BraceBlock> ScanBraceBlocks(IReadOnlyList<string> lines)
        {
            var blocks = new List<BraceBlock>();
            var open = new Stack<BraceBlock>();
            var header = new StringBuilder();
            // Interpolation holes still open: brace depth inside the hole, and whether the owning string is verbatim.
            var holes = new Stack<(int depth, bool verbatim)>();
            var mode = LexMode.Code;

            for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex] ?? string.Empty;
                if (mode == LexMode.Code && holes.Count == 0 && line.TrimStart().StartsWith("#"))
                    continue;

                int i = 0;
                while (i < line.Length)
                {
                    char c = line[i];
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';
                    switch (mode)
                    {
                        case LexMode.BlockComment:
                            if (c == '*' && next == '/') { mode = LexMode.Code; i += 2; }
                            else i++;
                            continue;

                        case LexMode.String:
                        case LexMode.InterpolatedString:
                            if (c == '\\') { i += 2; continue; }
                            if (c == '"') { mode = LexMode.Code; i++; continue; }
                            if (mode == LexMode.InterpolatedString && c == '{')
                            {
                                if (next == '{') { i += 2; continue; }
                                holes.Push((0, false));
                                mode = LexMode.Code;
                            }
                            i++;
                            continue;

                        case LexMode.VerbatimString:
                        case LexMode.InterpolatedVerbatimString:
                            if (c == '"')
                            {
                                if (next == '"') { i += 2; continue; }
                                mode = LexMode.Code;
                                i++;
                                continue;
                            }
                            if (mode == LexMode.InterpolatedVerbatimString && c == '{')
                            {
                                if (next == '{') { i += 2; continue; }
                                holes.Push((0, true));
                                mode = LexMode.Code;
                            }
                            i++;
                            continue;
                    }

                    // Code
                    if (c == '/' && next == '/')
                        break;
                    if (c == '/' && next == '*') { mode = LexMode.BlockComment; i += 2; continue; }
                    if (c == '"') { mode = LexMode.String; i++; continue; }
                    if (c == '@' && next == '"') { mode = LexMode.VerbatimString; i += 2; continue; }
                    if (c == '$' && next == '"') { mode = LexMode.InterpolatedString; i += 2; continue; }
                    if ((c == '$' && next == '@' || c == '@' && next == '$') && i + 2 < line.Length && line[i + 2] == '"')
                    {
                        mode = LexMode.InterpolatedVerbatimString;
                        i += 3;
                        continue;
                    }
                    if (c == '\'')
                    {
                        int j = i + 1;
                        while (j < line.Length && line[j] != '\'')
                            j += line[j] == '\\' ? 2 : 1;
                        if (j >= line.Length)
                            return null;
                        i = j + 1;
                        continue;
                    }

                    if (holes.Count > 0)
                    {
                        // Inside an interpolation hole every brace is part of the expression.
                        var hole = holes.Pop();
                        if (c == '{') holes.Push((hole.depth + 1, hole.verbatim));
                        else if (c == '}' && hole.depth > 0) holes.Push((hole.depth - 1, hole.verbatim));
                        else if (c == '}') mode = hole.verbatim ? LexMode.InterpolatedVerbatimString : LexMode.InterpolatedString;
                        else holes.Push(hole);
                        i++;
                        continue;
                    }

                    if (c == '{')
                    {
                        var block = new BraceBlock { OpenLine = lineIndex, Parent = open.Count > 0 ? open.Peek() : null };
                        DescribeBlockHeader(block, Regex.Replace(header.ToString(), @"\s+", " ").Trim());
                        open.Push(block);
                        header.Clear();
                    }
                    else if (c == '}')
                    {
                        if (open.Count == 0)
                            return null;
                        var block = open.Pop();
                        block.CloseLine = lineIndex;
                        block.CloseColumn = i;
                        blocks.Add(block);
                        header.Clear();
                    }
                    else if (c == ';')
                    {
                        header.Clear();
                    }
                    else
                    {
                        header.Append(c);
                    }
                    i++;
                }

                // A regular string or char literal cannot run past the end of its line.
                if (mode == LexMode.String || mode == LexMode.InterpolatedString)
                    return null;
                header.Append(' ');
            }

            return open.Count == 0 && holes.Count == 0 && mode == LexMode.Code ? blocks : null;
        }

        private enum LexMode { Code, BlockComment, String, InterpolatedString, VerbatimString, InterpolatedVerbatimString }

        private static void DescribeBlockHeader(BraceBlock block, string header)
        {
            var ns = NamespaceHeaderRegex.Match(header);
            if (ns.Success)
            {
                block.Namespace = ns.Groups["name"].Value;
                return;
            }

            var type = TypeHeaderRegex.Match(header);
            if (!type.Success || !type.Groups["name"].Success)
                return;
            block.TypeName = type.Groups["name"].Value;
            var kind = type.Groups["kind"].Value;
            block.HoldsMembers = kind.StartsWith("class") || kind.StartsWith("struct") || kind.StartsWith("record");
        }

        [UnitySkill("script_replace", "Find and replace content in a script file", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Modify,
            Tags = new[] { "script", "replace", "find", "refactor" },
            Outputs = new[] { "path", "replacements", "jobId", "waitUrl" },
            RequiresInput = new[] { "scriptPath" },
            RequiredParams = new[] { "find" },
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object ScriptReplace(string scriptPath,
            [SkillParam("Literal, case-sensitive text, every occurrence replaced; with isRegex a .NET regex.")]
            string find,
            [SkillParam("Replacement text; with isRegex, $1 / ${name} substitutions apply.")]
            string replace,
            bool isRegex = false, bool checkCompile = true, int diagnosticLimit = DefaultDiagnosticLimit)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (Validate.Required(find, "find") is object findErr) return findErr;
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var content = File.ReadAllText(scriptPath, System.Text.Encoding.UTF8);
            string newContent = isRegex
                ? Regex.Replace(content, find, replace, RegexOptions.None, System.TimeSpan.FromSeconds(2))
                : content.Replace(find, replace);
            int changes = isRegex
                ? Regex.Matches(content, find, RegexOptions.None, System.TimeSpan.FromSeconds(2)).Count
                : (content.Length - content.Replace(find, "").Length) / (find.Length > 0 ? find.Length : 1);

            File.WriteAllText(scriptPath, newContent, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(scriptPath);

            var result = CreateScriptMutationResult(scriptPath, "script_replace", checkCompile, diagnosticLimit);
            result["replacements"] = changes;
            return result;
        }

        [UnitySkill("script_list", "List C# script files in the project",
            Category = SkillCategory.Script, Operation = SkillOperation.Query,
            Tags = new[] { "script", "list", "project", "files" },
            Outputs = new[] { "count", "scripts" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptList(string folder = "Assets", string filter = null, int limit = 100)
        {
            var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { folder });
            var scripts = guids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => p.EndsWith(".cs"))
                .Where(p => string.IsNullOrEmpty(filter) || p.Contains(filter))
                .Take(limit)
                .Select(p => new { path = p, name = Path.GetFileNameWithoutExtension(p) })
                .ToArray();

            return new { count = scripts.Length, scripts };
        }

        [UnitySkill("script_get_info", "Get script info (class name, base class, methods)",
            Category = SkillCategory.Script, Operation = SkillOperation.Query,
            Tags = new[] { "script", "info", "class", "reflection" },
            Outputs = new[] { "path", "className", "baseClass", "publicMethods", "publicFields" },
            RequiresInput = new[] { "scriptPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptGetInfo(string scriptPath)
        {
            var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (monoScript == null) return new { error = $"MonoScript not found: {scriptPath}" };

            var type = monoScript.GetClass();
            if (type == null) return new { path = NormalizePath(scriptPath), className = "(unknown)", note = "Class not yet compiled or abstract" };

            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .ToArray();
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => new { name = f.Name, type = f.FieldType.Name })
                .ToArray();

            return new
            {
                path = NormalizePath(scriptPath),
                className = type.Name,
                baseClass = type.BaseType?.Name,
                namespaceName = type.Namespace,
                isMonoBehaviour = typeof(MonoBehaviour).IsAssignableFrom(type),
                publicMethods = methods,
                publicFields = fields
            };
        }

        [UnitySkill("script_rename", "Rename a script file", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Modify,
            Tags = new[] { "script", "rename", "refactor", "file" },
            Outputs = new[] { "path", "oldPath", "newName", "jobId", "waitUrl" },
            RequiresInput = new[] { "scriptPath" },
            RequiredParams = new[] { "newName" },
            MayTriggerReload = true, RiskLevel = "high", MutatesAssets = true)]
        public static object ScriptRename(string scriptPath,
            [SkillParam("New file name without .cs; only the file is renamed, not the class inside it.")]
            string newName,
            bool checkCompile = true, int diagnosticLimit = DefaultDiagnosticLimit)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath)) return new { error = $"Script not found: {scriptPath}" };
            if (Validate.Required(newName, "newName") is object newNameErr) return newNameErr;
            if (HasPathSeparators(newName))
                return new { error = "newName must not contain path separators" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var renameResult = AssetDatabase.RenameAsset(scriptPath, newName);
            if (!string.IsNullOrEmpty(renameResult)) return new { error = renameResult };

            var renamedPath = Path.Combine(Path.GetDirectoryName(scriptPath) ?? "", newName + ".cs");
            var result = CreateScriptMutationResult(renamedPath, "script_rename", checkCompile, diagnosticLimit);
            result["oldPath"] = NormalizePath(scriptPath);
            result["newName"] = newName;
            return result;
        }

        [UnitySkill("script_move", "Move a script to a new folder", TracksWorkflow = true,
            Category = SkillCategory.Script, Operation = SkillOperation.Modify,
            Tags = new[] { "script", "move", "reorganize", "file" },
            Outputs = new[] { "oldPath", "newPath", "jobId", "waitUrl" },
            RequiresInput = new[] { "scriptPath", "newFolder" },
            MayTriggerReload = true, RiskLevel = "high", MutatesAssets = true)]
        public static object ScriptMove(string scriptPath,
            [SkillParam("Destination folder, must start with Assets/ or Packages/; created if missing.")]
            string newFolder,
            bool checkCompile = true, int diagnosticLimit = DefaultDiagnosticLimit)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath)) return new { error = $"Script not found: {scriptPath}" };
            if (Validate.SafePath(newFolder, "newFolder") is object folderErr) return folderErr;

            var fileName = Path.GetFileName(scriptPath);
            var newPath = Path.Combine(newFolder, fileName);
            // Can't use System.IO to create the directory: that only touches the filesystem, and
            // AssetDatabase won't know about the folder until Refresh runs, so MoveAsset fails on an
            // unregistered parent directory ("Could not find parent directory GUID:0000...") and leaves
            // an orphan directory with no .meta. EnsureAssetFolderExists uses AssetDatabase.CreateFolder to create level by level, registering as it goes.
            RenderPipelineSkillsCommon.EnsureAssetFolderExists(newPath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var moveResult = AssetDatabase.MoveAsset(scriptPath, newPath);
            if (!string.IsNullOrEmpty(moveResult)) return new { error = moveResult };

            var result = CreateScriptMutationResult(newPath, "script_move", checkCompile, diagnosticLimit);
            result["oldPath"] = NormalizePath(scriptPath);
            result["newPath"] = NormalizePath(newPath);
            return result;
        }

        [UnitySkill("script_get_compile_feedback", "Get compile diagnostics related to a specific script. Use after script_create/script_append/script_replace/script_rename/script_move, or GET the waitUrl they return (/jobs/<jobId>?wait=90), which answers once compilation settles with the same diagnostics in resultData.compilation.",
            Category = SkillCategory.Script, Operation = SkillOperation.Query,
            Tags = new[] { "script", "compile", "diagnostics", "errors" },
            Outputs = new[] { "scriptPath", "isCompiling", "hasErrors", "errorCount", "errors" },
            RequiresInput = new[] { "scriptPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScriptGetCompileFeedback(string scriptPath, int limit = DefaultDiagnosticLimit)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath)) return new { error = $"Script not found: {scriptPath}" };
            return GetCompilationFeedbackSnapshot(scriptPath, limit);
        }

        private static Dictionary<string, object> CreateScriptMutationResult(string scriptPath, string operation, bool checkCompile, int diagnosticLimit)
        {
            var normalizedPath = NormalizePath(scriptPath);
            var job = AsyncJobService.StartScriptMutationJob(operation, normalizedPath, checkCompile, diagnosticLimit);
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["status"] = "accepted",
                ["path"] = normalizedPath,
                ["jobId"] = job.jobId,
                ["waitUrl"] = AsyncJobService.BuildWaitUrl(job.jobId)
            };

            ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                result,
                $"Script asset changed: {normalizedPath}. Unity may briefly reload the script domain.",
                alwaysInclude: true);

            return result;
        }

        internal static Dictionary<string, object> GetCompilationFeedbackSnapshot(string scriptPath, int limit)
        {
            string normalizedPath = NormalizePath(scriptPath);
            string fileName = Path.GetFileName(normalizedPath);
            string className = Path.GetFileNameWithoutExtension(normalizedPath);
            bool isCompiling = EditorApplication.isCompiling || EditorApplication.isUpdating;

            var diagnostics = FindRelevantCompileErrors(normalizedPath, fileName, className, limit)
                .Select(log => new
                {
                    type = log.type,
                    message = log.message,
                    file = NormalizePath(log.file),
                    line = log.line
                })
                .ToArray();

            return new Dictionary<string, object>
            {
                ["scriptPath"] = normalizedPath,
                ["isCompiling"] = isCompiling,
                ["hasErrors"] = diagnostics.Length > 0,
                ["errorCount"] = diagnostics.Length,
                ["errors"] = diagnostics,
                ["nextAction"] = isCompiling
                    ? "Unity is still compiling. Call script_get_compile_feedback again after compilation finishes."
                    : diagnostics.Length > 0
                        ? "Fix the script based on the reported errors, then call script_get_compile_feedback again."
                        : "No compile errors were found for this script."
            };
        }

        private static IEnumerable<DebugSkills.LogEntryInfo> FindRelevantCompileErrors(string normalizedPath, string fileName, string className, int limit)
        {
            int searchLimit = Mathf.Max(Mathf.Max(limit, DefaultDiagnosticLimit), 1) * 5;
            var logs = DebugSkills.ReadLogEntries(DebugSkills.ErrorModeMask, null, searchLimit);
            return logs
                .Where(log => IsRelevantCompileError(log, normalizedPath, fileName, className))
                .Take(Mathf.Max(limit, 1));
        }

        private static bool IsRelevantCompileError(DebugSkills.LogEntryInfo log, string normalizedPath, string fileName, string className)
        {
            if (log == null) return false;

            string logFile = NormalizePath(log.file);
            if (!string.IsNullOrEmpty(logFile))
            {
                if (logFile.EndsWith(normalizedPath, System.StringComparison.OrdinalIgnoreCase) ||
                    logFile.EndsWith("/" + fileName, System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(logFile), fileName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string message = log.message ?? "";
            return message.IndexOf(fileName, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (!string.IsNullOrEmpty(className) && message.IndexOf(className, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool HasPathSeparators(string value)
        {
            return value.Contains("/") || value.Contains("\\") || value.Contains("..");
        }

        internal static readonly string[] TemplateNames = { "MonoBehaviour", "ScriptableObject", "Editor", "EditorWindow" };

        // One line of letters, digits, '_', ' ', '.' or '-': no C# compilation unit can be written with only these
        // (every declaration needs braces or a semicolon, a comment starts with '/'), so such a template is a
        // misspelt template name, never literal source.
        private static readonly Regex BareTemplateNameRegex = new Regex(@"^[\p{L}_][\p{L}\p{Nd}_ \t.\-]*\z", RegexOptions.Compiled);

        /// <summary>
        /// Resolves <paramref name="template"/> to source with {CLASS}/{NAMESPACE} placeholders: blank is
        /// MonoBehaviour, a known name matches ignoring case and spaces, any text containing code is a literal
        /// template, and a bare unknown name is rejected with the valid names.
        /// </summary>
        internal static bool TryResolveTemplate(string template, string namespaceName, out string source, out object error)
        {
            error = null;
            var canonical = CanonicalTemplateName(template);
            if (canonical != null)
            {
                source = TemplateSource(canonical, namespaceName);
                return true;
            }

            var trimmed = template.Trim();
            if (BareTemplateNameRegex.IsMatch(trimmed))
            {
                source = null;
                var closest = SkillsCommon.ClosestMatch(trimmed.Replace(" ", string.Empty), TemplateNames);
                var fixes = new List<object>();
                if (closest != null)
                    fixes.Add(new { action = "fix_param", args = new { template = closest }, reason = "Closest known template." });
                fixes.Add(new { action = "fix_param", args = new { content = "<complete C# source>" }, reason = "Plain C# classes, interfaces, NetworkBehaviour etc. go in content." });
                error = new
                {
                    error = $"Invalid value '{template}' for parameter 'template': not a known template. Valid values: {string.Join(", ", TemplateNames)}. Custom source goes in content (verbatim) or in template as text containing code.",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry,
                    parameter = "template",
                    validValues = TemplateNames,
                    suggestedFixes = fixes.ToArray()
                };
                return false;
            }

            source = template;
            return true;
        }

        // The known template a value names (blank = MonoBehaviour), or null for anything else.
        private static string CanonicalTemplateName(string template)
        {
            if (string.IsNullOrWhiteSpace(template))
                return "MonoBehaviour";
            var compact = template.Replace(" ", string.Empty);
            return TemplateNames.FirstOrDefault(known => string.Equals(known, compact, System.StringComparison.OrdinalIgnoreCase));
        }

        private static string TemplateSource(string canonicalName, string namespaceName)
        {
            switch (canonicalName)
            {
                case "ScriptableObject":
                    return WrapInNamespace(namespaceName, @"using UnityEngine;

[CreateAssetMenu(fileName = ""{CLASS}"", menuName = ""Game/{CLASS}"")]
public class {CLASS} : ScriptableObject
{
}
");
                case "Editor":
                    return WrapInNamespace(namespaceName, @"using UnityEditor;

public class {CLASS} : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
    }
}
");
                case "EditorWindow":
                    return WrapInNamespace(namespaceName, @"using UnityEditor;

public class {CLASS} : EditorWindow
{
    [MenuItem(""Window/{CLASS}"")]
    public static void ShowWindow()
    {
        GetWindow<{CLASS}>(""{CLASS}"");
    }
}
");
                default:
                    return WrapInNamespace(namespaceName, @"using UnityEngine;

public class {CLASS} : MonoBehaviour
{
}
");
            }
        }

        internal static bool IsEditorOnlyTemplate(string template)
        {
            var canonical = CanonicalTemplateName(template);
            return canonical == "Editor" || canonical == "EditorWindow";
        }

        private static string WrapInNamespace(string namespaceName, string content)
        {
            if (string.IsNullOrEmpty(namespaceName))
                return content;

            return $@"namespace {{NAMESPACE}}
{{
{IndentContent(content, 1)}
}}";
        }

        private static string IndentContent(string content, int level)
        {
            string indent = new string(' ', level * 4);
            var normalized = content.Replace("\r\n", "\n").TrimEnd('\n');
            return string.Join("\n", normalized.Split('\n').Select(line =>
                string.IsNullOrEmpty(line) ? string.Empty : indent + line)) + "\n";
        }

        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace("\\", "/");
        }
    }
}

// Producer:Betsy
