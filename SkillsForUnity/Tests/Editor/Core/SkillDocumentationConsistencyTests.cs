using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class SkillDocumentationConsistencyTests
    {
        private static readonly Regex SkillHeadingRegex =
            new Regex(@"^###\s+`?(?<name>[a-z0-9]+(?:_[a-z0-9]+)+)`?\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 顶层 SKILL.md 中形如 skill 名的完整 code-span。要求前后都是反引号，所以
        /// `workflow_session_*` 这类通配写法整体不匹配，无需额外豁免。
        /// </summary>
        private static readonly Regex RootDocSkillTokenRegex =
            new Regex(@"`(?<name>[a-z0-9]+(?:_[a-z0-9]+)+)`", RegexOptions.Compiled);

        /// <summary>unity_skills.py 的模块级公开函数（缩进为 0 且不以 _ 开头）。</summary>
        private static readonly Regex PythonModuleDefRegex =
            new Regex(@"^def (?<name>[a-z][a-z0-9_]*)\(", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// 顶层 SKILL.md 里允许出现、但本就不是 skill 名的下划线 token。新增例外必须显式登记 ——
        /// 这份显式清单正是幽灵 skill 名再也漏不出去的机制。
        /// </summary>
        private static readonly HashSet<string> RootDocNonSkillTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            // errorCode / retryStrategy 取值
            "fix_and_retry", "find_target_and_retry", "install_and_retry",
            // GET /events 的事件类型
            "compilation_started", "compilation_finished", "before_domain_reload", "after_domain_reload",
            "server_restored", "playmode_changed", "console_error", "job_completed", "job_failed",
            // 响应字段 / 正文用语
            "rolled_back", "module_verb",
        };

        private static readonly HashSet<string> AdvisoryModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "architecture",
            "patterns",
            "performance",
            "asmdef",
            "async",
            "inspector",
            "blueprints",
            "adr",
            "project-scout",
            "scene-contracts",
            "script-roles",
            "scriptdesign",
            "testability",
            "bookmark",
            "history",
            "shadergraph-design",
            // 以下 *-design 均为纯设计指南（0 个 ### skill 端点定义），与 shadergraph-design 同质，
            // 统一豁免 schema-first（Exact Signatures）校验，避免误报。
            "addressables-design",
            "dotween-design",
            "primetween-design",
            "netcode-design",
            "unitask-design",
            "yooasset-design",
            "yaml-editing"
        };

        private static readonly HashSet<string> ExactSignatureOptionalModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "batch",
            "editor",
            "profiler",
            "scene",
            "timeline",
            "workflow"
        };

        [Test]
        public void SkillDocumentation_ShouldMatchCodeDefinitions()
        {
            var codeSkills = LoadCodeSkills();
            var docSkills = LoadDocumentedSkills();
            var issues = new List<string>();

            AssertSchemaFirstDocumentation(GetDocsRoot(), issues);

            foreach (var ghost in docSkills.Keys.Except(codeSkills.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                var docSkill = docSkills[ghost];
                issues.Add($"幽灵 Skill: {docSkill.Module}/SKILL.md -> `{ghost}`");
            }

            foreach (var name in codeSkills.Keys.Intersect(docSkills.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                CompareParameters(name, codeSkills[name], docSkills[name], issues);
            }

            AssertNoIssues(issues, "Skill 文档与 schema-first 约束不一致");
        }

        [Test]
        public void UnitySkillMetadata_ShouldBeComplete()
        {
            var issues = new List<string>();

            foreach (var skill in LoadCodeSkills().Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var attr = skill.Attribute;
                var owner = $"{skill.Method.DeclaringType?.Name}.{skill.Method.Name}";

                if (attr.Category == SkillCategory.Uncategorized)
                {
                    issues.Add($"缺少 Category: `{skill.Name}` ({owner})");
                }

                if (attr.Operation == 0)
                {
                    issues.Add($"缺少 Operation: `{skill.Name}` ({owner})");
                }

                if (attr.Tags == null || attr.Tags.Length == 0)
                {
                    issues.Add($"缺少 Tags: `{skill.Name}` ({owner})");
                }

                if (skill.Method.ReturnType != typeof(void) && (attr.Outputs == null || attr.Outputs.Length == 0))
                {
                    issues.Add($"缺少 Outputs: `{skill.Name}` ({owner})");
                }
            }

            AssertNoIssues(issues, "UnitySkill 元数据不完整");
        }

        [Test]
        public void YooAssetSkills_ShouldHaveEnglishAndChineseLocalization()
        {
            var yooAssetSkillNames = LoadCodeSkills()
                .Keys
                .Where(name => name.StartsWith("yooasset_", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.That(yooAssetSkillNames, Is.Not.Empty, "未发现 YooAsset Skill。");

            var english = GetLocalizationDictionary("_english");
            var chinese = GetLocalizationDictionary("_chinese");
            var issues = new List<string>();

            foreach (var skillName in yooAssetSkillNames)
            {
                if (!english.TryGetValue(skillName, out var englishText) || string.IsNullOrWhiteSpace(englishText))
                {
                    issues.Add($"缺少英文翻译: `{skillName}`");
                }

                if (!chinese.TryGetValue(skillName, out var chineseText) || string.IsNullOrWhiteSpace(chineseText))
                {
                    issues.Add($"缺少中文翻译: `{skillName}`");
                }
            }

            AssertNoIssues(issues, "YooAsset Skill 本地化不完整");
        }

        // ============================================================
        // 顶层 unity-skills~/SKILL.md 的引用一致性（issue #52）
        //
        // 其余测试经 GetDocsRoot() 只遍历 skills/*/SKILL.md，顶层 SKILL.md 零覆盖 —— 25 个
        // 幽灵 skill 名和一处裸 helper 名因此长期潜伏，最终导致 agent 反复调用不存在的
        // `get_skill_schema` / `health_check`。以下三个测试把这块盲区补上。
        // ============================================================

        [Test]
        public void RootSkillDoc_ShouldNotReferenceUnregisteredSkillNames()
        {
            var registered = LoadCodeSkills().Keys;
            var doc = ReadRootSkillDoc(out var docPath);
            var issues = new List<string>();

            foreach (Match match in RootDocSkillTokenRegex.Matches(doc))
            {
                var token = match.Groups["name"].Value;
                if (registered.Contains(token) || RootDocNonSkillTokens.Contains(token))
                {
                    continue;
                }

                issues.Add($"幽灵 Skill: SKILL.md -> `{token}`（不在已注册 skill 中；" +
                           "若它本就不是 skill 名，登记到 RootDocNonSkillTokens）");
            }

            AssertNoIssues(issues, $"顶层 SKILL.md 引用了未注册的 skill 名: {docPath}");
        }

        [Test]
        public void RootSkillDoc_ShouldQualifyPythonHelperCalls()
        {
            var helpers = LoadPythonHelperNames();
            var doc = ReadRootSkillDoc(out var docPath);

            // 只匹配调用形态 `name(`：文档里 `GET /health` 中的 health 与同名 helper 无关，
            // 不该被当成未限定调用。
            var pattern = new Regex(
                @"(?<!unity_skills\.)\b(?<name>" +
                string.Join("|", helpers.OrderByDescending(h => h.Length).Select(Regex.Escape)) +
                @")\s*\(",
                RegexOptions.Compiled);

            var issues = pattern.Matches(doc)
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(name => $"Python helper 缺少 `unity_skills.` 前缀: `{name}()` —— " +
                                $"裸名会被 agent 当作 skill 名 POST 到 /skill/{name}")
                .ToList();

            AssertNoIssues(issues, $"顶层 SKILL.md 的 Python helper 名未限定: {docPath}");
        }

        [Test]
        public void ClientHelperRestEquivalents_ShouldMapRealHelpersThatAreNotSkills()
        {
            var field = typeof(SkillRouter).GetField(
                "k_ClientHelperRestEquivalents", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "未找到 SkillRouter.k_ClientHelperRestEquivalents");

            var table = field.GetValue(null) as Dictionary<string, string>;
            Assert.That(table, Is.Not.Null, "k_ClientHelperRestEquivalents 类型不是 Dictionary<string, string>");
            Assert.That(table, Is.Not.Empty, "k_ClientHelperRestEquivalents 为空");

            var helpers = LoadPythonHelperNames();
            var registered = LoadCodeSkills().Keys;
            var issues = new List<string>();

            foreach (var entry in table.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!helpers.Contains(entry.Key))
                {
                    issues.Add($"表键不是 unity_skills.py 的模块级 helper: `{entry.Key}`" +
                               "（拼错的键永远命中不了，是静默失效）");
                }

                if (registered.Contains(entry.Key))
                {
                    issues.Add($"表键与已注册 skill 同名: `{entry.Key}`" +
                               "（该名会走正常执行路径，定向纠正永远不触发）");
                }

                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    issues.Add($"表值为空: `{entry.Key}`");
                }
            }

            AssertNoIssues(issues, "SkillRouter.k_ClientHelperRestEquivalents 与 Python 客户端脱钩");
        }

        private static void CompareParameters(string skillName, CodeSkill codeSkill, DocSkill docSkill, List<string> issues)
        {
            var codeParams = codeSkill.Parameters;
            var docParams = docSkill.Parameters;
            var isBatchEnvelope =
                skillName.EndsWith("_batch", StringComparison.Ordinal) &&
                codeParams.ContainsKey("items") &&
                docParams.ContainsKey("items");

            foreach (var docParam in docParams.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (isBatchEnvelope && !string.Equals(docParam.Name, "items", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsLooseParameterShorthand(docParam.Name))
                {
                    continue;
                }

                if (!codeParams.TryGetValue(docParam.Name, out var codeParam))
                {
                    issues.Add($"文档多出参数: `{skillName}.{docParam.Name}`");
                    continue;
                }
            }

        }

        private static void AssertSchemaFirstDocumentation(string docsRoot, List<string> issues)
        {
            foreach (var moduleDir in Directory.GetDirectories(docsRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (AdvisoryModules.Contains(moduleName))
                {
                    continue;
                }

                var skillDocPath = Path.Combine(moduleDir, "SKILL.md");
                if (!File.Exists(skillDocPath))
                {
                    continue;
                }

                var content = File.ReadAllText(skillDocPath);
                if (content.IndexOf("## Canonical Signatures", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    issues.Add($"残留 Canonical Signatures: {moduleName}/SKILL.md");
                }

                if (ExactSignatureOptionalModules.Contains(moduleName))
                {
                    continue;
                }

                var hasExactSignatures = content.IndexOf("## Exact Signatures", StringComparison.OrdinalIgnoreCase) >= 0;
                var mentionsSchemaEndpoint = content.IndexOf("/skills/schema", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hasExactSignatures || !mentionsSchemaEndpoint)
                {
                    issues.Add($"缺少 schema-first Exact Signatures 声明: {moduleName}/SKILL.md");
                }
            }
        }

        private static bool IsLooseParameterShorthand(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            return name.IndexOf(',') >= 0 ||
                   name.IndexOf('/') >= 0 ||
                   name.IndexOf(' ') >= 0 ||
                   name.IndexOf('*') >= 0;
        }

        private static string StripParameterShorthand(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            var eqIdx = name.IndexOf('=');
            if (eqIdx >= 0)
                name = name.Substring(0, eqIdx);

            if (name.EndsWith("?", StringComparison.Ordinal))
                name = name.Substring(0, name.Length - 1);

            return name.Trim();
        }

        private static Dictionary<string, CodeSkill> LoadCodeSkills()
        {
            var result = new Dictionary<string, CodeSkill>(StringComparer.Ordinal);
            var assembly = typeof(UnitySkillAttribute).Assembly;

            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<UnitySkillAttribute>();
                    if (attr == null || string.IsNullOrWhiteSpace(attr.Name))
                    {
                        continue;
                    }

                    var parameters = method
                        .GetParameters()
                        .Select(p => new CodeParameter
                        {
                            Name = p.Name,
                            Type = NormalizeCodeType(p.ParameterType),
                            Required = !p.IsOptional
                        })
                        .ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);

                    result[attr.Name] = new CodeSkill
                    {
                        Name = attr.Name,
                        Method = method,
                        Attribute = attr,
                        Parameters = parameters
                    };
                }
            }

            return result;
        }

        private static Dictionary<string, DocSkill> LoadDocumentedSkills()
        {
            var docsRoot = GetDocsRoot();
            Assert.That(Directory.Exists(docsRoot), Is.True, $"技能文档目录不存在: {docsRoot}");

            var result = new Dictionary<string, DocSkill>(StringComparer.Ordinal);

            foreach (var moduleDir in Directory.GetDirectories(docsRoot).OrderBy(x => x, StringComparer.Ordinal))
            {
                var moduleName = Path.GetFileName(moduleDir);
                if (AdvisoryModules.Contains(moduleName))
                {
                    continue;
                }

                var skillDocPath = Path.Combine(moduleDir, "SKILL.md");
                if (!File.Exists(skillDocPath))
                {
                    continue;
                }

                var lines = File.ReadAllLines(skillDocPath);
                for (var i = 0; i < lines.Length; i++)
                {
                    var match = SkillHeadingRegex.Match(lines[i]);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var skillName = match.Groups["name"].Value;
                    var parameters = new Dictionary<string, DocParameter>(StringComparer.Ordinal);
                    var parsedParameterBlock = false;

                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        if (lines[j].StartsWith("### ", StringComparison.Ordinal))
                        {
                            break;
                        }

                        if (!parsedParameterBlock)
                        {
                            var tableEndIndex = TryParseParameterTable(lines, j, parameters);
                            if (tableEndIndex >= j)
                            {
                                j = tableEndIndex;
                                parsedParameterBlock = true;
                                continue;
                            }

                            var inlineEndIndex = TryParseInlineParameters(lines, j, parameters);
                            if (inlineEndIndex >= j)
                            {
                                j = inlineEndIndex;
                                parsedParameterBlock = true;
                            }
                        }
                    }

                    result[skillName] = new DocSkill
                    {
                        Name = skillName,
                        Module = moduleName,
                        FilePath = skillDocPath,
                        Parameters = parameters
                    };
                }
            }

            return result;
        }

        private static int TryParseParameterTable(string[] lines, int startIndex, Dictionary<string, DocParameter> parameters)
        {
            var line = lines[startIndex].TrimStart();
            if (!line.StartsWith("|", StringComparison.Ordinal))
            {
                return -1;
            }

            var parsedAny = false;
            var endIndex = startIndex;

            for (var i = startIndex; i < lines.Length; i++)
            {
                var current = lines[i].TrimStart();
                if (!current.StartsWith("|", StringComparison.Ordinal))
                {
                    break;
                }

                endIndex = i;
                if (TryParseParameterRow(lines[i], out var parameter))
                {
                    parameters[parameter.Name] = parameter;
                    parsedAny = true;
                }
            }

            return parsedAny ? endIndex : -1;
        }

        private static int TryParseInlineParameters(string[] lines, int startIndex, Dictionary<string, DocParameter> parameters)
        {
            var trimmed = lines[startIndex].Trim();
            if (!trimmed.StartsWith("**Parameters:**", StringComparison.Ordinal))
            {
                return -1;
            }

            var remainder = trimmed.Substring("**Parameters:**".Length).Trim();
            if (remainder.StartsWith("None", StringComparison.OrdinalIgnoreCase))
            {
                return startIndex;
            }

            if (!string.IsNullOrEmpty(remainder))
            {
                foreach (Match match in Regex.Matches(remainder, @"`(?<name>[^`]+)`"))
                {
                    var name = match.Groups["name"].Value.Trim();
                    name = StripParameterShorthand(name);
                    if (!string.IsNullOrEmpty(name))
                    {
                        parameters[name] = new DocParameter { Name = name, Type = string.Empty, Required = true };
                    }
                }

                return parameters.Count > 0 ? startIndex : -1;
            }

            var parsedAny = false;
            var endIndex = startIndex;
            for (var i = startIndex + 1; i < lines.Length; i++)
            {
                var bullet = lines[i].Trim();
                if (!bullet.StartsWith("-", StringComparison.Ordinal))
                {
                    break;
                }

                endIndex = i;
                if (TryParseBulletParameterRow(bullet, out var parameter))
                {
                    parameters[parameter.Name] = parameter;
                    parsedAny = true;
                }
            }

            return parsedAny ? endIndex : -1;
        }

        private static bool TryParseParameterRow(string line, out DocParameter parameter)
        {
            parameter = null;
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal) || trimmed.Length < 2)
            {
                return false;
            }

            var cells = trimmed
                .Trim('|')
                .Split('|')
                .Select(x => x.Trim())
                .ToArray();

            if (cells.Length < 3)
            {
                return false;
            }

            var name = StripInlineCode(cells[0]);
            if (string.IsNullOrWhiteSpace(name) || name == "-" || name == "Parameter" || name.StartsWith("---", StringComparison.Ordinal))
            {
                return false;
            }

            parameter = new DocParameter
            {
                Name = name,
                Type = NormalizeDocType(cells[1]),
                Required = NormalizeRequired(cells[2])
            };
            return true;
        }

        private static bool TryParseBulletParameterRow(string line, out DocParameter parameter)
        {
            parameter = null;
            var match = Regex.Match(line, @"^-\s*`(?<name>[^`]+)`\s*(?:\((?<type>[^)]+)\))?");
            if (!match.Success)
            {
                return false;
            }

            parameter = new DocParameter
            {
                Name = match.Groups["name"].Value.Trim(),
                Type = NormalizeDocType(match.Groups["type"].Value),
                Required = true
            };
            return true;
        }

        private static bool NormalizeRequired(string cell)
        {
            var value = StripInlineCode(cell).Trim();
            return value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Required", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("True", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeDocType(string raw)
        {
            var value = StripInlineCode(raw).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Replace(" ", string.Empty);
            value = ReplaceIgnoreCase(value, "integer", "int");
            value = ReplaceIgnoreCase(value, "boolean", "bool");
            value = ReplaceIgnoreCase(value, "number", "float");
            value = ReplaceIgnoreCase(value, "any", "object");
            if (value.EndsWith("?", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }

            return value;
        }

        private static string NormalizeCodeType(Type type)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
            {
                type = nullableType;
            }

            if (type.IsArray)
            {
                return NormalizeCodeType(type.GetElementType()) + "[]";
            }

            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(long)) return "long";
            if (type == typeof(object)) return "object";

            if (type.IsGenericType)
            {
                var genericType = type.GetGenericTypeDefinition();
                if (genericType == typeof(List<>))
                {
                    return NormalizeCodeType(type.GetGenericArguments()[0]) + "[]";
                }
            }

            return type.Name;
        }

        private static bool TypesMatch(string docType, string codeType)
        {
            if (string.Equals(docType, codeType, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(docType, "array", StringComparison.OrdinalIgnoreCase) && codeType.EndsWith("[]", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static string StripInlineCode(string value)
        {
            return value.Replace("`", string.Empty).Trim();
        }

        private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
        {
            var index = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return input;
            }

            return input.Substring(0, index) + newValue + input.Substring(index + oldValue.Length);
        }

        private static string GetDocsRoot()
        {
            var projectDocsRoot = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("无法解析 Unity 项目根目录。"),
                "SkillsForUnity",
                "unity-skills~",
                "skills");

            if (Directory.Exists(projectDocsRoot))
            {
                return projectDocsRoot;
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly)
                             ?? PackageInfo.FindForAssembly(typeof(SkillDocumentationConsistencyTests).Assembly);
            if (packageInfo != null)
            {
                var packageDocsRoot = Path.Combine(packageInfo.resolvedPath, "unity-skills~", "skills");
                if (Directory.Exists(packageDocsRoot))
                {
                    return packageDocsRoot;
                }
            }

            return projectDocsRoot;
        }

        /// <summary>unity-skills~ 包根目录（GetDocsRoot() 的父级）。</summary>
        private static string GetPackageRoot()
        {
            return Directory.GetParent(GetDocsRoot())?.FullName
                   ?? throw new InvalidOperationException("无法解析 unity-skills~ 根目录。");
        }

        private static string ReadRootSkillDoc(out string path)
        {
            path = Path.Combine(GetPackageRoot(), "SKILL.md");
            Assert.That(File.Exists(path), Is.True, $"顶层 SKILL.md 不存在: {path}");
            return File.ReadAllText(path);
        }

        private static HashSet<string> LoadPythonHelperNames()
        {
            var scriptPath = Path.Combine(GetPackageRoot(), "scripts", "unity_skills.py");
            Assert.That(File.Exists(scriptPath), Is.True, $"Python 客户端不存在: {scriptPath}");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in PythonModuleDefRegex.Matches(File.ReadAllText(scriptPath)))
            {
                names.Add(match.Groups["name"].Value);
            }

            Assert.That(names, Is.Not.Empty, $"未从 {scriptPath} 解析到任何模块级 helper");
            return names;
        }

        private static Dictionary<string, string> GetLocalizationDictionary(string fieldName)
        {
            var field = typeof(SkillsLocalization).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"未找到 SkillsLocalization.{fieldName}");

            var dictionary = field.GetValue(null) as Dictionary<string, string>;
            Assert.That(dictionary, Is.Not.Null, $"SkillsLocalization.{fieldName} 类型不是 Dictionary<string, string>");
            return dictionary;
        }

        private static void AssertNoIssues(List<string> issues, string title)
        {
            if (issues.Count == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine(title);
            foreach (var issue in issues.Take(100))
            {
                builder.AppendLine(issue);
            }

            if (issues.Count > 100)
            {
                builder.AppendLine($"... 还有 {issues.Count - 100} 条");
            }

            Assert.Fail(builder.ToString());
        }

        private sealed class CodeSkill
        {
            public string Name;
            public MethodInfo Method;
            public UnitySkillAttribute Attribute;
            public Dictionary<string, CodeParameter> Parameters;
        }

        private sealed class DocSkill
        {
            public string Name;
            public string Module;
            public string FilePath;
            public Dictionary<string, DocParameter> Parameters;
        }

        private sealed class CodeParameter
        {
            public string Name;
            public string Type;
            public bool Required;
        }

        private sealed class DocParameter
        {
            public string Name;
            public string Type;
            public bool Required;
        }
    }
}

// Producer:Betsy
