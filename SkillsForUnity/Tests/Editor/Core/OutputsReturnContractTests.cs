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
    /// <summary>
    /// Outputs 元数据 ↔ 方法体返回形状的红线。
    ///
    /// <see cref="SkillDocumentationConsistencyTests.UnitySkillMetadata_ShouldBeComplete"/> 对 Outputs
    /// 只查「非空」，抓不到幽灵键：gameobject_find 曾声明 `list` 而实际返回 `objects`，batch 的
    /// $ref 据此引用会在真实执行时炸，dryRun 还拦不住（$ref 只做结构性校验，不核对上游真返回了什么）。
    ///
    /// 断言方向是单向的 —— 每个声明的输出键必须出现在返回形状里，反向不要求完备。多返回了未声明
    /// 的字段不算错（响应本来就带 success/error 之类的公共字段），只有「声明了却根本不返回」才算。
    ///
    /// 「出现在返回形状里」按任意层级算：嵌在数组元素或子对象里的键同样算数（例如 count/objects
    /// 里每个 object 的 instanceId）。这条放宽是刻意的 —— 严格只认顶层会把大量按嵌套形状写的
    /// Outputs 判红，而幽灵键（声明的名字在整棵响应树里根本不存在）照样跑不掉。
    /// </summary>
    [TestFixture]
    public class OutputsReturnContractTests
    {
        /// <summary>
        /// SkillRouter 在 manifest 层自动注入的键（见 SkillRouter.EntityIdParameterName 一族），
        /// 与方法体返回什么无关，声明与否都不算漂移，直接从比对里剔除。
        /// </summary>
        private static readonly HashSet<string> RouterInjectedOutputs = new HashSet<string>(StringComparer.Ordinal)
        {
            "entityId", "parentEntityId", "childEntityId"
        };

        /// <summary>BatchExecutor.Execute 的信封字段，须与 BatchExecutor.cs 的返回保持一致。</summary>
        private static readonly HashSet<string> BatchEnvelopeKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "success", "error", "errorCode", "retryStrategy", "suggestedFixes",
            "totalItems", "successCount", "failCount", "results"
        };

        /// <summary>
        /// 静态解析不了的技能豁免表 —— 当前为空，785 个技能全部能解析出返回键。
        ///
        /// 往这里加一条之前先确认它真的是「解析不出来」（测试会单独报「解析不出任何返回键」，
        /// 与「声明的键不存在」是两条不同的消息），而不是 Outputs 写错了就往这里躲：
        /// 写错就该改 Outputs。键为 skill 名，值为中文豁免理由。
        /// </summary>
        private static readonly Dictionary<string, string> UnparsableSkills =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
            };

        [Test]
        public void SkillOutputs_ShouldExistInReturnedShape()
        {
            var sources = LoadSkillSources();
            var index = new MethodIndex(sources);
            var issues = new List<string>();
            var checkedCount = 0;

            foreach (var skill in LoadCodeSkills().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (skill.Outputs == null || skill.Outputs.Length == 0)
                {
                    continue;
                }

                if (UnparsableSkills.ContainsKey(skill.Name))
                {
                    continue;
                }

                var owner = skill.Method.DeclaringType != null ? skill.Method.DeclaringType.Name : null;
                var implementations = index.Find(owner, skill.Method.Name);
                if (implementations.Count == 0)
                {
                    issues.Add($"未能在包源码中定位方法: `{skill.Name}` -> {owner}.{skill.Method.Name}");
                    continue;
                }

                // #if / #else 下的同名双分支（stub 与实现）都会命中，键集合取并集。
                var actual = new HashSet<string>(StringComparer.Ordinal);
                foreach (var implementation in implementations)
                {
                    actual.UnionWith(implementation.File.ReturnKeys(implementation));
                }

                checkedCount++;

                if (actual.Count == 0)
                {
                    issues.Add($"解析不出任何返回键: `{skill.Name}` ({owner}.{skill.Method.Name})" +
                               " —— 若确属静态解析不了的形态，登记到 UnparsableSkills 并写明理由");
                    continue;
                }

                var missing = skill.Outputs
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Where(x => !RouterInjectedOutputs.Contains(x))
                    .Where(x => !actual.Contains(x))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                foreach (var ghost in missing)
                {
                    issues.Add($"幽灵输出键: `{skill.Name}.{ghost}` ({owner}.{skill.Method.Name}) —— " +
                               $"成功路径返回的是 {{{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}}}；" +
                               "按 Outputs 规划 $ref 的 agent 会等到一个永不出现的字段");
                }
            }

            Assert.That(checkedCount, Is.GreaterThan(700),
                $"只比对到 {checkedCount} 个技能，解析器多半没找到包源码 —— 空跑的绿色比红色更危险。");

            AssertNoIssues(issues, "Outputs 声明的键在方法体返回中不存在");
        }

        /// <summary>
        /// 解析器自检。上面那条测试「全绿」既可能是真的没漂移，也可能是解析器坏了什么都没解析出来；
        /// 这里用几个形态各异的已知技能把解析结果钉死，坏掉时先在这里红。
        /// </summary>
        [Test]
        public void ReturnKeyParser_ShouldResolveKnownShapes()
        {
            var index = new MethodIndex(LoadSkillSources());

            // 直接 return new { ... }
            AssertParsedKeys(index, "GameObjectSkills", "GameObjectCreate",
                "name", "instanceId", "path", "parent", "position");

            // return BatchExecutor.Execute(...)：信封 + 逐项对象
            AssertParsedKeys(index, "GameObjectSkills", "GameObjectCreateBatch",
                "totalItems", "successCount", "failCount", "results");

            // Dictionary<string, object> 逐键赋值 + helper 内赋值
            AssertParsedKeys(index, "ScriptSkills", "ScriptCreate",
                "status", "path", "jobId", "className", "namespaceName");

            // 跨文件 helper 里的嵌套键（RenderPipelineSkillsCommon.DescribeVolumeComponent）
            AssertParsedKeys(index, "PostProcessSkills", "PostProcessGetEffect",
                "effectType", "parameters");
        }

        private static void AssertParsedKeys(MethodIndex index, string owner, string method, params string[] expected)
        {
            var implementations = index.Find(owner, method);
            Assert.That(implementations, Is.Not.Empty, $"未在包源码中找到 {owner}.{method}");

            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (var implementation in implementations)
            {
                actual.UnionWith(implementation.File.ReturnKeys(implementation));
            }

            foreach (var key in expected)
            {
                Assert.That(actual.Contains(key), Is.True,
                    $"解析器没能从 {owner}.{method} 解析出 `{key}`；实际解析到 " +
                    $"{{{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}}}");
            }
        }

        // ============================================================
        // 反射侧：Outputs 的权威来源
        // ============================================================

        private sealed class CodeSkill
        {
            public string Name;
            public MethodInfo Method;
            public string[] Outputs;
        }

        private static List<CodeSkill> LoadCodeSkills()
        {
            var result = new List<CodeSkill>();
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

                    result.Add(new CodeSkill { Name = attr.Name, Method = method, Outputs = attr.Outputs });
                }
            }

            Assert.That(result, Is.Not.Empty, "未从程序集中发现任何 UnitySkill。");
            return result;
        }

        // ============================================================
        // 源码侧：定位与加载
        // ============================================================

        private static List<SourceFile> LoadSkillSources()
        {
            var root = GetSkillsSourceRoot();
            Assert.That(Directory.Exists(root), Is.True, $"技能源码目录不存在: {root}");

            var files = Directory.GetFiles(root, "*.cs")
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(x => new SourceFile(x))
                .ToList();

            Assert.That(files, Is.Not.Empty, $"{root} 下没有任何 .cs 源码。");
            return files;
        }

        /// <summary>与 SkillDocumentationConsistencyTests.GetDocsRoot 同样的双路径解析：先项目内，后包缓存。</summary>
        private static string GetSkillsSourceRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot != null)
            {
                var inProject = Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills");
                if (Directory.Exists(inProject))
                {
                    return inProject;
                }
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly)
                              ?? PackageInfo.FindForAssembly(typeof(OutputsReturnContractTests).Assembly);
            if (packageInfo != null)
            {
                var inPackage = Path.Combine(packageInfo.resolvedPath, "Editor", "Skills");
                if (Directory.Exists(inPackage))
                {
                    return inPackage;
                }
            }

            return projectRoot != null
                ? Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills")
                : "SkillsForUnity/Editor/Skills";
        }

        // ============================================================
        // 源码侧：解析器
        //
        // 只做「返回了哪些键」这一件事，因此可以停在词法层：先把注释抹白、把字符串字面量内部
        // 填成占位符（花括号、逗号、分号都不会再从字符串里漏出来干扰括号配对），然后按括号配对
        // 找方法体、找 return 表达式、找对象初始化器的成员名。
        // ============================================================

        private sealed class SourceMethod
        {
            public string Name;
            public string Owner;
            public int SignatureStart;
            public int BodyStart;
            public int BodyEnd;
            public bool IsExpressionBodied;
            public SourceFile File;
        }

        private sealed class MethodIndex
        {
            private readonly Dictionary<string, List<SourceMethod>> _byName =
                new Dictionary<string, List<SourceMethod>>(StringComparer.Ordinal);

            public MethodIndex(List<SourceFile> files)
            {
                foreach (var file in files)
                {
                    file.Index = this;
                    foreach (var method in file.Methods)
                    {
                        List<SourceMethod> bucket;
                        if (!_byName.TryGetValue(method.Name, out bucket))
                        {
                            bucket = new List<SourceMethod>();
                            _byName[method.Name] = bucket;
                        }

                        bucket.Add(method);
                    }
                }
            }

            public List<SourceMethod> ByName(string name)
            {
                List<SourceMethod> bucket;
                return _byName.TryGetValue(name, out bucket) ? bucket : new List<SourceMethod>();
            }

            public List<SourceMethod> Find(string owner, string name)
            {
                return ByName(name)
                    .Where(x => string.Equals(x.Owner, owner, StringComparison.Ordinal))
                    .ToList();
            }
        }

        private sealed class SourceFile
        {
            private const char Filler = '_';

            private static readonly HashSet<string> StatementKeywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "if", "while", "for", "foreach", "switch", "catch", "lock",
                "using", "return", "new", "fixed", "sizeof", "typeof", "nameof"
            };

            private static readonly Regex CallableRegex =
                new Regex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);

            private static readonly Regex ModifierRegex =
                new Regex(@"\b(public|private|internal|protected|static)\b", RegexOptions.Compiled);

            private static readonly Regex TypeRegex =
                new Regex(@"\b(?:class|struct)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

            private static readonly Regex ReturnRegex = new Regex(@"\breturn\b", RegexOptions.Compiled);

            private static readonly Regex NewRegex = new Regex(@"\bnew\b", RegexOptions.Compiled);

            private static readonly Regex DelegateBodyRegex =
                new Regex(@"\bdelegate\s*(?:\([^)]*\))?\s*\{", RegexOptions.Compiled);

            private static readonly Regex DictAssignRegex =
                new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\s*\[\s*(?="")", RegexOptions.Compiled);

            private static readonly Regex IdentifierRegex =
                new Regex(@"(?<![.\w""])([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

            private static readonly Regex CallRegex =
                new Regex(@"([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:<[^();{}]*>)?\s*\(",
                    RegexOptions.Compiled);

            private static readonly Regex BareIdentifierRegex =
                new Regex(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*$", RegexOptions.Compiled);

            private static readonly Regex ForeachHeadRegex =
                new Regex(@"foreach\s*\(\s*var\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\s+", RegexOptions.Compiled);

            private static readonly Regex IdentifierTailRegex =
                new Regex(@"([A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.Compiled);

            public readonly string Path;
            public readonly string Masked;
            public readonly Dictionary<int, string> Literals = new Dictionary<int, string>();
            public readonly List<SourceMethod> Methods = new List<SourceMethod>();
            public MethodIndex Index;

            private readonly List<KeyValuePair<string, KeyValuePair<int, int>>> _types =
                new List<KeyValuePair<string, KeyValuePair<int, int>>>();

            public SourceFile(string path)
            {
                Path = path;
                Masked = Mask(File.ReadAllText(path));
                FindTypes();
                FindMethods();
            }

            // ---- 词法遮罩 --------------------------------------------------

            private string Mask(string src)
            {
                var buffer = src.ToCharArray();
                var n = src.Length;
                var i = 0;

                while (i < n)
                {
                    var c = src[i];
                    if (c == '/' && i + 1 < n && src[i + 1] == '/')
                    {
                        while (i < n && src[i] != '\n')
                        {
                            buffer[i] = ' ';
                            i++;
                        }
                    }
                    else if (c == '/' && i + 1 < n && src[i + 1] == '*')
                    {
                        var j = i;
                        while (j < n - 1 && !(src[j] == '*' && src[j + 1] == '/'))
                        {
                            if (src[j] != '\n')
                            {
                                buffer[j] = ' ';
                            }

                            j++;
                        }

                        buffer[j] = ' ';
                        if (j + 1 < n)
                        {
                            buffer[j + 1] = ' ';
                        }

                        i = j + 2;
                    }
                    else if (c == '\'')
                    {
                        var j = ParseChar(src, i);
                        for (var k = i; k < j; k++)
                        {
                            if (src[k] != '\n')
                            {
                                buffer[k] = Filler;
                            }
                        }

                        i = j;
                    }
                    else if (c == '"' || ((c == '$' || c == '@') && IsStringStart(src, i)))
                    {
                        var j = ParseString(src, i);
                        Literals[i] = src.Substring(i, j - i);

                        var quote = i;
                        while (quote < n && (src[quote] == '$' || src[quote] == '@'))
                        {
                            quote++;
                        }

                        for (var k = i; k < j; k++)
                        {
                            if (src[k] != '\n')
                            {
                                buffer[k] = Filler;
                            }
                        }

                        // 两端引号留着，后面的扫描才能看出字面量边界。
                        buffer[quote] = '"';
                        buffer[j - 1] = '"';
                        i = j;
                    }
                    else
                    {
                        i++;
                    }
                }

                return new string(buffer);
            }

            private static bool IsStringStart(string src, int i)
            {
                var j = i;
                while (j < src.Length && (src[j] == '$' || src[j] == '@'))
                {
                    j++;
                }

                return j > i && j < src.Length && src[j] == '"';
            }

            private static int ParseChar(string src, int i)
            {
                var n = src.Length;
                var j = i + 1;
                while (j < n)
                {
                    if (src[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }

                    if (src[j] == '\'')
                    {
                        return j + 1;
                    }

                    if (src[j] == '\n')
                    {
                        return j;
                    }

                    j++;
                }

                return j;
            }

            private static int ParseString(string src, int i)
            {
                var n = src.Length;
                var verbatim = false;
                var interpolated = false;
                while (i < n && (src[i] == '$' || src[i] == '@'))
                {
                    if (src[i] == '@')
                    {
                        verbatim = true;
                    }
                    else
                    {
                        interpolated = true;
                    }

                    i++;
                }

                i++; // 开引号
                while (i < n)
                {
                    var c = src[i];
                    if (!verbatim && c == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c == '"')
                    {
                        if (verbatim && i + 1 < n && src[i + 1] == '"')
                        {
                            i += 2;
                            continue;
                        }

                        return i + 1;
                    }

                    if (interpolated && c == '{')
                    {
                        if (i + 1 < n && src[i + 1] == '{')
                        {
                            i += 2;
                            continue;
                        }

                        i = ParseHole(src, i);
                        continue;
                    }

                    if (interpolated && c == '}' && i + 1 < n && src[i + 1] == '}')
                    {
                        i += 2;
                        continue;
                    }

                    if (!verbatim && c == '\n')
                    {
                        return i;
                    }

                    i++;
                }

                return i;
            }

            /// <summary>插值洞 `{expr}`：里面还能再嵌字符串，所以要递归着走完。</summary>
            private static int ParseHole(string src, int i)
            {
                var n = src.Length;
                var depth = 0;
                while (i < n)
                {
                    var c = src[i];
                    if (c == '{')
                    {
                        depth++;
                        i++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return i;
                        }
                    }
                    else if (c == '"' || ((c == '$' || c == '@') && IsStringStart(src, i)))
                    {
                        i = ParseString(src, i);
                    }
                    else if (c == '\'')
                    {
                        i = ParseChar(src, i);
                    }
                    else
                    {
                        i++;
                    }
                }

                return i;
            }

            private static string LiteralValue(string raw)
            {
                var quote = raw.IndexOf('"');
                if (quote < 0 || raw.Length < quote + 2)
                {
                    return string.Empty;
                }

                var verbatim = raw.Substring(0, quote).IndexOf('@') >= 0;
                var body = raw.Substring(quote + 1, raw.Length - quote - 2);
                if (verbatim)
                {
                    return body.Replace("\"\"", "\"");
                }

                return Regex.Replace(body, @"\\(.)", m =>
                {
                    switch (m.Groups[1].Value)
                    {
                        case "n": return "\n";
                        case "t": return "\t";
                        case "r": return "\r";
                        default: return m.Groups[1].Value;
                    }
                });
            }

            // ---- 结构表 ----------------------------------------------------

            private void FindTypes()
            {
                foreach (Match match in TypeRegex.Matches(Masked))
                {
                    var brace = Masked.IndexOf('{', match.Index + match.Length);
                    if (brace < 0)
                    {
                        continue;
                    }

                    var semicolon = Masked.IndexOf(';', match.Index + match.Length);
                    if (semicolon >= 0 && semicolon < brace)
                    {
                        continue; // 前置声明 / 泛型约束里的 class，没有体
                    }

                    var end = MatchBracket(Masked, brace, '{', '}');
                    if (end < 0)
                    {
                        continue;
                    }

                    _types.Add(new KeyValuePair<string, KeyValuePair<int, int>>(
                        match.Groups[1].Value, new KeyValuePair<int, int>(brace, end)));
                }
            }

            private string OwnerOf(int position)
            {
                string owner = null;
                var innermost = -1;
                foreach (var type in _types)
                {
                    var lo = type.Value.Key;
                    var hi = type.Value.Value;
                    if (lo <= position && position < hi && lo > innermost)
                    {
                        innermost = lo;
                        owner = type.Key;
                    }
                }

                return owner;
            }

            private void FindMethods()
            {
                foreach (Match match in CallableRegex.Matches(Masked))
                {
                    var name = match.Groups[1].Value;
                    if (StatementKeywords.Contains(name))
                    {
                        continue;
                    }

                    var parenOpen = match.Index + match.Length - 1;
                    var parenClose = MatchBracket(Masked, parenOpen, '(', ')');
                    if (parenClose < 0)
                    {
                        continue;
                    }

                    var k = SkipWhitespace(Masked, parenClose, Masked.Length);
                    if (k + 6 <= Masked.Length && string.CompareOrdinal(Masked, k, "where ", 0, 6) == 0)
                    {
                        var next = Masked.IndexOf('{', k);
                        if (next < 0)
                        {
                            continue;
                        }

                        k = next;
                    }

                    var expressionBodied = k + 2 <= Masked.Length && string.CompareOrdinal(Masked, k, "=>", 0, 2) == 0;
                    if (k >= Masked.Length || (Masked[k] != '{' && !expressionBodied))
                    {
                        continue;
                    }

                    // 必须长得像声明：本行前面得有访问修饰符，才不会把 `= new Foo() {`
                    // 或表达式里的调用当成方法。
                    var prefixStart = Math.Max(0, match.Index - 200);
                    var prefix = Masked.Substring(prefixStart, match.Index - prefixStart + 1);
                    var newline = prefix.LastIndexOf('\n');
                    var line = newline >= 0 ? prefix.Substring(newline + 1) : prefix;
                    if (!ModifierRegex.IsMatch(line))
                    {
                        continue;
                    }

                    int bodyStart;
                    int bodyEnd;
                    if (expressionBodied)
                    {
                        bodyStart = k + 2;
                        bodyEnd = StatementEnd(bodyStart, Masked.Length);
                    }
                    else
                    {
                        bodyStart = k;
                        bodyEnd = MatchBracket(Masked, k, '{', '}');
                        if (bodyEnd < 0)
                        {
                            continue;
                        }
                    }

                    Methods.Add(new SourceMethod
                    {
                        Name = name,
                        Owner = OwnerOf(match.Index),
                        SignatureStart = match.Index,
                        BodyStart = bodyStart,
                        BodyEnd = bodyEnd,
                        IsExpressionBodied = expressionBodied,
                        File = this
                    });
                }
            }

            // ---- 返回键 ----------------------------------------------------

            public HashSet<string> ReturnKeys(SourceMethod method)
            {
                return ReturnKeys(method, 0, new HashSet<string>(StringComparer.Ordinal), true);
            }

            private HashSet<string> ReturnKeys(SourceMethod method, int depth, HashSet<string> seen, bool ignoreLambdas)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var token = MethodToken(method);
                if (depth > 3 || seen.Contains(token))
                {
                    return keys;
                }

                seen = Extend(seen, token);
                var lo = method.BodyStart;
                var hi = method.BodyEnd;
                var lambdas = ignoreLambdas ? FindLambdaRanges(lo, hi) : new List<KeyValuePair<int, int>>();

                var spans = new List<KeyValuePair<int, int>>();
                if (method.IsExpressionBodied)
                {
                    spans.Add(new KeyValuePair<int, int>(lo, hi));
                }
                else
                {
                    foreach (Match match in ReturnRegex.Matches(Masked.Substring(lo, hi - lo)))
                    {
                        var position = lo + match.Index;
                        if (InRanges(position, lambdas))
                        {
                            continue; // lambda 里的 return 不是本方法的返回
                        }

                        var start = SkipWhitespace(Masked, lo + match.Index + match.Length, hi);
                        if (start < hi && Masked[start] == ';')
                        {
                            continue;
                        }

                        spans.Add(new KeyValuePair<int, int>(start, StatementEnd(start, hi)));
                    }
                }

                foreach (var span in spans)
                {
                    var start = span.Key;
                    var end = span.Value;
                    var expression = Masked.Substring(start, end - start);

                    keys.UnionWith(KeysInSpan(start, end));

                    if (expression.IndexOf("BatchExecutor.Execute", StringComparison.Ordinal) >= 0)
                    {
                        keys.UnionWith(BatchEnvelopeKeys);
                    }

                    // 返回形状里引用到的局部变量自带键（`var r = ...; r["k"] = v; return r;`）
                    foreach (Match identifier in IdentifierRegex.Matches(expression))
                    {
                        keys.UnionWith(LocalVariableKeys(method, identifier.Groups[1].Value, depth, seen));
                    }

                    // 委托返回 / 三目分支里的被调方
                    foreach (Match call in CallRegex.Matches(expression))
                    {
                        var callee = call.Groups[1].Value;
                        if (StatementKeywords.Contains(callee))
                        {
                            continue;
                        }

                        foreach (var target in ResolveCall(callee, method.Owner))
                        {
                            keys.UnionWith(target.File.ReturnKeys(target, depth + 1, seen, true));
                        }
                    }
                }

                if (spans.Count == 0 && ignoreLambdas && lambdas.Count > 0)
                {
                    // 整个方法只在 lambda 里 return（表达式体的 LINQ 之类），退回去收 lambda 的返回。
                    return ReturnKeys(method, depth, Shrink(seen, token), false);
                }

                return keys;
            }

            /// <summary>
            /// [lo, hi) 里所有对象初始化器发出的键。线性扫描天然覆盖嵌套初始化器，
            /// 所以埋在数组元素里的键也算「响应形状里有」。
            /// </summary>
            private HashSet<string> KeysInSpan(int lo, int hi)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var window = Masked.Substring(lo, hi - lo);

                foreach (Match match in NewRegex.Matches(window))
                {
                    var j = lo + match.Index + match.Length;
                    j = SkipWhitespace(Masked, j, hi);

                    var typeStart = j;
                    while (j < hi && (char.IsLetterOrDigit(Masked[j]) || Masked[j] == '_' || Masked[j] == '.'))
                    {
                        j++;
                    }

                    var typeName = Masked.Substring(typeStart, j - typeStart);
                    j = SkipWhitespace(Masked, j, hi);
                    j = SkipBracket(j, hi, '<', '>');
                    j = SkipBracket(j, hi, '[', ']');
                    j = SkipBracket(j, hi, '(', ')');

                    if (j >= hi || Masked[j] != '{')
                    {
                        continue;
                    }

                    keys.UnionWith(typeName.IndexOf("Dictionary", StringComparison.Ordinal) >= 0
                        ? DictionaryInitializerKeys(j)
                        : AnonymousObjectKeys(j));
                }

                // <identifier>["literal"] = value
                foreach (Match match in DictAssignRegex.Matches(window))
                {
                    AddLiteralAt(keys, lo + match.Index + match.Length);
                }

                return keys;
            }

            private int SkipBracket(int j, int hi, char open, char close)
            {
                if (j >= hi || Masked[j] != open)
                {
                    return j;
                }

                var end = MatchBracket(Masked, j, open, close);
                if (end < 0)
                {
                    return j;
                }

                return SkipWhitespace(Masked, end, hi);
            }

            private HashSet<string> AnonymousObjectKeys(int braceStart)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var end = MatchBracket(Masked, braceStart, '{', '}');
                if (end < 0)
                {
                    return keys;
                }

                foreach (var member in SplitTopLevel(braceStart + 1, end - 1))
                {
                    var key = MemberKey(Masked.Substring(member.Key, member.Value - member.Key));
                    if (!string.IsNullOrEmpty(key))
                    {
                        keys.Add(key);
                    }
                }

                return keys;
            }

            /// <summary>`new Dictionary&lt;string, object&gt; { ["k"] = v }` 与 `{ { "k", v } }` 两种写法。</summary>
            private HashSet<string> DictionaryInitializerKeys(int braceStart)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var end = MatchBracket(Masked, braceStart, '{', '}');
                if (end < 0)
                {
                    return keys;
                }

                foreach (var member in SplitTopLevel(braceStart + 1, end - 1))
                {
                    var i = SkipWhitespace(Masked, member.Key, member.Value);
                    if (i >= member.Value || (Masked[i] != '[' && Masked[i] != '{'))
                    {
                        continue;
                    }

                    i = SkipWhitespace(Masked, i + 1, member.Value);
                    AddLiteralAt(keys, i);
                }

                return keys;
            }

            private void AddLiteralAt(HashSet<string> keys, int position)
            {
                string raw;
                if (Literals.TryGetValue(position, out raw))
                {
                    var value = LiteralValue(raw);
                    if (!string.IsNullOrEmpty(value))
                    {
                        keys.Add(value);
                    }
                }
            }

            /// <summary>匿名对象一个成员发出的键：`key = value` 取等号左侧，简写成员取表达式末段标识符。</summary>
            private static string MemberKey(string member)
            {
                var depth = 0;
                var equals = -1;
                for (var i = 0; i < member.Length; i++)
                {
                    var c = member[i];
                    if (c == '(' || c == '[' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ')' || c == ']' || c == '}')
                    {
                        depth--;
                    }
                    else if (c == '=' && depth == 0)
                    {
                        if (i == 0 || i + 1 >= member.Length)
                        {
                            continue;
                        }

                        var next = member[i + 1];
                        var previous = member[i - 1];
                        if (next == '=' || next == '>' || "=<>!+-*/%&|^".IndexOf(previous) >= 0)
                        {
                            continue;
                        }

                        equals = i;
                        break;
                    }
                }

                if (equals >= 0)
                {
                    var match = IdentifierTailRegex.Match(member.Substring(0, equals).Trim());
                    return match.Success ? match.Groups[1].Value : null;
                }

                var expression = member.Trim();
                if (expression.EndsWith(")", StringComparison.Ordinal) ||
                    expression.EndsWith("]", StringComparison.Ordinal))
                {
                    return null; // 调用/索引结果没有成员名，C# 也不允许这么写简写成员
                }

                var shorthand = IdentifierTailRegex.Match(expression);
                return shorthand.Success ? shorthand.Groups[1].Value : null;
            }

            /// <summary>`var x = ...` 这类局部变量携带的键，含 helper 产出的字典与 foreach 合并。</summary>
            private HashSet<string> LocalVariableKeys(SourceMethod method, string variable, int depth, HashSet<string> seen)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var token = MethodToken(method) + "#" + variable;
                if (depth > 2 || seen.Contains(token))
                {
                    return keys;
                }

                seen = Extend(seen, token);
                var lo = method.BodyStart;
                var hi = method.BodyEnd;
                var body = Masked.Substring(lo, hi - lo);
                var escaped = Regex.Escape(variable);

                // x["key"] = ...
                foreach (Match match in Regex.Matches(body, @"\b" + escaped + @"\s*\[\s*(?="")"))
                {
                    AddLiteralAt(keys, lo + match.Index + match.Length);
                }

                // foreach (var kv in SRC) x[kv.Key] = ... —— SRC 的键会并进 x
                foreach (Match match in ForeachHeadRegex.Matches(body))
                {
                    var loopVariable = match.Groups[1].Value;
                    var sourceStart = lo + match.Index + match.Length;
                    var sourceEnd = sourceStart;
                    var openParens = 1;
                    while (sourceEnd < hi && openParens > 0)
                    {
                        if (Masked[sourceEnd] == '(')
                        {
                            openParens++;
                        }
                        else if (Masked[sourceEnd] == ')')
                        {
                            openParens--;
                            if (openParens == 0)
                            {
                                break;
                            }
                        }

                        sourceEnd++;
                    }

                    var tailEnd = Math.Min(hi, sourceEnd + 120);
                    var tail = Masked.Substring(sourceEnd, tailEnd - sourceEnd);
                    if (!Regex.IsMatch(tail, escaped + @"\s*\[\s*" + Regex.Escape(loopVariable) + @"\s*\."))
                    {
                        continue;
                    }

                    var source = Masked.Substring(sourceStart, sourceEnd - sourceStart);
                    foreach (Match identifier in IdentifierRegex.Matches(source))
                    {
                        keys.UnionWith(LocalVariableKeys(method, identifier.Groups[1].Value, depth + 1, seen));
                    }
                }

                // var x = <初始化表达式>;
                var declaration = new Regex(
                    @"(?<![.\w])(?:var|[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^<>;{}]*>)?(?:\s*\[\s*\])?)\s+"
                    + escaped + @"\s*=(?!=)");
                foreach (Match match in declaration.Matches(body))
                {
                    var start = lo + match.Index + match.Length;
                    var end = StatementEnd(start, hi);
                    keys.UnionWith(KeysInSpan(start, end));

                    var expression = Masked.Substring(start, end - start);
                    foreach (Match call in CallRegex.Matches(expression))
                    {
                        var callee = call.Groups[1].Value;
                        if (StatementKeywords.Contains(callee))
                        {
                            continue;
                        }

                        foreach (var target in ResolveCall(callee, method.Owner))
                        {
                            keys.UnionWith(target.File.ReturnKeys(target, depth + 1, seen, true));
                        }
                    }
                }

                return keys;
            }

            /// <summary>按类作用域解析被调方：`Type.M` 只认 Type 的 M，裸 `M` 只认同文件同类的 M。</summary>
            private List<SourceMethod> ResolveCall(string callee, string callerOwner)
            {
                if (Index == null)
                {
                    return new List<SourceMethod>();
                }

                var parts = callee.Split('.');
                var candidates = Index.ByName(parts[parts.Length - 1]);
                if (candidates.Count == 0)
                {
                    return new List<SourceMethod>();
                }

                if (parts.Length >= 2)
                {
                    var owner = parts[parts.Length - 2];
                    return candidates.Where(x => string.Equals(x.Owner, owner, StringComparison.Ordinal)).ToList();
                }

                return candidates
                    .Where(x => string.Equals(x.File.Path, Path, StringComparison.Ordinal))
                    .Where(x => callerOwner == null || string.Equals(x.Owner, callerOwner, StringComparison.Ordinal))
                    .ToList();
            }

            // ---- 通用扫描工具 ----------------------------------------------

            private List<KeyValuePair<int, int>> FindLambdaRanges(int lo, int hi)
            {
                var ranges = new List<KeyValuePair<int, int>>();
                var i = lo;
                while (true)
                {
                    var arrow = Masked.IndexOf("=>", i, StringComparison.Ordinal);
                    if (arrow < 0 || arrow >= hi)
                    {
                        break;
                    }

                    var k = SkipWhitespace(Masked, arrow + 2, hi);
                    if (k < hi && Masked[k] == '{')
                    {
                        var end = MatchBracket(Masked, k, '{', '}');
                        if (end > 0)
                        {
                            ranges.Add(new KeyValuePair<int, int>(k, end));
                            i = end;
                            continue;
                        }
                    }

                    i = arrow + 2;
                }

                foreach (Match match in DelegateBodyRegex.Matches(Masked.Substring(lo, hi - lo)))
                {
                    var k = lo + match.Index + match.Length - 1;
                    var end = MatchBracket(Masked, k, '{', '}');
                    if (end > 0)
                    {
                        ranges.Add(new KeyValuePair<int, int>(k, end));
                    }
                }

                return ranges;
            }

            private static bool InRanges(int position, List<KeyValuePair<int, int>> ranges)
            {
                return ranges.Any(x => x.Key <= position && position < x.Value);
            }

            private int StatementEnd(int i, int hi)
            {
                var depth = 0;
                while (i < hi)
                {
                    var c = Masked[i];
                    if (c == '(' || c == '[' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ')' || c == ']' || c == '}')
                    {
                        depth--;
                    }
                    else if (c == ';' && depth == 0)
                    {
                        return i;
                    }

                    i++;
                }

                return hi;
            }

            private List<KeyValuePair<int, int>> SplitTopLevel(int lo, int hi)
            {
                var parts = new List<KeyValuePair<int, int>>();
                var depth = 0;
                var start = lo;
                for (var i = lo; i < hi; i++)
                {
                    var c = Masked[i];
                    if (c == '(' || c == '[' || c == '{')
                    {
                        depth++;
                    }
                    else if (c == ')' || c == ']' || c == '}')
                    {
                        depth--;
                    }
                    else if (c == ',' && depth == 0)
                    {
                        parts.Add(new KeyValuePair<int, int>(start, i));
                        start = i + 1;
                    }
                }

                if (start < hi)
                {
                    parts.Add(new KeyValuePair<int, int>(start, hi));
                }

                return parts.Where(x => Masked.Substring(x.Key, x.Value - x.Key).Trim().Length > 0).ToList();
            }

            private static int SkipWhitespace(string text, int i, int hi)
            {
                while (i < hi && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
                {
                    i++;
                }

                return i;
            }

            private static string MethodToken(SourceMethod method)
            {
                return method.File.Path + "#" + method.SignatureStart.ToString();
            }

            private static HashSet<string> Extend(HashSet<string> seen, string token)
            {
                var next = new HashSet<string>(seen, StringComparer.Ordinal);
                next.Add(token);
                return next;
            }

            private static HashSet<string> Shrink(HashSet<string> seen, string token)
            {
                var next = new HashSet<string>(seen, StringComparer.Ordinal);
                next.Remove(token);
                return next;
            }
        }

        private static int MatchBracket(string text, int start, char open, char close)
        {
            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == open)
                {
                    depth++;
                }
                else if (text[i] == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                }
            }

            return -1;
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
    }
}

// Producer:Betsy
